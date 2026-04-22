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
    public const byte GetAllParams = 0xA0;

    // Buffer statistics (firmware v3+)
    public const byte GetBufferStats  = 0xB0;
    public const byte ResetBufferStats = 0xB1;

    // Preset system (firmware v3+)
    public const byte PresetSave           = 0x90;
    public const byte PresetLoad           = 0x91;
    public const byte PresetDelete         = 0x92;
    public const byte PresetGetName        = 0x93;
    public const byte PresetSetName        = 0x94;
    public const byte PresetGetDir         = 0x95;
    public const byte PresetSetStartup     = 0x96;
    public const byte PresetGetStartup     = 0x97;
    public const byte PresetSetIncludePins = 0x98;
    public const byte PresetGetIncludePins = 0x99;
    public const byte PresetGetActive      = 0x9A;
    public const byte SetChannelName       = 0x9B;
    public const byte GetChannelName       = 0x9C;

    // I2S output configuration
    public const byte SetOutputType    = 0xC0;
    public const byte GetOutputType    = 0xC1;
    public const byte SetI2SBckPin     = 0xC2;
    public const byte GetI2SBckPin     = 0xC3;
    public const byte SetMckEnable     = 0xC4;
    public const byte GetMckEnable     = 0xC5;
    public const byte SetMckPin        = 0xC6;
    public const byte GetMckPin        = 0xC7;
    public const byte SetMckMultiplier = 0xC8;
    public const byte GetMckMultiplier = 0xC9;

    // Volume leveller
    public const byte SetLevellerEnabled   = 0xB4;
    public const byte GetLevellerEnabled   = 0xB5;
    public const byte SetLevellerAmount    = 0xB6;
    public const byte GetLevellerAmount    = 0xB7;
    public const byte SetLevellerSpeed     = 0xB8;
    public const byte GetLevellerSpeed     = 0xB9;
    public const byte SetLevellerMaxGain   = 0xBA;
    public const byte GetLevellerMaxGain   = 0xBB;
    public const byte SetLevellerLookahead = 0xBC;
    public const byte GetLevellerLookahead = 0xBD;
    public const byte SetLevellerGate      = 0xBE;
    public const byte GetLevellerGate      = 0xBF;

    // Bootloader
    public const byte EnterBootloader = 0xF0;
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
/// Preset operation result codes from firmware.
/// </summary>
public static class PresetResult
{
    public const byte Ok = 0x00;
    public const byte InvalidSlot = 0x01;
    public const byte SlotEmpty = 0x02;
    public const byte CrcFailure = 0x03;
    public const byte FlashWriteError = 0x04;
}

/// <summary>
/// Output slot type: S/PDIF or I2S.
/// </summary>
public enum OutputSlotType : byte
{
    Spdif = 0,
    I2S = 1
}

/// <summary>
/// Directory info returned by PresetGetDir (0x95): 6 bytes (legacy) or 7 bytes (V12+).
/// </summary>
public struct PresetDirectoryInfo
{
    public ushort OccupiedMask;   // bit N = slot N occupied
    public byte StartupMode;     // 0=last used, 1=specific slot, 2=factory defaults
    public byte DefaultSlot;
    public byte LastActiveSlot;   // 0xFF if none
    public bool IncludePins;
}

/// <summary>
/// Identifies a discovered DSPi device without holding an open handle.
/// </summary>
public record DSPiDeviceInfo(string Serial, string DevicePath)
{
    public string DisplayName => Serial.Length >= 8 ? $"DSPi ({Serial[^8..]})" : "DSPi";
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

    // Multi-device tracking
    private List<DSPiDeviceInfo> _availableDevices = new();
    private DSPiDeviceInfo? _selectedDeviceInfo;
    private string? _lastSelectedSerial;
    private string? _openDeviceSerial; // serial of the currently open _device handle

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

    /// <summary>All currently connected DSPi devices.</summary>
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevicesList => _availableDevices;

