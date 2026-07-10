namespace DSPiConsole.Core.Models;

/// <summary>
/// Filter types matching the firmware definitions (config.h FilterType).
/// Value space is partitioned: 0-10 PEQ biquad types, 11-31 reserved,
/// 32-63 crossover filter types (Linkwitz-Riley / Butterworth / Bessel ×
/// order × HP/LP, used only in crossover bands), 64+ reserved.
/// firmware's filter_is_peq_type(t) == (t &lt; 32). The crossover block moved
/// from 8-39 to 32-63 at wire V13; PEQ types 8/9/10 (first-order all-pass and
/// shelves) were added at V13/V14. See crossover_filters_spec.md.
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
    AllPass = 7,       // 2nd-order (RBJ) all-pass
    AllPass1 = 8,      // 1st-order all-pass (V13+): frequency only
    LowShelf1 = 9,     // 1st-order low shelf (V14+): frequency + gain, no Q
    HighShelf1 = 10,   // 1st-order high shelf (V14+): frequency + gain, no Q

    // 11-31 reserved

    // ── Crossover types (FILTER_XOVER_FIRST = 32 .. FILTER_XOVER_LAST = 63) ──
    // Linkwitz-Riley (orders 2/4/6/8)
    Lr2Lp = 32, Lr2Hp = 33,
    Lr4Lp = 34, Lr4Hp = 35,
    Lr6Lp = 36, Lr6Hp = 37,
    Lr8Lp = 38, Lr8Hp = 39,
    // Butterworth (orders 1..8)
    Bw1Lp = 40, Bw1Hp = 41,
    Bw2Lp = 42, Bw2Hp = 43,
    Bw3Lp = 44, Bw3Hp = 45,
    Bw4Lp = 46, Bw4Hp = 47,
    Bw5Lp = 48, Bw5Hp = 49,
    Bw6Lp = 50, Bw6Hp = 51,
    Bw7Lp = 52, Bw7Hp = 53,
    Bw8Lp = 54, Bw8Hp = 55,
    // Bessel (orders 2/4/6/8)
    Bes2Lp = 56, Bes2Hp = 57,
    Bes4Lp = 58, Bes4Hp = 59,
    Bes6Lp = 60, Bes6Hp = 61,
    Bes8Lp = 62, Bes8Hp = 63
}

/// <summary>
/// Extension methods for FilterType
/// </summary>
public static class FilterTypeExtensions
{
    /// <summary>
    /// The PEQ-only filter types (enum values 0-10). The crossover types
    /// (32-63) share the same enum but are never offered in a PEQ band's type
    /// picker — enumerate this instead of <c>Enum.GetValues</c> there.
    /// </summary>
    public static readonly FilterType[] PeqTypes =
    {
        FilterType.Flat, FilterType.Peaking, FilterType.LowShelf, FilterType.HighShelf,
        FilterType.LowPass, FilterType.HighPass, FilterType.Notch, FilterType.AllPass,
        FilterType.AllPass1, FilterType.LowShelf1, FilterType.HighShelf1
    };

    /// <summary>True for the crossover filter types (32-63).</summary>
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
        FilterType.AllPass1 => "All Pass (1st)",
        FilterType.LowShelf1 => "Low Shelf (1st)",
        FilterType.HighShelf1 => "High Shelf (1st)",
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
        FilterType.AllPass1 => "AP1",
        FilterType.LowShelf1 => "LS1",
        FilterType.HighShelf1 => "HS1",
        _ => "?"
    };

    public static bool HasGain(this FilterType type) =>
        type is FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf
              or FilterType.LowShelf1 or FilterType.HighShelf1;

    // First-order all-pass/shelves are defined by frequency alone (no Q).
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
