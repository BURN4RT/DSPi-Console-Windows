using System;
using System.ComponentModel;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Hardware › Control Interfaces. UART and I2C-target links that carry the vendor
/// command set to a host MCU (firmware 0xF5–0xF9). Each section stages a draft and
/// applies the whole 8-byte config in one deferred flash write. Only registered
/// when the connected firmware reports support (see <see cref="IsAvailable"/>).
/// </summary>
public sealed partial class HardwareControlInterfacesPage : SettingsModule, ISettingsPage
{
    private bool _suppress;
    private UartCtrlConfig _uartDraft = new();
    private I2cCtrlConfig _i2cDraft = new();

    private static readonly SolidColorBrush Green = new(Color.FromArgb(255, 100, 200, 140));
    private static readonly SolidColorBrush Amber = new(Color.FromArgb(255, 240, 180, 90));
    private static readonly SolidColorBrush Red = new(Color.FromArgb(255, 240, 100, 100));
    private static readonly SolidColorBrush Grey = new(Color.FromArgb(255, 150, 150, 150));

    public HardwareControlInterfacesPage()
    {
        InitializeComponent();

        // Populate the mux-appropriate pin candidates once; RefreshConflicts only
        // toggles their enabled state.
        foreach (var pin in HardwarePins.ValidPins)
        {
            if (pin % 4 == 0) UartTxCombo.Items.Add(PinItem(pin));
            if (pin % 4 == 1) UartRxCombo.Items.Add(PinItem(pin));
            if (pin % 2 == 0) I2cSdaCombo.Items.Add(PinItem(pin));
            if (pin % 2 == 1) I2cSclCombo.Items.Add(PinItem(pin));
        }
        foreach (var baud in CtrlIfaceLimits.BaudChoices)
            UartBaudCombo.Items.Add(new ComboBoxItem { Content = BaudLabel(baud), Tag = baud });

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private static ComboBoxItem PinItem(byte pin) => new() { Content = $"GPIO {pin}", Tag = pin };

    private static string BaudLabel(uint baud) =>
        baud % 1000 == 0 ? $"{baud / 1000}k" : baud.ToString();

    public override void Attach(MainViewModel vm, IPendingChangeTracker tracker)
    {
        base.Attach(vm, tracker);
        var fetchVm = vm;
        _ = Task.Run(() => fetchVm.FetchControlInterfaces())
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
        if (e.PropertyName == nameof(MainViewModel.UartCtrlConfig)
            || e.PropertyName == nameof(MainViewModel.I2cCtrlConfig)
            || e.PropertyName == nameof(MainViewModel.CtrlIfaceStatus)
            || e.PropertyName == nameof(MainViewModel.ControlInterfacesSupported))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;
        // Re-seed drafts from live config only when the user has no pending edits,
        // so an external change doesn't strand an in-progress edit.
        if (_uartDraft.ValueEquals(Vm.UartCtrlConfig) || !UartDirty()) _uartDraft = Vm.UartCtrlConfig.Clone();
        if (_i2cDraft.ValueEquals(Vm.I2cCtrlConfig) || !I2cDirty()) _i2cDraft = Vm.I2cCtrlConfig.Clone();
        WriteUartControls();
        WriteI2cControls();
        RefreshConflicts();
        RefreshPillsAndButtons();
    }

    // ── Seed controls from drafts ────────────────────────────────────────────

    private void WriteUartControls()
    {
        _suppress = true;
        try
        {
            UartEnableToggle.IsOn = _uartDraft.Enabled;
            UartNotifyToggle.IsOn = _uartDraft.NotifyEnable;
            SelectByTag(UartTxCombo, _uartDraft.TxPin);
            SelectByTag(UartRxCombo, _uartDraft.RxPin);
            SelectByTag(UartBaudCombo, _uartDraft.Baud);
        }
        finally { _suppress = false; }
    }

    private void WriteI2cControls()
    {
        _suppress = true;
        try
        {
            I2cEnableToggle.IsOn = _i2cDraft.Enabled;
            SelectByTag(I2cSdaCombo, _i2cDraft.SdaPin);
            SelectByTag(I2cSclCombo, _i2cDraft.SclPin);
            I2cAddrBox.Value = _i2cDraft.Address;
            I2cAddrHex.Text = $"0x{_i2cDraft.Address:X2}";
        }
        finally { _suppress = false; }
    }

    // ── Read drafts from controls ────────────────────────────────────────────

    private void ReadUartDraft()
    {
        _uartDraft.Enabled = UartEnableToggle.IsOn;
        _uartDraft.NotifyEnable = UartNotifyToggle.IsOn;
        if (UartTxCombo.SelectedItem is ComboBoxItem tx && tx.Tag is byte txp) _uartDraft.TxPin = txp;
        if (UartRxCombo.SelectedItem is ComboBoxItem rx && rx.Tag is byte rxp) _uartDraft.RxPin = rxp;
        if (UartBaudCombo.SelectedItem is ComboBoxItem b && b.Tag is uint baud) _uartDraft.Baud = baud;
    }

    private void ReadI2cDraft()
    {
        _i2cDraft.Enabled = I2cEnableToggle.IsOn;
        if (I2cSdaCombo.SelectedItem is ComboBoxItem sda && sda.Tag is byte sdap) _i2cDraft.SdaPin = sdap;
        if (I2cSclCombo.SelectedItem is ComboBoxItem scl && scl.Tag is byte sclp) _i2cDraft.SclPin = sclp;
    }

    private void OnUartFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        ReadUartDraft();
        RefreshPillsAndButtons();
    }