    /// <summary>The currently selected/active device.</summary>
    public DSPiDeviceInfo? SelectedDeviceInfo
    {
        get => _selectedDeviceInfo;
        private set
        {
            if (_selectedDeviceInfo == value) return;
            _selectedDeviceInfo = value;
            OnPropertyChanged(nameof(SelectedDeviceInfo));
        }
    }

    public event EventHandler? DeviceConnected;
    public event EventHandler? DeviceDisconnected;
    public event EventHandler<SystemStatus>? StatusUpdated;
    public event EventHandler? AvailableDevicesChanged;

    public DspDevice()
    {
        // Poll for devices every 500ms
        _pollTimer = new System.Timers.Timer(500);
        _pollTimer.Elapsed += (_, _) => ScanDevices();
        _pollTimer.AutoReset = true;

        // Poll for status every 100ms when connected
        _statusPollTimer = new System.Timers.Timer(100);
        _statusPollTimer.Elapsed += (_, _) => PollStatus();
        _statusPollTimer.AutoReset = true;
    }

    public void StartMonitoring()
    {
        _pollTimer.Start();
        ScanDevices();
    }

    public void StopMonitoring()
    {
        _pollTimer.Stop();
    }

    /// <summary>
    /// Read the serial number from a temporarily opened USB device.
    /// </summary>
    private static string? ReadSerialFromDevice(UsbDevice tempDevice)
    {
        var setupPacket = new UsbSetupPacket(RequestTypeIn, VendorCommands.GetSerial, 0, VendorInterfaceNumber, 16);
        var buffer = new byte[16];
        if (tempDevice.ControlTransfer(ref setupPacket, buffer, 16, out int transferred) && transferred > 0)
            return System.Text.Encoding.ASCII.GetString(buffer, 0, transferred).TrimEnd('\0');
        return null;
    }

