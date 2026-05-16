using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › External DAC Mute — flat SettingsCard list, no
/// Expander. All five persistent fields (enable, polarity, pin, hold,
/// release) ride a single firmware command, so every edit stages
/// under one tracker key with the full updated config; the InfoBar
/// shows a single pending entry regardless of how many fields the
/// user touched.
///
/// <para>
/// The Test Pulse button stays live — it's a one-shot device action
/// with no persistent state, so there's nothing to stage. Slider
/// edits coalesce via a short DispatcherTimer so a steady drag
/// produces one Stage call per pause rather than one per pixel.
/// </para>
/// </summary>
public sealed partial class HardwareDacMutePage : SettingsModule, ISettingsPage
{
    private bool _suppress;
    private DispatcherTimer? _sliderDebounce;
    private const int SliderDebounceMs = 200;

    private const string ConfigKey = "hardware.dac-mute.config";

    public HardwareDacMutePage()
    {
        InitializeComponent();
        PopulatePinCombo();

        Unloaded += (_, _) =>
        {
            HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
            if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
            _sliderDebounce?.Stop();
        };
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;

        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
        vm.PropertyChanged += OnVmPropertyChanged;

        base.Attach(vm, tracker);
    }

    private void OnExternalPinChange() =>
        DispatcherQueue.TryEnqueue(RefreshPinConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // VM signals a fresh device read (e.g. after a preset load or
        // reconnect) via these properties. Pull the UI back into sync.
        if (e.PropertyName == nameof(MainViewModel.DacHwMute)
            || e.PropertyName == nameof(MainViewModel.DacHwMuteSupported))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    private void PopulatePinCombo()
    {
        // "No Pin" sentinel keeps the feature disabled even when
        // Enabled=true (matches the firmware semantics).
        PinCombo.Items.Add(new ComboBoxItem
        {
            Content = "No pin (disabled)",
            Tag = DacHwMuteConfig.PinNone
        });
        foreach (var pin in HardwarePins.ValidPins)
            PinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        var cfg = Vm.DacHwMute;
        _suppress = true;
        try
        {
            EnableToggle.IsOn = cfg.Enabled;
            // Index 0 = Active Low, 1 = Active High.
            PolarityCombo.SelectedIndex = cfg.ActiveLow ? 0 : 1;
            SelectPinInCombo(cfg.Pin);
            HoldSlider.Value = cfg.HoldMs;
            ReleaseSlider.Value = cfg.ReleaseMs;
            UpdateHoldDescription(cfg.HoldMs);
            UpdateReleaseDescription(cfg.ReleaseMs);
        }
        finally { _suppress = false; }

        UpdateTestButtonEnablement();
        RefreshPinConflicts();
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
        // Pin not in our list — fall back to "No pin" so the UI doesn't
        // show a confusing blank selection.
        PinCombo.SelectedIndex = 0;
    }

    private byte SelectedPin() =>
        PinCombo.SelectedItem is ComboBoxItem item && item.Tag is byte b
            ? b : DacHwMuteConfig.PinNone;

    private void RefreshPinConflicts()
    {
        if (Vm == null) return;

        // Build the cross-page pin-owner map excluding our own current
        // pin so the user can re-confirm their existing selection.
        var owners = HardwarePins.BuildOwnerMap(Vm, excludeDacMuteSelf: true);
        var current = SelectedPin();

        _suppress = true;
        for (int i = 1; i < PinCombo.Items.Count; i++) // skip "No pin" at index 0
        {
            if (PinCombo.Items[i] is not ComboBoxItem item || item.Tag is not byte pin) continue;
            if (owners.TryGetValue(pin, out var owner))
            {
                item.Content = $"GPIO {pin} ({owner})";
                // Leave the user's own current pin pickable.
                item.IsEnabled = pin == current;
            }
            else
            {
                item.Content = $"GPIO {pin}";
                item.IsEnabled = true;
            }
        }
        _suppress = false;
    }

    private void UpdateHoldDescription(double ms) =>
        HoldCard.Description = $"How long the mute pin is held asserted before clocks stop. Current: {(int)ms} ms";

    private void UpdateReleaseDescription(double ms) =>
        ReleaseCard.Description = $"How long to dwell after un-muting before resuming audio. Current: {(int)ms} ms";

    private void UpdateTestButtonEnablement()
    {
        // The firmware rejects test pulses when the feature is disabled
        // or no pin is configured. Disabling the button locally saves
        // a wasted USB round-trip and clarifies UX.
        TestButton.IsEnabled = EnableToggle.IsOn && SelectedPin() != DacHwMuteConfig.PinNone;
    }

    // ── Field handlers ────────────────────────────────────────────────

    private void OnConfigChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        UpdateTestButtonEnablement();

        // Pin selection changing should also notify the cross-page
        // bus so other Hardware pages refresh their conflict labels
        // — but only AFTER the change actually applies. Phase 2's
        // tracker pattern: stage now; the Apply lambda raises the
        // event on success.
        StageConfigSnapshot();
    }

