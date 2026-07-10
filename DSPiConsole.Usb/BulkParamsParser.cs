using System.Text;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Usb;

/// <summary>
/// Parsed result from a bulk parameter fetch (REQ_GET_ALL_PARAMS, 0xA0 /
/// chunked 0xA2). Wire format V20 (firmware 1.1.x, unified channel model).
///
/// V16 broke bulk-params backward compatibility with no migration: inputs are
/// now first-class channels (no "master"), and the channel index space is
/// [ inputs 0..NumInputChannels-1 ][ outputs NumInputChannels..NumChannels-1 ].
/// The firmware rejects any payload whose format_version != 20 or whose length
/// != sizeof(WireBulkParams) (5876), so there are no legacy size anchors or
/// per-section version gates anymore — every section is always present.
/// </summary>
public class BulkParams
{
    // ── Header (offset 0, 16 bytes) ──
    public byte FormatVersion;
    public byte PlatformId;         // 0=RP2040, 1=RP2350
    public byte NumChannels;        // total valid channels (7 on RP2040, 17 on RP2350)
    public byte NumOutputChannels;  // valid outputs (5 or 9)
    public byte NumInputChannels;   // valid inputs (2 or 8)
    public byte MaxBands;           // PEQ bands per channel (12)
    public ushort PayloadLength;    // total packet size including header
    public ushort FwVersionMajor;
    public ushort FwVersionMinor;

    // ── Global (offset 16, 16 bytes) ──
    public float PreampGainDb;
    public bool Bypass;
    public bool LoudnessEnabled;
    public ushort LoudnessOutputMask;  // bit k = loudness processes output channel k (V19+)
    public float LoudnessRefSpl;
    public float LoudnessIntensityPct;

    // ── Crossfeed (offset 32, 16 bytes) ──
    public bool CrossfeedEnabled;
    public byte CrossfeedPreset;
    public bool CrossfeedItd;
    public byte CrossfeedOutputPairMask;  // bit p = crossfeed runs on output pair p (V20+)
    public float CrossfeedFreq;
    public float CrossfeedFeedDb;

    // ── Delays (offset 64, 68 bytes) — float[17] ──
    public float[] Delays = Array.Empty<float>();

    // ── Matrix crosspoints (offset 132, 576 bytes) — [input 0..7, output 0..8] ──
    public (bool enabled, bool invert, float gain)[,] Crosspoints =
        new (bool, bool, float)[BulkParamsParser.WireMaxInputChannels, BulkParamsParser.WireMaxOutputChannels];

    // ── Outputs (offset 708, 108 bytes) — 9 × 12 bytes, output-relative ──
    public (bool enabled, bool muted, float gain, float delay)[] Outputs =
        new (bool, bool, float, float)[BulkParamsParser.WireMaxOutputChannels];

    // ── Pin config (offset 816, 8 bytes) ──
    public byte NumPinOutputs;
    public byte[] Pins = Array.Empty<byte>();

    // ── EQ bands (offset 824, 3264 bytes) — [channel 0..16, band 0..11] ──
    public FilterParams[,] Eq =
        new FilterParams[BulkParamsParser.WireMaxChannels, BulkParamsParser.WireMaxBands];

    // ── Channel names (offset 4088, 544 bytes) — 17 × 32-char strings ──
    public string[] ChannelNames = Array.Empty<string>();

    // ── I2S output config (offset 4632, 16 bytes) ──
    public byte[] OutputSlotTypes = new byte[4]; // per-slot: 0=S/PDIF, 1=I2S
    public byte BckPin;
    public byte MckPin;
    public bool MckEnabled;
    public byte MckMultiplierEncoded; // 0=128x, 1=256x
    public bool HasI2SConfig;

    // ── Volume leveller (offset 4648, 20 bytes) ──
    public bool LevellerEnabled;
    public byte LevellerSpeed;       // 0=Slow, 1=Medium, 2=Fast
    public bool LevellerLookahead;
    public float LevellerAmount;     // 0-100
    public float LevellerMaxGainDb;  // 0-35
    public float LevellerGateDb;     // -96 to 0
    public byte LevellerDetectorMask; // bit k = input channel k feeds the detector (V18+)
    public byte LevellerApplyMask;    // bit k = gain applied to input channel k (V18+)
    public bool HasLevellerConfig;

