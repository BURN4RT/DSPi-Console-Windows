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
/// Hardware › S/PDIF Input — single RX pin combo. Only registered in
/// the sidebar when the connected firmware supports input-source
/// switching (V7+).
/// </summary>
public sealed partial class HardwareSpdifInputPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public HardwareSpdifInputPage()
    {
        InitializeComponent();
        foreach (var pin in HardwarePins.ValidPins)
            SpdifRxPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        Unloaded += (_, _) =>
        {
            HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
            if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        HardwarePins.PinAssignmentsChanged -= OnExternalPinChange;
        HardwarePins.PinAssignmentsChanged += OnExternalPinChange;

        if (Vm != null) Vm.PropertyChanged -= OnVmPropertyChanged;
        vm.PropertyChanged += OnVmPropertyChanged;

        base.Attach(vm, tracker);

        // Fetch from device on a background thread.
        var fetchVm = vm;
        _ = Task.Run(() => fetchVm.FetchSpdifRxPin())
            .ContinueWith(_ => DispatcherQueue.TryEnqueue(Refresh));
    }

    private void OnExternalPinChange() =>
        DispatcherQueue.TryEnqueue(RefreshConflicts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // SpdifRxPin can change externally on preset load / reconnect.
        // Bulk-params parse raises this in MainViewModel; we refresh
        // the combo so the UI stays in sync with the device.
        if (e.PropertyName == nameof(MainViewModel.SpdifRxPin))
            DispatcherQueue.TryEnqueue(Refresh);
    }

    protected override void Refresh()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            var idx = System.Array.IndexOf(HardwarePins.ValidPins, Vm.SpdifRxPin);
            if (idx >= 0) SpdifRxPinCombo.SelectedIndex = idx;
        }
        finally { _suppress = false; }
        RefreshConflicts();
    }

    private void RefreshConflicts()
    {
        if (Vm == null) return;

        // SPDIF RX excludes its own self-entry so the current pin remains
        // selectable. We also need to include BCK explicitly because the
        // owner map already has it via the I²S clock-pin entries.
        var owners = HardwarePins.BuildOwnerMap(Vm, excludeSpdifRxSelf: true);

        _suppress = true;
        for (int i = 0; i < HardwarePins.ValidPins.Length; i++)
        {
            if (SpdifRxPinCombo.Items[i] is not ComboBoxItem item) continue;
            byte pin = HardwarePins.ValidPins[i];
            if (owners.TryGetValue(pin, out var owner))
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
        _suppress = false;
    }

    private async void OnSpdifRxPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (SpdifRxPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();

        // Live apply — per-preset parameter, writes through immediately
        // to RAM. The firmware call still travels over USB so we Task.Run
        // it to keep the UI responsive; status feedback surfaces inline.
        var status = await Task.Run(() => Vm.SetSpdifRxPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            RefreshConflicts();
            ShowStatus($"S/PDIF RX pin set to GPIO {newPin}", false);
            return;
        }

        // Revert combo to device's actual value on failure.
        _suppress = true;
        var idx = System.Array.IndexOf(HardwarePins.ValidPins, Vm.SpdifRxPin);
        if (idx >= 0) SpdifRxPinCombo.SelectedIndex = idx;
        _suppress = false;

        var msg = status switch
        {
            PinConfigResult.PinInUse => $"GPIO {newPin} is already in use",
            _ => $"Failed to set RX pin (0x{status:X2})"
        };
        ShowStatus(msg, true);
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
    public string IconGlyph => ""; // OpenWith / input
    public int Order => 30;
    // V7+ feature — hide the sidebar entry entirely on older firmware.
    public bool IsAvailable(MainViewModel vm) => vm.InputSourceSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareSpdifInputPage();
        p.Attach(vm, tracker);
        return p;
    }
}
