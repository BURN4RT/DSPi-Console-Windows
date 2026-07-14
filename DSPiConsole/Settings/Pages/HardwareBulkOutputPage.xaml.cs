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
/// Hardware › Bulk Output. The RP2350-only ADAT optical output: an enable toggle
/// and a conflict-aware data-pin combo. Only registered when the connected
/// firmware reports ADAT support (see <see cref="IsAvailable"/>).
/// </summary>
public sealed partial class HardwareBulkOutputPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public HardwareBulkOutputPage()
    {
        InitializeComponent();

        // The ADAT default (GPIO 12) is deliberately absent from ValidPins (it's
        // not a general audio-routing pin), so add it explicitly like the Mac's
        // adatPinOptions. Populate once; RefreshConflicts only toggles state.
        var pins = new System.Collections.Generic.List<byte>(HardwarePins.ValidPins);
        if (!pins.Contains(MainViewModel.AdatDefaultPin)) pins.Add(MainViewModel.AdatDefaultPin);
        pins.Sort();
        foreach (var pin in pins)
            PinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);
        var fetchVm = vm;
        _ = Task.Run(() => fetchVm.FetchAdatConfig())
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
        if (e.PropertyName == nameof(MainViewModel.AdatEnabled)
            || e.PropertyName == nameof(MainViewModel.AdatPin)
            || e.PropertyName == nameof(MainViewModel.AdatSupported))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;
        _suppress = true;
        try { EnableToggle.IsOn = Vm.AdatEnabled; }
        finally { _suppress = false; }
        RefreshConflicts();
    }

    /// <summary>Grey-out pins owned by other features and reselect the current pin.
    /// Items are never rebuilt here (that races WinUI's ComboBox popup dismissal).</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            var owners = HardwarePins.BuildOwnerMap(Vm, excludeAdatSelf: true);
            byte currentPin = Vm.AdatPin;
            for (int i = 0; i < PinCombo.Items.Count; i++)
            {
                if (PinCombo.Items[i] is not ComboBoxItem item) continue;
                if (item.Tag is not byte pin) continue;

                bool isCurrent = pin == currentPin;
                string? ownerLabel = null;
                if (!isCurrent && owners.TryGetValue(pin, out var owner))
                    ownerLabel = owner;

                item.Content = ownerLabel != null ? $"GPIO {pin} ({ownerLabel})" : $"GPIO {pin}";
                item.IsEnabled = ownerLabel == null;
            }
            SelectPinInCombo(currentPin);
        }
        finally { _suppress = false; }
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
    }

    private async void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        bool enable = EnableToggle.IsOn;

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
        EnableToggle.IsOn = Vm.AdatEnabled;
        _suppress = false;
        ShowStatus(EnableStatusMessage(status), true);
    }

    private async void OnAdatPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (PinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetAdatPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"ADAT pin set to GPIO {newPin}", false);
            return;
        }

        _suppress = true;
        SelectPinInCombo(Vm.AdatPin);
        _suppress = false;

        var msg = status switch
        {
            PinConfigResult.PinInUse => $"GPIO {newPin} is already assigned to another output",
            PinConfigResult.InvalidPin => $"GPIO {newPin} can't drive the ADAT output",
            _ => $"Failed to set ADAT pin (0x{status:X2})"
        };
        ShowStatus(msg, true);
    }

    private static string EnableStatusMessage(byte status) => status switch
    {
        PinConfigResult.PinInUse => "The ADAT pin is already assigned — pick a free GPIO first.",
        PinConfigResult.InvalidPin => "The configured ADAT pin isn't valid — pick a different GPIO.",
        PinConfigResult.InvalidOutput => "ADAT output isn't supported on this device.",
        _ => $"Failed to change ADAT output (0x{status:X2})"
    };

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
    public string Id => "hardware.bulk-output";
    public string Title => "Bulk Output";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => "";
    public int Order => 15;
    public bool IsAvailable(MainViewModel vm) => vm.AdatSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareBulkOutputPage();
        p.Attach(vm, tracker);
        return p;
    }
}
