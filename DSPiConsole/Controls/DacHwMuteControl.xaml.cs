using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Controls;

/// <summary>
/// "External DAC Hardware Mute" Settings module. Self-contained — owns its
/// XAML, ViewModel hook, debouncing, capability gating, and pin-conflict
/// awareness. Drop it into a Settings tab via XAML; call
/// <see cref="Attach(MainViewModel)"/> once after the host's ViewModel is
/// ready; subscribe to <see cref="PinChanged"/> if the host needs to refresh
/// other pickers' conflict highlights.
///
/// <para>
/// The control writes through <see cref="MainViewModel.ApplyDacHwMuteAsync"/>
/// — a single typed-config apply path that swaps the whole config object,
/// so a future field added to <see cref="DacHwMuteConfig"/> needs no changes
/// here beyond a new XAML control + one wire-up line.
/// </para>
/// </summary>
public sealed partial class DacHwMuteControl : UserControl
{
    private MainViewModel? _vm;
    private bool _suppressApply;          // Block apply during programmatic UI updates.
    private bool _hasSetInitialExpansion; // Refresh seeds expansion state once; user owns it afterward.
    private DispatcherTimer? _sliderDebounce;
    private DacHwMuteConfig? _pendingSliderConfig;
    private IReadOnlyDictionary<byte, string>? _pinOwners;

