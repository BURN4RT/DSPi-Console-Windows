using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace DSPiConsole.Settings;

/// <summary>
/// A page that can point at the control setting a given GPIO. The Overview's
/// map is a read-only summary, so clicking a pin there has nowhere useful to go
/// except the page that owns it — and landing on a page of a dozen cards
/// without saying which one you came for only moves the search.
///
/// <para>Implemented for free by <see cref="SettingsModule"/>: a page registers
/// each pin-owning control as it builds or refreshes it (where it already knows
/// the pin), and the base class does the lookup and the flash. A page that hosts
/// something other than a SettingsModule implements this itself.</para>
/// </summary>
internal interface IPinHighlightPage
{
    /// <summary>Bring the control that sets <paramref name="pin"/> into view and
    /// flash it. False when this page has no control for that pin right now — a
    /// page still loading, or a pin whose owner it doesn't show.</summary>
    bool HighlightPin(byte pin);
}

/// <summary>
/// The flash itself: a short accent ring around the control that sets the pin
/// you clicked.
///
/// <para>It pulses the colour of the control's own border rather than overlaying
/// anything, so nothing has to be inserted into a page's visual tree to be
/// highlighted, and the control is left exactly as it was found when the
/// animation ends. Colour only: thickness takes part in layout, so widening a
/// border to make the ring bolder grows the control and nudges everything below
/// it down the page, then drops it back when the flash ends. A control with no
/// border to colour blinks its opacity instead, which is layout-neutral too.</para>
/// </summary>
internal static class PinFlash
{
    private static readonly TimeSpan PulseLength = TimeSpan.FromMilliseconds(320);
    private const int Pulses = 3;

    public static void Play(FrameworkElement target)
    {
        if (target == null) return;

        // A control that has not been realised yet still reports the property
        // default for BorderThickness, because the style that sets it is applied
        // with the template. Asking now would send a perfectly ordinary picker to
        // the border-less fallback - which is why the first flash on a freshly
        // built page blinked and every one after it outlined.
        if (!target.IsLoaded)
        {
            void Ready(object sender, RoutedEventArgs e)
            {
                target.Loaded -= Ready;
                Play(target);
            }
            target.Loaded += Ready;
            return;
        }

        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        if (target is Control control && HasBorder(control)) PulseBorder(control);
        else PulseOpacity(target);
    }

    /// <summary>Run the control's own border between its resting colour and the
    /// accent. The brush is this animation's own instance, so animating it cannot
    /// disturb one the theme shares with the rest of the page, and it starts and
    /// ends on the resting colour so the border is never left looking wrong.</summary>
    private static void PulseBorder(Control control)
    {
        var previousBrush = control.BorderBrush;
        var resting = previousBrush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
        var flash = new SolidColorBrush(resting);
        control.BorderBrush = flash;

        var storyboard = new Storyboard();
        var animation = new ColorAnimationUsingKeyFrames { EnableDependentAnimation = true };
        var time = TimeSpan.Zero;
        for (int i = 0; i < Pulses; i++)
        {
            animation.KeyFrames.Add(ColorFrame(time, AccentColor()));
            time += PulseLength / 2;
            animation.KeyFrames.Add(ColorFrame(time, resting));
            time += PulseLength / 2;
        }
        Storyboard.SetTarget(animation, flash);
        Storyboard.SetTargetProperty(animation, "Color");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => control.BorderBrush = previousBrush;
        storyboard.Begin();
    }

    /// <summary>True when a control draws a border for the pulse to colour.</summary>
    private static bool HasBorder(Control control)
    {
        var t = control.BorderThickness;
        return t.Left > 0 || t.Top > 0 || t.Right > 0 || t.Bottom > 0;
    }

    private static LinearColorKeyFrame ColorFrame(TimeSpan at, Color value) =>
        new() { KeyTime = KeyTime.FromTimeSpan(at), Value = value };

    /// <summary>The fallback for something with no border of its own: blink it.
    /// Restores the opacity it was found at rather than assuming 1.</summary>
    private static void PulseOpacity(FrameworkElement target)
    {
        double previous = target.Opacity;
        var storyboard = new Storyboard();
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        var time = TimeSpan.Zero;
        for (int i = 0; i < Pulses; i++)
        {
            animation.KeyFrames.Add(Frame(time, previous));
            time += PulseLength / 2;
            animation.KeyFrames.Add(Frame(time, previous * 0.25));
            time += PulseLength / 2;
        }
        animation.KeyFrames.Add(Frame(time, previous));
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => target.Opacity = previous;
        storyboard.Begin();
    }

