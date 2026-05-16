using System.ComponentModel;
using System.Threading.Tasks;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › I²S Configuration — MCK enable + pin + multiplier, BCK
/// pin. Mirrors the legacy dialog's I²S section, with the same firmware
/// constraints encoded in real-time control enablement.
///
/// <para>
/// Subscribes to <see cref="HardwarePins.PinAssignmentsChanged"/> for
/// cross-page conflict refreshes and to <see cref="MainViewModel"/>
/// PropertyChanged for sample-rate-driven multiplier locking.
/// </para>
/// </summary>
public sealed partial class HardwareI2SPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public HardwareI2SPage()
    {
        InitializeComponent();

        // BCK and MCK combo items are added by RefreshConflicts —
        // both filter on populate (only show pins this picker can
        // actually use; pins claimed elsewhere are omitted, not
        // greyed out). Initial empty state is fine because Refresh()
        // calls RefreshConflicts before the user sees anything.

        // Subscriptions go in Loaded/Unloaded so they survive sidebar
        // navigation cycles — see HardwareOutputAssignmentPage for the
        // rationale.
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);

        // Kick off background fetches to populate the page with the
        // device's current values. The PropertyChanged handler is what
        // pushes them into the UI once they arrive — combos are
        // rebuilt by RefreshConflicts on every Refresh, so the MCK
        // pin combo doesn't need a separate platform-aware populate.
        var fetchVm = vm;
        _ = Task.Run(() =>
        {
            fetchVm.FetchI2SBckPin();
            fetchVm.FetchMckEnable();
            fetchVm.FetchMckPin();
            fetchVm.FetchMckMultiplier();
        });
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;
        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;
            // Re-sync from VM state in case events were missed while
            // we were unloaded (e.g., a preset switch happened while
            // the user was viewing a different Settings page).
            Refresh();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnExternalPinChange() =>
        DispatcherQueue.TryEnqueue(RefreshConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Platform changes (first connect after Settings opens, or a
        // board swap from RP2040 to RP2350 / vice versa) change the
        // set of MCK-capable pins. Refresh rebuilds the combo from
        // McKCapablePins(Vm.Platform) so this just falls through.
        if (e.PropertyName == nameof(MainViewModel.Platform))
        {
            DispatcherQueue.TryEnqueue(Refresh);
            return;
        }

        // Refresh on any I²S-relevant VM property change:
        //   • SampleRateHz / AnySlotIsI2S — enable-state of the
        //     multiplier and BCK pin combos depend on them.
        //   • MckEnabled — gates the MCK pin combo.
        //   • MckMultiplier / MckPin / I2SBckPin — direct combo state,
        //     fired from setters AND from the bulk-params load path
        //     so preset reloads / reconnects also repaint.
        if (e.PropertyName == nameof(MainViewModel.SampleRateHz)
            || e.PropertyName == nameof(MainViewModel.AnySlotIsI2S)
            || e.PropertyName == nameof(MainViewModel.MckEnabled)
            || e.PropertyName == nameof(MainViewModel.MckMultiplier)
            || e.PropertyName == nameof(MainViewModel.MckPin)
            || e.PropertyName == nameof(MainViewModel.I2SBckPin))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        _suppress = true;
        try
        {
            // BCK + MCK pin pickers are rebuilt by RefreshConflicts at
            // the end of this method — both filter on populate (only
            // usable pins shown). The Card descriptions and combo
            // enablement still belong here.
            BckPinCard.Description = $"LRCK auto-assigned to GPIO {Vm.I2SBckPin + 1} (BCK + 1).";
            BckPinCombo.IsEnabled = !Vm.AnySlotIsI2S;

            MckToggle.IsOn = Vm.MckEnabled;
            MckPinCombo.IsEnabled = !Vm.MckEnabled;
            MckPinCard.Description = Vm.MckEnabled
                ? "Turn MCK off to change its pin."
                : "Pin on which MCK is generated.";

            // MCK multiplier
            MckMultiplierCombo.SelectedIndex = Vm.MckMultiplier == 256 ? 1 : 0;
            var highRate = Vm.SampleRateHz >= 96000;
            MckMultiplierCombo.IsEnabled = !highRate;
            MckMultiplierCard.Description = highRate
                ? $"Locked to 128× at {Vm.SampleRateHz / 1000.0:F1} kHz."
                : "Use 256× for DACs that require higher MCK rates.";
        }
        finally { _suppress = false; }

        RefreshConflicts();
    }

    /// <summary>Rebuild both pin pickers so each one shows only the
    /// pins it can actually use. Replaces the old grey-out approach:
    /// blocked pins are omitted from the dropdown entirely, with the
    /// current device pin always included so the user can see and
    /// re-confirm their selection.</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;

        _suppress = true;
        try
        {
            // ── BCK ──────────────────────────────────────────────────
            // BCK reserves pin AND pin+1 (LRCK). Include a pin only if
            //   • it's the current BCK (always selectable so the user
            //     can re-confirm), OR
            //   • the pin isn't claimed by a non-clock feature, AND
            //     pin+1 is audio-capable, AND
            //     pin+1 isn't claimed by a non-clock feature.
            // The current BCK/LRCK pair is exempted as an "owner"
            // because both pins move atomically with the reassignment.
            var owners = HardwarePins.BuildOwnerMap(Vm);
            byte currentBck = Vm.I2SBckPin;

            BckPinCombo.Items.Clear();
            foreach (var pin in HardwarePins.ValidPins)
            {
                if (pin == currentBck)
                {
                    BckPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
                    continue;
                }
                byte lrck = (byte)(pin + 1);

                if (owners.TryGetValue(pin, out var owner)
                    && owner != "BCK" && owner != "LRCK")
                    continue;
                if (!HardwarePins.IsAudioCapable(lrck))
                    continue;
                if (owners.TryGetValue(lrck, out var nextOwner)
                    && nextOwner != "BCK" && nextOwner != "LRCK")
                    continue;

                BckPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
            }
            SelectPinInCombo(BckPinCombo, currentBck);

            // ── MCK ──────────────────────────────────────────────────
            // MCK is restricted to GPOUT-capable pins (platform-
            // dependent — RP2040: 21 only; RP2350: 13/15/21). Include
            // the current MCK plus any unclaimed GPOUT pin.
            var mckOwners = HardwarePins.BuildOwnerMap(Vm, excludeMckSelf: true);
            byte currentMck = Vm.MckPin;

            MckPinCombo.Items.Clear();
            foreach (var pin in HardwarePins.McKCapablePins(Vm.Platform))
            {
                if (pin == currentMck || !mckOwners.ContainsKey(pin))
                    MckPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
            }
            SelectPinInCombo(MckPinCombo, currentMck);
        }
        finally { _suppress = false; }
    }

    // ── Live-apply handlers ──────────────────────────────────────────
    // Per-preset parameters — each control change writes through
    // immediately. Status text + revert-on-error mirror the legacy
    // dialog's pattern.

    private async void OnBckPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (BckPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetI2SBckPin(newPin));
        if (status == PinConfigResult.Success)
        {
            BckPinCard.Description = $"LRCK auto-assigned to GPIO {newPin + 1} (BCK + 1).";
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshConflicts();
            ShowStatus($"BCK pin set to GPIO {newPin}, LRCK = GPIO {newPin + 1}", false);
            return;
        }

        _suppress = true;
        SelectPinInCombo(BckPinCombo, Vm.I2SBckPin);
        _suppress = false;

        var msg = status switch
        {
            PinConfigResult.OutputActive => "All outputs must be S/PDIF before changing BCK pin",
            PinConfigResult.PinInUse     => $"GPIO {newPin} or {newPin + 1} is already in use",
            _ => $"Failed to set BCK pin (0x{status:X2})"
        };
        ShowStatus(msg, true);
    }

    private async void OnMckToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        ClearStatus();

        bool newVal = MckToggle.IsOn;
        var status = await Task.Run(() => Vm.SetMckEnable(newVal));
        if (status == PinConfigResult.Success)
        {
            // Refresh reads the new MckEnabled and rebuilds enablement.
            Refresh();
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"Master clock {(newVal ? "enabled" : "disabled")}", false);
            return;
        }

        _suppress = true;
        MckToggle.IsOn = Vm.MckEnabled;
        _suppress = false;
        ShowStatus($"Failed to {(newVal ? "enable" : "disable")} master clock", true);
    }

    private async void OnMckPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (MckPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetMckPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshConflicts();
            ShowStatus($"MCK pin set to GPIO {newPin}", false);
            return;
        }

        // Revert by matching the device's actual MCK pin in the combo
        // by Tag — MckPinCombo is platform-restricted and rebuilt by
        // RefreshConflicts, so item indices don't match ValidPins.
        _suppress = true;
        SelectPinInCombo(MckPinCombo, Vm.MckPin);
        _suppress = false;

        var msg = status switch
        {
            PinConfigResult.OutputActive => "Disable MCK before changing its pin",
            PinConfigResult.PinInUse     => $"GPIO {newPin} is already in use",
            // Filter-on-populate already keeps non-GPOUT pins out of
            // the combo, so this branch is defensive — kept for the
            // unlikely case of a stale combo or firmware mismatch.
            PinConfigResult.InvalidPin   => $"GPIO {newPin} can't drive MCK on this platform. "
                                            + $"Use {string.Join(" / ", System.Array.ConvertAll(HardwarePins.McKCapablePins(Vm.Platform), p => $"GPIO {p}"))}.",
            _ => $"Failed to set MCK pin (0x{status:X2})"
        };
        ShowStatus(msg, true);
    }

    /// <summary>Select the combo entry whose byte Tag matches
    /// <paramref name="pin"/>. No-op if no match — leaves the
    /// previous selection in place rather than blanking the combo.</summary>
    private static void SelectPinInCombo(ComboBox combo, byte pin)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag is byte p && p == pin)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private async void OnMckMultiplierChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        // ComboBoxItem.Tag set in XAML (Tag="128") is parsed as STRING,
        // not int — the pattern `is not int` always fails, which is why
        // the original silently dropped multiplier changes. Parse the
        // string explicitly. (BCK / MCK-pin combos populate from C# and
        // store byte tags, so they don't hit this XAML quirk.)
        if (MckMultiplierCombo.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out var newMul)) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetMckMultiplier(newMul));
        if (status == PinConfigResult.Success)
        {
            ShowStatus($"MCK multiplier set to {newMul}×", false);
            return;
        }

        _suppress = true;
        MckMultiplierCombo.SelectedIndex = Vm.MckMultiplier == 256 ? 1 : 0;
        _suppress = false;
        ShowStatus("Failed to set MCK multiplier", true);
    }

    private void ShowStatus(string msg, bool isError)
    {
        StatusText.Text = msg;
        StatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 240, 100, 100)
            : Color.FromArgb(255, 100, 200, 140));
        StatusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus() => StatusText.Visibility = Visibility.Collapsed;

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "hardware.i2s";
    public string Title => "I²S Configuration";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => ""; // SoundLevels (waveform)
    public int Order => 20;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareI2SPage();
        p.Attach(vm, tracker);
        return p;
    }
}
