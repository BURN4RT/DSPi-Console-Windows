using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings;

/// <summary>One thing that needs a different GPIO before an enable can go
/// through: what it is, the pin it wanted, and what already holds that pin.</summary>
internal sealed record PinReassignment(string FeatureName, byte WantedPin, PinAssignment Owner);

/// <summary>
/// What to do when switching something on would land it on GPIOs that are
/// already taken.
///
/// <para>Reporting the clash and stopping there leaves the user to work out the
/// fix, go and do it somewhere else, and come back to retry. Since the only
/// thing in the way is which pins the feature uses, and the feature is not on
/// yet, they can simply be chosen here.</para>
///
/// <para>Every clash is shown at once, one row each. Raising the I2S channel
/// count brings up several pairs together, so three of them can be blocked by
/// three different things; resolving the first and retrying would just refuse on
/// the second, and the user would be walked through the same dialog three times
/// with no idea how many were left. One row per blocked slot says how much is
/// actually wrong before anything is changed.</para>
/// </summary>
internal static class PinConflictPrompt
{
    internal enum Outcome
    {
        /// <summary>Dismissed; nothing was changed.</summary>
        Cancelled,
        /// <summary>New pins were chosen and the caller's work went through.</summary>
        Applied,
        /// <summary>The user went to look at whatever holds the pin instead.</summary>
        WentToOwner,
    }