    // GPIO pin list. Mirrors the platform's exposed audio-capable pins (same
    // set the output / I²S / SPDIF-RX pickers use). 0xFF is presented as
    // "No Pin" — the firmware's sentinel for "feature disabled even if
    // enabled=1". Centralizing this list in a HardwarePins constants class
    // would be ideal future cleanup; for now we keep it inline to avoid
    // racing the Settings dialog's ValidPins constant out of sync.
    private static readonly byte[] ValidPins =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
        13, 14, 15, 16, 17, 18, 19, 20, 21, 22,
        26, 27, 28
    ];

    // Debounce window for the two slider controls. Long enough that a steady
    // drag coalesces to one SET-per-pause, short enough that letting go of
    // the thumb feels instant. The firmware persists every SET to flash, so
    // smaller windows here would chew flash endurance on rapid drags.
    private static readonly TimeSpan SliderDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Raised when the user picks a different mute GPIO pin. The host
    /// Settings dialog should refresh other pin pickers' conflict states so
    /// the just-claimed pin appears as in-use elsewhere.
    /// </summary>
    public event EventHandler? PinChanged;

    public DacHwMuteControl()
    {
        InitializeComponent();
        PopulatePinCombo();
        WireEvents();
    }

    /// <summary>Bind this control to the application's ViewModel. Must be
    /// called once after construction; safe to call again on reconnect.</summary>
    public void Attach(MainViewModel vm)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Refresh();
    }

    /// <summary>Provide a snapshot of pins currently claimed by other features
    /// (outputs, I²S, SPDIF RX). Each entry maps GPIO → owner label so the
    /// pin combo can grey-out and label conflicted choices. Call again when
    /// any of those upstream pins change.</summary>
    public void SetPinOwners(IReadOnlyDictionary<byte, string>? pinOwners)
    {
        _pinOwners = pinOwners;
        RefreshPinComboConflicts();
    }

    // -----------------------------------------------------------------------
    // Initial wiring
    // -----------------------------------------------------------------------

    private void PopulatePinCombo()
    {
        PinCombo.Items.Clear();
        // First item is the "no pin" sentinel.
        PinCombo.Items.Add(new ComboBoxItem
        {
            Content = "No Pin (disabled)",
            Tag = DacHwMuteConfig.PinNone
        });
        foreach (var pin in ValidPins)
            PinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
    }

    private void WireEvents()
    {
        // No Expander.Expanding / .Collapsed subscriptions — the chevron is a
        // pure "peek" affordance now. Expansion state is driven only by:
        //   • The Expander's own click-to-expand behavior (user → chevron).
        //   • Refresh()'s initial seed (first-load convenience: open when
        //     the feature is enabled so settings are immediately visible).
        //   • The forced-open we apply when the firmware doesn't support
        //     the feature, so the "not supported" notice is discoverable.
        // The enable toggle never moves the chevron; the chevron never
        // moves the toggle. Clean decoupling, no re-entry guards needed.
        EnableToggle.Toggled += OnEnabledToggled;
        PolarityToggle.Toggled += OnPolarityToggled;
        PinCombo.SelectionChanged += OnPinSelectionChanged;
        HoldMsSlider.ValueChanged += OnHoldChanged;
        ReleaseMsSlider.ValueChanged += OnReleaseChanged;
        TestButton.Click += OnTestClick;
    }

    // -----------------------------------------------------------------------
    // ViewModel ↔ UI synchronization
    // -----------------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Bulk re-fetches, capability probe completion, and preset loads all
        // funnel into these two property changes — refresh the whole panel.
        if (e.PropertyName == nameof(MainViewModel.DacHwMute)
            || e.PropertyName == nameof(MainViewModel.DacHwMuteSupported))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    private void Refresh()
    {
        if (_vm == null) return;
        var supported = _vm.DacHwMuteSupported;

        // The toggle is greyed out and the controls are hidden when the
        // firmware doesn't support the feature, but the Expander itself
        // stays visible and we force it open — that's how the notice in
        // the body becomes discoverable to the user. The section's outer
        // shape is consistent across supported / unsupported.
        EnableToggle.IsEnabled = supported;
        UnsupportedNotice.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
        CollapsibleContent.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;

        // Programmatic UI updates here must not trigger an apply round-trip
        // — otherwise the fetched value would be re-sent back to the device.
        _suppressApply = true;
        try
        {
            if (supported)
            {
                var cfg = _vm.DacHwMute;
                EnableToggle.IsOn = cfg.Enabled;
                PolarityToggle.IsOn = cfg.ActiveLow;
                SelectPinInCombo(cfg.Pin);
                HoldMsSlider.Value = cfg.HoldMs;
                ReleaseMsSlider.Value = cfg.ReleaseMs;
                UpdateHoldText(cfg.HoldMs);
                UpdateReleaseText(cfg.ReleaseMs);

                // Seed the initial expansion state to match the enabled flag
                // the first time we have real config — feels natural that an
                // already-enabled feature reveals its settings on first paint.
                // After this seed, the chevron is the user's domain; later
                // Refresh() calls (preset loads, bulk re-fetches) leave the
                // user's open/closed choice alone.
                if (!_hasSetInitialExpansion)
                {
                    SectionExpander.IsExpanded = cfg.Enabled;
                    _hasSetInitialExpansion = true;
                }
            }
            else
            {
                // Force expanded so the user sees the "not supported" notice
                // instead of just a greyed-out header row. We also reset the
                // seed flag — on a later reconnect to V10 firmware, Refresh
                // should re-seed expansion from the freshly-fetched enabled
                // value rather than carry over the forced-open state.
                SectionExpander.IsExpanded = true;
                _hasSetInitialExpansion = false;
            }
            UpdateTestButtonEnablement();
        }
        finally
        {
            _suppressApply = false;
        }
    }

    private void SelectPinInCombo(byte pin)
    {
        for (int i = 0; i < PinCombo.Items.Count; i++)
        {
            if (PinCombo.Items[i] is ComboBoxItem item && item.Tag is byte p && p == pin)
            {
                PinCombo.SelectedIndex = i;
                return;
            }
        }
        // Pin not in our list (shouldn't happen; firmware validates) — fall
        // back to "No Pin" so the UI doesn't show a confusing blank.
        PinCombo.SelectedIndex = 0;
    }

    private void RefreshPinComboConflicts()
    {
        var current = SelectedPin();
        for (int i = 1; i < PinCombo.Items.Count; i++) // skip "No Pin" at index 0
        {
            if (PinCombo.Items[i] is not ComboBoxItem item || item.Tag is not byte pin) continue;
            if (_pinOwners != null && _pinOwners.TryGetValue(pin, out var owner))
            {
                item.Content = $"GPIO {pin} ({owner})";
                // Disable conflicting items unless they're the current selection
                // — letting the user see and re-confirm their own pin is OK,
                // but they can't pick another feature's pin.
                item.IsEnabled = pin == current;
            }
            else
            {
                item.Content = $"GPIO {pin}";
                item.IsEnabled = true;
            }
        }
    }

    private byte SelectedPin() =>
        PinCombo.SelectedItem is ComboBoxItem item && item.Tag is byte b
            ? b : DacHwMuteConfig.PinNone;

    private void UpdateHoldText(double ms) =>
        HoldMsText.Text = ((int)ms).ToString(CultureInfo.InvariantCulture) + " ms";

    private void UpdateReleaseText(double ms) =>
        ReleaseMsText.Text = ((int)ms).ToString(CultureInfo.InvariantCulture) + " ms";

    private void UpdateTestButtonEnablement()
    {
        // The firmware rejects test pulses when the feature is disabled or
        // no pin is configured (returns non-zero status). Disabling the
        // button here saves a wasted USB round-trip and clarifies UX.
        TestButton.IsEnabled = EnableToggle.IsOn && SelectedPin() != DacHwMuteConfig.PinNone;
    }

    // -----------------------------------------------------------------------
    // User edits → ViewModel apply
    // -----------------------------------------------------------------------
    // Every field changes by producing a new DacHwMuteConfig via
    // <see cref="DacHwMuteConfig.With"/> and handing it to ApplyDacHwMuteAsync.
    // No five-arg method, no parameter-order bugs, no in-place mutation.

    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        // Toggle is now purely the feature's on/off switch — it does NOT
        // touch the Expander. The chevron remains where the user left it
        // (peek mode: toggle off but section open; or: toggle on but section
        // collapsed, e.g. to make room for other Global sections).
        UpdateTestButtonEnablement();
        if (_suppressApply || _vm == null) return;
        _ = _vm.ApplyDacHwMuteAsync(_vm.DacHwMute.With(enabled: EnableToggle.IsOn));
    }

    private void OnPolarityToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressApply || _vm == null) return;
        _ = _vm.ApplyDacHwMuteAsync(_vm.DacHwMute.With(activeLow: PolarityToggle.IsOn));
    }

    private void OnPinSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressApply || _vm == null) return;
        UpdateTestButtonEnablement();
        var newPin = SelectedPin();
        _ = _vm.ApplyDacHwMuteAsync(_vm.DacHwMute.With(pin: newPin));
        PinChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHoldChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateHoldText(e.NewValue);
        if (_suppressApply) return;
        ScheduleSliderApply(_vm?.DacHwMute.With(holdMs: (ushort)e.NewValue));
    }

    private void OnReleaseChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateReleaseText(e.NewValue);
        if (_suppressApply) return;
        ScheduleSliderApply(_vm?.DacHwMute.With(releaseMs: (ushort)e.NewValue));
    }

    /// <summary>
    /// Schedule a slider-driven apply, coalescing consecutive ticks within
    /// <see cref="SliderDebounce"/> into a single SET. Each new edit during
    /// the debounce window resets the timer and replaces the pending config,
    /// so only the value the user actually pauses on hits the wire.
    /// </summary>
    private void ScheduleSliderApply(DacHwMuteConfig? pending)
    {
        if (pending == null) return;
        _pendingSliderConfig = pending;
        if (_sliderDebounce == null)
        {
            _sliderDebounce = new DispatcherTimer { Interval = SliderDebounce };
            _sliderDebounce.Tick += OnSliderDebounceTick;
        }
        _sliderDebounce.Stop();
        _sliderDebounce.Start();
    }

    private async void OnSliderDebounceTick(object? sender, object e)
    {
        _sliderDebounce!.Stop();
        var cfg = _pendingSliderConfig;
        _pendingSliderConfig = null;
        if (cfg != null && _vm != null)
            await _vm.ApplyDacHwMuteAsync(cfg);
    }

    // -----------------------------------------------------------------------
    // Test pulse
    // -----------------------------------------------------------------------

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        TestButton.IsEnabled = false;
        TestFeedback.Text = "Pulsing mute for ~1 second…";
        try
        {
            var status = await _vm.TestDacHwMuteAsync();
            TestFeedback.Text = status switch
            {
                0 => "Test pulse fired. The DAC should mute audibly for ~1 s.",
                0xFF => "USB transfer failed — is the device still connected?",
                _ => $"Firmware rejected the test (status 0x{status:X2})."
            };
        }
        catch (Exception ex)
        {
            TestFeedback.Text = "Test failed: " + ex.Message;
        }
        finally
        {
            // 1.2s window: firmware test pulse is ~1s; this prevents double-
            // clicks landing while the firmware is still in the pulsing state.
            await Task.Delay(1200);
            UpdateTestButtonEnablement();
        }
    }
}
