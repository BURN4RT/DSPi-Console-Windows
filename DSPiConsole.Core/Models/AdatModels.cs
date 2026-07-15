using System;

namespace DSPiConsole.Core.Models;

/// <summary>
/// The 8-byte AdatStatus wire struct (REQ_GET_ADAT_STATUS, 0xCE). Little-endian.
/// Reports the live state of the optical ADAT "bulk" output. On RP2040 the
/// firmware returns all zeros (feature unsupported).
/// </summary>
public sealed class AdatStatus
{
    public const int WireSize = 8;

    public bool Enabled;        // [0] configured enable (persisted intent)
    public bool Active;         // [1] stream currently running
    public byte Pin;            // [2] configured data GPIO
    public bool RateOk;         // [3] current sample rate is 44.1/48 kHz
    public ushort ResyncCount;  // [4] stream restarts since boot
    public ushort SlipCount;    // [6] emergency local resyncs since boot (should stay 0)

    /// <summary>Enabled but not streaming because the sample rate is &gt; 48 kHz —
    /// the stream auto-suspends and resumes when the rate returns to 44.1/48 kHz.</summary>
    public bool Suspended => Enabled && !Active && !RateOk;

    public static AdatStatus? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new AdatStatus
        {
            Enabled = d[0] != 0,
            Active = d[1] != 0,
            Pin = d[2],
            RateOk = d[3] != 0,
            ResyncCount = BitConverter.ToUInt16(d, 4),
            SlipCount = BitConverter.ToUInt16(d, 6),
        };
    }
}

/// <summary>ADAT input lock state (AdatInputStatusPacket.state).</summary>
public enum AdatInputState : byte
{
    Inactive = 0, Acquiring = 1, Syncing = 2, Locked = 3, Relocking = 4
}

/// <summary>
/// The 20-byte AdatInputStatusPacket (REQ_GET_ADAT_INPUT_STATUS, 0x6E). Live state
/// of the 8-channel ADAT optical INPUT (RP2350 only; all zeros on RP2040). Distinct
/// from <see cref="AdatStatus"/>, which is the ADAT output.
/// </summary>
public sealed class AdatInputStatus
{
    public const int WireSize = 20;
    public const byte PinUnset = 0xFF;

    public AdatInputState State;  // [0]
    public byte ClockMode;        // [1] 0=master, 1=slave
    public bool Enabled;          // [2]
    public byte Pin;              // [3] 0xFF = unset
    public bool RateOk;           // [4]
    public byte LockCount;        // [5] saturating
    public byte LossCount;        // [6] saturating
    public byte SlipCount;        // [7]
    public ushort HeaderErrors;   // [8]
    public uint DetectedRate;     // [12] Hz
    public uint MeasuredHz;       // [16] slave raw; 0 in master

    public bool IsSlave => ClockMode == 1;
    public bool IsLocked => State == AdatInputState.Locked;

    public string StateText => State switch
    {
        AdatInputState.Inactive => "Inactive",
        AdatInputState.Acquiring => "Acquiring",
        AdatInputState.Syncing => "Syncing",
        AdatInputState.Locked => "Locked",
        AdatInputState.Relocking => "Relocking",
        _ => "Unknown"
    };

    public string DetectedRateText => DetectedRate > 0 ? $"{DetectedRate / 1000.0:0.0} kHz" : "—";

    public static AdatInputStatus? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new AdatInputStatus
        {
            State = (AdatInputState)d[0],
            ClockMode = d[1],
            Enabled = d[2] != 0,
            Pin = d[3],
            RateOk = d[4] != 0,
            LockCount = d[5],
            LossCount = d[6],
            SlipCount = d[7],
            HeaderErrors = BitConverter.ToUInt16(d, 8),
            DetectedRate = BitConverter.ToUInt32(d, 12),
            MeasuredHz = BitConverter.ToUInt32(d, 16),
        };
    }
}
