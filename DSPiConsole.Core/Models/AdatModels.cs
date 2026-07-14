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