    private void OnConfigChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null || Tracker == null) return;
        UpdateTestButtonEnablement();
        StageConfigSnapshot();
    }

    private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;

        // Update the live description right away so the user sees the
        // numeric value tracking the thumb, but coalesce the actual
        // Stage call via a short timer. Steady drag → one staged
        // change at the user's pause, instead of one per ValueChanged.
        // ReferenceEquals avoids the CS0252 "possible unintended
        // reference comparison" warning we'd get from `sender ==` —
        // sender is object, the right side is a Slider field.
        if (ReferenceEquals(sender, HoldSlider))
            UpdateHoldDescription(e.NewValue);
        else if (ReferenceEquals(sender, ReleaseSlider))
            UpdateReleaseDescription(e.NewValue);

        if (Vm == null || Tracker == null) return;
        ScheduleSliderStage();
    }

    private void ScheduleSliderStage()
    {
        if (_sliderDebounce == null)
        {
            _sliderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SliderDebounceMs) };
            _sliderDebounce.Tick += (_, _) =>
            {
                _sliderDebounce!.Stop();
                StageConfigSnapshot();
            };
        }
        _sliderDebounce.Stop();
        _sliderDebounce.Start();
    }

    /// <summary>
    /// Capture the current UI state into a <see cref="DacHwMuteConfig"/>
    /// and stage it. All five fields share one key so the InfoBar
    /// shows a single entry, with the old/new display strings
    /// derived from the most-changed field (or a generic label when
    /// multiple fields differ).
    /// </summary>
    private void StageConfigSnapshot()
    {
        if (Vm == null || Tracker == null) return;

        var current = Vm.DacHwMute;
        var pending = current.With(
            enabled: EnableToggle.IsOn,
            activeLow: PolarityCombo.SelectedIndex != 1,
            pin: SelectedPin(),
            holdMs: (ushort)HoldSlider.Value,
            releaseMs: (ushort)ReleaseSlider.Value);

        var vm = Vm;
        Tracker.Stage(new PendingChange(
            Key: ConfigKey,
            PageId: Id,
            FieldLabel: "DAC mute config",
            OldDisplay: DescribeConfig(current),
            NewDisplay: DescribeConfig(pending),
            Apply: async () =>
            {
                var result = await vm.ApplyDacHwMuteAsync(pending);
                if (result == null) return (byte)0xFF;
                // Pin change → tell other Hardware pages to refresh
                // their conflict labels.
                if (result.Pin != current.Pin)
                    HardwarePins.RaisePinAssignmentsChanged();
                return (byte)0;
            }));
    }

    private static string DescribeConfig(DacHwMuteConfig c)
    {
        // Always include every field. The tracker dedupes staged changes
        // by OldDisplay == NewDisplay; collapsing the disabled state to
        // a bare "Off" string caused edits made while disabled (pin,
        // polarity, hold, release) to compare equal to the saved state
        // and silently drop from the pending list.
        var pinLabel = c.Pin == DacHwMuteConfig.PinNone ? "no pin" : $"GPIO {c.Pin}";
        var polarity = c.ActiveLow ? "active-low" : "active-high";
        var enabled = c.Enabled ? "on" : "off";
        return string.Create(CultureInfo.InvariantCulture,
            $"{enabled}, {pinLabel}, {polarity}, {c.HoldMs}/{c.ReleaseMs} ms");
    }

    // ── Test pulse (live action, not staged) ──────────────────────────

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        TestButton.IsEnabled = false;
        TestFeedback.Text = "Pulsing mute for ~1 second…";
        TestFeedback.Visibility = Visibility.Visible;
        try
        {
            var status = await Vm.TestDacHwMuteAsync();
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
            // 1.2 s window: firmware test pulse is ~1 s; this prevents
            // double-clicks landing while the firmware is still pulsing.
            await Task.Delay(1200);
            UpdateTestButtonEnablement();
        }
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "hardware.dac-mute";
    public string Title => "External DAC Mute";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => ""; // Mute
    public int Order => 40;
    // V10+ feature — hide the sidebar entry entirely on older firmware.
    public bool IsAvailable(MainViewModel vm) => vm.DacHwMuteSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareDacMutePage();
        p.Attach(vm, tracker);
        return p;
    }
}
