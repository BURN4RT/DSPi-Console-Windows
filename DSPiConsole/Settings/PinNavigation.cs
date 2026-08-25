using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
/// <para>It pulses the control's own border rather than overlaying anything, so
/// nothing has to be inserted into a page's visual tree to be highlighted, and
/// the control is left exactly as it was found when the animation ends. A
/// control with no border to pulse (a bare panel) blinks its opacity instead.</para>
/// </summary>
internal static class PinFlash
{
    private static readonly TimeSpan PulseLength = TimeSpan.FromMilliseconds(320);
    private const int Pulses = 3;

    public static void Play(FrameworkElement target)
    {
        if (target == null) return;
        target.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        if (target is Control control) PulseBorder(control); else PulseOpacity(target);
    }

    /// <summary>Ring the control in the accent colour and fade it out. The brush
    /// is this animation's own instance, so animating its opacity cannot disturb
    /// a brush the theme shares with everything else on the page.</summary>
    private static void PulseBorder(Control control)
    {
        var previousBrush = control.BorderBrush;
        var previousThickness = control.BorderThickness;
        var flash = new SolidColorBrush(AccentColor());

        control.BorderBrush = flash;
        control.BorderThickness = new Thickness(2);

        var storyboard = Pulse(flash, "Opacity");
        storyboard.Completed += (_, _) =>
        {
            control.BorderBrush = previousBrush;
            control.BorderThickness = previousThickness;
        };
        storyboard.Begin();
    }

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

    /// <summary>Full, out, full … so the ring arrives immediately rather than
    /// fading up after the page has already settled.</summary>
    private static Storyboard Pulse(DependencyObject target, string property)
    {
        var storyboard = new Storyboard();
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        var time = TimeSpan.Zero;
        for (int i = 0; i < Pulses; i++)
        {
            animation.KeyFrames.Add(Frame(time, 1));
            time += PulseLength / 2;
            animation.KeyFrames.Add(Frame(time, 0));
            time += PulseLength / 2;
        }
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
        return storyboard;
    }

    private static LinearDoubleKeyFrame Frame(TimeSpan at, double value) =>
        new() { KeyTime = KeyTime.FromTimeSpan(at), Value = value };

    private static Color AccentColor() =>
        Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var b)
        && b is SolidColorBrush brush
            ? brush.Color
            : Color.FromArgb(255, 0x4A, 0x8F, 0xE3);
}