    // ── Per-channel preamp (offset 4668, 32 bytes) — float[8], per input channel ──
    public float[] Preamp = Array.Empty<float>();
    public float PreampLDb;          // convenience: Preamp[0]
    public float PreampRDb;          // convenience: Preamp[1]
    public bool HasPerChannelPreamp;

    // ── Master volume (offset 4700, 16 bytes) ──
    public float MasterVolumeDb;
    public bool HasMasterVolume;

    // ── Input source config (offset 4716, 16 bytes) ──
    public byte InputSource;         // 0 = USB, 1 = S/PDIF, 2 = I2S
    public byte SpdifRxPin;          // primary SPDIF RX GPIO
    public bool HasInputConfig;

    // I2S input fields within WireInputConfig — I2sInputRateEncoded: 0=44100, 1=48000, 2=96000.
    public byte I2sRxPin;            // I2S input data GPIO, stereo pair 0
    public byte I2sInputRateEncoded; // 0=44100, 1=48000, 2=96000 (NOT Hz)
    public byte I2sInputChannels;    // active I2S input channels: 2/4/6/8 (0 = absent)
    public byte[] I2sRxPinExt = new byte[3];   // I2S RX GPIOs for pairs 1..3
    public bool HasI2sInputConfig;

    // Optional SPDIF inputs 2/3 (stored enable-mask PLUS ONE on the wire).
    public byte[] SpdifRxPinExt = new byte[2];      // SPDIF RX 2/3 GPIOs (0 = absent)
    public byte SpdifRxEnabledExt;                  // decoded enable mask (0 = both disabled)
    public bool HasSpdifExtInputs;

    // ── LG Sound Sync (offset 4732, 16 bytes) ──
    public bool LgSoundSyncEnabled;
    public bool HasLgSoundSync;

    // ── User volume / vendor mute (offset 4748, 16 bytes) ──
    public float UserVolumeDb;
    public bool UserMute;
    public bool HasUserVolume;

    // ── External DAC hardware mute (offset 4764, 16 bytes) ──
    public DacHwMuteConfig? DacHwMute;
    public bool HasDacHwMute;

    // ── Crossover bands (offset 4780, 1088 bytes) — [channel 0..16, localBand 0..3] ──
    // localBand 0..3 maps to wire band index 20..23. Input rows are zeroed by
    // firmware (crossover is output-only).
    public FilterParams[,] Xover =
        new FilterParams[BulkParamsParser.WireMaxChannels, BulkParamsParser.WireMaxXoverBands];
    public bool HasCrossover;

    // ── ADAT output config (offset 5868, 8 bytes) — RP2350-only (V17+) ──
    public bool AdatEnabled;
    public byte AdatPin;
    public bool HasAdat;
}

/// <summary>
/// Parses the V20 bulk parameter packet (5876 bytes, 17-channel unified model).
/// The firmware only emits the full current layout, so this parser requires the
/// full size; older/truncated payloads are rejected (return null) rather than
/// mis-parsed. See firmware bulk_params.h for the authoritative struct.
/// </summary>
public static class BulkParamsParser
{
    // Wire-format maximums (must match firmware bulk_params.h WIRE_MAX_* defines).
    internal const int WireMaxInputChannels = 8;
    internal const int WireMaxOutputChannels = 9;
    internal const int WireMaxChannels = 17;     // inputs + outputs
    internal const int WireMaxBands = 12;
    internal const int WireMaxXoverBands = 4;
    internal const int WireMaxPinOutputs = 5;
    internal const int WireNameLen = 32;
    internal const int WireBandSize = 16;        // sizeof(WireBandParams)

    public const int PacketSizeV20 = 5876;       // sizeof(WireBulkParams) at V20
    public const byte MinFormatVersion = 16;     // unified channel model floor
    public const byte CurrentFormatVersion = 20;

