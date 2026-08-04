using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;

namespace DSPiConsole.ViewModels;

/// <summary>
/// UART / I2C control-interface state and device orchestration (firmware
/// 0xF5–0xF9). The device can speak the vendor command set over an external UART
/// link or as an I2C target. The whole 8-byte config is applied in one deferred
/// flash write; the authoritative PIN_CONFIG_* outcome is read back via the live
/// status packet (0xF9), whose <c>*Live</c> flags also tell the UI whether a
/// stored-and-enabled interface actually came up.
///
/// <para>SET methods block (they wait for the deferred apply + read status back) —
/// call them off the UI thread. A disabled interface reserves no GPIOs.</para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>True once the status probe (0xF9) answers. Older firmware STALLs,
    /// hiding the whole feature.</summary>
    [ObservableProperty]
    private bool _controlInterfacesSupported;

    private UartCtrlConfig _uartCtrlConfig = new();
    private I2cCtrlConfig _i2cCtrlConfig = new();
    private CtrlIfaceStatus _ctrlIfaceStatus = new();

    public UartCtrlConfig UartCtrlConfig => _uartCtrlConfig;
    public I2cCtrlConfig I2cCtrlConfig => _i2cCtrlConfig;
    public CtrlIfaceStatus CtrlIfaceStatus => _ctrlIfaceStatus;

    /// <summary>Probe support and read both configs + live status. Sets
    /// <see cref="ControlInterfacesSupported"/> (false if 0xF9 STALLs). Blocking —
    /// call off the UI thread.</summary>
    public void FetchControlInterfaces()
    {
        var status = _device.GetCtrlIfaceStatus();
        if (status == null)
        {
            _dispatcher.TryEnqueue(() => ControlInterfacesSupported = false);
            return;
        }
        _ctrlIfaceStatus = status;
        var uart = _device.GetUartCtrlConfig();
        var i2c = _device.GetI2cCtrlConfig();
        if (uart != null) _uartCtrlConfig = uart;
        if (i2c != null) _i2cCtrlConfig = i2c;

        _dispatcher.TryEnqueue(() =>
        {
            ControlInterfacesSupported = true;
            OnPropertyChanged(nameof(UartCtrlConfig));
            OnPropertyChanged(nameof(I2cCtrlConfig));
            OnPropertyChanged(nameof(CtrlIfaceStatus));
        });
    }

    /// <summary>Re-read only the live status packet (0xF9). Notifies only when a
    /// field actually moved, so a page can poll this without forcing a UI rebuild
    /// every tick. Blocking — call off the UI thread.</summary>
    public void RefreshCtrlIfaceStatus()
    {
        var status = _device.GetCtrlIfaceStatus();
        if (status == null) return;
        bool changed = !status.ValueEquals(_ctrlIfaceStatus);
        _ctrlIfaceStatus = status;
        if (changed) _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(CtrlIfaceStatus)));
    }

    /// <summary>Apply a UART config (deferred + persisted). Re-reads config + status
    /// and returns the firmware PIN_CONFIG_* outcome byte.</summary>
    public byte SetUartCtrlConfig(UartCtrlConfig config)
    {
        byte result = _device.SetUartCtrlConfig(config);
        var cfg = _device.GetUartCtrlConfig();
        var status = _device.GetCtrlIfaceStatus();
        if (cfg != null) _uartCtrlConfig = cfg;
        if (status != null) _ctrlIfaceStatus = status;
        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(UartCtrlConfig));
            OnPropertyChanged(nameof(CtrlIfaceStatus));
        });
        return result;
    }

    /// <summary>Apply an I2C config (deferred + persisted). Returns the PIN_CONFIG_*
    /// outcome byte.</summary>
    public byte SetI2cCtrlConfig(I2cCtrlConfig config)
    {
        byte result = _device.SetI2cCtrlConfig(config);
        var cfg = _device.GetI2cCtrlConfig();
        var status = _device.GetCtrlIfaceStatus();
        if (cfg != null) _i2cCtrlConfig = cfg;
        if (status != null) _ctrlIfaceStatus = status;
        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(I2cCtrlConfig));
            OnPropertyChanged(nameof(CtrlIfaceStatus));
        });
        return result;
    }
}
