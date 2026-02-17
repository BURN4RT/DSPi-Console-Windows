namespace DSPiConsole.Core.Models;

public enum PlatformType : byte
{
    RP2040 = 0,
    RP2350 = 1
}

public class PlatformInfo
{
    public PlatformType Platform { get; }
    public int OutputCount { get; }
    public int PdmOutputIndex { get; }
    public string Name { get; }
    public int TotalChannelCount => 2 + OutputCount;

    private PlatformInfo(PlatformType platform, int outputCount, int pdmOutputIndex, string name)
    {
        Platform = platform;
        OutputCount = outputCount;
        PdmOutputIndex = pdmOutputIndex;
        Name = name;
    }

    public static PlatformInfo ForPlatform(PlatformType platform) => platform switch
    {
        PlatformType.RP2040 => new PlatformInfo(PlatformType.RP2040, 3, 2, "RP2040"),
        PlatformType.RP2350 => new PlatformInfo(PlatformType.RP2350, 9, 8, "RP2350"),
        _ => new PlatformInfo(PlatformType.RP2040, 3, 2, "RP2040") // default
    };

    /// <summary>
    /// Default platform used when device is not connected.
    /// </summary>
    public static PlatformInfo Default => ForPlatform(PlatformType.RP2040);
}
