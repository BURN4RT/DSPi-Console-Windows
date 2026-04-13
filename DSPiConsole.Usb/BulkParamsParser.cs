using System.Text;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Usb;

/// <summary>
/// Parsed result from a bulk parameter fetch (REQ_GET_ALL_PARAMS, 0xA0).
/// </summary>
public class BulkParams
{
    // Header (offset 0, 16 bytes)
    public byte FormatVersion;
    public byte PlatformId;       // 0=RP2040, 1=RP2350
    public byte NumChannels;      // total (11)
    public byte NumOutputChannels; // outputs (9)
    public byte NumInputChannels;  // inputs (2)
    public byte MaxBands;          // bands per channel (12)

    // Global (offset 16, 16 bytes)
    public float PreampGainDb;
    public bool Bypass;
    public bool LoudnessEnabled;
    public float LoudnessRefSpl;
    public float LoudnessIntensityPct;

    // Crossfeed (offset 32, 16 bytes)
    public bool CrossfeedEnabled;
    public byte CrossfeedPreset;
    public bool CrossfeedItd;
    public float CrossfeedFreq;
    public float CrossfeedFeedDb;

    // Delays (offset 64, 44 bytes) — float[11]
    public float[] Delays = Array.Empty<float>();

    // Matrix crosspoints (offset 108, 144 bytes) — [input, output]
    public (bool enabled, bool invert, float gain)[,] Crosspoints = new (bool, bool, float)[2, 9];

    // Outputs (offset 252, 108 bytes) — 9 × 12 bytes
    public (bool enabled, bool muted, float gain, float delay)[] Outputs =
        new (bool, bool, float, float)[9];

    // Pin config (offset 360, 8 bytes)
    public byte NumPinOutputs;
    public byte[] Pins = Array.Empty<byte>();

    // EQ bands (offset 368, 2112 bytes) — [channel, band] 11×12×16
    public FilterParams[,] Eq = new FilterParams[11, 12];

    // Channel names (offset 2480, 352 bytes) — 11 × 32-char strings
    public string[] ChannelNames = Array.Empty<string>();

    // I2S config (offset 2832, 16 bytes) — present when packet >= 2848
    public byte[] OutputSlotTypes = new byte[4]; // per-slot: 0=S/PDIF, 1=I2S
    public byte BckPin;
    public byte MckPin;
    public bool MckEnabled;
    public byte MckMultiplierEncoded; // 0=128x, 1=256x
    public bool HasI2SConfig;

    // Volume leveller (offset 2848, 16 bytes) — present when packet >= 2864
    public bool LevellerEnabled;
    public byte LevellerSpeed;       // 0=Slow, 1=Medium, 2=Fast
    public bool LevellerLookahead;
    public float LevellerAmount;     // 0-100
    public float LevellerMaxGainDb;  // 0-35
    public float LevellerGateDb;     // -96 to 0
    public bool HasLevellerConfig;
}

/// <summary>
/// Parses the 2832-byte bulk parameter packet from firmware.
/// </summary>
public static class BulkParamsParser
{
    public const int PacketSize = 2832;
    public const byte ExpectedFormatVersion = 2;

    // Section offsets
    private const int OffsetHeader = 0;
    private const int OffsetGlobal = 16;
    private const int OffsetCrossfeed = 32;
    // offset 48 = legacy channels (ignored)
    private const int OffsetDelays = 64;
    private const int OffsetCrosspoints = 108;
    private const int OffsetOutputs = 252;
    private const int OffsetPinConfig = 360;
    private const int OffsetEq = 368;
    private const int OffsetChannelNames = 2480;

