using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DSPiConsole.Settings;

/// <summary>
/// One staged-but-not-yet-applied device-flash change. Pages produce
/// these from their control-change handlers and pass them to
/// <see cref="IPendingChangeTracker.Stage"/>; the InfoBar's Apply
/// button drains them via <see cref="IPendingChangeTracker.ApplyAllAsync"/>.
///
/// <para>
/// The <paramref name="Apply"/> delegate is the closure that performs
/// the actual USB call. It captures the new value internally so the
/// tracker doesn't need to know the field's type. Returning a non-zero
/// byte signals firmware failure — same convention the legacy dialog's
/// per-call <c>PinConfigResult</c> uses; <c>0</c> means success.
/// </para>
/// </summary>
/// <param name="Key">Stable identifier for this change.
/// Format <c>"{page-id}.{field}"</c> — e.g. <c>"hardware.output-assignment.output-1.pin"</c>.
/// Used to de-duplicate: staging a second change with the same key
/// replaces the first (the user edited the field again before applying).</param>
/// <param name="PageId">Which page produced this change. Drives the
/// sidebar pending-dot propagation.</param>
/// <param name="FieldLabel">Human-readable label for the field (e.g.
/// "Output 1 GPIO"). Shown in the InfoBar's hover-detail and in
/// per-field inline diff chips.</param>
/// <param name="OldDisplay">Pre-edit value formatted for display
/// (e.g. "GPIO 6", "S/PDIF", "Off"). Stored at first stage of this
/// key; subsequent same-key stages don't update it.</param>
/// <param name="NewDisplay">Currently-pending value formatted for
/// display (e.g. "GPIO 7"). Updated on each same-key restage.</param>
/// <param name="Apply">The actual USB call. Returns 0 for success or
/// firmware's status byte for failure.</param>
/// <param name="DependsOn">Optional keys that must apply successfully
/// before this one runs. Used for "output type → S/PDIF first, then
/// BCK pin change". The tracker topologically sorts the batch.</param>
public sealed record PendingChange(
    string Key,
    string PageId,
    string FieldLabel,
    string OldDisplay,
    string NewDisplay,
    Func<Task<byte>> Apply,
    IReadOnlyList<string>? DependsOn = null);

/// <summary>
/// Summary of a completed <see cref="IPendingChangeTracker.ApplyAllAsync"/>
/// run. Surfaced to the InfoBar so it can show "3 applied, 1 failed"
/// and keep the failing change(s) staged for the user to retry or
/// discard.
/// </summary>
public sealed record ApplyReport(
    int Applied,
    int Failed,
    IReadOnlyList<(PendingChange Change, string Error)> Failures);

/// <summary>
/// In-memory store of pending device-flash changes for the Settings
/// window. Pages stage edits here instead of writing directly to the
/// device; the top-of-window InfoBar's Apply button drains the buffer
/// in dependency order.
///
/// <para>
/// Lifetime: one tracker per <see cref="SettingsWindow"/> instance.
/// Closed-and-reopened settings windows get fresh trackers, so
/// abandoned edits don't leak across sessions.
/// </para>
///
/// <para>
/// Thread-safety: all members are expected to be called from the UI
/// thread. Pages dispatch their Stage calls from control-change
/// handlers (already UI-thread). Apply runs USB I/O on a background
/// task but invokes its progress callbacks on the UI thread.
/// </para>
/// </summary>
public interface IPendingChangeTracker
{
    /// <summary>Number of currently-staged changes. Drives InfoBar
    /// visibility and the pending-dot in the sidebar.</summary>
    int Count { get; }

    /// <summary>Snapshot of currently-staged changes, in stage order.
    /// Used by the InfoBar's expander to list them and by per-page
    /// code to find "is this field's change staged?" for inline diff
    /// chip rendering.</summary>
    IReadOnlyCollection<PendingChange> Pending { get; }

    /// <summary>Raised whenever <see cref="Count"/> or the contents of
    /// <see cref="Pending"/> change. Subscribers refresh their UI
    /// (InfoBar, sidebar dots, inline diff chips). Always raised on
    /// the UI thread.</summary>
    event EventHandler? Changed;

    /// <summary>Add or replace a staged change. Two stages with the
    /// same <c>Key</c> are de-duplicated — the second overrides the
    /// first's <c>NewDisplay</c> and <c>Apply</c>, but keeps the
    /// <c>OldDisplay</c> from the first (the user's pre-edit state).</summary>
    void Stage(PendingChange change);

    /// <summary>Remove a single staged change by key. Used by per-field
    /// "revert" chips and by Apply on success.</summary>
    bool Discard(string key);

    /// <summary>Remove all staged changes. Used by the InfoBar's
    /// Discard button and by the close-confirm modal's Discard path.</summary>
    void DiscardAll();

    /// <summary>
    /// Apply every staged change to the device, in dependency order
    /// (changes with <c>DependsOn</c> wait for their predecessors to
    /// succeed). Successful changes are removed from the buffer;
    /// failed changes remain and surface in the returned report so
    /// the user can retry or discard them individually.
    /// </summary>
    /// <param name="ct">Cancellation token. Apply runs sequentially —
    /// cancellation stops the batch between calls; any change already
    /// in flight runs to completion (USB control transfers are short).</param>
    Task<ApplyReport> ApplyAllAsync(CancellationToken ct = default);
}
