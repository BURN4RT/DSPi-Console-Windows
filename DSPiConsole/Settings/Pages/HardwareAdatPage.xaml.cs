using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › ADAT. Both directions of the RP2350-only optical link: the 8-channel
/// transmitter (enable + TX pin) and the 8-channel receiver (enable + RX pin). The
/// receiver's clock source and lock state are on Hardware › Clocking. Registered
/// when the firmware reports either half (see <see cref="IsAvailable"/>);
/// whichever half is missing hides.
/// </summary>
public sealed partial class HardwareAdatPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public HardwareAdatPage()
    {
        InitializeComponent();

        // Transmit: the ADAT default (GPIO 12) is deliberately absent from
        // ValidPins (it's not a general audio-routing pin), so add it explicitly.
        // No "(Not set)" — the output always holds a pin, valid or not.
        var txPins = new List<byte>(HardwarePins.ValidPins);
        if (!txPins.Contains(MainViewModel.AdatDefaultPin)) txPins.Add(MainViewModel.AdatDefaultPin);
        txPins.Sort();
        foreach (var pin in txPins)
            OutPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        // Receive: "(Not set)" sentinel first — an unset RX pin is a real state,
        // and the input can't be enabled until it's chosen.
        InPinCombo.Items.Add(new ComboBoxItem { Content = "(Not set)", Tag = MainViewModel.AdatInputPinUnset });
        foreach (var pin in HardwarePins.ValidPins)
            InPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);
        var fetchVm = vm;
        // Each fetch is skipped when its half isn't supported — the reads STALL on
        // firmware that lacks it, and the baselined support flags already know.
        _ = Task.Run(() =>
            {
                if (fetchVm.AdatSupported) fetchVm.FetchAdatConfig();
                if (fetchVm.AdatInputSupported) fetchVm.FetchAdatInputConfig();
            })
            .ContinueWith(_ => DispatcherQueue.TryEnqueue(Refresh));
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
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

    private void OnExternalPinChange() => DispatcherQueue.TryEnqueue(RefreshConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.AdatEnabled):
            case nameof(MainViewModel.AdatPin):
            case nameof(MainViewModel.AdatSupported):
            case nameof(MainViewModel.AdatInputEnabled):
            case nameof(MainViewModel.AdatInputPin):
            case nameof(MainViewModel.AdatInputSupported):
                DispatcherQueue.TryEnqueue(Refresh);
                break;
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            OutEnableToggle.IsOn = Vm.AdatEnabled;
            InEnableToggle.IsOn = Vm.AdatInputEnabled;
        }
        finally { _suppress = false; }
        RefreshSections();
        RefreshConflicts();
    }

    /// <summary>Show only the halves this firmware has. The "Output"/"Input"
    /// headings and the rule between them exist to tell the two apart, so they
    /// only appear when there are in fact two.</summary>
    private void RefreshSections()
    {
        if (Vm == null) return;
        bool tx = Vm.AdatSupported, rx = Vm.AdatInputSupported;
        bool both = tx && rx;

        OutEnableCard.Visibility = Vis(tx);
        OutPinCard.Visibility = Vis(tx);
        OutputHeading.Visibility = Vis(both);

        SectionDivider.Visibility = Vis(both);
        InputHeading.Visibility = Vis(both);
        InEnableCard.Visibility = Vis(rx);
        InPinCard.Visibility = Vis(rx);
    }

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Grey-out pins owned by other features and reselect the current pin
    /// in each combo. Items are never rebuilt here (that races WinUI's ComboBox
    /// popup dismissal) — only their content and enabled state change.</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            // Each combo excludes only its own claim, so the transmit pin still
            // shows as taken on the receive combo and vice versa.
            if (Vm.AdatSupported)
                ApplyOwners(OutPinCombo, HardwarePins.BuildOwnerMap(Vm, excludeAdatSelf: true), Vm.AdatPin);
            if (Vm.AdatInputSupported)
                ApplyOwners(InPinCombo, HardwarePins.BuildOwnerMap(Vm, excludeAdatInputSelf: true), Vm.AdatInputPin);
        }
        finally { _suppress = false; }
    }

    private static void ApplyOwners(ComboBox combo, IReadOnlyDictionary<byte, string> owners, byte currentPin)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is not ComboBoxItem item || item.Tag is not byte pin) continue;
            if (pin == MainViewModel.AdatInputPinUnset) { item.IsEnabled = true; continue; }

            bool isCurrent = pin == currentPin;
            string? owner = null;
            if (!isCurrent && owners.TryGetValue(pin, out var o)) owner = o;
            item.Content = owner != null ? $"GPIO {pin} ({owner})" : $"GPIO {pin}";
            item.IsEnabled = owner == null;
        }
        SelectPinInCombo(combo, currentPin);
    }

    // ── Output handlers ────────────────────────────────────────────────────

    private async void OnOutEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        bool enable = OutEnableToggle.IsOn;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatEnable(enable));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus(enable ? "ADAT output enabled" : "ADAT output disabled", false);
            RefreshConflicts();
            return;
        }

        // Failed — revert the toggle to the actual state and explain.
        _suppress = true;
        OutEnableToggle.IsOn = Vm.AdatEnabled;
        _suppress = false;
        ShowStatus(status switch
        {
            PinConfigResult.PinInUse => "The transmit pin is already assigned — pick a free GPIO first.",
            PinConfigResult.InvalidPin => "The transmit pin isn't valid — pick a different GPIO.",
            PinConfigResult.InvalidOutput => "ADAT output isn't supported on this device.",
            _ => $"Failed to change ADAT output (0x{status:X2})"
        }, true);
    }

    private async void OnOutPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (OutPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"ADAT transmit pin set to GPIO {newPin}", false);
            return;
        }

        _suppress = true;
        SelectPinInCombo(OutPinCombo, Vm.AdatPin);
        _suppress = false;
        ShowStatus(status switch
        {
            PinConfigResult.PinInUse => $"GPIO {newPin} is already assigned to another output",
            PinConfigResult.InvalidPin => $"GPIO {newPin} can't drive the ADAT output",
            _ => $"Failed to set the ADAT transmit pin (0x{status:X2})"
        }, true);
    }

    // ── Input handlers ─────────────────────────────────────────────────────

    private async void OnInEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        bool enable = InEnableToggle.IsOn;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatInputEnable(enable));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshConflicts();
            return;
        }
        _suppress = true;
        InEnableToggle.IsOn = Vm.AdatInputEnabled;
        _suppress = false;
        ShowStatus(status switch
        {
            PinConfigResult.InvalidPin => "Set a valid receive pin before enabling the ADAT input.",
            PinConfigResult.InvalidOutput => "ADAT input isn't supported on this device.",
            PinConfigResult.PinInUse => "The receive pin is already claimed — pick a free GPIO.",
            _ => $"Failed to change ADAT input (0x{status:X2})."
        }, true);
    }

    private async void OnInPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (InPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;
        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatInputPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            string label = newPin == MainViewModel.AdatInputPinUnset ? "cleared" : $"set to GPIO {newPin}";
            ShowStatus($"ADAT receive pin {label}", false);
            return;
        }
        _suppress = true;
        SelectPinInCombo(InPinCombo, Vm.AdatInputPin);
        _suppress = false;
        ShowStatus(status switch
        {
            PinConfigResult.PinInUse => $"GPIO {newPin} is already assigned to another peripheral.",
            PinConfigResult.InvalidPin => $"GPIO {newPin} can't receive the ADAT input.",
            _ => $"Failed to set the ADAT receive pin (0x{status:X2})."
        }, true);
    }

    /// <summary>Select the combo entry whose byte Tag matches <paramref name="pin"/>.
    /// No-op if no match — leaves the previous selection rather than blanking.</summary>
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

    private void ShowStatus(string msg, bool isError)
    {
        StatusText.Text = msg;
        StatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 240, 100, 100)
            : Color.FromArgb(255, 100, 200, 140));
        StatusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus() => StatusText.Visibility = Visibility.Collapsed;

    // ── ISettingsPage ──────────────────────────────────────────────────────
    public string Id => "hardware.adat";
    public string Title => "ADAT";
    public SettingsCategory Category => SettingsCategory.System;
    public string IconGlyph => "";
    public int Order => 20;
    public bool IsAvailable(MainViewModel vm) => vm.AdatSupported || vm.AdatInputSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareAdatPage();
        p.Attach(vm, tracker);
        return p;
    }
}
