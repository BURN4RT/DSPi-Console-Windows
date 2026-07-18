using Windows.UI;

namespace DSPiConsole.Core.Models;

/// <summary>
/// Audio channel definitions matching firmware
/// </summary>
public enum ChannelId
{
    MasterLeft = 0,
    MasterRight = 1,
    Spdif1L = 2,
    Spdif1R = 3,
    Spdif2L = 4,
    Spdif2R = 5,
    Spdif3L = 6,
    // Alias: RP2040 has no SPDIF 3 in firmware — the channel-index
    // slot at 6 is occupied by PDM instead (CH_OUT_5_PDM in config.h).
    // Same underlying enum value as Spdif3L; the platform-specific
    // Channel instance (PdmRp2040 vs Spdif3L) determines what
    // metadata the UI shows.
    PdmRp2040 = 6,
    Spdif3R = 7,
    Spdif4L = 8,
    Spdif4R = 9,
    Pdm = 10,

    // Extra unified-model input channels (RP2350 V16+). Ids 11..16 map to wire
    // input indices 2..7 via ChannelMap; kept above the outputs so the existing
    // 0..10 ids (and all persisted state keyed by them) are undisturbed.
    Input3 = 11,
    Input4 = 12,
    Input5 = 13,
    Input6 = 14,
    Input7 = 15,
    Input8 = 16
}

/// <summary>
/// Channel configuration and metadata
/// </summary>
public class Channel
{
    public ChannelId Id { get; }
    public string Name { get; }
    public string ShortName { get; }
    public string Descriptor { get; }
    public int BandCount { get; }
    public bool IsOutput { get; }
    public Color Color { get; }

    private Channel(ChannelId id, string name, string shortName, string descriptor,
                    int bandCount, bool isOutput, Color color)
    {
        Id = id;
        Name = name;
        ShortName = shortName;
        Descriptor = descriptor;
        BandCount = bandCount;
        IsOutput = isOutput;
        Color = color;
    }

    public static readonly Channel MasterLeft = new(
        ChannelId.MasterLeft, "Master L", "ML", "IN1",
        10, false, Color.FromArgb(255, 56, 199, 207)); // Cyan

    public static readonly Channel MasterRight = new(
        ChannelId.MasterRight, "Master R", "MR", "IN2",
        10, false, Color.FromArgb(255, 230, 160, 60)); // Amber

    public static readonly Channel Spdif1L = new(
        ChannelId.Spdif1L, "SPDIF 1 L", "S1L", "OUT1",
        10, true, Color.FromArgb(255, 74, 143, 227)); // Blue

    public static readonly Channel Spdif1R = new(
        ChannelId.Spdif1R, "SPDIF 1 R", "S1R", "OUT2",
        10, true, Color.FromArgb(255, 245, 115, 115)); // Red

    public static readonly Channel Spdif2L = new(
        ChannelId.Spdif2L, "SPDIF 2 L", "S2L", "OUT3",
        10, true, Color.FromArgb(255, 69, 194, 163)); // Teal

    public static readonly Channel Spdif2R = new(
        ChannelId.Spdif2R, "SPDIF 2 R", "S2R", "OUT4",
        10, true, Color.FromArgb(255, 240, 196, 89)); // Yellow

    public static readonly Channel Spdif3L = new(
        ChannelId.Spdif3L, "SPDIF 3 L", "S3L", "OUT5",
        10, true, Color.FromArgb(255, 109, 179, 126)); // Green

    public static readonly Channel Spdif3R = new(
        ChannelId.Spdif3R, "SPDIF 3 R", "S3R", "OUT6",
        10, true, Color.FromArgb(255, 232, 144, 90)); // Orange

    public static readonly Channel Spdif4L = new(
        ChannelId.Spdif4L, "SPDIF 4 L", "S4L", "OUT7",
        10, true, Color.FromArgb(255, 232, 123, 191)); // Pink

    public static readonly Channel Spdif4R = new(
        ChannelId.Spdif4R, "SPDIF 4 R", "S4R", "OUT8",
        10, true, Color.FromArgb(255, 168, 137, 224)); // Lavender

    public static readonly Channel Pdm = new(
        ChannelId.Pdm, "PDM", "PDM", "OUT9",
        10, true, Color.FromArgb(255, 186, 135, 243)); // Purple

