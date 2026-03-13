using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace DSPiConsole.Usb;

/// <summary>
/// Command IDs for the vendor interface (matching firmware REQ_* defines)
/// These are sent as bRequest in USB control transfers to Interface 2.
/// </summary>
public static class VendorCommands
{
    public const byte SetEqParam = 0x42;
    public const byte GetEqParam = 0x43;
    public const byte SetPreamp = 0x44;
    public const byte GetPreamp = 0x45;
    public const byte SetBypass = 0x46;
    public const byte GetBypass = 0x47;
    public const byte SetDelay = 0x48;
    public const byte GetDelay = 0x49;
    public const byte GetStatus = 0x50;
    public const byte SaveParams = 0x51;
    public const byte LoadParams = 0x52;
    public const byte FactoryReset = 0x53;
    public const byte SetChannelGain = 0x54;
    public const byte GetChannelGain = 0x55;
    public const byte SetChannelMute = 0x56;
    public const byte GetChannelMute = 0x57;
    public const byte SetLoudnessEnabled = 0x58;
    public const byte GetLoudnessEnabled = 0x59;
    public const byte SetLoudnessRefSPL = 0x5A;
    public const byte GetLoudnessRefSPL = 0x5B;
    public const byte SetLoudnessIntensity = 0x5C;
    public const byte GetLoudnessIntensity = 0x5D;
    public const byte SetCrossfeedEnabled = 0x5E;
    public const byte GetCrossfeedEnabled = 0x5F;
    public const byte SetCrossfeedPreset = 0x60;
    public const byte GetCrossfeedPreset = 0x61;
    public const byte SetCrossfeedFreq = 0x62;
    public const byte GetCrossfeedFreq = 0x63;
    public const byte SetCrossfeedFeed = 0x64;
    public const byte GetCrossfeedFeed = 0x65;
    public const byte SetCrossfeedItd = 0x66;
    public const byte GetCrossfeedItd = 0x67;
    public const byte SetMatrixRoute = 0x70;
    public const byte GetMatrixRoute = 0x71;
    public const byte SetOutputEnable = 0x72;
    public const byte GetOutputEnable = 0x73;
    public const byte SetOutputGain = 0x74;
    public const byte GetOutputGain = 0x75;
    public const byte SetOutputMute = 0x76;
    public const byte GetOutputMute = 0x77;
    public const byte SetOutputDelay = 0x78;
    public const byte GetOutputDelay = 0x79;
    public const byte SetOutputPin = 0x7C;
    public const byte GetOutputPin = 0x7D;
    public const byte GetSerial   = 0x7E;
    public const byte GetPlatform = 0x7F;
    public const byte ClearClips = 0x83;
}

/// <summary>
/// Flash operation result codes from firmware.
/// </summary>
public static class FlashResult
{
    public const byte Ok = 0;
    public const byte ErrWrite = 1;
    public const byte ErrNoData = 2;
    public const byte ErrCrc = 3;
}

/// <summary>
/// Pin configuration result codes from firmware.
/// </summary>
public static class PinConfigResult
{
    public const byte Success = 0x00;
    public const byte InvalidPin = 0x01;
    public const byte PinInUse = 0x02;
    public const byte InvalidOutput = 0x03;
    public const byte OutputActive = 0x04;
}

/// <summary>
/// EQ parameter packet structure matching firmware EqParamPacket.
/// Used for control transfer data payload (13 bytes).
/// Channel and band are specified in wValue/wIndex of the setup packet.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EqParamPacket
{
    public byte Type;
    public float Frequency;
    public float Q;
    public float GainDb;

    public const int Size = 13; // 1 + 4 + 4 + 4

    public static EqParamPacket FromFilterParams(FilterParams p) => new()
    {
        Type = (byte)p.Type,
        Frequency = p.Frequency,
        Q = p.Q,
        GainDb = p.Gain
    };

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        bytes[0] = Type;
        BitConverter.GetBytes(Frequency).CopyTo(bytes, 1);
        BitConverter.GetBytes(Q).CopyTo(bytes, 5);
        BitConverter.GetBytes(GainDb).CopyTo(bytes, 9);
        return bytes;
    }

    public static EqParamPacket FromBytes(byte[] data, int offset = 0)
    {
        return new EqParamPacket
        {
            Type = data[offset + 0],
            Frequency = BitConverter.ToSingle(data, offset + 1),
            Q = BitConverter.ToSingle(data, offset + 5),
            GainDb = BitConverter.ToSingle(data, offset + 9)
        };
    }

    public FilterParams ToFilterParams() => new()
    {
        Type = (FilterType)Type,
        Frequency = Frequency,
        Q = Q,
        Gain = GainDb
    };
}