    /// <summary>
    /// Scan for all connected DSPi devices, update the available list,
    /// and auto-select/reconnect as needed.
    /// </summary>
    private void ScanDevices()
    {
        if (_disposed) return;

        try
        {
            // Phase 1: Collect all matching USB registry entries
            var allRegs = UsbDevice.AllDevices
                .Cast<UsbRegistry>()
                .Where(r => r.Vid == VendorId && r.Pid == ProductId)
                .ToList();

            // Phase 2: Build current device list with serials
            var currentDevices = new List<DSPiDeviceInfo>();
            bool addedOpenDevice = false;
            foreach (var reg in allRegs)
            {
                var devicePath = reg.SymbolicName ?? reg.DevicePath ?? "";

                // Try briefly opening to read serial
                try
                {
                    if (reg.Open(out var tempDevice))
                    {
                        try
                        {
                            if (tempDevice is IUsbDevice wholeTmp)
                            {
                                wholeTmp.SetConfiguration(1);
                                wholeTmp.ClaimInterface(VendorInterfaceNumber);
                            }
                            var serial = ReadSerialFromDevice(tempDevice);
                            if (!string.IsNullOrEmpty(serial))
                            {
                                currentDevices.Add(new DSPiDeviceInfo(serial, devicePath));
                            }
                        }
                        finally
                        {
                            if (tempDevice is IUsbDevice wholeTmp2)
                                wholeTmp2.ReleaseInterface(VendorInterfaceNumber);
                            tempDevice.Close();
                        }
                    }
                    else if (_device != null && IsConnected && _selectedDeviceInfo != null && !addedOpenDevice)
                    {
                        // Open failed — likely because this is our already-open device
                        currentDevices.Add(_selectedDeviceInfo);
                        addedOpenDevice = true;
                    }
                }
                catch
                {
                    // Device busy — if we have an open device, assume it's this one
                    if (_device != null && IsConnected && _selectedDeviceInfo != null && !addedOpenDevice)
                    {
                        currentDevices.Add(_selectedDeviceInfo);
                        addedOpenDevice = true;
                    }
                }
            }

            // Phase 3: Detect changes
            var oldSerials = _availableDevices.Select(d => d.Serial).ToHashSet();
            var newSerials = currentDevices.Select(d => d.Serial).ToHashSet();
            bool listChanged = !oldSerials.SetEquals(newSerials);

            if (listChanged)
            {
                _availableDevices = currentDevices;
                AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
            }

            // Phase 4: Handle selected device removal
            if (_selectedDeviceInfo != null && !newSerials.Contains(_selectedDeviceInfo.Serial))
            {
                lock (_lock)
                {
                    HandleDisconnect();
                }
                // Preserve serial for auto-reconnect but clear selection if no devices left
                if (currentDevices.Count == 0)
                {
                    SelectedDeviceInfo = null;
                }
            }

            // Phase 5: Verify currently connected device is still present
            if (_device != null && IsConnected)
            {
                if (!allRegs.Any())
                {
                    lock (_lock) { HandleDisconnect(); }
                }
                return; // Already connected to a device, no auto-select needed
            }

            // Phase 6: Auto-select / auto-reconnect
            if (_device == null && currentDevices.Count > 0)
            {
                // Try auto-reconnect to previously selected device
                var reconnectTarget = _lastSelectedSerial != null
                    ? currentDevices.FirstOrDefault(d => d.Serial == _lastSelectedSerial)
                    : null;

                var target = reconnectTarget ?? currentDevices[0];
                OpenDevice(target);
            }
            else if (currentDevices.Count == 0)
            {
                if (ErrorMessage == null || ErrorMessage == "Disconnected")
                {
                    var allDevices = UsbDevice.AllDevices;
                    if (allDevices.Count == 0)
                        ErrorMessage = "No USB devices visible to LibUsbDotNet. Install libusb-win32 filter driver.";
                    else
                        ErrorMessage = "Disconnected";
                }
            }
        }
        catch (Exception ex)
        {
            // Don't let scan errors kill the timer
            System.Diagnostics.Debug.WriteLine($"ScanDevices error: {ex.Message}");
        }
    }

    /// <summary>
    /// Open and connect to a specific device by its info.
    /// </summary>
    private void OpenDevice(DSPiDeviceInfo deviceInfo)
    {
        lock (_lock)
        {
            try
            {
                // Close any existing connection
                if (_device != null)
                {
                    HandleDisconnect();
                }

                // Find and open the device matching this serial
                UsbDevice? opened = null;
                foreach (UsbRegistry reg in UsbDevice.AllDevices)
                {
                    if (reg.Vid != VendorId || reg.Pid != ProductId) continue;

                    if (reg.Open(out var tempDevice))
                    {
                        if (tempDevice is IUsbDevice wholeTmp)
                        {
                            wholeTmp.SetConfiguration(1);
                            wholeTmp.ClaimInterface(VendorInterfaceNumber);
                        }

                        var serial = ReadSerialFromDevice(tempDevice);
                        if (serial == deviceInfo.Serial)
                        {
                            opened = tempDevice;
                            break;
                        }

                        // Not the one we want, close it
                        if (tempDevice is IUsbDevice wholeTmp2)
                            wholeTmp2.ReleaseInterface(VendorInterfaceNumber);
                        tempDevice.Close();
                    }
                }

                if (opened == null)
                {
                    ErrorMessage = "Failed to open device";
                    return;
                }

                _device = opened;
                _openDeviceSerial = deviceInfo.Serial;
                _selectedDeviceInfo = deviceInfo;
                _lastSelectedSerial = deviceInfo.Serial;
                SelectedDeviceInfo = deviceInfo;

                IsConnected = true;
                ErrorMessage = null;

                _statusPollTimer.Start();
                DeviceConnected?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error: {ex.Message}";
                _device?.Close();
                _device = null;
                _openDeviceSerial = null;
            }
        }
    }

