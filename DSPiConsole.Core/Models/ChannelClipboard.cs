namespace DSPiConsole.Core.Models;

public class ChannelClipboard
{
    public bool SourceIsOutput { get; init; }
    public List<FilterParams> Filters { get; init; } = new();
    public float? Delay { get; init; }
    public float? Gain { get; init; }
    public bool? Mute { get; init; }
}
