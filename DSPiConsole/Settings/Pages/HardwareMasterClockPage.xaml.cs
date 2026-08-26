using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › Master Clock — what the DSPi generates when it owns the clock: the
/// sample rate, and the master clock (MCK) it supplies to external DACs, with its
/// pin and multiplier. Each link's own master/slave choice lives with that link
/// (Hardware › I2S, Hardware › ADAT); this page is what they have in common.
///
/// <para>
/// MCK's pin is the one GPIO assignment that isn't on a wiring page, because it
/// can only be changed while MCK is off and so has to sit beside the switch that
/// turns it off. Nothing here is firmware-gated except the rate card, so the page
/// always registers.
/// </para>
/// </summary>
public sealed partial class HardwareMasterClockPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public HardwareMasterClockPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        // Which GPIOs can drive MCK is platform-dependent, so the combo's items
        // can't be declared in XAML. Populated here and again on Platform change
        // (board swap, or Settings opened before the first connect); both paths
        // are outside a SelectionChanged dispatch, which is what makes mutating
        // the Items collection safe — see PopulateMckPinCombo.
        PopulateMckPinCombo(vm.Platform);

        base.Attach(vm, tracker);
        var fetchVm = vm;
        _ = Task.Run(() =>
            {
                if (fetchVm.InputI2sSupported) fetchVm.FetchI2sInputRate();
                // Not for a control on this page — I2sSlaveActive comes out of it,
                // and that greys out everything here.
                fetchVm.FetchI2sClockConfig();
                fetchVm.FetchMckEnable();
                fetchVm.FetchMckPin();
                fetchVm.FetchMckMultiplier();
            })
            .ContinueWith(_ => DispatcherQueue.TryEnqueue(Refresh));
    }

    /// <summary>Fill the MCK pin combo with the platform's GPOUT-capable GPIOs
    /// (RP2040 = 21 only; RP2350 = 13/15/21). Only ever called from Attach and
    /// from the Platform PropertyChanged path — never from inside a
    /// SelectionChanged handler — because WinUI's ComboBox throws "Element not
    /// found" (E_FAIL) when its Items are rebuilt on a tick that races the
    /// popup-dismissal of a user selection. RefreshMckConflicts then only
    /// relabels and toggles the items that are already there.</summary>
    private void PopulateMckPinCombo(string platform)
    {
        _suppress = true;
        try
        {
            MckPinCombo.Items.Clear();
            foreach (var pin in HardwarePins.McKCapablePins(platform))
                MckPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        }
        finally { _suppress = false; }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Another page claiming or releasing a GPIO changes which pins MCK can
        // still use, so the pin combo's labels have to follow.
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;

        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;
            Refresh();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnExternalPinChange() =>
        DispatcherQueue.TryEnqueue(RefreshMckConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A board swap changes the set of MCK-capable pins, which is a rebuild of
        // the combo's contents rather than a relabel — so it can't go through the
        // plain Refresh path below.
        if (e.PropertyName == nameof(MainViewModel.Platform))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Vm != null) PopulateMckPinCombo(Vm.Platform);
                Refresh();
            });
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.MckEnabled):
            case nameof(MainViewModel.MckPin):
            case nameof(MainViewModel.MckMultiplier):
            // Not the same as I2sInputRateHz below: this is the rate the device
            // reports it is actually running at, and it decides whether 256× is
            // reachable.
            case nameof(MainViewModel.SampleRateHz):
            case nameof(MainViewModel.I2sInputRateHz):
            case nameof(MainViewModel.InputI2sSupported):
            // Set on the I2S page, but it greys out everything here.
            case nameof(MainViewModel.I2sSlaveActive):
                DispatcherQueue.TryEnqueue(Refresh);
                break;
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        RateCard.Visibility = Vm.InputI2sSupported ? Visibility.Visible : Visibility.Collapsed;

        _suppress = true;
        try
        {
            SelectRate(Vm.I2sInputRateHz);
            RefreshMck();
        }
        finally { _suppress = false; }

        RefreshMckConflicts();

        // Where MCK is set, for a click on it in the Overview's map. Only while
        // it is on: a disabled MCK holds no GPIO, so the map shows none.
        ClearPinTargets();
        if (Vm.MckEnabled) RegisterPinTarget(Vm.MckPin, MckPinCard);
    }

    /// <summary>The rate and MCK group's live state. In I2S slave mode an external
    /// master owns the clock, so none of it applies — grey the lot out rather than
    /// leave live-looking controls with no effect. On top of that the MCK pin is
    /// settable only while MCK is off (the firmware refuses to re-route a clock it
    /// is actively driving), and 256× is out of reach at 96 kHz and above. Runs
    /// under the <c>_suppress</c> guard (called from Refresh).</summary>
    private void RefreshMck()
    {
        if (Vm == null) return;
        bool slave = Vm.I2sSlaveActive;

        RateCombo.IsEnabled = !slave;

        MckToggle.IsOn = Vm.MckEnabled;
        MckToggle.IsEnabled = !slave;
        MckPinCombo.IsEnabled = !Vm.MckEnabled && !slave;
        MckPinCard.Description = Vm.MckEnabled
            ? "Turn MCK off to change its pin."
            : "Pin on which MCK is generated.";

        MckMultiplierCombo.SelectedIndex = Vm.MckMultiplier == 256 ? 1 : 0;
        bool highRate = Vm.SampleRateHz >= 96000;
        MckMultiplierCombo.IsEnabled = !highRate && !slave;
        MckMultiplierCard.Description = highRate
            ? $"Locked to 128× at {Vm.SampleRateHz / 1000.0:F1} kHz."
            : "Use 256× for DACs that require higher MCK rates.";
    }

    /// <summary>Relabel the MCK pin picker so pins claimed by other features read
    /// as "GPIO 13 (OUT 1/2)" and can't be picked, while free ones stay a plain
    /// "GPIO 13". The Items collection is never touched here — that's a hard
    /// requirement, see PopulateMckPinCombo.</summary>
    private void RefreshMckConflicts()
    {
        if (Vm == null) return;

        _suppress = true;
        try
        {
            var owners = HardwarePins.BuildOwnerMap(Vm, excludeMckSelf: true);
            byte current = Vm.MckPin;

            for (int i = 0; i < MckPinCombo.Items.Count; i++)
            {
                if (MckPinCombo.Items[i] is not ComboBoxItem item) continue;
                if (item.Tag is not byte pin) continue;

                // The current pin always stays selectable so the user can
                // re-confirm it.
                string? ownerLabel = null;
                if (pin != current && owners.TryGetValue(pin, out var owner))
                    ownerLabel = owner;

                item.Content = ownerLabel != null ? $"GPIO {pin} ({ownerLabel})" : $"GPIO {pin}";
                item.IsEnabled = ownerLabel == null;
            }
            SelectPinInCombo(MckPinCombo, current);
        }
        finally { _suppress = false; }
    }

    // ── Handlers ───────────────────────────────────────────────────────────

    private async void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (RateCombo.SelectedItem is not ComboBoxItem item) return;
        if (!uint.TryParse(item.Tag?.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var hz)) return;
        if (hz == Vm.I2sInputRateHz) return;

        ClearStatus();
        var ok = await Task.Run(() => Vm.SetI2sInputRate(hz));
        if (ok)
        {
            ShowStatus($"Sample rate set to {hz / 1000.0:0.#} kHz", false);
            return;
        }
        _suppress = true;
        SelectRate(Vm.I2sInputRateHz);
        _suppress = false;
        ShowStatus("Failed to set sample rate", true);
    }

    private async void OnMckToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        ClearStatus();

        bool newVal = MckToggle.IsOn;
        var status = await Task.Run(() => Vm.SetMckEnable(newVal));
        if (status == PinConfigResult.Success)
        {
            // Turning MCK on locks its pin picker and claims the GPIO; both have
            // to be repainted here and on the pin pages.
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
            // The queued PropertyChanged(MckPin) and the PinAssignmentsChanged
            // broadcast relabel the combos on the next tick. Neither mutates an
            // Items collection, so dispatching from inside a SelectionChanged is
            // safe here.
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"MCK pin set to GPIO {newPin}", false);
            return;
        }

        // Revert by matching the device's actual pin by Tag — the combo is
        // platform-restricted, so item indices don't line up with ValidPins.
        _suppress = true;
        SelectPinInCombo(MckPinCombo, Vm.MckPin);
        _suppress = false;

        if (status == PinConfigResult.PinInUse)
        {
            ShowPinConflict(StatusText, StatusPinButton, HardwarePins.BuildAssignmentMap(Vm, excludeMckSelf: true),
                $"GPIO {newPin} is already in use", newPin);
            return;
        }
        ShowStatus(status switch
        {
            PinConfigResult.OutputActive => "Disable MCK before changing its pin",
            // Filter-on-populate already keeps non-GPOUT pins out of the combo, so
            // this branch is defensive — a stale combo or a firmware mismatch.
            PinConfigResult.InvalidPin   => $"GPIO {newPin} can't drive MCK on this platform. "
                                            + $"Use {string.Join(" / ", Array.ConvertAll(HardwarePins.McKCapablePins(Vm.Platform), p => $"GPIO {p}"))}.",
            _ => $"Failed to set MCK pin (0x{status:X2})"
        }, true);
    }

    private async void OnMckMultiplierChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        // ComboBoxItem.Tag set in XAML (Tag="128") is parsed as STRING, not int —
        // the pattern `is not int` always fails, which is why an earlier version
        // silently dropped multiplier changes. Parse the string explicitly. (The
        // pin combo populates from C# and stores byte tags, so it doesn't hit this
        // XAML quirk.)
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

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Select the combo entry whose byte Tag matches <paramref name="pin"/>.
    /// No-op if no match — leaves the previous selection rather than blanking the
    /// combo.</summary>
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

    private void SelectRate(uint hz)
    {
        for (int i = 0; i < RateCombo.Items.Count; i++)
        {
            if (RateCombo.Items[i] is ComboBoxItem item
                && uint.TryParse(item.Tag?.ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var v)
                && v == hz)
            {
                RateCombo.SelectedIndex = i;
                return;
            }
        }
    }

    private void ShowStatus(string msg, bool isError)
    {
        // Any message that isn't a pin conflict takes the eye away, so one is
        // never left pointing at the destination of the message before it.
        PinConflict.Disarm(StatusPinButton);
        StatusText.Text = msg;
        StatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 240, 100, 100)
            : Color.FromArgb(255, 100, 200, 140));
        StatusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus()
    {
        PinConflict.Disarm(StatusPinButton);
        StatusText.Visibility = Visibility.Collapsed;
    }

    // ── ISettingsPage ──────────────────────────────────────────────────────
    public string Id => "hardware.master-clock";
    public string Title => "Master Clock";
    public SettingsCategory Category => SettingsCategory.System;
    public string IconGlyph => "";
    public int Order => 15;
    // MCK is unconditional, so there is always something to show even when the
    // firmware can't report the sample rate.
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareMasterClockPage();
        p.Attach(vm, tracker);
        return p;
    }
}
