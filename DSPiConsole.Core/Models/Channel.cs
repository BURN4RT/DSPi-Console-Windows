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
    Spdif3R = 7,
    Spdif4L = 8,
    Spdif4R = 9,
    Pdm = 10
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
        ChannelId.MasterLeft, "Master L", "ML", "USB",
        10, false, Color.FromArgb(255, 74, 143, 227)); // Blue

    public static readonly Channel MasterRight = new(
        ChannelId.MasterRight, "Master R", "MR", "USB",
        10, false, Color.FromArgb(255, 245, 115, 115)); // Red

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

    public static IReadOnlyList<Channel> All { get; } = new[]
    {
        MasterLeft, MasterRight,
        Spdif1L, Spdif1R, Spdif2L, Spdif2R, Spdif3L, Spdif3R, Spdif4L, Spdif4R, Pdm
    };

    public static IReadOnlyList<Channel> Inputs { get; } = new[]
    {
        MasterLeft, MasterRight
    };

    public static IReadOnlyList<Channel> Outputs { get; } = new[]
    {
        Spdif1L, Spdif1R, Spdif2L, Spdif2R, Spdif3L, Spdif3R, Spdif4L, Spdif4R, Pdm
    };

    public static IReadOnlyList<Channel> Rp2040Outputs { get; } = new[]
    {
        Spdif1L, Spdif1R, Spdif2L, Spdif2R, Pdm
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
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    public static Channel FromIndex(int index) => FromId((ChannelId)index);
}
