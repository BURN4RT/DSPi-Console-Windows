namespace DSPiConsole.Core.Models;

/// <summary>
/// Per-instance SPDIF consumer buffer statistics (8 bytes on wire).
/// </summary>
public struct SpdifBufferStats
{
    /// <summary>Number of free buffers (0-4).</summary>
    public byte ConsumerFree { get; set; }

    /// <summary>Number of prepared (queued) buffers (0-4).</summary>
    public byte ConsumerPrepared { get; set; }

    /// <summary>Number of buffers currently playing (0-1).</summary>
    public byte ConsumerPlaying { get; set; }

    /// <summary>Current consumer fill percentage (0-100).</summary>
    public byte ConsumerFillPct { get; set; }

    /// <summary>Lowest observed fill percentage since last reset.</summary>
    public byte ConsumerMinFillPct { get; set; }

    /// <summary>Highest observed fill percentage since last reset.</summary>
    public byte ConsumerMaxFillPct { get; set; }

    public static SpdifBufferStats Parse(byte[] data, int offset)
    {
        return new SpdifBufferStats
        {
            ConsumerFree = data[offset],
            ConsumerPrepared = data[offset + 1],
            ConsumerPlaying = data[offset + 2],
            ConsumerFillPct = data[offset + 3],
            ConsumerMinFillPct = data[offset + 4],
            ConsumerMaxFillPct = data[offset + 5],
        };
    }
}

/// <summary>
/// PDM buffer statistics (8 bytes on wire).
/// </summary>
public struct PdmBufferStats
{
    public byte DmaFillPct { get; set; }
    public byte DmaMinFillPct { get; set; }
    public byte DmaMaxFillPct { get; set; }
    public byte RingFillPct { get; set; }
    public byte RingMinFillPct { get; set; }
    public byte RingMaxFillPct { get; set; }

    public static PdmBufferStats Parse(byte[] data, int offset)
    {
        return new PdmBufferStats
        {
            DmaFillPct = data[offset],
            DmaMinFillPct = data[offset + 1],
            DmaMaxFillPct = data[offset + 2],
            RingFillPct = data[offset + 3],
            RingMinFillPct = data[offset + 4],
            RingMaxFillPct = data[offset + 5],
        };
    }
}

/// <summary>
/// Complete 44-byte buffer statistics packet from REQ_GET_BUFFER_STATS (0xB0).
/// </summary>
public class BufferStatsPacket
{
    public const int PacketSize = 44;

    /// <summary>Number of active SPDIF instances (2 on RP2040, 4 on RP2350).</summary>
    public byte NumSpdif { get; set; }

    /// <summary>Status flags: bit 0 = PDM active, bit 1 = audio streaming.</summary>
    public byte Flags { get; set; }

    /// <summary>Monotonic sequence counter for missed-poll detection.</summary>
    public ushort Sequence { get; set; }

    /// <summary>Per-instance SPDIF stats (always 4 slots; only NumSpdif are valid).</summary>
    public SpdifBufferStats[] Spdif { get; set; } = new SpdifBufferStats[4];

    /// <summary>PDM buffer statistics.</summary>
    public PdmBufferStats Pdm { get; set; }

    public bool IsPdmActive => (Flags & 0x01) != 0;
    public bool IsAudioStreaming => (Flags & 0x02) != 0;

    public static BufferStatsPacket? Parse(byte[] data)
    {
        if (data == null || data.Length < PacketSize)
            return null;

        var packet = new BufferStatsPacket
        {
            NumSpdif = data[0],
            Flags = data[1],
            Sequence = BitConverter.ToUInt16(data, 2),
        };

        // 4 SPDIF instances at offset 4, 8 bytes each
        for (int i = 0; i < 4; i++)
            packet.Spdif[i] = SpdifBufferStats.Parse(data, 4 + i * 8);

        // PDM at offset 36
        packet.Pdm = PdmBufferStats.Parse(data, 36);

        return packet;
    }
}
