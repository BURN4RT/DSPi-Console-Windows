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

        // BCK can use any audio-capable GPIO; populate it from the
        // full list at construction time.  MCK's combo is populated
        // in Attach() once we know the platform — see McKCapablePins
        // in HardwarePins.cs for why only a subset is valid.
        foreach (var pin in HardwarePins.ValidPins)
            BckPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        // Detach on Unloaded so static + VM events don't keep us alive.
        Unloaded += (_, _) =>
        {
            HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
            if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        // Populate (or repopulate, on reconnect to a different
        // platform) the MCK pin combo with ONLY GPOUT-capable pins.
        // Picking a non-GPOUT pin is the firmware's only meaningful
        // PIN_CONFIG_INVALID_PIN trigger for MCK and produces a
        // confusing "Failed to set MCK pin (0x01)" error if exposed
        // in the UI.
        MckPinCombo.Items.Clear();
        foreach (var pin in HardwarePins.McKCapablePins(vm.Platform))
            MckPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;

        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
        vm.PropertyChanged += OnVmPropertyChanged;

        base.Attach(vm, tracker);

        // Kick off background fetches to populate the page with the
        // device's current values. The PropertyChanged handler is what
        // pushes them into the UI once they arrive.
        var fetchVm = vm;
        _ = Task.Run(() =>
        {
            fetchVm.FetchI2SBckPin();
            fetchVm.FetchMckEnable();
            fetchVm.FetchMckPin();
            fetchVm.FetchMckMultiplier();
        });
    }

    private void OnExternalPinChange() =>
        DispatcherQueue.TryEnqueue(RefreshConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            // BCK pin
            var bckIdx = System.Array.IndexOf(HardwarePins.ValidPins, Vm.I2SBckPin);
            if (bckIdx >= 0) BckPinCombo.SelectedIndex = bckIdx;
            BckPinCard.Description = $"LRCK auto-assigned to GPIO {Vm.I2SBckPin + 1} (BCK + 1).";
            BckPinCombo.IsEnabled = !Vm.AnySlotIsI2S;

            // MCK toggle
            MckToggle.IsOn = Vm.MckEnabled;

            // MCK pin — combo is platform-restricted; locate by Tag,
            // not by ValidPins index.
            SelectMckPinInCombo(Vm.MckPin);
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

    private void RefreshConflicts()
    {
        if (Vm == null) return;

        // BCK: reserves pin and pin+1 (LRCK). Disable any candidate
        // whose pin collides with another non-LRCK owner, or whose
        // pin+1 collides with anything (including someone else's LRCK).
        _suppress = true;
        try
        {
            // Get owners excluding LRCK so we can tell BCK candidates from
            // their would-be LRCK collisions properly.
            var owners = HardwarePins.BuildOwnerMap(Vm);

            for (int i = 0; i < HardwarePins.ValidPins.Length; i++)
            {
                if (BckPinCombo.Items[i] is not ComboBoxItem item) continue;
                byte pin = HardwarePins.ValidPins[i];

                bool conflict = false;
                string? ownerLabel = null;
                if (owners.TryGetValue(pin, out var owner) && owner != "LRCK" && owner != "BCK")
                {
                    conflict = true;
                    ownerLabel = owner;
                }
                else if (owners.TryGetValue((byte)(pin + 1), out var nextOwner)
                         && nextOwner != "LRCK")
                {
                    conflict = true;
                    ownerLabel = $"{nextOwner}+LRCK";
                }
                item.Content = ownerLabel != null ? $"GPIO {pin} ({ownerLabel})" : $"GPIO {pin}";
                item.IsEnabled = !conflict;
            }

            // MCK: exclude MCK's own entry so the user can keep its
            // current selection visible / re-selectable. The MCK combo
            // is platform-restricted (only GPOUT-capable pins), so
            // iterate the combo's own items rather than ValidPins.
            var mckOwners = HardwarePins.BuildOwnerMap(Vm, excludeMckSelf: true);
            foreach (var raw in MckPinCombo.Items)
            {
                if (raw is not ComboBoxItem item) continue;
                if (item.Tag is not byte pin) continue;
                if (mckOwners.TryGetValue(pin, out var owner))
                {
                    item.Content = $"GPIO {pin} ({owner})";
                    item.IsEnabled = false;
                }
                else
                {
                    item.Content = $"GPIO {pin}";
                    item.IsEnabled = true;
                }
            }
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
        var idx = System.Array.IndexOf(HardwarePins.ValidPins, Vm.I2SBckPin);
        if (idx >= 0) BckPinCombo.SelectedIndex = idx;
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

        // Revert: find the device's current MCK pin in the combo's
        // own items (not ValidPins) — MckPinCombo is restricted to
        // GPOUT-capable pins, so the indices don't align with the
        // shared ValidPins list.
        _suppress = true;
        SelectMckPinInCombo(Vm.MckPin);
        _suppress = false;

        var msg = status switch
        {
            PinConfigResult.OutputActive => "Disable MCK before changing its pin",
            PinConfigResult.PinInUse     => $"GPIO {newPin} is already in use",
            PinConfigResult.InvalidPin   => $"GPIO {newPin} can't drive MCK on this platform. "
                                            + $"Use {string.Join(" / ", System.Array.ConvertAll(HardwarePins.McKCapablePins(Vm.Platform), p => $"GPIO {p}"))}.",
            _ => $"Failed to set MCK pin (0x{status:X2})"
        };
        ShowStatus(msg, true);
    }

    /// <summary>Select an MCK pin in the platform-restricted combo by
    /// matching its byte Tag. No-ops if the pin isn't in the current
    /// combo (e.g. a stale value from a different platform's slot).</summary>
    private void SelectMckPinInCombo(byte pin)
    {
        for (int i = 0; i < MckPinCombo.Items.Count; i++)
        {
            if (MckPinCombo.Items[i] is ComboBoxItem item
                && item.Tag is byte p && p == pin)
            {
                MckPinCombo.SelectedIndex = i;
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
