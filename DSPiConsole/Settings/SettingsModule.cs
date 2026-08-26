using System.Collections.Generic;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings;

/// <summary>
/// Base UserControl for a Settings page's content. Provides the standard
/// <c>Attach</c> pattern the rest of the settings infrastructure expects
/// — concrete subclasses get the ViewModel and pending-change tracker
/// handed to them once and call <see cref="Refresh"/> to push values
/// into their controls.
///
/// <para>
/// Not abstract. The WinUI 3 XAML loader can be sensitive to abstract
/// base types as the root element of a XAML file; making this concrete
/// removes that source of runtime risk. Subclasses override
/// <see cref="Refresh"/> to do their work; the default implementation
/// is a no-op.
/// </para>
///
/// <para>
/// Phase 2 contract: pages classified as "device-flash" stage their
/// changes via <see cref="Tracker"/> instead of writing immediately;
/// "live" pages (graph display, app preferences) continue to apply on
/// each control change and ignore the tracker. The split is per-page,
/// not per-control, so the choice lives in the page's handler logic.
/// </para>
/// </summary>
public partial class SettingsModule : UserControl, IPinHighlightPage
{
    /// <summary>Which control on this page sets which GPIO. Populated by the
    /// page as it builds or refreshes its pin controls — at that point it
    /// already knows the pin, so registering is a line rather than a lookup —
    /// and read by <see cref="HighlightPin"/> when the Overview sends someone
    /// here after clicking that pin.</summary>
    private readonly Dictionary<byte, FrameworkElement> _pinTargets = new();

    /// <summary>Note that <paramref name="element"/> is where <paramref name="pin"/>
    /// is set. Call from wherever the page fills that control in; re-registering
    /// the same pin replaces the previous element.</summary>
    protected void RegisterPinTarget(byte pin, FrameworkElement? element)
    {
        if (element != null) _pinTargets[pin] = element;
    }

    /// <summary>Forget the page's pin controls, for a refresh that rebuilds
    /// them. A page whose controls are fixed in XAML never needs this.</summary>
    protected void ClearPinTargets() => _pinTargets.Clear();

    /// <summary>Report a pin change the device refused by naming what already
    /// holds the pin and linking to the page that sets it, which is what the user
    /// needs in order to act. Falls back to <paramref name="fallback"/> when none
    /// of <paramref name="pins"/> turns out to be claimed — the device can refuse
    /// for reasons the host's map cannot see, and a wrong specific answer would be
    /// worse than a vague true one.
    ///
    /// <para>Build <paramref name="claims"/> with whatever self-exclusion the page
    /// uses for its own pickers, so a feature is never reported as blocking
    /// itself.</para></summary>
    private protected static void ShowPinConflict(TextBlock status, Button link,
                                                 IReadOnlyDictionary<byte, PinAssignment> claims,
                                                 string fallback, params byte[] pins)
    {
        var brush = new SolidColorBrush(Color.FromArgb(255, 240, 100, 100));
        if (PinConflict.Describe(status, link, PinConflict.FirstHeld(claims, pins), brush)) return;

        // Nothing identified: say the generic thing, and take the eye away rather
        // than leave one pointing at whatever the last conflict was.
        PinConflict.Disarm(link);
        status.Text = fallback;
        status.Foreground = brush;
        status.Visibility = Visibility.Visible;
    }

    /// <inheritdoc/>
    public virtual bool HighlightPin(byte pin)
    {
        if (!_pinTargets.TryGetValue(pin, out var target) || target.XamlRoot == null) return false;
        PinFlash.Play(target);
        return true;
    }

    /// <summary>The application ViewModel, set by <see cref="Attach"/>.
    /// Concrete pages read/write VM state through this reference. Null
    /// before <see cref="Attach"/> runs — guard if your event handlers
    /// might fire during construction.</summary>
    protected MainViewModel? Vm { get; private set; }

    /// <summary>The Settings window's pending-change tracker. Pages
    /// hosting device-flash settings stage changes here; pages hosting
    /// live settings ignore it. Null before <see cref="Attach"/> runs.</summary>
    protected IPendingChangeTracker? Tracker { get; private set; }

    /// <summary>Bind this page to the application ViewModel and the
    /// settings window's tracker. Called once by <see cref="SettingsShell"/>
    /// when the page is first navigated to. Safe to call again on
    /// reconnect — subclasses should unhook any prior subscriptions
    /// before re-subscribing.</summary>
    public virtual void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        Vm = vm;
        Tracker = tracker;
        Refresh();
    }

    /// <summary>Push current VM state into the page's controls.
    /// Subclasses MUST suppress any apply round-trip that their control
    /// event handlers would otherwise trigger — typically via an
    /// _suppressEvents flag set true for the duration of this method.
    /// Default implementation is a no-op so the base type is concrete
    /// (XAML loader-safe).</summary>
    protected virtual void Refresh() { }

    /// <summary>Internal hook so the shell can request a Refresh after
    /// global state changes (e.g. tracker DiscardAll / ApplyAll
    /// completed). Pages can override this if they need a separate
    /// code path from "VM property changed → Refresh"; the default
    /// just calls <see cref="Refresh"/>.</summary>
    internal virtual void RefreshFromShell() => Refresh();
}

/// <summary>No-op tracker used by pages that don't stage changes
/// (live-apply AppSettings pages). Lets those pages share the
/// tracker-aware Attach signature without sprinkling null-checks.</summary>
internal sealed class NullPendingChangeTracker : IPendingChangeTracker
{
    public static readonly NullPendingChangeTracker Instance = new();
    private NullPendingChangeTracker() { }

    public int Count => 0;
    public System.Collections.Generic.IReadOnlyCollection<PendingChange> Pending
        => System.Array.Empty<PendingChange>();
    public event System.EventHandler? Changed { add { } remove { } }

    public void Stage(PendingChange change) { }
    public bool Discard(string key) => false;
    public void DiscardAll() { }
    public System.Threading.Tasks.Task<ApplyReport> ApplyAllAsync(System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult(new ApplyReport(0, 0, System.Array.Empty<(PendingChange, string)>()));
}