    // PDM on RP2040 lives at firmware channel index 6 (CH_OUT_5_PDM in
    // config.h), not 10. The ChannelId.PdmRp2040 enum value aliases
    // Spdif3L (both = 6) — not a collision, because Spdif3L doesn't
    // exist on RP2040 hardware. Using a distinct Channel instance
    // keeps PDM metadata (name, colour) correct even though the
    // underlying ID is shared with another platform's channel.
    public static readonly Channel PdmRp2040 = new(
        ChannelId.PdmRp2040, "PDM", "PDM", "OUT5",
        10, true, Color.FromArgb(255, 186, 135, 243)); // Purple

    // Extra unified-model input channels (RP2350). Only surfaced when the device
    // actually streams more than 2 USB input channels; otherwise inert.
    public static readonly Channel Input3 = new(
        ChannelId.Input3, "Input 3", "I3", "IN3",
        10, false, Color.FromArgb(255, 69, 194, 163)); // Teal

    public static readonly Channel Input4 = new(
        ChannelId.Input4, "Input 4", "I4", "IN4",
        10, false, Color.FromArgb(255, 240, 196, 89)); // Gold

    public static readonly Channel Input5 = new(
        ChannelId.Input5, "Input 5", "I5", "IN5",
        10, false, Color.FromArgb(255, 109, 179, 126)); // Green

    public static readonly Channel Input6 = new(
        ChannelId.Input6, "Input 6", "I6", "IN6",
        10, false, Color.FromArgb(255, 232, 144, 90)); // Orange

    public static readonly Channel Input7 = new(
        ChannelId.Input7, "Input 7", "I7", "IN7",
        10, false, Color.FromArgb(255, 232, 123, 191)); // Pink

    public static readonly Channel Input8 = new(
        ChannelId.Input8, "Input 8", "I8", "IN8",
        10, false, Color.FromArgb(255, 168, 137, 224)); // Lavender

    public static IReadOnlyList<Channel> All { get; } = new[]
    {
        MasterLeft, MasterRight,
        Input3, Input4, Input5, Input6, Input7, Input8,
        Spdif1L, Spdif1R, Spdif2L, Spdif2R, Spdif3L, Spdif3R, Spdif4L, Spdif4R, Pdm
    };

    /// <summary>The two base input channels (Master L/R). Consumers that assume a
    /// fixed stereo input pair (output-page routing rows, leveller) use this.</summary>
    public static IReadOnlyList<Channel> Inputs { get; } = new[]
    {
        MasterLeft, MasterRight
    };

    /// <summary>All potential input channels (up to 8 on RP2350). The active subset
    /// shown in the UI is driven by the USB input channel count.</summary>
    public static IReadOnlyList<Channel> AllInputs { get; } = new[]
    {
        MasterLeft, MasterRight, Input3, Input4, Input5, Input6, Input7, Input8
    };

    public static IReadOnlyList<Channel> Outputs { get; } = new[]
    {
        Spdif1L, Spdif1R, Spdif2L, Spdif2R, Spdif3L, Spdif3R, Spdif4L, Spdif4R, Pdm
    };

    public static IReadOnlyList<Channel> Rp2040Outputs { get; } = new[]
    {
        Spdif1L, Spdif1R, Spdif2L, Spdif2R, PdmRp2040
    };

    public static Channel FromId(ChannelId id) => id switch
    {
        ChannelId.MasterLeft => MasterLeft,
        ChannelId.MasterRight => MasterRight,
        ChannelId.Spdif1L => Spdif1L,
        ChannelId.Spdif1R => Spdif1R,
        ChannelId.Spdif2L => Spdif2L,
        ChannelId.Spdif2R => Spdif2R,
        ChannelId.Spdif3L => Spdif3L,
        ChannelId.Spdif3R => Spdif3R,
        ChannelId.Spdif4L => Spdif4L,
        ChannelId.Spdif4R => Spdif4R,
        ChannelId.Pdm => Pdm,
        ChannelId.Input3 => Input3,
        ChannelId.Input4 => Input4,
        ChannelId.Input5 => Input5,
        ChannelId.Input6 => Input6,
        ChannelId.Input7 => Input7,
        ChannelId.Input8 => Input8,
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    public static Channel FromIndex(int index) => FromId((ChannelId)index);
}
