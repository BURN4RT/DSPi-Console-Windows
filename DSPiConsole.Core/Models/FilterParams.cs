namespace DSPiConsole.Core.Models;

/// <summary>
/// Filter types matching the firmware definitions (config.h FilterType).
/// Values 0-7 are PEQ biquad types; values 8-39 are the crossover filter
/// types (Linkwitz-Riley / Butterworth / Bessel × order × HP/LP) used only
/// in crossover bands. See Documentation/Features/crossover_filters_spec.md.
/// </summary>
public enum FilterType
{
    Flat = 0,
    Peaking = 1,
    LowShelf = 2,
    HighShelf = 3,
    LowPass = 4,
    HighPass = 5,
    Notch = 6,
    AllPass = 7,

    // ── Crossover types (FILTER_XOVER_FIRST = 8 .. FILTER_XOVER_LAST = 39) ──
    // Linkwitz-Riley (orders 2/4/6/8)
    Lr2Lp = 8,  Lr2Hp = 9,
    Lr4Lp = 10, Lr4Hp = 11,
    Lr6Lp = 12, Lr6Hp = 13,
    Lr8Lp = 14, Lr8Hp = 15,
    // Butterworth (orders 1..8)
    Bw1Lp = 16, Bw1Hp = 17,
    Bw2Lp = 18, Bw2Hp = 19,
    Bw3Lp = 20, Bw3Hp = 21,
    Bw4Lp = 22, Bw4Hp = 23,
    Bw5Lp = 24, Bw5Hp = 25,
    Bw6Lp = 26, Bw6Hp = 27,
    Bw7Lp = 28, Bw7Hp = 29,
    Bw8Lp = 30, Bw8Hp = 31,
    // Bessel (orders 2/4/6/8)
    Bes2Lp = 32, Bes2Hp = 33,
    Bes4Lp = 34, Bes4Hp = 35,
    Bes6Lp = 36, Bes6Hp = 37,
    Bes8Lp = 38, Bes8Hp = 39
}

/// <summary>
/// Extension methods for FilterType
/// </summary>
public static class FilterTypeExtensions
{
    /// <summary>
    /// The PEQ-only filter types (enum values 0-7). The crossover types
    /// (8-39) share the same enum but are never offered in a PEQ band's type
    /// picker — enumerate this instead of <c>Enum.GetValues</c> there.
    /// </summary>
    public static readonly FilterType[] PeqTypes =
    {
        FilterType.Flat, FilterType.Peaking, FilterType.LowShelf, FilterType.HighShelf,
        FilterType.LowPass, FilterType.HighPass, FilterType.Notch, FilterType.AllPass
    };

    /// <summary>True for the crossover filter types (8-39).</summary>
    public static bool IsCrossover(this FilterType type) =>
        (int)type >= (int)FilterType.Lr2Lp && (int)type <= (int)FilterType.Bes8Hp;

    public static string GetDisplayName(this FilterType type) => type switch
    {
        FilterType.Flat => "Off",
        FilterType.Peaking => "Peaking",
        FilterType.LowShelf => "Low Shelf",
        FilterType.HighShelf => "High Shelf",
        FilterType.LowPass => "Low Pass",
        FilterType.HighPass => "High Pass",
        FilterType.Notch => "Notch",
        FilterType.AllPass => "All Pass",
        _ => "Unknown"
    };

    public static string GetShortName(this FilterType type) => type switch
    {
        FilterType.Flat => "OFF",
        FilterType.Peaking => "PK",
        FilterType.LowShelf => "LS",
        FilterType.HighShelf => "HS",
        FilterType.LowPass => "LP",
        FilterType.HighPass => "HP",
        FilterType.Notch => "NO",
        FilterType.AllPass => "AP",
        _ => "?"
    };

    public static bool HasGain(this FilterType type) =>
        type is FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf;

    public static bool HasQ(this FilterType type) =>
        type is FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf
              or FilterType.LowPass or FilterType.HighPass
              or FilterType.Notch or FilterType.AllPass;

    public static bool HasFrequency(this FilterType type) =>
        type != FilterType.Flat;
}

/// <summary>
/// Parameters for a single biquad filter band
/// </summary>
public class FilterParams : IEquatable<FilterParams>
{
    public Guid Id { get; } = Guid.NewGuid();
    public FilterType Type { get; set; } = FilterType.Flat;
    public float Frequency { get; set; } = 1000.0f;
    public float Q { get; set; } = 0.707f;
    public float Gain { get; set; } = 0.0f;
    public bool IsActive { get; set; } = true; // For UI visibility toggle only

    /// <summary>
    /// User-bypass for this single band, preserving freq/Q/gain so the band can
    /// be re-enabled to its previous response. Firmware 1.1.4+. Wire encoding is
    /// strict: only the value 1 means bypassed — see band_bypass_spec.md §5.
    /// </summary>
    public bool Bypass { get; set; } = false;

    public FilterParams() { }

    public FilterParams(FilterType type, float freq, float q, float gain)
    {
        Type = type;
        Frequency = freq;
        Q = q;
        Gain = gain;
    }

    public FilterParams Clone() => new()
    {
        Type = Type,
        Frequency = Frequency,
        Q = Q,
        Gain = Gain,
        IsActive = IsActive,
        Bypass = Bypass
    };

    public bool Equals(FilterParams? other)
    {
        if (other is null) return false;
        return Type == other.Type &&
               Math.Abs(Frequency - other.Frequency) < 0.01f &&
               Math.Abs(Q - other.Q) < 0.001f &&
               Math.Abs(Gain - other.Gain) < 0.01f &&
               Bypass == other.Bypass;
    }

    public override bool Equals(object? obj) => Equals(obj as FilterParams);
    public override int GetHashCode() => HashCode.Combine(Type, Frequency, Q, Gain, Bypass);
}