    public static BulkParams? Parse(byte[] buffer)
    {
        if (buffer == null || buffer.Length < PacketSize)
            return null;

        var p = new BulkParams();

        // ── Header (16 bytes) ──
        p.FormatVersion = buffer[OffsetHeader + 0];
        if (p.FormatVersion != ExpectedFormatVersion)
            return null;

        p.PlatformId = buffer[OffsetHeader + 1];
        p.NumChannels = buffer[OffsetHeader + 2];
        p.NumOutputChannels = buffer[OffsetHeader + 3];
        p.NumInputChannels = buffer[OffsetHeader + 4];
        p.MaxBands = buffer[OffsetHeader + 5];

        // ── Global (16 bytes) ──
        p.PreampGainDb = BitConverter.ToSingle(buffer, OffsetGlobal + 0);
        p.Bypass = buffer[OffsetGlobal + 4] != 0;
        p.LoudnessEnabled = buffer[OffsetGlobal + 5] != 0;
        // bytes 6-7 reserved
        p.LoudnessRefSpl = BitConverter.ToSingle(buffer, OffsetGlobal + 8);
        p.LoudnessIntensityPct = BitConverter.ToSingle(buffer, OffsetGlobal + 12);

        // ── Crossfeed (16 bytes) ──
        p.CrossfeedEnabled = buffer[OffsetCrossfeed + 0] != 0;
        p.CrossfeedPreset = buffer[OffsetCrossfeed + 1];
        p.CrossfeedItd = buffer[OffsetCrossfeed + 2] != 0;
        // byte 3 reserved
        p.CrossfeedFreq = BitConverter.ToSingle(buffer, OffsetCrossfeed + 4);
        p.CrossfeedFeedDb = BitConverter.ToSingle(buffer, OffsetCrossfeed + 8);

        // ── Delays (44 bytes = 11 × float) ──
        p.Delays = new float[11];
        for (int i = 0; i < 11; i++)
            p.Delays[i] = BitConverter.ToSingle(buffer, OffsetDelays + i * 4);

        // ── Crosspoints (144 bytes = 2 inputs × 9 outputs × 8 bytes) ──
        // Each crosspoint: enabled(1), invert(1), reserved(2), gain(4)
        p.Crosspoints = new (bool, bool, float)[2, 9];
        for (int inp = 0; inp < 2; inp++)
        {
            for (int outp = 0; outp < 9; outp++)
            {
                int off = OffsetCrosspoints + (inp * 9 + outp) * 8;
                bool enabled = buffer[off + 0] != 0;
                bool invert = buffer[off + 1] != 0;
                float gain = BitConverter.ToSingle(buffer, off + 4);
                p.Crosspoints[inp, outp] = (enabled, invert, gain);
            }
        }

        // ── Outputs (108 bytes = 9 × 12 bytes) ──
        // Each: enabled(1), mute(1), reserved(2), gain(4), delay(4)
        p.Outputs = new (bool, bool, float, float)[9];
        for (int o = 0; o < 9; o++)
        {
            int off = OffsetOutputs + o * 12;
            bool enabled = buffer[off + 0] != 0;
            bool muted = buffer[off + 1] != 0;
            float gain = BitConverter.ToSingle(buffer, off + 4);
            float delay = BitConverter.ToSingle(buffer, off + 8);
            p.Outputs[o] = (enabled, muted, gain, delay);
        }

        // ── Pin config (8 bytes) ──
        p.NumPinOutputs = buffer[OffsetPinConfig + 0];
        p.Pins = new byte[5];
        for (int i = 0; i < 5; i++)
            p.Pins[i] = buffer[OffsetPinConfig + 1 + i];

        // ── EQ bands (2112 bytes = 11 channels × 12 bands × 16 bytes) ──
        // Each band: type(1), reserved(3), freq(4), Q(4), gain(4)
        p.Eq = new FilterParams[11, 12];
        for (int ch = 0; ch < 11; ch++)
        {
            for (int band = 0; band < 12; band++)
            {
                int off = OffsetEq + (ch * 12 + band) * 16;
                p.Eq[ch, band] = new FilterParams
                {
                    Type = (FilterType)buffer[off + 0],
                    Frequency = BitConverter.ToSingle(buffer, off + 4),
                    Q = BitConverter.ToSingle(buffer, off + 8),
                    Gain = BitConverter.ToSingle(buffer, off + 12)
                };
            }
        }

        // ── Channel names (352 bytes = 11 × 32-char null-terminated strings) ──
        p.ChannelNames = new string[11];
        for (int ch = 0; ch < 11; ch++)
        {
            int off = OffsetChannelNames + ch * 32;
            int len = 0;
            while (len < 32 && buffer[off + len] != 0) len++;
            p.ChannelNames[ch] = Encoding.UTF8.GetString(buffer, off, len);
        }

        // ── I2S config (16 bytes, optional) ──
        const int OffsetI2S = 2832;
        if (buffer.Length >= OffsetI2S + 16)
        {
            p.HasI2SConfig = true;
            for (int i = 0; i < 4; i++)
                p.OutputSlotTypes[i] = buffer[OffsetI2S + i];
            p.BckPin = buffer[OffsetI2S + 4];
            p.MckPin = buffer[OffsetI2S + 5];
            p.MckEnabled = buffer[OffsetI2S + 6] != 0;
            p.MckMultiplierEncoded = buffer[OffsetI2S + 7];
            // bytes 8-15 reserved
        }

        // ── Volume leveller (16 bytes, optional) ──
        const int OffsetLeveller = 2848;
        if (buffer.Length >= OffsetLeveller + 16)
        {
            p.HasLevellerConfig = true;
            p.LevellerEnabled = buffer[OffsetLeveller] != 0;
            p.LevellerSpeed = buffer[OffsetLeveller + 1];
            p.LevellerLookahead = buffer[OffsetLeveller + 2] != 0;
            // byte 3 reserved
            p.LevellerAmount = BitConverter.ToSingle(buffer, OffsetLeveller + 4);
            p.LevellerMaxGainDb = BitConverter.ToSingle(buffer, OffsetLeveller + 8);
            p.LevellerGateDb = BitConverter.ToSingle(buffer, OffsetLeveller + 12);
        }

        return p;
    }
}
