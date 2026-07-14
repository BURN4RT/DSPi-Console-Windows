namespace DSPiConsole.Core.Models;

/// <summary>
/// Psychoacoustic bass (psybass, firmware wire V23) parameter ranges and defaults.
/// One global parameter set is applied per output channel selected by a 16-bit
/// mask. The firmware silently clamps every value to these ranges. See firmware
/// Documentation/Features/psychoacoustic_bass_spec.md.
/// </summary>
public static class PsybassLimits
{
    public const float CutoffMinHz = 30f;
    public const float CutoffMaxHz = 300f;
    public const float CutoffDefaultHz = 80f;

    public const float HarmonicsMinDb = -24f;
    public const float HarmonicsMaxDb = 12f;
    public const float HarmonicsDefaultDb = 0f;

    public const float DriveMinDb = 0f;
    public const float DriveMaxDb = 18f;
    public const float DriveDefaultDb = 6f;

    public const float CharacterMinPct = 0f;
    public const float CharacterMaxPct = 100f;
    public const float CharacterDefaultPct = 50f;

    public const float OriginalMinDb = -60f;
    public const float OriginalMaxDb = 0f;
    public const float OriginalDefaultDb = 0f;

    public const ushort DefaultOutputMask = 0xFFFF;
}
