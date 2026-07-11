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
/// Hardware › I2S Input — channel count (RP2350), one conflict-aware RX data pin
/// per active stereo pair, and the master sample rate. Registered when the
/// firmware exposes I2S input (V12+). The shared BCK/LRCK/MCK clock pins live on
/// the Hardware › I2S page.
/// </summary>
public sealed partial class HardwareI2SInputPage : SettingsModule, ISettingsPage
{
    private bool _suppress;
    private ComboBox[] _pinCombos = System.Array.Empty<ComboBox>();

    public HardwareI2SInputPage()
    {
        InitializeComponent();

        _pinCombos = new[] { RxPinCombo0, RxPinCombo1, RxPinCombo2, RxPinCombo3 };

        // Populate each pin combo once and tag it with its stereo-pair index.
        // RefreshConflicts only toggles IsEnabled / Content — never rebuilds Items
        // (that races popup-dismissal and throws in WinUI's ComboBox layout).
        for (int pair = 0; pair < _pinCombos.Length; pair++)
        {
            _pinCombos[pair].Tag = pair;
            foreach (var pin in HardwarePins.ValidPins)
                _pinCombos[pair].Items.Add(new ComboBoxItem { Content = $"GPIO {pin}", Tag = pin });
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
            fetchVm.FetchI2sInputConfig();
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
        if (e.PropertyName == nameof(MainViewModel.I2sRxPin)
            || e.PropertyName == nameof(MainViewModel.I2sInputChannels)
            || e.PropertyName == nameof(MainViewModel.I2sInputRateHz))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        // Channel selector only on parts with more than one stereo pair (RP2350).
        bool multi = Vm.I2sMaxInputChannels > 2;
        ChannelsCard.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        _suppress = true;
        try
        {
            if (multi) SelectChannelCount(Vm.I2sInputChannels);
        }
        finally { _suppress = false; }

        // One data-pin card per active stereo pair.
        int pairs = Vm.I2sActivePairs;
        RxPinCard0.Header = pairs > 1 ? "Serial Data 1" : "Serial Data";
        RxPinCard1.Visibility = pairs >= 2 ? Visibility.Visible : Visibility.Collapsed;
        RxPinCard2.Visibility = pairs >= 3 ? Visibility.Visible : Visibility.Collapsed;
        RxPinCard3.Visibility = pairs >= 4 ? Visibility.Visible : Visibility.Collapsed;

        RefreshConflicts();
        RefreshRate();
    }

    /// <summary>Refresh per-item state on every active pin combo so pins claimed
    /// by other features (including sibling I2S pairs) appear disabled and
    /// labelled with their owner. Items are never rebuilt here.</summary>
    private void RefreshConflicts()
    {
        if (Vm == null) return;
        int pairs = Vm.I2sActivePairs;

        _suppress = true;
        try
        {
            for (int pair = 0; pair < _pinCombos.Length; pair++)
            {
                if (pair >= pairs) continue; // hidden card — skip
                var combo = _pinCombos[pair];
                var owners = HardwarePins.BuildOwnerMap(Vm, excludeI2sRxPair: pair);
                byte currentPin = Vm.I2sRxPinAt(pair);

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

    private void SelectChannelCount(int count)
    {
        for (int i = 0; i < ChannelsCombo.Items.Count; i++)
        {
            if (ChannelsCombo.Items[i] is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), out var c) && c == count)
            {
                ChannelsCombo.SelectedIndex = i;
                return;
            }
        }
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

    private async void OnChannelsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (ChannelsCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string s || !int.TryParse(s, out int count)) return;
        if (count == Vm.I2sInputChannels) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetI2sInputChannels(count));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"{count} channels ({count / 2} pair{(count / 2 == 1 ? "" : "s")})", false);
        }
        else
        {
            var msg = status switch
            {
                PinConfigResult.InvalidOutput => "Multichannel I2S isn't supported on this device",
                PinConfigResult.PinInUse => "A pair's data pin conflicts — assign different GPIOs first",
                _ => $"Failed to set channel count (0x{status:X2})"
            };
            ShowStatus(msg, true);
        }
        DispatcherQueue.TryEnqueue(Refresh);
    }

    private async void OnI2sRxPinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        if (sender is not ComboBox combo) return;
        int pair = combo.Tag is int t ? t : 0;
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not byte newPin) return;

        ClearStatus();
        var status = await Task.Run(() => Vm.SetI2sRxPin(newPin, pair));
        if (status == PinConfigResult.Success)
        {
            HardwarePins.RaisePinAssignmentsChanged();
            ShowStatus($"{PairLabel(pair)} pin set to GPIO {newPin}", false);
            return;
        }

        _suppress = true;
        SelectPinInCombo(combo, Vm.I2sRxPinAt(pair));
        _suppress = false;

        var msg = status switch
        {
            PinConfigResult.PinInUse => $"GPIO {newPin} is already in use",
            PinConfigResult.InvalidPin => $"GPIO {newPin} is not a valid pin",
            _ => $"Failed to set I2S RX pin (0x{status:X2})"
        };
        ShowStatus(msg, true);
    }

    private string PairLabel(int pair) =>
        Vm != null && Vm.I2sActivePairs > 1 ? $"Serial Data {pair + 1}" : "I2S RX data";

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
    public bool IsAvailable(MainViewModel vm) => vm.InputI2sSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareI2SInputPage();
        p.Attach(vm, tracker);
        return p;
    }
}
