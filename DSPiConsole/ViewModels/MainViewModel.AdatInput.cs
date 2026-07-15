using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// ADAT optical INPUT (firmware wire V24, RP2350 only) — a selectable 8-channel
/// input source (INPUT_SOURCE_ADAT = 3) with its own RX pin and master/slave clock
/// mode. Opcodes 0x68–0x6E. Distinct from the ADAT output (0xCA–0xCE). Enable/pin/
/// clock are IO-block state: setters apply live, record an undo, and mark the output
/// config dirty, exactly like the ADAT output and the SPDIF/I2S input pins.
/// </summary>
public partial class MainViewModel
{
    public const byte AdatInputPinUnset = 0xFF;

    private bool _adatInputSupported;
    private bool _adatInputEnabled;
    private byte _adatInputPin = AdatInputPinUnset;
    private byte _adatInputClockMode;   // 0=master, 1=slave
    private AdatInputStatus? _adatInputStatus;

    public bool AdatInputSupported => _adatInputSupported;
    public bool AdatInputEnabled => _adatInputEnabled;
    public byte AdatInputPin => _adatInputPin;
    public byte AdatInputClockMode => _adatInputClockMode;
    public AdatInputStatus? AdatInputStatus => _adatInputStatus;

    /// <summary>ADAT is offerable as an input source only once it's enabled with a
    /// valid RX pin (matching the firmware / macOS gating).</summary>
    public bool AdatInputSelectable =>
        _adatInputSupported && _adatInputEnabled && _adatInputPin != AdatInputPinUnset;

    /// <summary>Seed ADAT-input state from a bulk fetch (no device write). Supported
    /// requires wire V24 and the RP2350 platform.</summary>
    internal void SeedAdatInputFromBulk(BulkParams bp)
    {
        _adatInputSupported = bp.HasAdatInput && Platform == "RP2350";
        if (!_adatInputSupported) return;
        _adatInputEnabled = bp.AdatInputEnabled;
        if (bp.AdatInputPin != 0) _adatInputPin = bp.AdatInputPin;
        _adatInputClockMode = bp.AdatInputClockMode;
        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(AdatInputSupported));
            OnPropertyChanged(nameof(AdatInputEnabled));
            OnPropertyChanged(nameof(AdatInputPin));
            OnPropertyChanged(nameof(AdatInputClockMode));
            OnPropertyChanged(nameof(AdatInputSelectable));
        });
    }

    /// <summary>Probe + read the ADAT-input config and live status from the device
    /// (0x69/0x6B/0x6D/0x6E). Clears <see cref="AdatInputSupported"/> if the firmware
    /// STALLs or the platform is RP2040. Blocking — call off the UI thread.</summary>
    public void FetchAdatInputConfig()
    {
        var en = _device.GetAdatInputEnable();
        if (en == null || Platform != "RP2350")
        {
            _dispatcher.TryEnqueue(() =>
            {
                _adatInputSupported = false;
                OnPropertyChanged(nameof(AdatInputSupported));
                OnPropertyChanged(nameof(AdatInputSelectable));
            });
            return;
        }
        var pin = _device.GetAdatInputPin();
        var clock = _device.GetAdatInputClockMode();
        var status = _device.GetAdatInputStatus();

        _adatInputEnabled = en.Value;
        if (pin.HasValue) _adatInputPin = pin.Value;
        if (clock.HasValue) _adatInputClockMode = clock.Value;
        _adatInputStatus = status;

        _dispatcher.TryEnqueue(() =>
        {
            _adatInputSupported = true;
            OnPropertyChanged(nameof(AdatInputSupported));
            OnPropertyChanged(nameof(AdatInputEnabled));
            OnPropertyChanged(nameof(AdatInputPin));
            OnPropertyChanged(nameof(AdatInputClockMode));
            OnPropertyChanged(nameof(AdatInputStatus));
            OnPropertyChanged(nameof(AdatInputSelectable));
        });
    }

    /// <summary>Re-read only the live ADAT-input status (0x6E).</summary>
    public void RefreshAdatInputStatus()
    {
        var status = _device.GetAdatInputStatus();
        if (status == null) return;
        _adatInputStatus = status;
        _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(AdatInputStatus)));
    }

    /// <summary>Enable/disable the ADAT input (0x68). Returns the firmware
    /// <see cref="PinConfigResult"/> status byte.</summary>
    public byte SetAdatInputEnable(bool enable)
    {
        bool before = _adatInputEnabled;
        var status = _device.SetAdatInputEnable(enable);
        if (status == PinConfigResult.Success)
        {
            _adatInputEnabled = enable;
            if (before != enable) RecordIoUndo(() => SetAdatInputEnable(before));
            _dispatcher.TryEnqueue(() =>
            {
                OnPropertyChanged(nameof(AdatInputEnabled));
                OnPropertyChanged(nameof(AdatInputSelectable));
            });
            CheckDirty();
        }
        return status;
    }

    /// <summary>Set the ADAT-input RX GPIO (0x6A; 0xFF clears).</summary>
    public byte SetAdatInputPin(byte pin)
    {
        byte before = _adatInputPin;
        var status = _device.SetAdatInputPin(pin);
        if (status == PinConfigResult.Success)
        {
            _adatInputPin = pin;
            if (before != pin) RecordIoUndo(() => SetAdatInputPin(before));
            _dispatcher.TryEnqueue(() =>
            {
                OnPropertyChanged(nameof(AdatInputPin));
                OnPropertyChanged(nameof(AdatInputSelectable));
            });
            CheckDirty();
        }
        return status;
    }

    /// <summary>Set the ADAT-input clock mode (0x6C; 0=master, 1=slave).</summary>
    public byte SetAdatInputClockMode(byte mode)
    {
        byte before = _adatInputClockMode;
        byte want = (byte)(mode == 1 ? 1 : 0);
        var status = _device.SetAdatInputClockMode(want);
        if (status == PinConfigResult.Success)
        {
            _adatInputClockMode = want;
            if (before != want) RecordIoUndo(() => SetAdatInputClockMode(before));
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(AdatInputClockMode)));
            CheckDirty();
        }
        return status;
    }
}