/// <summary>
/// Manages USB communication with the DSPi device using LibUsbDotNet.
/// Uses USB Control Transfers on Interface 2 (vendor-specific, control-only).
/// </summary>
public partial class DspDevice : ObservableObject, IDisposable
{
    // Device identification
    private const int VendorId = 0x2E8A;
    private const int ProductId = 0xFEAA;

    // Interface 2 is the vendor-specific control interface
    private const int VendorInterfaceNumber = 2;

    // USB Request Types (matching Python script)
    // 0x41 = 01000001 (Dir: Host-to-Device | Type: Vendor | Recipient: Interface)
    // 0xC1 = 11000001 (Dir: Device-to-Host | Type: Vendor | Recipient: Interface)
    private const byte RequestTypeOut = 0x41;
    private const byte RequestTypeIn = 0xC1;

    private UsbDevice? _device;
    private readonly object _lock = new();
    private readonly System.Timers.Timer _pollTimer;
    private readonly System.Timers.Timer _statusPollTimer;
    private bool _disposed;

    /// <summary>
    /// Number of audio channels (set after GetDeviceInfo). RP2040=7, RP2350=11.
    /// </summary>
    public int NumChannels { get; set; } = 5; // Legacy default (5 peaks)

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private SystemStatus? _currentStatus;

    public event EventHandler? DeviceConnected;
    public event EventHandler? DeviceDisconnected;
    public event EventHandler<SystemStatus>? StatusUpdated;

    public DspDevice()
    {
        // Poll for device every 500ms
        _pollTimer = new System.Timers.Timer(500);
        _pollTimer.Elapsed += (_, _) => CheckForDevice();
        _pollTimer.AutoReset = true;

        // Poll for status every 100ms when connected
        _statusPollTimer = new System.Timers.Timer(100);
        _statusPollTimer.Elapsed += (_, _) => PollStatus();
        _statusPollTimer.AutoReset = true;
    }

    public void StartMonitoring()
    {
        _pollTimer.Start();
        CheckForDevice();
    }

    public void StopMonitoring()
    {
        _pollTimer.Stop();
    }

