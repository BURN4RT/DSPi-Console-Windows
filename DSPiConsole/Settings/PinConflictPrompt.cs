using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings;

/// <summary>
/// What to do when switching something on would land it on a GPIO that is
/// already taken.
///
/// <para>Reporting the clash and stopping there leaves the user to work out the
/// fix, go and do it somewhere else, and come back to retry the thing they
/// wanted. Since the only thing standing in the way is which pin the feature
/// uses, and the feature is not on yet, the pin can simply be chosen here: the
/// prompt says what holds it, offers the pins that are free, and switches the
/// feature on once one is picked.</para>
///
/// <para>Three ways out, because there are three sensible answers: pick a
/// different pin and go ahead, go and look at what already holds this one, or
/// leave it alone. The status line's eye stays as the fallback for the refusals
/// this cannot resolve — a pin the host's map cannot account for, or a device
/// that refuses for a reason other than the pin.</para>
/// </summary>
internal static class PinConflictPrompt
{
    internal enum Outcome
    {
        /// <summary>Dismissed; nothing was changed.</summary>
        Cancelled,
        /// <summary>A new pin was chosen and the caller's work went through.</summary>
        Applied,
        /// <summary>The user went to look at whatever holds the pin instead.</summary>
        WentToOwner,
    }

    /// <summary>
    /// Ask for a different pin for <paramref name="featureName"/>, given that
    /// <paramref name="owner"/> holds the one it wanted.
    /// </summary>
    /// <param name="claims">The live assignment map, to mark which candidates are
    /// spoken for — the same annotation the pin pickers show.</param>
    /// <param name="candidates">Pins this feature may legally use.</param>
    /// <param name="applyAsync">Move the feature to the chosen pin and carry out
    /// whatever was refused. Returns false to keep the prompt open, which is what
    /// happens when the retry is itself refused.</param>
    public static async Task<Outcome> ShowAsync(
        XamlRoot root,
        string featureName,
        PinAssignment owner,
        IReadOnlyDictionary<byte, PinAssignment> claims,
        IEnumerable<byte> candidates,
        Func<byte, Task<bool>> applyAsync)
    {
        var picker = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        int firstFree = -1;
        foreach (byte pin in candidates)
        {
            bool taken = claims.TryGetValue(pin, out var claim);
            picker.Items.Add(new ComboBoxItem
            {
                // The wording the pin pickers already use, so a pin reads the
                // same here as it does on the page you would have gone to.
                Content = taken ? $"GPIO {pin} ({claim.Label})" : $"GPIO {pin}",
                Tag = pin,
                IsEnabled = !taken,
            });
            if (!taken && firstFree < 0) firstFree = picker.Items.Count - 1;
        }
        picker.SelectedIndex = firstFree;

        var message = new TextBlock
        {
            Text = $"GPIO {owner.Pin} is already used by {owner.Label}. "
                   + $"Choose a different pin for {featureName}, or go and look at what is using it.",
            TextWrapping = TextWrapping.Wrap,
        };
        var failure = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 240, 100, 100)),
        };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(message);
        body.Children.Add(picker);
        body.Children.Add(failure);
        if (firstFree < 0)
        {
            failure.Text = "Every pin this can use is already taken. Free one first.";
            failure.Visibility = Visibility.Visible;
        }

        var dialog = new ContentDialog
        {
            Title = $"GPIO {owner.Pin} is in use",
            Content = body,
            PrimaryButtonText = "Apply",
            SecondaryButtonText = $"Go to {PageTitle(owner.PageId)}",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = firstFree >= 0,
            XamlRoot = root,
        };

        // The retry happens while the dialog is still up, so a refusal can be
        // shown against the picker that caused it rather than behind a dialog
        // that has already closed.
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (picker.SelectedItem is not ComboBoxItem item || item.Tag is not byte pin) return;
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            bool ok = await applyAsync(pin);
            if (!ok)
            {
                args.Cancel = true;
                failure.Text = $"GPIO {pin} was refused too. Try another.";
                failure.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = true;
            }
            deferral.Complete();
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) return Outcome.Applied;
        if (result != ContentDialogResult.Secondary) return Outcome.Cancelled;

        // Sent to the owner: the shell selects that page and flashes the control,
        // which is the same trip the Overview's map and the status eye make.
        SettingsShell.RequestPin(owner.PageId, owner.Pin);
        return Outcome.WentToOwner;
    }

    private static string PageTitle(string pageId)
    {
        foreach (var page in SettingsRegistry.Pages)
            if (page.Id == pageId) return page.Title;
        return "Settings";
    }
}
