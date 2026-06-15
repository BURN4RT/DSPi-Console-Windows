using System.ComponentModel;
using System.Globalization;
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
/// Hardware › I2S Input — RX data pin (conflict-aware) and master sample rate.
/// Only registered when the connected firmware exposes I2S input (V12+). The
/// shared BCK/LRCK/MCK clock pins live on the Hardware › I2S page.
/// </summary>
public sealed partial class HardwareI2SInputPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    public HardwareI2SInputPage()
    {
        InitializeComponent();

        // Populate the RX pin combo once at construction. RefreshConflicts only
        // toggles IsEnabled / Content — it MUST NOT clear/rebuild the Items
        // collection (doing so races popup-dismissal and throws "Element not
        // found" in WinUI's ComboBox layout). Same rule as the SPDIF/I2S pages.
        foreach (var pin in HardwarePins.ValidPins)
            I2sRxPinCombo.Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);

        var fetchVm = vm;
        _ = Task.Run(() =>
        {
            fetchVm.FetchI2sRxPin();
            fetchVm.FetchI2sInputRate();
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
        // Both can change externally (preset load / reconnect / source switch).
        if (e.PropertyName == nameof(MainViewModel.I2sRxPin)
            || e.PropertyName == nameof(MainViewModel.I2sInputRateHz))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;
        RefreshConflicts();
        RefreshRate();
    }

    /// <summary>Refresh per-item state on the RX pin combo so pins claimed by
    /// other features appear disabled and labelled with their owner. The Items
    /// collection itself is never modified here (see ctor note).</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;

        var owners = HardwarePins.BuildOwnerMap(Vm, excludeI2sRxSelf: true);
        byte currentPin = Vm.I2sRxPin;

        _suppress = true;
        try
        {
            for (int i = 0; i < I2sRxPinCombo.Items.Count; i++)
            {
                if (I2sRxPinCombo.Items[i] is not ComboBoxItem item) continue;
                if (item.Tag is not byte pin) continue;

                bool isCurrent = pin == currentPin;
                string? ownerLabel = null;
                if (!isCurrent && owners.TryGetValue(pin, out var owner))
                    ownerLabel = owner;

                item.Content = ownerLabel != null
                    ? $"GPIO {pin} ({ownerLabel})"
                    : $"GPIO {pin}";
                item.IsEnabled = ownerLabel == null;
            }
            SelectPinInCombo(I2sRxPinCombo, currentPin);
        }
        finally { _suppress = false; }
    }

    private void RefreshRate()
    {
        if (Vm == null) return;
        _suppress = true;
        try
        {
            uint current = Vm.I2sInputRateHz;
            for (int i = 0; i < I2sRateCombo.Items.Count; i++)
            {
                if (I2sRateCombo.Items[i] is ComboBoxItem item
                    && uint.TryParse(item.Tag?.ToString(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var hz)
                    && hz == current)
                {
                    I2sRateCombo.SelectedIndex = i;
                    return;
                }
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

    private async void OnI2sRxPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (I2sRxPinCombo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();

        var status = await Task.Run(() => Vm.SetI2sRxPin(newPin));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"I2S RX data pin set to GPIO {newPin}", false);
            return;
        }

        // Revert to the device's actual value on failure.
        _suppress = true;
        SelectPinInCombo(I2sRxPinCombo, Vm.I2sRxPin);
        _suppress = false;

        var msg = status switch
        {
            PinConfigResult.PinInUse => $"GPIO {newPin} is already in use",
            PinConfigResult.InvalidPin => $"GPIO {newPin} is not a valid pin",
            _ => $"Failed to set I2S RX pin (0x{status:X2})"
        };
        ShowStatus(msg, true);
    }

    private async void OnI2sRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (I2sRateCombo.SelectedItem is not ComboBoxItem item) return;
        if (!uint.TryParse(item.Tag?.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var hz)) return;
        if (hz == Vm.I2sInputRateHz) return;

        ClearStatus();
        var ok = await Task.Run(() => Vm.SetI2sInputRate(hz));
        if (ok)
            ShowStatus($"I2S sample rate set to {hz / 1000.0:0.#} kHz", false);
        else
        {
            _suppress = true;
            RefreshRate();
            _suppress = false;
            ShowStatus("Failed to set sample rate", true);
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

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "hardware.i2s-input";
    public string Title => "I2S Input";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => "";
    public int Order => 35; // just after S/PDIF Input (30)
    // V12+ feature — hide the sidebar entry entirely on older firmware.
    public bool IsAvailable(MainViewModel vm) => vm.InputI2sSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareI2SInputPage();
        p.Attach(vm, tracker);
        return p;
    }
}
