using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml.Controls;

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
public partial class SettingsModule : UserControl
{
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