    // Section offsets (bytes) into WireBulkParams. Derived directly from the
    // struct member order + sizes in firmware bulk_params.h.
    private const int OffsetHeader = 0;          // 16
    private const int OffsetGlobal = 16;         // 16
    private const int OffsetCrossfeed = 32;      // 16
    private const int OffsetLegacy = 48;         // 16 (ignored)
    private const int OffsetDelays = 64;         // 68  (17 × float)
    private const int OffsetCrosspoints = 132;   // 576 (8 × 9 × 8)
    private const int OffsetOutputs = 708;       // 108 (9 × 12)
    private const int OffsetPinConfig = 816;     // 8
    internal const int OffsetEq = 824;           // 3264 (17 × 12 × 16); exposed for notify dispatch
    internal const int OffsetChannelNames = 4088; // 544 (17 × 32); exposed for notify dispatch
    private const int OffsetI2S = 4632;          // 16
    private const int OffsetLeveller = 4648;     // 20
    private const int OffsetPreamp = 4668;       // 32 (8 × float)
    private const int OffsetMasterVol = 4700;    // 16
    internal const int OffsetInputCfg = 4716;    // 16; exposed for notify dispatch
    private const int OffsetLgSoundSync = 4732;  // 16
    internal const int OffsetUserVolume = 4748;  // 16; exposed for notify dispatch
    private const int OffsetDacHwMute = 4764;    // 16
    internal const int OffsetCrossover = 4780;   // 1088 (17 × 4 × 16); exposed for notify dispatch
    private const int OffsetAdat = 5868;         // 8

