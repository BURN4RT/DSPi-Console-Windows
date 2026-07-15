using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// I2S clock master/slave mode (0x88/0x89/0x8A) and clock-pin unified/split mode
/// (0xFE/0xFF) with a role-indexed slave BCK pair (0xC2/0xC3). In slave mode an
/// external master drives BCK/LRCLK and the rate is auto-detected. SPLIT routes the
/// slave role to its own pin pair. All three are IO-block state: setters apply live,
/// record an undo, and mark the output config dirty.
/// </summary>
public partial class MainViewModel
{
    private bool _i2sClockModeSupported;
    private byte _i2sClockMode;               // 0=master, 1=slave
    private I2sSlaveStatus? _i2sSlaveStatus;

    private bool _i2sClockPinModeSupported;
    private byte _i2sClockPinMode;            // 0=unified, 1=split
    private byte _i2sBckPinSlave = 26;        // slave-pair BCK GPIO (LRCLK = +1)

    public bool I2sClockModeSupported => _i2sClockModeSupported;
    public byte I2sClockMode => _i2sClockMode;
    public I2sSlaveStatus? I2sSlaveStatus => _i2sSlaveStatus;

    public bool I2sClockPinModeSupported => _i2sClockPinModeSupported;
    public byte I2sClockPinMode => _i2sClockPinMode;
    public byte I2sBckPinSlave => _i2sBckPinSlave;

    /// <summary>True when the I2S input is running off an external (slave) clock —
    /// the DSPi no longer drives BCK/LRCLK or owns the sample rate.</summary>
    public bool I2sSlaveActive => _i2sClockModeSupported && _i2sClockMode == I2sClock.ModeSlave;

    /// <summary>Split-mode reserves a separate slave BCK pair; only then does it
    /// claim those GPIOs.</summary>
    public bool I2sClockSplit => _i2sClockPinModeSupported && _i2sClockPinMode == I2sClock.PinModeSplit;

    /// <summary>Seed I2S clock state from a bulk fetch (no device write).</summary>
    internal void SeedI2sClockFromBulk(BulkParams bp)
    {
        _i2sClockModeSupported = bp.HasI2sClockMode;
        if (_i2sClockModeSupported) _i2sClockMode = bp.I2sClockMode;

        _i2sClockPinModeSupported = bp.HasI2sClockPinMode;
        if (_i2sClockPinModeSupported)
        {
            _i2sClockPinMode = bp.I2sClockPinMode;
            if (bp.I2sBckPinSlave != 0) _i2sBckPinSlave = bp.I2sBckPinSlave;
        }

        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(I2sClockModeSupported));
            OnPropertyChanged(nameof(I2sClockMode));
            OnPropertyChanged(nameof(I2sClockPinModeSupported));
            OnPropertyChanged(nameof(I2sClockPinMode));
            OnPropertyChanged(nameof(I2sBckPinSlave));
            OnPropertyChanged(nameof(I2sSlaveActive));
            OnPropertyChanged(nameof(I2sClockSplit));
        });
    }

    /// <summary>Probe + read the I2S clock mode, slave status, clock-pin mode and
    /// slave BCK pin from the device. Blocking — call off the UI thread.</summary>
    public void FetchI2sClockConfig()
    {
        var mode = _device.GetI2SClockMode();
        _i2sClockModeSupported = mode.HasValue;
        if (mode.HasValue)
        {
            _i2sClockMode = mode.Value;
            _i2sSlaveStatus = _device.GetI2SSlaveStatus();
        }

        var pinMode = _device.GetI2SClockPinMode();
        _i2sClockPinModeSupported = pinMode.HasValue;
        if (pinMode.HasValue)
        {
            _i2sClockPinMode = pinMode.Value;
            var slaveBck = _device.GetI2SBckPin(I2sClock.BckRoleSlave);
            if (slaveBck.HasValue && slaveBck.Value != 0) _i2sBckPinSlave = slaveBck.Value;
        }

        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(I2sClockModeSupported));
            OnPropertyChanged(nameof(I2sClockMode));
            OnPropertyChanged(nameof(I2sSlaveStatus));
            OnPropertyChanged(nameof(I2sClockPinModeSupported));
            OnPropertyChanged(nameof(I2sClockPinMode));
            OnPropertyChanged(nameof(I2sBckPinSlave));
            OnPropertyChanged(nameof(I2sSlaveActive));
            OnPropertyChanged(nameof(I2sClockSplit));
        });
    }

    /// <summary>Re-read only the live I2S slave-clock status (0x8A).</summary>
    public void RefreshI2sSlaveStatus()
    {
        var status = _device.GetI2SSlaveStatus();
        if (status == null) return;
        _i2sSlaveStatus = status;
        _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(I2sSlaveStatus)));
    }

    /// <summary>Set the I2S clock mode (0=master, 1=slave). Deferred on the device;
    /// the live mode is read back to confirm.</summary>
    public void SetI2sClockMode(byte mode)
    {
        byte before = _i2sClockMode;
        byte want = (byte)(mode == I2sClock.ModeSlave ? I2sClock.ModeSlave : I2sClock.ModeMaster);
        if (!_device.SetI2SClockMode(want)) return;

        var live = _device.GetI2SClockMode();
        _i2sClockMode = live ?? want;
        if (before != _i2sClockMode) RecordIoUndo(() => SetI2sClockMode(before));
        _i2sSlaveStatus = _device.GetI2SSlaveStatus();
        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(I2sClockMode));
            OnPropertyChanged(nameof(I2sSlaveActive));
            OnPropertyChanged(nameof(I2sSlaveStatus));
        });
        CheckDirty();
    }

    /// <summary>Set the I2S clock-pin mode (0=unified, 1=split). Returns the firmware
    /// <see cref="PinConfigResult"/> status byte.</summary>
    public byte SetI2sClockPinMode(byte mode)
    {
        byte before = _i2sClockPinMode;
        byte want = (byte)(mode == I2sClock.PinModeSplit ? I2sClock.PinModeSplit : I2sClock.PinModeUnified);
        var status = _device.SetI2SClockPinMode(want);
        if (status == PinConfigResult.Success)
        {
            _i2sClockPinMode = want;
            if (before != want) RecordIoUndo(() => SetI2sClockPinMode(before));
            _dispatcher.TryEnqueue(() =>
            {
                OnPropertyChanged(nameof(I2sClockPinMode));
                OnPropertyChanged(nameof(I2sClockSplit));
            });
            CheckDirty();
        }
        return status;
    }

    /// <summary>Set the slave-pair BCK GPIO (LRCLK = pin + 1). Returns the firmware
    /// <see cref="PinConfigResult"/> status byte.</summary>
    public byte SetI2sBckPinSlave(byte pin)
    {
        byte before = _i2sBckPinSlave;
        var status = _device.SetI2SBckPin(pin, I2sClock.BckRoleSlave);
        if (status == PinConfigResult.Success)
        {
            _i2sBckPinSlave = pin;
            if (before != pin) RecordIoUndo(() => SetI2sBckPinSlave(before));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(I2sBckPinSlave)));
            CheckDirty();
        }
        return status;
    }
}