    private void OnI2cFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress || Vm == null) return;
        ReadI2cDraft();
        RefreshPillsAndButtons();
    }

    private void OnI2cAddrChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppress || Vm == null) return;
        if (double.IsNaN(args.NewValue)) return;
        int addr = Math.Clamp((int)args.NewValue, CtrlIfaceLimits.I2cAddressMin, CtrlIfaceLimits.I2cAddressMax);
        _i2cDraft.Address = (byte)addr;
        I2cAddrHex.Text = $"0x{addr:X2}";
        RefreshPillsAndButtons();
    }

    // ── Conflict + status refresh ────────────────────────────────────────────

    private void RefreshConflicts()
    {
        if (Vm == null) return;
        var uartOwners = HardwarePins.BuildOwnerMap(Vm, excludeUartSelf: true);
        GreyOwnedPins(UartTxCombo, uartOwners, _uartDraft.TxPin);
        GreyOwnedPins(UartRxCombo, uartOwners, _uartDraft.RxPin);
        var i2cOwners = HardwarePins.BuildOwnerMap(Vm, excludeI2cSelf: true);
        GreyOwnedPins(I2cSdaCombo, i2cOwners, _i2cDraft.SdaPin);
        GreyOwnedPins(I2cSclCombo, i2cOwners, _i2cDraft.SclPin);
    }

    private void GreyOwnedPins(ComboBox combo, System.Collections.Generic.IReadOnlyDictionary<byte, string> owners, byte current)
    {
        _suppress = true;
        try
        {
            foreach (var obj in combo.Items)
            {
                if (obj is not ComboBoxItem item || item.Tag is not byte pin) continue;
                bool isCurrent = pin == current;
                string? owner = null;
                if (!isCurrent && owners.TryGetValue(pin, out var o)) owner = o;
                item.Content = owner != null ? $"GPIO {pin} ({owner})" : $"GPIO {pin}";
                item.IsEnabled = owner == null;
            }
            SelectByTag(combo, current);
        }
        finally { _suppress = false; }
    }

    private void RefreshPillsAndButtons()
    {
        if (Vm == null) return;
        var status = Vm.CtrlIfaceStatus;

        SetPill(UartPill, status.UartLive, _uartDraft.Enabled, Vm.UartCtrlConfig.Enabled);
        SetPill(I2cPill, status.I2cLive, _i2cDraft.Enabled, Vm.I2cCtrlConfig.Enabled);

        bool uartDirty = UartDirty();
        UartApplyButton.IsEnabled = uartDirty;
        UartRevertButton.IsEnabled = uartDirty;

        bool i2cDirty = I2cDirty();
        I2cApplyButton.IsEnabled = i2cDirty;
        I2cRevertButton.IsEnabled = i2cDirty;
    }

    private static void SetPill(TextBlock pill, bool live, bool draftEnabled, bool configEnabled)
    {
        if (live) { pill.Text = "Active"; pill.Foreground = Green; }
        else if (configEnabled) { pill.Text = "Inactive"; pill.Foreground = Amber; }
        else { pill.Text = "Disabled"; pill.Foreground = Grey; }
    }

    private bool UartDirty() => Vm != null && !_uartDraft.ValueEquals(Vm.UartCtrlConfig);
    private bool I2cDirty() => Vm != null && !_i2cDraft.ValueEquals(Vm.I2cCtrlConfig);

    // ── Apply / revert ───────────────────────────────────────────────────────

    private async void OnUartApply(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        UartApplyButton.IsEnabled = UartRevertButton.IsEnabled = false;
        UartStatusText.Visibility = Visibility.Collapsed;
        var cfg = _uartDraft.Clone();
        byte status = await Task.Run(() => Vm.SetUartCtrlConfig(cfg));

        _uartDraft = Vm.UartCtrlConfig.Clone();
        WriteUartControls();
        HardwarePins.RaisePinAssignmentsChanged();
        RefreshConflicts();
        RefreshPillsAndButtons();
        ShowStatus(UartStatusText, StatusMessage("UART", status, isUart: true), status != PinConfigResult.Success);
    }

    private async void OnI2cApply(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        I2cApplyButton.IsEnabled = I2cRevertButton.IsEnabled = false;
        I2cStatusText.Visibility = Visibility.Collapsed;
        var cfg = _i2cDraft.Clone();
        byte status = await Task.Run(() => Vm.SetI2cCtrlConfig(cfg));

        _i2cDraft = Vm.I2cCtrlConfig.Clone();
        WriteI2cControls();
        HardwarePins.RaisePinAssignmentsChanged();
        RefreshConflicts();
        RefreshPillsAndButtons();
        ShowStatus(I2cStatusText, StatusMessage("I2C", status, isUart: false), status != PinConfigResult.Success);
    }

    private void OnUartRevert(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        _uartDraft = Vm.UartCtrlConfig.Clone();
        WriteUartControls();
        RefreshConflicts();
        RefreshPillsAndButtons();
        UartStatusText.Visibility = Visibility.Collapsed;
    }

    private void OnI2cRevert(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        _i2cDraft = Vm.I2cCtrlConfig.Clone();
        WriteI2cControls();
        RefreshConflicts();
        RefreshPillsAndButtons();
        I2cStatusText.Visibility = Visibility.Collapsed;
    }

    private static string StatusMessage(string iface, byte status, bool isUart) => status switch
    {
        PinConfigResult.Success => $"{iface} configuration applied and saved.",
        PinConfigResult.InvalidPin => $"A pin is out of range or lacks the required {iface} mux function.",
        PinConfigResult.PinInUse => "A pin is already claimed by another output or interface.",
        PinConfigResult.InvalidParam => isUart
            ? "Baud rate is out of range (9600–1000000)."
            : "Address is out of range (0x08–0x77).",
        _ => $"Failed to apply {iface} configuration (0x{status:X2})."
    };

    private static void ShowStatus(TextBlock text, string msg, bool isError)
    {
        text.Text = msg;
        text.Foreground = isError ? Red : Green;
        text.Visibility = Visibility.Visible;
    }

    private void SelectByTag(ComboBox combo, object value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem item && Equals(item.Tag, value))
            {
                combo.SelectedIndex = i;
                return;
            }
        combo.SelectedIndex = -1;
    }

    // ── ISettingsPage ──────────────────────────────────────────────────────
    public string Id => "hardware.control-interfaces";
    public string Title => "Control Interfaces";
    public SettingsCategory Category => SettingsCategory.Hardware;
    public string IconGlyph => "";
    public int Order => 80;
    public bool IsAvailable(MainViewModel vm) => vm.ControlInterfacesSupported;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new HardwareControlInterfacesPage();
        p.Attach(vm, tracker);
        return p;
    }
}
