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
/// Hardware › S/PDIF Input. An "Instances" selector (1..3) on firmware that
/// exposes multiple selectable inputs, one conflict-aware RX pin combo per
/// active input, and the LG Sound Sync toggle. Only registered in the sidebar
/// when the connected firmware supports input-source switching (V7+).
/// </summary>
public sealed partial class HardwareSpdifInputPage : SettingsModule, ISettingsPage
{
    private bool _suppress;
    private ComboBox[] _pinCombos = System.Array.Empty<ComboBox>();

    public HardwareSpdifInputPage()
    {
        InitializeComponent();

        _pinCombos = new[] { RxPinCombo0, RxPinCombo1, RxPinCombo2 };

        // Populate each RX pin combo once with every ValidPins entry and tag the
        // combo with its input index. RefreshConflicts only toggles IsEnabled /
        // Content and the selection — it MUST NOT clear/rebuild Items (that races
        // popup dismissal and throws "Element not found" in WinUI's ComboBox).
        for (int idx = 0; idx < _pinCombos.Length; idx++)
        {
            _pinCombos[idx].Tag = idx;
            foreach (var pin in HardwarePins.ValidPins)
                _pinCombos[idx].Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
        }

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);
        var fetchVm = vm;
        _ = Task.Run(() =>
        {
            fetchVm.FetchSpdifInputConfig();
            fetchVm.FetchLgSoundSync();
        }).ContinueWith(_ => DispatcherQueue.TryEnqueue(Refresh));
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

    private void OnExternalPinChange() =>
        DispatcherQueue.TryEnqueue(RefreshConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SpdifRxPin)
            || e.PropertyName == nameof(MainViewModel.MultiSpdifSupported)
            || e.PropertyName == nameof(MainViewModel.LgSoundSyncEnabled)
            || e.PropertyName == nameof(MainViewModel.LgSoundSyncSupported))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        bool multi = Vm.MultiSpdifSupported;
        int count = multi ? Vm.SpdifEnabledCount : 1;

        InstancesCard.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        _suppress = true;
        try { InstancesCombo.SelectedIndex = System.Math.Clamp(count - 1, 0, 2); }
        finally { _suppress = false; }

        RxPinCard0.Header = multi ? "S/PDIF 1 RX pin" : "RX pin";
        RxPinCard1.Visibility = (multi && count >= 2) ? Visibility.Visible : Visibility.Collapsed;
        RxPinCard2.Visibility = (multi && count >= 3) ? Visibility.Visible : Visibility.Collapsed;

        RefreshConflicts();
        RefreshLgSoundSync();
    }

    /// <summary>Refresh per-item enablement + selection on every RX pin combo so
    /// pins claimed by other features (including sibling S/PDIF inputs) appear
    /// disabled and labelled with their owner. Items are never rebuilt here.</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;

        _suppress = true;
        try
        {
            for (int idx = 0; idx < _pinCombos.Length; idx++)
            {
                var combo = _pinCombos[idx];
                var owners = HardwarePins.BuildOwnerMap(Vm, excludeSpdifRxIndex: idx);
                byte currentPin = Vm.SpdifRxPinAt(idx);

                for (int i = 0; i < combo.Items.Count; i++)
                {
                    if (combo.Items[i] is not ComboBoxItem item) continue;
                    if (item.Tag is not byte pin) continue;

                    bool isCurrent = pin == currentPin;
                    string? ownerLabel = null;
                    if (!isCurrent && owners.TryGetValue(pin, out var owner))
                        ownerLabel = owner;

                    item.Content = ownerLabel != null ? $"GPIO {pin} ({ownerLabel})" : $"GPIO {pin}";
                    item.IsEnabled = ownerLabel == null;
                }
                SelectPinInCombo(combo, currentPin);
            }
        }
        finally { _suppress = false; }
    }

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

    private async void OnInstancesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (InstancesCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string s || !int.TryParse(s, out int target)) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetSpdifInputCount(target));
        HardwarePins.RaisePinAssignmentsChanged();
        if (status == PinConfigResult.Success)
            ShowStatus($"{target} S/PDIF input{(target == 1 ? "" : "s")} active", false);
        else
            ShowStatus(status == PinConfigResult.PinInUse
                ? "A pin conflict blocked enabling an input — assign different GPIOs."
                : $"Failed to change instance count (0x{status:X2})", true);
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private async void OnSpdifRxPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (sender is not ComboBox combo) return;
        int index = combo.Tag is int t ? t : 0;
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetSpdifRxPin(newPin, index));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"{InputLabel(index)} pin set to GPIO {newPin}", false);
            return;
        }

        _suppress = true;
        SelectPinInCombo(combo, Vm.SpdifRxPinAt(index));
        _suppress = false;

        var msg = status switch
        {
            PinConfigResult.PinInUse => $"GPIO {newPin} is already in use",
            _ => $"Failed to set RX pin (0x{status:X2})"
        };
        ShowStatus(msg, true);
    }

    private string InputLabel(int index) =>
        Vm != null && Vm.MultiSpdifSupported ? $"S/PDIF {index + 1}" : "S/PDIF RX";

    private void RefreshLgSoundSync()
    {
        if (Vm == null) return;
        LgSoundSyncCard.Visibility = Vm.LgSoundSyncSupported
            ? Visibility.Visible : Visibility.Collapsed;
        _suppress = true;
        try { LgSoundSyncToggle.IsOn = Vm.LgSoundSyncEnabled; }
        finally { _suppress = false; }
    }

    private void OnLgSoundSyncToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        Vm.LgSoundSyncEnabled = LgSoundSyncToggle.IsOn;
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
    public string Id => "hardware.spdif-input";
    public string Title => "S/PDIF Input";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => "";
    public int Order => 40;
    public bool IsAvailable(MainViewModel vm) => vm.InputSourceSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareSpdifInputPage();
        p.Attach(vm, tracker);
        return p;
    }
}