    public static BulkParams? Parse(byte[] buffer)
    {
        if (buffer == null || buffer.Length < PacketSizeV20)
            return null;

        var p = new BulkParams();

        // ── Header (16 bytes) ──
        p.FormatVersion = buffer[OffsetHeader + 0];
        if (p.FormatVersion < MinFormatVersion)
            return null;

        p.PlatformId = buffer[OffsetHeader + 1];
        p.NumChannels = buffer[OffsetHeader + 2];
        p.NumOutputChannels = buffer[OffsetHeader + 3];
        p.NumInputChannels = buffer[OffsetHeader + 4];
        p.MaxBands = buffer[OffsetHeader + 5];
        p.PayloadLength = BitConverter.ToUInt16(buffer, OffsetHeader + 6);
        p.FwVersionMajor = BitConverter.ToUInt16(buffer, OffsetHeader + 8);
        p.FwVersionMinor = BitConverter.ToUInt16(buffer, OffsetHeader + 10);

        // ── Global (16 bytes) ──
        p.PreampGainDb = BitConverter.ToSingle(buffer, OffsetGlobal + 0);
        p.Bypass = buffer[OffsetGlobal + 4] != 0;
        p.LoudnessEnabled = buffer[OffsetGlobal + 5] != 0;
        p.LoudnessOutputMask = BitConverter.ToUInt16(buffer, OffsetGlobal + 6);
        p.LoudnessRefSpl = BitConverter.ToSingle(buffer, OffsetGlobal + 8);
        p.LoudnessIntensityPct = BitConverter.ToSingle(buffer, OffsetGlobal + 12);

        // ── Crossfeed (16 bytes) ──
        p.CrossfeedEnabled = buffer[OffsetCrossfeed + 0] != 0;
        p.CrossfeedPreset = buffer[OffsetCrossfeed + 1];
        p.CrossfeedItd = buffer[OffsetCrossfeed + 2] != 0;
        p.CrossfeedOutputPairMask = buffer[OffsetCrossfeed + 3];
        p.CrossfeedFreq = BitConverter.ToSingle(buffer, OffsetCrossfeed + 4);
        p.CrossfeedFeedDb = BitConverter.ToSingle(buffer, OffsetCrossfeed + 8);

        // ── Delays (68 bytes = 17 × float) ──
        p.Delays = new float[WireMaxChannels];
        for (int i = 0; i < WireMaxChannels; i++)
            p.Delays[i] = BitConverter.ToSingle(buffer, OffsetDelays + i * 4);

        // ── Crosspoints (576 bytes = 8 inputs × 9 outputs × 8 bytes) ──
        // Each crosspoint: enabled(1), phase_invert(1), reserved(2), gain(4)
        p.Crosspoints = new (bool, bool, float)[WireMaxInputChannels, WireMaxOutputChannels];
        for (int inp = 0; inp < WireMaxInputChannels; inp++)
        {
            for (int outp = 0; outp < WireMaxOutputChannels; outp++)
            {
                int off = OffsetCrosspoints + (inp * WireMaxOutputChannels + outp) * 8;
                bool enabled = buffer[off + 0] != 0;
                bool invert = buffer[off + 1] != 0;
                float gain = BitConverter.ToSingle(buffer, off + 4);
                p.Crosspoints[inp, outp] = (enabled, invert, gain);
            }
        }

        // ── Outputs (108 bytes = 9 × 12 bytes) ──
        // Each: enabled(1), mute(1), reserved(2), gain(4), delay(4)
        p.Outputs = new (bool, bool, float, float)[WireMaxOutputChannels];
        for (int o = 0; o < WireMaxOutputChannels; o++)
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
        p.Pins = new byte[WireMaxPinOutputs];
        for (int i = 0; i < WireMaxPinOutputs; i++)
            p.Pins[i] = buffer[OffsetPinConfig + 1 + i];

        // ── EQ bands (3264 bytes = 17 channels × 12 bands × 16 bytes) ──
        p.Eq = new FilterParams[WireMaxChannels, WireMaxBands];
        for (int ch = 0; ch < WireMaxChannels; ch++)
        {
            for (int band = 0; band < WireMaxBands; band++)
            {
                int off = OffsetEq + (ch * WireMaxBands + band) * WireBandSize;
                p.Eq[ch, band] = ParseBand(buffer, off);
            }
        }

        // ── Channel names (544 bytes = 17 × 32-char null-terminated strings) ──
        p.ChannelNames = new string[WireMaxChannels];
        for (int ch = 0; ch < WireMaxChannels; ch++)
        {
            int off = OffsetChannelNames + ch * WireNameLen;
            int len = 0;
            while (len < WireNameLen && buffer[off + len] != 0) len++;
            p.ChannelNames[ch] = Encoding.UTF8.GetString(buffer, off, len);
        }

        // ── I2S output config (16 bytes) ──
        p.HasI2SConfig = true;
        for (int i = 0; i < 4; i++)
            p.OutputSlotTypes[i] = buffer[OffsetI2S + i];
        p.BckPin = buffer[OffsetI2S + 4];
        p.MckPin = buffer[OffsetI2S + 5];
        p.MckEnabled = buffer[OffsetI2S + 6] != 0;
        p.MckMultiplierEncoded = buffer[OffsetI2S + 7];

        // ── Volume leveller (20 bytes) ──
        p.HasLevellerConfig = true;
        p.LevellerEnabled = buffer[OffsetLeveller + 0] != 0;
        p.LevellerSpeed = buffer[OffsetLeveller + 1];
        p.LevellerLookahead = buffer[OffsetLeveller + 2] != 0;
        p.LevellerAmount = BitConverter.ToSingle(buffer, OffsetLeveller + 4);
        p.LevellerMaxGainDb = BitConverter.ToSingle(buffer, OffsetLeveller + 8);
        p.LevellerGateDb = BitConverter.ToSingle(buffer, OffsetLeveller + 12);
        p.LevellerDetectorMask = buffer[OffsetLeveller + 16];
        p.LevellerApplyMask = buffer[OffsetLeveller + 17];

        // ── Per-channel preamp (32 bytes = 8 × float) ──
        p.HasPerChannelPreamp = true;
        p.Preamp = new float[WireMaxInputChannels];
        for (int i = 0; i < WireMaxInputChannels; i++)
            p.Preamp[i] = BitConverter.ToSingle(buffer, OffsetPreamp + i * 4);
        p.PreampLDb = p.Preamp[0];
        p.PreampRDb = p.Preamp.Length > 1 ? p.Preamp[1] : p.Preamp[0];

        // ── Master volume (16 bytes) ──
        p.HasMasterVolume = true;
        p.MasterVolumeDb = BitConverter.ToSingle(buffer, OffsetMasterVol + 0);

        // ── Input source config (16 bytes) ──
        // WireInputConfig: input_source(1), spdif_rx_pin(1), i2s_rx_pin(1),
        // i2s_input_rate(1), i2s_input_channels(1), i2s_rx_pin_ext[3],
        // spdif_rx_pin_ext[2], spdif_rx_enabled_ext_p1(1), reserved[5].
        p.HasInputConfig = true;
        p.InputSource = buffer[OffsetInputCfg + 0];
        p.SpdifRxPin = buffer[OffsetInputCfg + 1];
        p.HasI2sInputConfig = true;
        p.I2sRxPin = buffer[OffsetInputCfg + 2];
        p.I2sInputRateEncoded = buffer[OffsetInputCfg + 3];
        p.I2sInputChannels = buffer[OffsetInputCfg + 4];
        p.I2sRxPinExt = new byte[3];
        for (int i = 0; i < 3; i++)
            p.I2sRxPinExt[i] = buffer[OffsetInputCfg + 5 + i];
        p.SpdifRxPinExt = new byte[2];
        p.SpdifRxPinExt[0] = buffer[OffsetInputCfg + 8];
        p.SpdifRxPinExt[1] = buffer[OffsetInputCfg + 9];
        // Enable mask is stored PLUS ONE (0 = absent/keep-live); decode by subtracting 1.
        byte spdifEnP1 = buffer[OffsetInputCfg + 10];
        if (spdifEnP1 != 0)
        {
            p.HasSpdifExtInputs = true;
            p.SpdifRxEnabledExt = (byte)(spdifEnP1 - 1);
        }

        // ── LG Sound Sync (16 bytes) ──
        p.HasLgSoundSync = true;
        p.LgSoundSyncEnabled = buffer[OffsetLgSoundSync + 0] != 0;

        // ── User volume / vendor mute (16 bytes) ──
        p.HasUserVolume = true;
        p.UserVolumeDb = BitConverter.ToSingle(buffer, OffsetUserVolume + 0);
        p.UserMute = buffer[OffsetUserVolume + 4] != 0;

        // ── External DAC hardware mute (16 bytes) ──
        p.DacHwMute = DacHwMuteConfig.TryParse(buffer, OffsetDacHwMute);
        p.HasDacHwMute = p.DacHwMute != null;

        // ── Crossover bands (1088 bytes = 17 × 4 × 16) ──
        p.HasCrossover = true;
        p.Xover = new FilterParams[WireMaxChannels, WireMaxXoverBands];
        for (int ch = 0; ch < WireMaxChannels; ch++)
        {
            for (int band = 0; band < WireMaxXoverBands; band++)
            {
                int off = OffsetCrossover + (ch * WireMaxXoverBands + band) * WireBandSize;
                p.Xover[ch, band] = ParseBand(buffer, off);
            }
        }

        // ── ADAT output config (8 bytes) ──
        p.HasAdat = true;
        p.AdatEnabled = buffer[OffsetAdat + 0] != 0;
        p.AdatPin = buffer[OffsetAdat + 1];

        return p;
    }

    /// <summary>
    /// Parse a single 16-byte WireBandParams entry at the given buffer offset.
    /// Shared between the bulk-params path and the notify-endpoint path so
    /// both decode the wire layout identically (incl. the bypass byte at +1).
    /// </summary>
    internal static FilterParams ParseBand(byte[] buffer, int offset)
    {
        return new FilterParams
        {
            Type = (FilterType)buffer[offset + 0],
            Bypass = buffer[offset + 1] == 1,
            Frequency = BitConverter.ToSingle(buffer, offset + 4),
            Q = BitConverter.ToSingle(buffer, offset + 8),
            Gain = BitConverter.ToSingle(buffer, offset + 12)
        };
    }
}