    /// <summary>
    /// Switch to a different connected device. Called from ViewModel after unsaved changes check.
    /// </summary>
    public void SelectDevice(DSPiDeviceInfo device)
    {
        if (device.Serial == _openDeviceSerial && IsConnected) return;
        OpenDevice(device);
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
        _openDeviceSerial = null;

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
        ScanDevices();
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

    #region I2S Configuration

    /// <summary>
    /// Set output slot type (S/PDIF or I2S). wValue = (type &lt;&lt; 8) | slot.
    /// Returns status byte (PinConfigResult codes), or 0xFF on transfer failure.
    /// </summary>
    public byte SetOutputType(int slot, OutputSlotType type)
    {
        ushort wValue = (ushort)(((byte)type << 8) | slot);
        var response = ControlTransferIn(VendorCommands.SetOutputType, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current output type for a slot. wValue = slot index.
    /// </summary>
    public OutputSlotType? GetOutputType(int slot)
    {
        var response = ControlTransferIn(VendorCommands.GetOutputType, (ushort)slot, 1);
        if (response == null || response.Length < 1) return null;
        return (OutputSlotType)response[0];
    }

    /// <summary>
    /// Set I2S BCK (bit clock) pin. LRCLK is always BCK + 1.
    /// Returns status byte, or 0xFF on transfer failure.
    /// </summary>
    public byte SetI2SBckPin(byte pin)
    {
        var response = ControlTransferIn(VendorCommands.SetI2SBckPin, pin, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current I2S BCK pin number, or null on failure.
    /// </summary>
    public byte? GetI2SBckPin()
    {
        var response = ControlTransferIn(VendorCommands.GetI2SBckPin, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Enable or disable master clock (MCK) output.
    /// Returns status byte, or 0xFF on transfer failure.
    /// </summary>
    public byte SetMckEnable(bool enabled)
    {
        ushort wValue = (ushort)(enabled ? 1 : 0);
        var response = ControlTransferIn(VendorCommands.SetMckEnable, wValue, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get whether master clock (MCK) is enabled, or null on failure.
    /// </summary>
    public bool? GetMckEnable()
    {
        var response = ControlTransferIn(VendorCommands.GetMckEnable, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set MCK GPIO pin. MCK must be disabled first.
    /// Returns status byte, or 0xFF on transfer failure.
    /// </summary>
    public byte SetMckPin(byte pin)
    {
        var response = ControlTransferIn(VendorCommands.SetMckPin, pin, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current MCK GPIO pin, or null on failure.
    /// </summary>
    public byte? GetMckPin()
    {
        var response = ControlTransferIn(VendorCommands.GetMckPin, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    /// <summary>
    /// Set MCK multiplier. Wire encoding: 0 = 128x, 1 = 256x.
    /// Returns status byte, or 0xFF on transfer failure.
    /// </summary>
    public byte SetMckMultiplier(int multiplier)
    {
        var response = ControlTransferIn(VendorCommands.SetMckMultiplier, (ushort)multiplier, 1);
        return response != null && response.Length >= 1 ? response[0] : (byte)0xFF;
    }

    /// <summary>
    /// Get current MCK multiplier (128 or 256), or null on failure.
    /// </summary>
    public int? GetMckMultiplier()
    {
        var response = ControlTransferIn(VendorCommands.GetMckMultiplier, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] == 1 ? 256 : 128;
    }

    #endregion

    #region Volume Leveller

    public bool SetLevellerEnabled(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetLevellerEnabled, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    public bool? GetLevellerEnabled()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerEnabled, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    public bool SetLevellerAmount(float amount)
    {
        return ControlTransferOut(VendorCommands.SetLevellerAmount, 0, BitConverter.GetBytes(amount));
    }

    public float? GetLevellerAmount()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerAmount, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    public bool SetLevellerSpeed(int speed)
    {
        return ControlTransferOut(VendorCommands.SetLevellerSpeed, 0, new[] { (byte)speed });
    }

    public int? GetLevellerSpeed()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerSpeed, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0];
    }

    public bool SetLevellerMaxGain(float db)
    {
        return ControlTransferOut(VendorCommands.SetLevellerMaxGain, 0, BitConverter.GetBytes(db));
    }

    public float? GetLevellerMaxGain()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerMaxGain, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    public bool SetLevellerLookahead(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetLevellerLookahead, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    public bool? GetLevellerLookahead()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerLookahead, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    public bool SetLevellerGate(float db)
    {
        return ControlTransferOut(VendorCommands.SetLevellerGate, 0, BitConverter.GetBytes(db));
    }

    public float? GetLevellerGate()
    {
        var response = ControlTransferIn(VendorCommands.GetLevellerGate, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    #endregion

    /// <summary>
    /// Reboot the device into UF2 bootloader mode. Device disconnects immediately.
    /// </summary>
    public void EnterBootloaderMode()
    {
        ControlTransferIn(VendorCommands.EnterBootloader, 0, 1);
    }

    /// <summary>
    /// Fetch all DSP parameters in a single bulk transfer (firmware v2+).
    /// Returns up to 2896-byte packet, or null if unsupported/failed.
    /// </summary>
    public byte[]? GetAllParams()
    {
        return ControlTransferIn(VendorCommands.GetAllParams, 0, 2896);
    }

    #region Buffer Statistics

    /// <summary>
    /// Fetch the 44-byte buffer statistics snapshot (REQ_GET_BUFFER_STATS 0xB0).
    /// </summary>
    public BufferStatsPacket? GetBufferStats()
    {
        var response = ControlTransferIn(VendorCommands.GetBufferStats, 0, BufferStatsPacket.PacketSize);
        return response != null ? BufferStatsPacket.Parse(response) : null;
    }

    /// <summary>
    /// Reset buffer statistics watermarks (REQ_RESET_BUFFER_STATS 0xB1).
    /// wValue bit 0 = reset watermarks.
    /// </summary>
    public bool ResetBufferStats()
    {
        var response = ControlTransferIn(VendorCommands.ResetBufferStats, 1, 1);
        return response != null && response.Length >= 1 && response[0] == 0x01;
    }

    #endregion

    #region Preset Commands

    /// <summary>
    /// Save current parameters to a preset slot (0-9).
    /// Returns PresetResult code.
    /// </summary>
    public byte SavePreset(int slot)
    {
        var response = ControlTransferIn(VendorCommands.PresetSave, (ushort)slot, 1);
        return response != null && response.Length >= 1 ? response[0] : PresetResult.FlashWriteError;
    }

    /// <summary>
    /// Load a preset slot (0-9) into active parameters.
    /// Returns PresetResult code.
    /// </summary>
    public byte LoadPreset(int slot)
    {
        var response = ControlTransferIn(VendorCommands.PresetLoad, (ushort)slot, 1);
        return response != null && response.Length >= 1 ? response[0] : PresetResult.FlashWriteError;
    }

    /// <summary>
    /// Delete a preset slot (0-9).
    /// Returns PresetResult code.
    /// </summary>
    public byte DeletePreset(int slot)
    {
        var response = ControlTransferIn(VendorCommands.PresetDelete, (ushort)slot, 1);
        return response != null && response.Length >= 1 ? response[0] : PresetResult.FlashWriteError;
    }

    /// <summary>
    /// Set the name for a preset slot. Max 31 chars (32-byte UTF-8 buffer, null-terminated).
    /// </summary>
    public bool SetPresetName(int slot, string name)
    {
        var data = new byte[32];
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        Array.Copy(bytes, data, Math.Min(bytes.Length, 31));
        return ControlTransferOut(VendorCommands.PresetSetName, (ushort)slot, data);
    }

    /// <summary>
    /// Get the name for a preset slot. Returns null on failure.
    /// </summary>
    public string? GetPresetName(int slot)
    {
        var response = ControlTransferIn(VendorCommands.PresetGetName, (ushort)slot, 32);
        if (response == null || response.Length < 1) return null;
        return System.Text.Encoding.UTF8.GetString(response).TrimEnd('\0');
    }

    /// <summary>
    /// Get the currently active preset slot. Returns -1 if no preset is active.
    /// </summary>
    public int GetActivePreset()
    {
        var response = ControlTransferIn(VendorCommands.PresetGetActive, 0, 1);
        if (response == null || response.Length < 1) return -1;
        return response[0] == 0xFF ? -1 : response[0];
    }

    /// <summary>
    /// Get the full preset directory: occupied mask, startup config, last active, include-pins.
    /// GET_DIR (0x95) returns 7 bytes on V12+ firmware (adds include_master_volume at byte 6)
    /// and 6 bytes on earlier firmware. Request 7 so newer firmware doesn't overflow the
    /// host's buffer (WinUSB treats a device-overrun as a babble error and fails the transfer).
    /// </summary>
    public PresetDirectoryInfo? GetPresetDirectory()
    {
        var response = ControlTransferIn(VendorCommands.PresetGetDir, 0, 7);
        if (response == null || response.Length < 6) return null;
        return new PresetDirectoryInfo
        {
            OccupiedMask = BitConverter.ToUInt16(response, 0),
            StartupMode = response[2],
            DefaultSlot = response[3],
            LastActiveSlot = response[4],
            IncludePins = response[5] != 0
        };
    }

    /// <summary>
    /// Set preset startup mode and default slot.
    /// Mode: 0=last used, 1=specific slot, 2=factory defaults.
    /// </summary>
    public bool SetPresetStartup(byte mode, byte defaultSlot)
    {
        return ControlTransferOut(VendorCommands.PresetSetStartup, 0, new[] { mode, defaultSlot });
    }

    /// <summary>
    /// Set whether pin assignments are included in presets.
    /// </summary>
    public bool SetPresetIncludePins(bool include)
    {
        return ControlTransferOut(VendorCommands.PresetSetIncludePins, 0, new[] { (byte)(include ? 1 : 0) });
    }

    /// <summary>
    /// Clear all presets by deleting each slot individually.
    /// Returns PresetResult code (first failure, or Ok if all succeed).
    /// </summary>
    public byte ClearAllPresets()
    {
        for (int i = 0; i < 10; i++)
        {
            var result = DeletePreset(i);
            if (result != PresetResult.Ok && result != PresetResult.SlotEmpty)
                return result;
            // Firmware defers each delete to its main loop (~45ms flash erase
            // with interrupts disabled). Pacing avoids ramming the next control
            // transfer into a USB blackout window, which otherwise shows up as
            // a transport failure even though every slot still gets cleared.
            if (i < 9) System.Threading.Thread.Sleep(50);
        }
        return PresetResult.Ok;
    }

    #endregion

    #region Channel Name Commands

    /// <summary>
    /// Set a channel name on the device. wValue = channel index, 32-byte UTF-8 buffer.
    /// </summary>
    public bool SetChannelNameOnDevice(int channel, string name)
    {
        var data = new byte[32];
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        Array.Copy(bytes, data, Math.Min(bytes.Length, 31));
        return ControlTransferOut(VendorCommands.SetChannelName, (ushort)channel, data);
    }

    /// <summary>
    /// Get a channel name from the device. wValue = channel index. Returns null on failure.
    /// </summary>
    public string? GetChannelNameFromDevice(int channel)
    {
        var response = ControlTransferIn(VendorCommands.GetChannelName, (ushort)channel, 32);
        if (response == null || response.Length < 1) return null;
        return System.Text.Encoding.UTF8.GetString(response).TrimEnd('\0');
    }

    #endregion

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