    /// <summary>
    /// Ask for a different pin for each of <paramref name="blocked"/>.
    /// </summary>
    /// <param name="claims">The live assignment map, to mark which candidates are
    /// spoken for — the same annotation the pin pickers show.</param>
    /// <param name="candidates">Pins these features may legally use.</param>
    /// <param name="applyAsync">Move each feature to its chosen pin, in row order,
    /// and carry out whatever was refused. Returns false to keep the prompt open,
    /// which is what happens when the retry is itself refused.</param>
    public static async Task<Outcome> ShowAsync(
        XamlRoot root,
        IReadOnlyList<PinReassignment> blocked,
        IReadOnlyDictionary<byte, PinAssignment> claims,
        IEnumerable<byte> candidates,
        Func<IReadOnlyList<byte>, Task<bool>> applyAsync)
    {
        if (blocked.Count == 0) return Outcome.Cancelled;
        var pool = candidates.ToList();

        var body = new StackPanel { Spacing = 12, MinWidth = 380 };
        body.Children.Add(new TextBlock
        {
            // Neither the count nor the pin belongs here: the rows below carry
            // both, along with what holds each one.
            Text = blocked.Count == 1
                ? "This pin is already in use. Choose a different one."
                : "The following pins are already in use. Choose a different one for each.",
            TextWrapping = TextWrapping.Wrap,
            // Set off from the rows below rather than sitting on the same rhythm
            // as them: it introduces the list, it is not the first item in it.
            Margin = new Thickness(0, 0, 0, 8),
        });

        // A row's eye leaves for another page, so it has to take the prompt with
        // it: navigating out from under a dialog that stays up just puts the
        // dialog in front of the thing it sent you to look at. The dialog does
        // not exist yet, hence the deferred reference.
        ContentDialog? open = null;
        bool wentToOwner = false;
        void GoTo(PinReassignment item)
        {
            wentToOwner = true;
            open?.Hide();
            SettingsShell.RequestPin(item.Owner.PageId, item.Owner.Pin);
        }

        var pickers = new List<ComboBox>();
        var rows = new StackPanel { Spacing = 8 };
        foreach (var item in blocked)
        {
            var picker = new ComboBox { MinWidth = 200 };
            pickers.Add(picker);
            rows.Children.Add(BuildRow(item, picker, GoTo));
        }
        body.Children.Add(rows);

        var failure = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 100, 100)),
        };
        body.Children.Add(failure);

        var dialog = new ContentDialog
        {
            // The line under it already names the pin, or counts them, so the
            // title has nothing left to add by repeating either.
            Title = "GPIO Conflict",
            Content = body,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        open = dialog;
        // One clash has one place to go, so it keeps the button the rest of the
        // app uses. Several may point at several pages, so each row carries its
        // own eye instead and a single button would have to pick a favourite.
        if (blocked.Count == 1)
            dialog.SecondaryButtonText = $"Go to {PageTitle(blocked[0].Owner.PageId)}";

        // Every picker offers the free pins, minus the ones the other rows have
        // taken: three rows fixed by hand to the same GPIO would fail on the
        // second assignment, having looked perfectly reasonable on screen.
        void Repopulate()
        {
            var chosen = pickers
                .Select(p => (p.SelectedItem as ComboBoxItem)?.Tag as byte?)
                .Where(v => v.HasValue).Select(v => v!.Value).ToList();

            for (int i = 0; i < pickers.Count; i++)
            {
                var picker = pickers[i];
                byte? mine = (picker.SelectedItem as ComboBoxItem)?.Tag as byte?;
                foreach (var entry in picker.Items.OfType<ComboBoxItem>())
                {
                    if (entry.Tag is not byte pin) continue;
                    bool takenByRow = pin != mine && chosen.Contains(pin);
                    bool takenOnDevice = claims.ContainsKey(pin);
                    entry.IsEnabled = !takenOnDevice && !takenByRow;
                }
            }
            dialog.IsPrimaryButtonEnabled =
                pickers.All(p => p.SelectedItem is ComboBoxItem { Tag: byte }) &&
                chosen.Distinct().Count() == pickers.Count;
        }

        // Seed each row with a distinct free pin so the common case is one click.
        var used = new HashSet<byte>(claims.Keys);
        foreach (var picker in pickers)
        {
            int select = -1;
            foreach (byte pin in pool)
            {
                bool free = !claims.ContainsKey(pin);
                picker.Items.Add(new ComboBoxItem
                {
                    // The wording the pin pickers already use, so a pin reads the
                    // same here as on the page you would otherwise have gone to.
                    Content = free ? $"GPIO {pin}" : $"GPIO {pin} ({claims[pin].Label})",
                    Tag = pin,
                    IsEnabled = free,
                });
                if (free && select < 0 && used.Add(pin)) select = picker.Items.Count - 1;
            }
            picker.SelectedIndex = select;
            picker.SelectionChanged += (_, _) => Repopulate();
        }
        Repopulate();

        if (!dialog.IsPrimaryButtonEnabled)
        {
            failure.Text = "There aren't enough free pins. Free one elsewhere first.";
            failure.Visibility = Visibility.Visible;
        }

        // The retry runs with the dialog still up, so a refusal is reported
        // against the pickers that caused it rather than behind a closed dialog.
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var chosen = pickers
                .Select(p => (byte)((ComboBoxItem)p.SelectedItem).Tag).ToList();
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            bool ok = await applyAsync(chosen);
            if (!ok)
            {
                args.Cancel = true;
                failure.Text = "The device refused that too. Try different pins.";
                failure.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = true;
            }
            deferral.Complete();
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) return Outcome.Applied;
        if (result == ContentDialogResult.Secondary)
        {
            SettingsShell.RequestPin(blocked[0].Owner.PageId, blocked[0].Owner.Pin);
            return Outcome.WentToOwner;
        }
        // Hide() reports as a dismissal, so a row's eye is told apart from Cancel
        // by whether one was actually used.
        return wentToOwner ? Outcome.WentToOwner : Outcome.Cancelled;
    }

    /// <summary>One blocked slot: what it is, what is in its way, the picker, and
    /// an eye to go and look at the thing in its way.</summary>
    private static FrameworkElement BuildRow(PinReassignment item, ComboBox picker,
                                            Action<PinReassignment> onGoTo)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var caption = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(new TextBlock { Text = item.FeatureName, FontSize = 13 });
        caption.Children.Add(new TextBlock
        {
            Text = $"GPIO {item.WantedPin} — {item.Owner.Label}",
            FontSize = 11,
            Foreground = Secondary(),
        });
        Grid.SetColumn(caption, 0);
        grid.Children.Add(caption);

        Grid.SetColumn(picker, 1);
        grid.Children.Add(picker);

        var eye = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 13 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(eye,
            $"Show {PageTitle(item.Owner.PageId)}, where GPIO {item.WantedPin} is set");
        eye.Click += (_, _) => onGoTo(item);
        Grid.SetColumn(eye, 2);
        grid.Children.Add(eye);

        return grid;
    }

    private static Brush Secondary() =>
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var b) && b is Brush br
            ? br : new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    private static string PageTitle(string pageId)
    {
        foreach (var page in SettingsRegistry.Pages)
            if (page.Id == pageId) return page.Title;
        return "Settings";
    }
}