    private void CheckForDevice()
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_device != null && IsConnected)
            {
                // Verify the device is still present in the system
                bool stillPresent = UsbDevice.AllDevices
                    .Any(r => r.Vid == VendorId && r.Pid == ProductId);
                if (stillPresent) return;

                // Device was removed
                HandleDisconnect();
                return;
            }

            try
            {
                // First try the standard method
                var finder = new UsbDeviceFinder(VendorId, ProductId);
                _device = UsbDevice.OpenUsbDevice(finder);

                // If that fails, try opening via registry
                if (_device == null)
                {
                    foreach (UsbRegistry reg in UsbDevice.AllDevices)
                    {
                        if (reg.Vid == VendorId && reg.Pid == ProductId)
                        {
                            // Try to open via registry entry
                            if (reg.Open(out _device))
                            {
                                break;
                            }
                            else
                            {
                                // Get more info about why it failed
                                var deviceType = reg.GetType().Name;
                                ErrorMessage = $"Device found ({deviceType}) but Open() failed. Run Zadig, select Interface 2, install WinUSB.";
                            }
                        }
                    }
                }

                if (_device == null)
                {
                    if (IsConnected)
                    {
                        HandleDisconnect();
                    }
                    else if (ErrorMessage == null || ErrorMessage == "Disconnected")
                    {
                        // Only show detailed diagnostics if we've never connected
                        var allDevices = UsbDevice.AllDevices;
                        if (allDevices.Count == 0)
                        {
                            ErrorMessage = "No USB devices visible to LibUsbDotNet. Install libusb-win32 filter driver.";
                        }
                        else
                        {
                            bool found = allDevices.Any(r => r.Vid == VendorId && r.Pid == ProductId);
                            if (!found)
                            {
                                ErrorMessage = "Disconnected";
                            }
                        }
                    }
                    return;
                }

                // For whole USB devices, set configuration
                if (_device is IUsbDevice wholeDevice)
                {
                    wholeDevice.SetConfiguration(1);
                    wholeDevice.ClaimInterface(VendorInterfaceNumber);
                }

                IsConnected = true;
                ErrorMessage = null;

                // Start status polling
                _statusPollTimer.Start();

                DeviceConnected?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error: {ex.Message}";
                _device?.Close();
                _device = null;
            }
        }
    }

    /// <summary>
    /// Poll status using control transfer. Called by timer.
    /// </summary>
    private void PollStatus()
    {
        if (_disposed || !IsConnected) return;

        try
        {
            var status = GetStatus();
            if (status != null)
            {
                CurrentStatus = status;
                StatusUpdated?.Invoke(this, status);
            }
        }
        catch
        {
            // Ignore polling errors
        }
    }

    private SystemStatus ParseStatusResponse(byte[] buffer)
    {
        // Platform-aware status packet:
        // numChannels * uint16 peaks + cpu0(1) + cpu1(1) + clipFlags uint16(2)
        int numCh = NumChannels;
        int peakBytes = numCh * 2;

        var peaks = new float[11]; // Always 11 slots (max channels)
        for (int i = 0; i < numCh && (i * 2 + 1) < buffer.Length; i++)
        {
            peaks[i] = BitConverter.ToUInt16(buffer, i * 2) / 32767.0f;
        }

        int cpuOffset = peakBytes;
        int cpu0 = cpuOffset < buffer.Length ? buffer[cpuOffset] : 0;
        int cpu1 = cpuOffset + 1 < buffer.Length ? buffer[cpuOffset + 1] : 0;

        ushort clipFlags = 0;
        int clipOffset = cpuOffset + 2;
        if (clipOffset + 1 < buffer.Length)
        {
            clipFlags = BitConverter.ToUInt16(buffer, clipOffset);
        }

        return new SystemStatus
        {
            Peaks = peaks,
            Cpu0Load = cpu0,
            Cpu1Load = cpu1,
            ClipFlags = clipFlags
        };
    }

    private void HandleDisconnect()
    {
        _statusPollTimer.Stop();

        var wasConnected = IsConnected;

        _device?.Close();
        _device = null;

        IsConnected = false;

        if (wasConnected)
        {
            ErrorMessage = "Disconnected";
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
    }

    public void Reconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
        CheckForDevice();
    }

    /// <summary>
    /// Send a vendor control OUT transfer (host to device)
    /// </summary>
    private bool ControlTransferOut(byte request, ushort value = 0, byte[]? data = null)
    {
        lock (_lock)
        {
            if (_device == null) return false;

            var setupPacket = new UsbSetupPacket(
                RequestTypeOut,
                request,
                (short)value,
                VendorInterfaceNumber,
                (short)(data?.Length ?? 0));

            int transferred;
            var buffer = data ?? Array.Empty<byte>();

            return _device.ControlTransfer(ref setupPacket, buffer, buffer.Length, out transferred);
        }
    }

    /// <summary>
    /// Send a vendor control IN transfer (device to host)
    /// </summary>
    private byte[]? ControlTransferIn(byte request, ushort value = 0, int length = 4)
    {
        lock (_lock)
        {
            if (_device == null) return null;

            var setupPacket = new UsbSetupPacket(
                RequestTypeIn,
                request,
                (short)value,
                VendorInterfaceNumber,
                (short)length);

            var buffer = new byte[length];
            int transferred;

            if (_device.ControlTransfer(ref setupPacket, buffer, buffer.Length, out transferred))
            {
                if (transferred > 0)
                {
                    if (transferred < length)
                    {
                        var result = new byte[transferred];
                        Array.Copy(buffer, result, transferred);
                        return result;
                    }
                    return buffer;
                }
            }

            return null;
        }
    }

    #region High-Level Commands

    // EQ parameter indices for wValue encoding
    private const int EqParamType = 0;
    private const int EqParamFreq = 1;
    private const int EqParamQ = 2;
    private const int EqParamGain = 3;

    /// <summary>
    /// Encode wValue for EQ parameter access: (channel &lt;&lt; 8) | (band &lt;&lt; 4) | param
    /// </summary>
    private static ushort EncodeEqValue(int channel, int band, int param = 0)
    {
        return (ushort)((channel << 8) | (band << 4) | param);
    }

    /// <summary>
    /// Set EQ filter parameters for a specific channel and band.
    /// Sends 16-byte packet: channel(1), band(1), type(1), reserved(1), freq(4), Q(4), gain(4)
    /// </summary>
    public bool SetFilter(int channel, int band, FilterParams p)
    {
        var data = new byte[16];
        data[0] = (byte)channel;
        data[1] = (byte)band;
        data[2] = (byte)p.Type;
        data[3] = 0; // reserved
        BitConverter.GetBytes(p.Frequency).CopyTo(data, 4);
        BitConverter.GetBytes(p.Q).CopyTo(data, 8);
        BitConverter.GetBytes(p.Gain).CopyTo(data, 12);

        return ControlTransferOut(VendorCommands.SetEqParam, 0, data);
    }

    /// <summary>
    /// Get EQ filter parameters for a specific channel and band.
    /// Reads each parameter individually (4 bytes each) like the Python script.
    /// </summary>
    public FilterParams? GetFilter(int channel, int band)
    {
        // Read type (returned as uint32)
        var typeData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamType), 4);
        if (typeData == null || typeData.Length < 4) return null;
        var type = BitConverter.ToUInt32(typeData, 0);

        // Read frequency (float)
        var freqData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamFreq), 4);
        if (freqData == null || freqData.Length < 4) return null;
        var freq = BitConverter.ToSingle(freqData, 0);

        // Read Q (float)
        var qData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamQ), 4);
        if (qData == null || qData.Length < 4) return null;
        var q = BitConverter.ToSingle(qData, 0);

        // Read gain (float)
        var gainData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamGain), 4);
        if (gainData == null || gainData.Length < 4) return null;
        var gain = BitConverter.ToSingle(gainData, 0);

        return new FilterParams
        {
            Type = (FilterType)type,
            Frequency = freq,
            Q = q,
            Gain = gain
        };
    }

    /// <summary>
    /// Set master preamp gain in dB.
    /// </summary>
    public bool SetPreamp(float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetPreamp, 0, data);
    }

    /// <summary>
    /// Get current master preamp gain in dB.
    /// </summary>
    public float? GetPreamp()
    {
        var response = ControlTransferIn(VendorCommands.GetPreamp, 0, 4);

        if (response == null || response.Length < 4)
            return null;

        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Enable or disable master EQ bypass.
    /// </summary>
    public bool SetBypass(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetBypass, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get current bypass state.
    /// </summary>
    public bool? GetBypass()
    {
        var response = ControlTransferIn(VendorCommands.GetBypass, 0, 1);

        if (response == null || response.Length < 1)
            return null;

        return response[0] != 0;
    }

    /// <summary>
    /// Set delay for a specific channel in milliseconds.
    /// Channel is encoded in wValue.
    /// </summary>
    public bool SetDelay(int channel, float ms)
    {
        var data = BitConverter.GetBytes(ms);
        return ControlTransferOut(VendorCommands.SetDelay, (ushort)channel, data);
    }

    /// <summary>
    /// Get delay for a specific channel in milliseconds.
    /// </summary>
    public float? GetDelay(int channel)
    {
        var response = ControlTransferIn(VendorCommands.GetDelay, (ushort)channel, 4);

        if (response == null || response.Length < 4)
            return null;

        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Get system status (peak levels, CPU load, clip flags).
    /// wValue=9 requests full status. Packet size = numChannels*2 + 2 (CPU) + 2 (clipFlags).
    /// </summary>
    public SystemStatus? GetStatus()
    {
        int packetSize = NumChannels * 2 + 4; // peaks + cpu0 + cpu1 + clipFlags(2)
        var response = ControlTransferIn(VendorCommands.GetStatus, 9, packetSize);

        if (response == null || response.Length < NumChannels * 2 + 2)
            return null;

        return ParseStatusResponse(response);
    }

    /// <summary>
    /// Save current parameters to flash memory.
    /// Returns FlashResult code.
    /// </summary>
    public byte SaveParams()
    {
        var response = ControlTransferIn(VendorCommands.SaveParams, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : FlashResult.ErrWrite;
    }

    /// <summary>
    /// Load parameters from flash memory.
    /// Returns FlashResult code.
    /// </summary>
    public byte LoadParams()
    {
        var response = ControlTransferIn(VendorCommands.LoadParams, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : FlashResult.ErrWrite;
    }

    /// <summary>
    /// Reset all parameters to factory defaults.
    /// Returns FlashResult code.
    /// </summary>
    public byte FactoryReset()
    {
        var response = ControlTransferIn(VendorCommands.FactoryReset, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : FlashResult.ErrWrite;
    }

    /// <summary>
    /// Set output channel gain in dB. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public bool SetChannelGain(int outputChannel, float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetChannelGain, (ushort)outputChannel, data);
    }

    /// <summary>
    /// Get output channel gain in dB. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public float? GetChannelGain(int outputChannel)
    {
        var response = ControlTransferIn(VendorCommands.GetChannelGain, (ushort)outputChannel, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set output channel mute state. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public bool SetChannelMute(int outputChannel, bool muted)
    {
        return ControlTransferOut(VendorCommands.SetChannelMute, (ushort)outputChannel, new[] { (byte)(muted ? 1 : 0) });
    }

    /// <summary>
    /// Get output channel mute state. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public bool? GetChannelMute(int outputChannel)
    {
        var response = ControlTransferIn(VendorCommands.GetChannelMute, (ushort)outputChannel, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set loudness compensation enabled state.
    /// </summary>
    public bool SetLoudnessEnabled(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetLoudnessEnabled, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get loudness compensation enabled state.
    /// </summary>
    public bool? GetLoudnessEnabled()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessEnabled, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set loudness reference SPL (40-100 dB, default 83).
    /// </summary>
    public bool SetLoudnessRefSPL(float spl)
    {
        var data = BitConverter.GetBytes(spl);
        return ControlTransferOut(VendorCommands.SetLoudnessRefSPL, 0, data);
    }

    /// <summary>
    /// Get loudness reference SPL.
    /// </summary>
    public float? GetLoudnessRefSPL()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessRefSPL, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set loudness intensity (0-200%, default 100).
    /// </summary>
    public bool SetLoudnessIntensity(float intensity)
    {
        var data = BitConverter.GetBytes(intensity);
        return ControlTransferOut(VendorCommands.SetLoudnessIntensity, 0, data);
    }

    /// <summary>
    /// Get loudness intensity.
    /// </summary>
    public float? GetLoudnessIntensity()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessIntensity, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set crossfeed enabled state.
    /// </summary>
    public bool SetCrossfeedEnabled(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetCrossfeedEnabled, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get crossfeed enabled state.
    /// </summary>
    public bool? GetCrossfeedEnabled()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedEnabled, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set crossfeed preset (0=Default, 1=Chu Moy, 2=Jan Meier, 3=Custom).
    /// </summary>
    public bool SetCrossfeedPreset(int preset)
    {
        return ControlTransferOut(VendorCommands.SetCrossfeedPreset, 0, new[] { (byte)preset });
    }

    /// <summary>
    /// Get crossfeed preset.
    /// </summary>
    public int? GetCrossfeedPreset()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedPreset, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Set crossfeed cutoff frequency in Hz (500-2000).
    /// </summary>
    public bool SetCrossfeedFreq(float freq)
    {
        var data = BitConverter.GetBytes(freq);
        return ControlTransferOut(VendorCommands.SetCrossfeedFreq, 0, data);
    }

    /// <summary>
    /// Get crossfeed cutoff frequency.
    /// </summary>
    public float? GetCrossfeedFreq()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedFreq, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set crossfeed feed level in dB (0-15).
    /// </summary>
    public bool SetCrossfeedFeed(float feed)
    {
        var data = BitConverter.GetBytes(feed);
        return ControlTransferOut(VendorCommands.SetCrossfeedFeed, 0, data);
    }

    /// <summary>
    /// Get crossfeed feed level.
    /// </summary>
    public float? GetCrossfeedFeed()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedFeed, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set interaural time delay (ITD) enabled state.
    /// </summary>
    public bool SetCrossfeedItd(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetCrossfeedItd, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get interaural time delay enabled state.
    /// </summary>
    public bool? GetCrossfeedItd()
    {
        var response = ControlTransferIn(VendorCommands.GetCrossfeedItd, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Get a 4-byte unsigned status value. wValue selects the stat type.
    /// </summary>
    public uint? GetStatusUInt32(ushort wValue)
    {
        var response = ControlTransferIn(VendorCommands.GetStatus, wValue, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToUInt32(response, 0);
    }

    /// <summary>
    /// Get a 4-byte signed status value. wValue selects the stat type.
    /// </summary>
    public int? GetStatusInt32(ushort wValue)
    {
        var response = ControlTransferIn(VendorCommands.GetStatus, wValue, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToInt32(response, 0);
    }

    /// <summary>
    /// Set a matrix route: enabled, invert, and gain for a given input/output pair.
    /// 9-byte packet: input(1), output(1), enabled(1), invert(1), gain(4), pad(1).
    /// </summary>
    public bool SetMatrixRoute(int input, int output, bool enabled, bool invert, float gain)
    {
        var data = new byte[9];
        data[0] = (byte)input;
        data[1] = (byte)output;
        data[2] = (byte)(enabled ? 1 : 0);
        data[3] = (byte)(invert ? 1 : 0);
        BitConverter.GetBytes(gain).CopyTo(data, 4);
        data[8] = 0; // pad
        return ControlTransferOut(VendorCommands.SetMatrixRoute, 0, data);
    }

    /// <summary>
    /// Get a matrix route. wValue = (input &lt;&lt; 8) | output. Returns 9-byte response.
    /// </summary>
    public (bool enabled, bool invert, float gain)? GetMatrixRoute(int input, int output)
    {
        ushort wValue = (ushort)((input << 8) | output);
        var response = ControlTransferIn(VendorCommands.GetMatrixRoute, wValue, 9);
        if (response == null || response.Length < 8) return null;
        bool enabled = response[2] != 0;
        bool invert = response[3] != 0;
        float gain = BitConverter.ToSingle(response, 4);
        return (enabled, invert, gain);
    }

    /// <summary>
    /// Set output enable state. wValue = output index.
    /// </summary>
    public bool SetOutputEnable(int output, bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetOutputEnable, (ushort)output,
            new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get output enable state. wValue = output index.
    /// </summary>
    public bool? GetOutputEnable(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputEnable, (ushort)output, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set output gain in dB (matrix mixer output gain). wValue = output index.
    /// </summary>
    public bool SetOutputGain(int output, float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetOutputGain, (ushort)output, data);
    }

    /// <summary>
    /// Get output gain in dB (matrix mixer output gain). wValue = output index.
    /// </summary>
    public float? GetOutputGain(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputGain, (ushort)output, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set output mute state (matrix mixer). wValue = output index.
    /// </summary>
    public bool SetOutputMute(int output, bool muted)
    {
        return ControlTransferOut(VendorCommands.SetOutputMute, (ushort)output,
            new[] { (byte)(muted ? 1 : 0) });
    }

    /// <summary>
    /// Get output mute state (matrix mixer). wValue = output index.
    /// </summary>
    public bool? GetOutputMute(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputMute, (ushort)output, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set output delay in ms (matrix mixer). wValue = output index.
    /// </summary>
    public bool SetOutputDelay(int output, float ms)
    {
        var data = BitConverter.GetBytes(ms);
        return ControlTransferOut(VendorCommands.SetOutputDelay, (ushort)output, data);
    }

    /// <summary>
    /// Get output delay in ms (matrix mixer). wValue = output index.
    /// </summary>
    public float? GetOutputDelay(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputDelay, (ushort)output, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    public string? GetDeviceSerial()
    {
        var response = ControlTransferIn(VendorCommands.GetSerial, 0, 16);
        if (response == null || response.Length < 1) return null;
        return System.Text.Encoding.ASCII.GetString(response).TrimEnd('\0');
    }

    /// <summary>
    /// Clear clip flags on the device.
    /// </summary>
    public void ClearClips()
    {
        ControlTransferIn(VendorCommands.ClearClips, 0, 2);
    }

    public (string Platform, string FirmwareVersion)? GetDeviceInfo()
    {
        var response = ControlTransferIn(VendorCommands.GetPlatform, 0, 4);
        if (response == null || response.Length < 3) return null;
        var platform = response[0] == 1 ? "RP2350" : "RP2040";
        var major = response[1];
        var minor = response[2] >> 4;
        var patch = response[2] & 0x0F;
        return (platform, $"v{major}.{minor}.{patch}");
    }

    /// <summary>
    /// Set output pin assignment. wValue = (pin &lt;&lt; 8) | outputIndex.
    /// Returns status byte (PinConfigResult), or 0xFF on transfer failure.
    /// </summary>
    public byte SetOutputPin(int output, byte pin)
    {
        ushort wValue = (ushort)((pin << 8) | output);
        var response = ControlTransferIn(VendorCommands.SetOutputPin, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current GPIO pin for an output. wValue = outputIndex.
    /// Returns pin number, or null on failure.
    /// </summary>
    public byte? GetOutputPin(int output)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputPin, (ushort)output, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Dispose();
        _statusPollTimer.Stop();
        _statusPollTimer.Dispose();
        Disconnect();

        GC.SuppressFinalize(this);
    }
}