    private static LinearDoubleKeyFrame Frame(TimeSpan at, double value) =>
        new() { KeyTime = KeyTime.FromTimeSpan(at), Value = value };

    private static Color AccentColor() =>
        Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var b)
        && b is SolidColorBrush brush
            ? brush.Color
            : Color.FromArgb(255, 0x4A, 0x8F, 0xE3);
}

/// <summary>
/// Naming the thing in the way, when a pin change or an enable is refused for a
/// conflict.
///
/// <para>"A pair's data pin conflicts — assign different GPIOs first" tells you
/// that you are stuck without telling you what you are stuck on: which pin, what
/// holds it, or where to go and free it. Everything needed to say all three is
/// already in the assignment map, and the Overview's click-through already knows
/// how to get you there, so a refusal can name the owner and hand you a link to
/// the page that sets it.</para>
/// </summary>
internal static class PinConflict
{
    /// <summary>The first of <paramref name="pins"/> that something already
    /// holds, or null if none of them is claimed. Pass a map built with whatever
    /// exclusion the asking feature uses for its own pickers, so a feature is
    /// never reported as blocking itself.</summary>
    public static PinAssignment? FirstHeld(IReadOnlyDictionary<byte, PinAssignment> claims,
                                           params byte[] pins)
    {
        foreach (byte pin in pins)
            if (claims.TryGetValue(pin, out var claim)) return claim;
        return null;
    }

    /// <summary>Same, for a run of pins a feature would light up at once — the
    /// extra I2S pairs or S/PDIF inputs an enable brings in.</summary>
    public static PinAssignment? FirstHeld(IReadOnlyDictionary<byte, PinAssignment> claims,
                                           IEnumerable<byte> pins)
    {
        foreach (byte pin in pins)
            if (claims.TryGetValue(pin, out var claim)) return claim;
        return null;
    }

    /// <summary>Say what already holds the pin, and arm the eye button beside the
    /// message to go there. Returns false when there is no claim to describe, so
    /// the caller can fall back to its own wording rather than leave a blank line.
    ///
    /// <para>The message is plain text and the eye is an ordinary button next to
    /// it. It was briefly a link inside the text: a TextBlock takes only inline
    /// content, so a button among its inlines is not a layout quirk but a throw
    /// that takes the app with it.</para></summary>
    public static bool Describe(TextBlock target, Button link, PinAssignment? owner,
                                Brush foreground, string? lead = null)
    {
        if (owner is not { } claim) return false;

        target.Text = string.IsNullOrEmpty(lead)
            ? $"GPIO {claim.Pin} is already used by {claim.Label}."
            : $"{lead} GPIO {claim.Pin} is already used by {claim.Label}.";
        target.Foreground = foreground;
        target.Visibility = Visibility.Visible;

        Arm(link, claim);
        return true;
    }

    /// <summary>Point the eye at a claim. The handler is replaced rather than
    /// added to, so a button re-armed for a second conflict doesn't still carry
    /// the first one's destination.</summary>
    private static void Arm(Button link, PinAssignment claim)
    {
        if (_armed.TryGetValue(link, out var previous)) link.Click -= previous;
        RoutedEventHandler handler = (_, _) => SettingsShell.RequestPin(claim.PageId, claim.Pin);
        link.Click += handler;
        _armed.Remove(link);
        _armed.Add(link, handler);

        ToolTipService.SetToolTip(link, $"Show {PageTitle(claim.PageId)}, where GPIO {claim.Pin} is set");
        link.Visibility = Visibility.Visible;
    }

    /// <summary>Take the eye away — for a message that names no pin.</summary>
    public static void Disarm(Button? link)
    {
        if (link == null) return;
        if (_armed.TryGetValue(link, out var previous))
        {
            link.Click -= previous;
            _armed.Remove(link);
        }
        link.Visibility = Visibility.Collapsed;
    }

    /// <summary>What each eye is currently wired to, so re-arming can unhook the
    /// last one. Keyed weakly: a page the shell drops should not be held alive by
    /// a handler recorded here.</summary>
    private static readonly ConditionalWeakTable<Button, RoutedEventHandler> _armed = new();

    /// <summary>What a page calls itself, so the tooltip names where the eye goes.
    /// Read from the registry rather than a table here, which would be one more
    /// thing to keep in step with the page titles.</summary>
    private static string PageTitle(string pageId)
    {
        foreach (var page in SettingsRegistry.Pages)
            if (page.Id == pageId) return page.Title;
        return "Settings";
    }
}
