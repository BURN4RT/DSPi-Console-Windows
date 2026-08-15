using System;

namespace DSPiConsole.Core.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Control Surfaces + IR remote (firmware control_surfaces.h; caps v9, config v2,
// IR config v2, group/macro config v1). Physical GPIO controls (buttons, switches,
// pots, encoders, LEDs, PWM LEDs) and an IR receiver with learned remote commands,
// each bound to a DSP "noun" (parameter) + "action" (verb). Caps v9 adds target
// groups (one control drives a named set of channels) and macros (a button fires a
// short sequence of delayed steps). All wire structs are packed, little-endian.
//
// The whole editor is CAPS-DRIVEN: the firmware serves a capabilities header, a
// per-type action/pin table, and a per-noun descriptor table over 0x86; the host
// builds its pickers from those rather than hardcoding the noun/type tables (they
// are append-only and versioned). Only a client-side display-name table for nouns
// is baked in here (the wire format carries no strings for them).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Physical control component (firmware CsType). Index into the caps
/// type table.</summary>
public enum CsType : byte
{
    None = 0, Button = 1, Switch = 2, Pot = 3, Encoder = 4,
    Led = 5, LedPwm = 6, Ir = 7
}

/// <summary>DSP parameter a control drives or reflects (firmware CsNoun, 0..52).
/// The picker reads which nouns are available (and their ranges/units/targets)
/// live from caps — this enum is for the few places that special-case a noun.</summary>
public enum CsNoun : byte
{
    UserVolume = 0, MasterVolume = 1, UserMute = 2, Loudness = 3, Crossfeed = 4,
    Leveller = 5, Preset = 6, InputSource = 7, Clip = 8, EqBypass = 9,
    LgSync = 10, CrossfeedPreset = 11, CrossfeedItd = 12, LevellerAmount = 13,
    LevellerSpeed = 14, LevellerLookahead = 15, Preamp = 16, OutputGain = 17,
    OutputMute = 18, OutputEnable = 19, FilterFreq = 20, FilterGain = 21,
    FilterQ = 22, FilterType = 23, FilterBypass = 24, Siggen = 25,
    DacMuteTest = 26, ClipCh = 27, Level = 28, SpdifLock = 29, SampleRate = 30,
    UsbStreaming = 31, AdatActive = 32, LgPresent = 33, LgMuted = 34,
    // caps v4 additions. The six upmixer nouns are RP2350-only (action mask 0
    // on RP2040, the same convention as AdatActive).
    Upmix = 35, UpmixCenterMode = 36, UpmixSurroundMode = 37, UpmixStrength = 38,
    UpmixWidth = 39, UpmixPresence = 40, Psybass = 41, PsybassCutoff = 42,
    PsybassHarmonics = 43, PsybassDrive = 44, PsybassCharacter = 45,
    PsybassOriginal = 46, OutputDelay = 47, PresetReload = 48,
    // caps v7 additions: the two remaining loudness parameters.
    LoudnessSpl = 49, LoudnessIntensity = 50,
    // caps v8: loudest channel of the active input (read-only, untargeted) —
    // signal-presence sensing for amplifier trigger outputs.
    InputLevelMax = 51,
    // caps v9: an enum of macro slots. SET fires macro `value`; IND_EQUALS lights
    // while it runs. The live read is the running index, or 255 when idle.
    Macro = 52
}

/// <summary>Verb a control performs (firmware CsAction). Value = bit position;
/// caps action masks use <c>1 &lt;&lt; action</c>.</summary>
public enum CsAction : byte
{
    Adjust = 0,     // pot: absolute position → value range
    Step = 1,       // encoder: ± step per detent
    Inc = 2,        // button: + step per press
    Dec = 3,        // button: - step per press
    Toggle = 4,     // button: invert a bool
    Set = 5,        // button: set noun to `value`
    Follow = 6,     // switch: bool tracks switch position
    Trigger = 7,    // button: fire the noun's command
    IndEquals = 8,  // LED: lit while value == `value`
    Momentary = 9,  // button: set to `value` while held, restore on release
    IndAbove = 10,  // LED: lit while value >= `value`
    IndLevel = 11   // PWM LED: brightness follows value across range
}

/// <summary>Button gesture (firmware CsEvent); 0 for non-button types.</summary>
public enum CsEvent : byte { Press = 0, Long = 1, Double = 2 }

/// <summary>CsBinding.flags / IrCommand.flags / CsMacroStep.flags bitfield.</summary>
[Flags]
public enum CsFlags : byte
{
    None = 0,
    Invert = 0x01,   // input active-high w/ pull-down; LED active-low
    Reverse = 0x02,  // pot / encoder: invert direction
    Wrap = 0x04,     // enum STEP/INC/DEC wraps around
    Accel = 0x08,    // encoder only: fast rotation multiplies step
    Repeat = 0x10,   // button INC/DEC on press: auto-repeat while held
    // caps v9. Group re-reads `target` as a group index; the two modifiers
    // require it and the firmware rejects them without it.
    Group = 0x20,
    LinkAbs = 0x40,  // grouped pot ADJUST: drive members identical, not offset-preserving
    GroupAll = 0x80  // grouped IND_EQUALS/IND_ABOVE: lit when every member matches, not any
}

/// <summary>Noun value shape (firmware CS_KIND_*).</summary>
public enum CsKind : byte { Continuous = 0, Bool = 1, Enum = 2 }

/// <summary>Noun unit; fixes the wire encoding of value/range/step and the
/// stepping law (firmware CS_UNIT_*). <c>Ms</c> is a caps-v4 addition (8.8
/// milliseconds, linear, default step 0.1 ms) used by OutputDelay.</summary>
public enum CsUnit : byte { None = 0, Db = 1, Hz = 2, Q = 3, Percent = 4, Ms = 5 }

/// <summary>What a noun's <c>target</c> addresses (firmware CS_TARGET_*).</summary>
public enum CsTarget : byte
{
    None = 0, InputCh = 1, OutputCh = 2, DspCh = 3, DspBand = 4
}

/// <summary>Pin capability required by a type (firmware CS_PINCLASS_*).</summary>
public enum CsPinClass : byte { Any = 0, Adc = 1 }

/// <summary>IR protocol of a learned code (firmware CS_IR_PROTO_*).</summary>
public enum CsIrProto : byte { None = 0, Nec = 1, Rc5 = 2, Rc6 = 3, Hash = 4 }

/// <summary>REQ_CS_IR_LEARN wValue action.</summary>
public static class CsIrLearnAction
{
    public const ushort Cancel = 0;
    public const ushort Arm = 1;
    public const ushort Read = 2;
}

/// <summary>IR learn engine state (firmware CS_IR_LEARN_*).</summary>
public enum CsIrLearnState : byte { Idle = 0, Armed = 1, Done = 2, Timeout = 3 }

/// <summary>Control-surface deferred-apply status codes. 0x00..0x05 reuse the
/// shared PIN_CONFIG_* namespace (see Usb PinConfigResult); 0x10..0x1E are the
/// CS extension.</summary>
public static class CsStatus
{
    public const byte Success = 0x00;      // PIN_CONFIG_SUCCESS
    public const byte InvalidPin = 0x01;   // (CS: also "encoder's two pins equal")
    public const byte PinInUse = 0x02;

    public const byte InvalidSlot = 0x10;
    public const byte InvalidType = 0x11;
    public const byte InvalidNoun = 0x12;
    public const byte InvalidAction = 0x13;
    public const byte InvalidValue = 0x14;
    public const byte PinNotAdc = 0x15;
    public const byte Pending = 0x16;      // accepted, apply not yet run — poll again
    public const byte InvalidTarget = 0x17;
    public const byte InvalidEvent = 0x18;
    public const byte PwmConflict = 0x19;
    public const byte EventInUse = 0x1A;
    public const byte Busy = 0x1B;
    public const byte FlashError = 0x1C;
    public const byte IrInUse = 0x1D;
    public const byte NoIr = 0x1E;
    public const byte InvalidGroup = 0x1F;  // v9: group empty, out of range, or kind-mismatched
    public const byte InvalidMacro = 0x20;  // v9: bad macro index or step count
    public const byte InvalidStep = 0x21;   // v9: macro step record invalid

    /// <summary>Human-readable message for a CS status code.</summary>
    public static string Message(byte code) => code switch
    {
        Success => "OK",
        InvalidPin => "Invalid pin (or encoder pins are equal)",
        PinInUse => "Pin already in use by another peripheral",
        InvalidSlot => "Invalid slot",
        InvalidType => "Invalid component type",
        InvalidNoun => "Parameter not available on this device",
        InvalidAction => "Action not valid for this parameter",
        InvalidValue => "Invalid value",
        PinNotAdc => "This control needs an ADC pin (GPIO 26–28)",
        Pending => "Applying…",
        InvalidTarget => "Invalid channel/band target",
        InvalidEvent => "Invalid button event",
        PwmConflict => "PWM slice already driven by another LED",
        EventInUse => "That button gesture is already bound on this pin",
        Busy => "Device busy — try again",
        FlashError => "Flash write failed",
        IrInUse => "An IR receiver is already configured",
        NoIr => "No IR receiver is active",
        InvalidGroup => "Group is empty or doesn't match this parameter's channels",
        InvalidMacro => "Invalid macro",
        InvalidStep => "Invalid macro step",
        _ => $"Error 0x{code:X2}"
    };
}

/// <summary>Shared constants for the control-surface feature.</summary>
public static class CsLimits
{
    public const int MaxBindings = 16;
    public const int MaxIrCommands = 16;   // caps v6 doubled the table from 8
    public const int MaxGroups = 8;        // caps v9
    public const int MaxMacros = 8;        // caps v9
    public const int MaxMacroSteps = 8;    // caps v9
    public const int NameLen = 32;          // per-slot name buffer, NUL-terminated
    public const byte GpioUnused = 0xFF;
    public const ushort CapsAll = 0xFFFF;   // 0x86 wValue selecting the caps header
    public const byte LastSlotSave = 0xFF;  // CsStatusPacket.LastSlot for save/revert
    public const byte LastSlotIrFlag = 0x80;// high bit of LastSlot marks an IR sub-slot
    public const byte LastSlotGroupFlag = 0x40; // v9: LastSlot tag for a group SET
    public const byte LastSlotMacroFlag = 0x60; // v9: LastSlot tag for a macro header/step SET
    public const byte MacroIdle = 0xFF;     // CsExtStatusPacket.MacroRunning when nothing runs
    public const ushort MacroFireCancel = 0xFFFF; // 0x25 wValue cancelling the running macro
    public const byte NdfDeferred = 0x01;   // CsNounDesc.dflags

    /// <summary>Macro step <c>pre_delay</c> is in 10 ms units; the indicator
    /// <c>on_delay</c>/<c>off_delay</c> pair is in 0.1 s units.</summary>
    public const double MacroDelayUnitSeconds = 0.01;
    public const double IndicatorDelayUnitSeconds = 0.1;

    /// <summary>The only GPIOs a pot may occupy (ADC0..2). GPIO 29 (VSYS) excluded.</summary>
    public static readonly byte[] AdcPins = { 26, 27, 28 };
}

// ─────────────────────────────────────────────────────────────────────────────
// Fixed-point wire encoding helpers (firmware unit encoding; mirror the macOS
// csEncode*/csDecode* helpers verbatim — the 8.8 encoding and "step in octaves
// for Hz/Q" law are load-bearing).
// ─────────────────────────────────────────────────────────────────────────────
public static class CsWire
{
    /// <summary>Units whose value/range are 8.8 signed fixed point (1.0 = 256).</summary>
    public static bool UnitIsFixedPoint(CsUnit u) =>
        u is CsUnit.Db or CsUnit.Q or CsUnit.Percent or CsUnit.Ms;

    /// <summary>Encode a value/range operand for a noun's unit. DB/Q/PERCENT/MS
    /// use 8.8; NONE and HZ are plain integers.</summary>
    public static short EncodeValue(double v, CsUnit u) =>
        (short)Math.Round(UnitIsFixedPoint(u) ? v * 256.0 : v);

    public static double DecodeValue(short raw, CsUnit u) =>
        UnitIsFixedPoint(u) ? raw / 256.0 : raw;

    /// <summary>Encode a step operand. Everything except NONE is scaled ×256 —
    /// for HZ/Q the value is in octaves (256 = 1 octave); for DB/PERCENT/MS it is
    /// a linear dB/%/ms step. NONE (bool/enum) is a plain position count.</summary>
    public static short EncodeStep(double v, CsUnit u) =>
        (short)Math.Round(u == CsUnit.None ? v : v * 256.0);

    public static double DecodeStep(short raw, CsUnit u) =>
        u == CsUnit.None ? raw : raw / 256.0;

    public static string UnitSymbol(CsUnit u) => u switch
    {
        CsUnit.Db => "dB",
        CsUnit.Hz => "Hz",
        CsUnit.Q => "Q",
        CsUnit.Percent => "%",
        CsUnit.Ms => "ms",
        _ => ""
    };

    /// <summary>The step the firmware applies when a binding leaves <c>step</c>
    /// at 0, in the unit's own terms (octaves for the log units). Display only.</summary>
    public static double DefaultStep(CsUnit u) => u switch
    {
        CsUnit.Hz or CsUnit.Q => 1.0 / 12.0,  // 1/12 octave
        CsUnit.Ms => 0.1,                     // caps v4: ms detents are finer
        CsUnit.None => 1,                     // one enum position
        _ => 1                                // 1 dB / 1 %
    };

    /// <summary>Write a NUL-terminated 32-byte name field (group / macro records).
    /// Truncated to 31 UTF-8 bytes so the terminator always fits.</summary>
    public static void WriteName(string name, byte[] dest, int offset)
    {
        if (string.IsNullOrEmpty(name)) return;
        var raw = System.Text.Encoding.UTF8.GetBytes(name);
        int len = Math.Min(raw.Length, CsLimits.NameLen - 1);
        Array.Copy(raw, 0, dest, offset, len);
    }

    /// <summary>Read a NUL-terminated 32-byte name field.</summary>
    public static string ReadName(byte[] src, int offset)
    {
        int end = offset;
        int limit = Math.Min(src.Length, offset + CsLimits.NameLen);
        while (end < limit && src[end] != 0) end++;
        return System.Text.Encoding.UTF8.GetString(src, offset, end - offset);
    }

    public static string IrProtocolName(CsIrProto p) => p switch
    {
        CsIrProto.Nec => "NEC",
        CsIrProto.Rc5 => "RC5",
        CsIrProto.Rc6 => "RC6",
        CsIrProto.Hash => "Generic",
        _ => "None"
    };
}

/// <summary>Client-side display metadata for the 53 nouns (the wire format
/// carries no strings). Kept minimal — the picker still reads availability,
/// ranges, units and targets from caps.</summary>
public static class CsNounInfo
{
    private static readonly string[] Names =
    {
        "User Volume", "Master Volume", "User Mute", "Loudness", "Crossfeed",
        "Volume Leveller", "Preset", "Input Source", "Clip", "EQ Bypass",
        "LG Sound Sync", "Crossfeed Preset", "Crossfeed ITD", "Leveller Amount",
        "Leveller Speed", "Leveller Lookahead", "Preamp", "Output Gain",
        "Output Mute", "Output Enable", "Filter Frequency", "Filter Gain",
        "Filter Q", "Filter Type", "Filter Bypass", "Signal Generator",
        "DAC Mute Test", "Clip (Channel)", "Level", "S/PDIF Lock", "Sample Rate",
        "USB Streaming", "ADAT Active", "LG Present", "LG Muted",
        // caps v4
        "Stereo Upmixer", "Upmix Centre Mode", "Upmix Surround Mode",
        "Upmix Strength", "Upmix Centre Width", "Upmix Centre Presence",
        "Psychoacoustic Bass", "Bass Cutoff Frequency", "Bass Harmonics",
        "Bass Drive", "Bass Character", "Original Bass Level",
        "Output Delay", "Reload Preset",
        // caps v7
        "Loudness Reference SPL", "Loudness Intensity",
        // caps v8 / v9
        "Input Level (Loudest)", "Macro"
    };

    // Value labels for the enum-kind nouns. The picker only uses as many entries
    // as the noun's caps enum_count reports, and falls back to the bare index for
    // anything past the end, so a firmware that grows an enum still works.
    private static readonly string[] PresetNames =
        { "Preset 1", "Preset 2", "Preset 3", "Preset 4", "Preset 5",
          "Preset 6", "Preset 7", "Preset 8", "Preset 9", "Preset 10" };

    private static readonly string[] InputSourceNames =
        { "USB", "S/PDIF", "I2S", "ADAT", "S/PDIF 2", "S/PDIF 3", "S/PDIF 4" };

    private static readonly string[] CrossfeedPresetNames =
        { "Default", "Chu Moy", "Jan Meier", "Custom" };

    private static readonly string[] LevellerSpeedNames = { "Slow", "Medium", "Fast" };

    // Indexed by the wire FilterType value. Firmware only cycles 0-10 from the
    // front panel, but a band can hold any PEQ type the host wrote, so the table
    // covers the whole PEQ block.
    private static readonly string[] FilterTypeNames =
        { "Flat", "Peaking", "Low Shelf", "High Shelf", "High Cut 12dB", "Low Cut 12dB",
          "Notch", "All Pass", "All Pass (1st)", "Low Shelf (1st)",
          "High Shelf (1st)", "Linkwitz Transform", "High Cut 6dB", "Low Cut 6dB" };

    private static readonly string[] SampleRateNames = { "44.1 kHz", "48 kHz", "96 kHz" };

    // Both wire 0/1/2; the app shows the upmixer's product mode names. The centre
    // enum puts Off last (appended at caps v5) while the surround enum puts it first.
    private static readonly string[] UpmixCenterModeNames = { "Sinner", "Logician", "Off" };
    private static readonly string[] UpmixSurroundModeNames = { "Off", "Sinner", "Logician" };

    /// <summary>Label for one value of an enum-kind noun, e.g. "S/PDIF" for
    /// InputSource 1. Falls back to the plain index for unknown nouns/values.</summary>
    public static string EnumLabel(int noun, int value)
    {
        var table = (CsNoun)noun switch
        {
            CsNoun.Preset => PresetNames,
            CsNoun.InputSource => InputSourceNames,
            CsNoun.CrossfeedPreset => CrossfeedPresetNames,
            CsNoun.LevellerSpeed => LevellerSpeedNames,
            CsNoun.FilterType => FilterTypeNames,
            CsNoun.SampleRate => SampleRateNames,
            CsNoun.UpmixCenterMode => UpmixCenterModeNames,
            CsNoun.UpmixSurroundMode => UpmixSurroundModeNames,
            _ => null
        };
        return table != null && value >= 0 && value < table.Length
            ? table[value]
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Name(int noun) =>
        noun >= 0 && noun < Names.Length ? Names[noun] : $"Noun {noun}";

    public static string Name(CsNoun noun) => Name((int)noun);

    /// <summary>Coarse UI grouping for the noun picker.</summary>
    public static string Group(int noun) => (CsNoun)noun switch
    {
        CsNoun.UserVolume or CsNoun.MasterVolume or CsNoun.UserMute or CsNoun.Preamp
            or CsNoun.Level or CsNoun.InputLevelMax => "Volume",
        CsNoun.Loudness or CsNoun.Crossfeed or CsNoun.Leveller or CsNoun.EqBypass
            or CsNoun.CrossfeedPreset or CsNoun.CrossfeedItd or CsNoun.LevellerAmount
            or CsNoun.LevellerSpeed or CsNoun.LevellerLookahead
            or CsNoun.LoudnessSpl or CsNoun.LoudnessIntensity => "DSP",
        CsNoun.Upmix or CsNoun.UpmixCenterMode or CsNoun.UpmixSurroundMode
            or CsNoun.UpmixStrength or CsNoun.UpmixWidth or CsNoun.UpmixPresence => "Upmixer",
        CsNoun.Psybass or CsNoun.PsybassCutoff or CsNoun.PsybassHarmonics
            or CsNoun.PsybassDrive or CsNoun.PsybassCharacter
            or CsNoun.PsybassOriginal => "Psychoacoustic Bass",
        CsNoun.OutputGain or CsNoun.OutputMute or CsNoun.OutputEnable
            or CsNoun.OutputDelay => "Output",
        CsNoun.FilterFreq or CsNoun.FilterGain or CsNoun.FilterQ or CsNoun.FilterType
            or CsNoun.FilterBypass => "Filter",
        CsNoun.Preset or CsNoun.InputSource or CsNoun.Siggen or CsNoun.DacMuteTest
            or CsNoun.SampleRate or CsNoun.UsbStreaming or CsNoun.AdatActive
            or CsNoun.PresetReload or CsNoun.Macro => "System",
        CsNoun.LgSync or CsNoun.LgPresent or CsNoun.LgMuted or CsNoun.SpdifLock => "LG / S/PDIF",
        CsNoun.Clip or CsNoun.ClipCh => "Status",
        _ => "Other"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Wire structs
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The 24-byte CsBinding wire struct (REQ_SET/GET_CS_BINDING). A
/// default (all-zero) instance is the "cleared slot" blob (type = None). A
/// configured single-pin binding must set <see cref="Gpio1"/> to 0xFF.</summary>
public sealed class CsBinding
{
    public const int WireSize = 24;

    public CsType Type;         // @0
    public byte Noun;           // @1  CsNoun
    public byte Action;         // @2  CsAction
    public CsFlags Flags;       // @3
    public byte Gpio0;          // @4  primary GPIO
    public byte Gpio1 = CsLimits.GpioUnused; // @5  second GPIO (encoder); 0xFF = single-pin
    public byte Event;          // @6  CsEvent (buttons); 0 otherwise
    public byte Target;         // @7  channel address
    public byte Index;          // @8  filter band (DSP_BAND nouns)
    // @9 reserved
    public short Value;         // @10 SET/MOMENTARY / IND comparand (unit-encoded)
    public short Step;          // @12 STEP/INC/DEC size; 0 = unit default
    public short RangeMin;      // @14 pot/IND_LEVEL span low; both 0 = full range
    public short RangeMax;      // @16 pot/IND_LEVEL span high
    public ushort OnDelay;      // @18 caps v8: TON filter, 0.1 s units (LED IND_EQUALS/IND_ABOVE only)
    public ushort OffDelay;     // @20 caps v8: TOF filter, same units and rules
    // @22..23 reserved2[2]

    public bool IsConfigured => Type != CsType.None;

    /// <summary>True when <see cref="Target"/> is a group index rather than a
    /// channel (caps v9).</summary>
    public bool IsGrouped => (Flags & CsFlags.Group) != 0;

    /// <summary>A fresh cleared-slot binding (gpio1 = 0 so the blob is all-zero).</summary>
    public static CsBinding Cleared() => new() { Gpio1 = 0 };

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = (byte)Type;
        b[1] = Noun;
        b[2] = Action;
        b[3] = (byte)Flags;
        b[4] = Gpio0;
        b[5] = Gpio1;
        b[6] = Event;
        b[7] = Target;
        b[8] = Index;
        // b[9] reserved
        BitConverter.GetBytes(Value).CopyTo(b, 10);
        BitConverter.GetBytes(Step).CopyTo(b, 12);
        BitConverter.GetBytes(RangeMin).CopyTo(b, 14);
        BitConverter.GetBytes(RangeMax).CopyTo(b, 16);
        BitConverter.GetBytes(OnDelay).CopyTo(b, 18);
        BitConverter.GetBytes(OffDelay).CopyTo(b, 20);
        return b;
    }

    public static CsBinding? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsBinding
        {
            Type = (CsType)d[0],
            Noun = d[1],
            Action = d[2],
            Flags = (CsFlags)d[3],
            Gpio0 = d[4],
            Gpio1 = d[5],
            Event = d[6],
            Target = d[7],
            Index = d[8],
            Value = BitConverter.ToInt16(d, 10),
            Step = BitConverter.ToInt16(d, 12),
            RangeMin = BitConverter.ToInt16(d, 14),
            RangeMax = BitConverter.ToInt16(d, 16),
            OnDelay = BitConverter.ToUInt16(d, 18),
            OffDelay = BitConverter.ToUInt16(d, 20),
        };
    }

    public CsBinding Clone() => (CsBinding)MemberwiseClone();

    /// <summary>Wire-equality: compares the serialized bytes (ignores any
    /// reserved padding differences, which are always zero).</summary>
    public bool WireEquals(CsBinding other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>The 16-byte IrCommand wire struct (REQ_SET/GET_CS_IR_CMD). A
/// button-shaped command fired by a learned code instead of a GPIO edge. There
/// is no gpio/event field — the receiver's pin lives on the container CsBinding
/// (CS_TYPE_IR). An occupied sub-slot with <see cref="Code"/> == 0 is rejected.</summary>
public sealed class IrCommand
{
    public const int WireSize = 16;

    public byte Noun;           // @0  CsNoun
    public byte Action;         // @1  CsAction (button subset: Inc/Dec/Toggle/Set/Trigger/Momentary)
    public CsFlags Flags;       // @2  Wrap | Repeat only
    public byte Target;         // @3  channel address
    public byte Index;          // @4  filter band
    public CsIrProto Proto;     // @5  0 = empty sub-slot
    public short Value;         // @6  SET/MOMENTARY target (unit-encoded)
    public short Step;          // @8  INC/DEC size; 0 = unit default
    // @10,11 reserved
    public uint Code;           // @12 learned code, LE; 0 = never learned

    public bool IsConfigured => Proto != CsIrProto.None;

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = Noun;
        b[1] = Action;
        b[2] = (byte)Flags;
        b[3] = Target;
        b[4] = Index;
        b[5] = (byte)Proto;
        BitConverter.GetBytes(Value).CopyTo(b, 6);
        BitConverter.GetBytes(Step).CopyTo(b, 8);
        // b[10],b[11] reserved
        BitConverter.GetBytes(Code).CopyTo(b, 12);
        return b;
    }

    public static IrCommand? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new IrCommand
        {
            Noun = d[0],
            Action = d[1],
            Flags = (CsFlags)d[2],
            Target = d[3],
            Index = d[4],
            Proto = (CsIrProto)d[5],
            Value = BitConverter.ToInt16(d, 6),
            Step = BitConverter.ToInt16(d, 8),
            Code = BitConverter.ToUInt32(d, 12),
        };
    }

    public IrCommand Clone() => (IrCommand)MemberwiseClone();

    public bool WireEquals(IrCommand other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Display label for the learned code, e.g. "NEC 0x00FF00FF".</summary>
    public string CodeLabel => IsConfigured
        ? $"{CsWire.IrProtocolName(Proto)} 0x{Code:X8}"
        : "Not learned";
}

/// <summary>The 40-byte CsGroup wire struct (REQ_SET/GET_CS_GROUP; caps v9). A
/// named set of channels one control addresses as a unit. <see cref="Kind"/> 0
/// marks the slot empty, and the firmware then requires the whole record to be
/// zero, so <see cref="Cleared"/> is the only legal clear payload.</summary>
public sealed class CsGroup
{
    public const int WireSize = 40;

    public CsTarget Kind;       // @0  InputCh / OutputCh / DspCh; None = empty slot
    // @1..3 reserved
    public uint MemberMask;     // @4  bit N = channel N of the kind's space
    public string Name = "";    // @8  32 bytes, NUL-terminated

    public bool IsConfigured => Kind != CsTarget.None && MemberMask != 0;

    /// <summary>Number of channels in the group.</summary>
    public int MemberCount
    {
        get
        {
            int n = 0;
            for (uint m = MemberMask; m != 0; m &= m - 1) n++;
            return n;
        }
    }

    public static CsGroup Cleared() => new();

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = (byte)Kind;
        BitConverter.GetBytes(MemberMask).CopyTo(b, 4);
        CsWire.WriteName(Name, b, 8);
        return b;
    }

    public static CsGroup? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsGroup
        {
            Kind = (CsTarget)d[0],
            MemberMask = BitConverter.ToUInt32(d, 4),
            Name = CsWire.ReadName(d, 8),
        };
    }

    public CsGroup Clone() => (CsGroup)MemberwiseClone();

    public bool WireEquals(CsGroup other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>The 12-byte CsMacroStep wire struct (REQ_SET_CS_MACRO_STEP; caps v9).
/// A stripped, button-shaped binding fired by the sequencer after
/// <see cref="PreDelay"/>. An all-zero record is an empty step (skipped).</summary>
public sealed class CsMacroStep
{
    public const int WireSize = 12;

    public byte Noun;           // @0  CsNoun (never Macro — no nesting)
    public byte Action;         // @1  CsAction: Set / Toggle / Inc / Dec / Trigger
    public CsFlags Flags;       // @2  Wrap | Group only
    public byte Target;         // @3  channel, or group index when Group is set
    public byte Index;          // @4  filter band for DSP_BAND nouns
    // @5 reserved
    public short Value;         // @6  as CsBinding.Value
    public short Step;          // @8  INC/DEC size; 0 = unit default
    public ushort PreDelay;     // @10 delay before this step runs, 10 ms units

    /// <summary>An empty step is the all-zero record. Noun 0 with action 0
    /// (ADJUST, which no step may use) never occurs in a written step.</summary>
    public bool IsConfigured =>
        Noun != 0 || Action != 0 || Flags != CsFlags.None || Target != 0
        || Index != 0 || Value != 0 || Step != 0 || PreDelay != 0;

    public bool IsGrouped => (Flags & CsFlags.Group) != 0;

    /// <summary>Delay before this step runs, in seconds.</summary>
    public double PreDelaySeconds
    {
        get => PreDelay * CsLimits.MacroDelayUnitSeconds;
        set => PreDelay = (ushort)Math.Clamp(Math.Round(value / CsLimits.MacroDelayUnitSeconds), 0, ushort.MaxValue);
    }

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = Noun;
        b[1] = Action;
        b[2] = (byte)Flags;
        b[3] = Target;
        b[4] = Index;
        // b[5] reserved
        BitConverter.GetBytes(Value).CopyTo(b, 6);
        BitConverter.GetBytes(Step).CopyTo(b, 8);
        BitConverter.GetBytes(PreDelay).CopyTo(b, 10);
        return b;
    }

    public static CsMacroStep? FromBytes(byte[] d, int off = 0)
    {
        if (d == null || d.Length < off + WireSize) return null;
        return new CsMacroStep
        {
            Noun = d[off],
            Action = d[off + 1],
            Flags = (CsFlags)d[off + 2],
            Target = d[off + 3],
            Index = d[off + 4],
            Value = BitConverter.ToInt16(d, off + 6),
            Step = BitConverter.ToInt16(d, off + 8),
            PreDelay = BitConverter.ToUInt16(d, off + 10),
        };
    }

    public CsMacroStep Clone() => (CsMacroStep)MemberwiseClone();

    public bool WireEquals(CsMacroStep other)
    {
        if (other == null) return false;
        var a = ToBytes();
        var b = other.ToBytes();
        for (int i = 0; i < WireSize; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>One macro slot: the 132-byte CsMacro read back by REQ_GET_CS_MACRO
/// (caps v9). Writing is split — the 36-byte header (name + step count) goes over
/// REQ_SET_CS_MACRO and each step over REQ_SET_CS_MACRO_STEP, because a whole
/// macro exceeds the 64-byte vendor SET buffer. Write the steps first and the
/// header last so a concurrent fire never sees a count past the written steps.</summary>
public sealed class CsMacro
{
    public const int WireSize = 132;
    public const int HeaderWireSize = 36;   // CsMacroHeaderWire

    public string Name = "";    // @0  32 bytes, NUL-terminated
    public byte StepCount;      // @32 steps executed = Steps[0..StepCount-1]
    // @33..35 reserved
    public CsMacroStep[] Steps = NewSteps();   // @36, 8 × 12 bytes

    public bool IsConfigured => StepCount > 0 || Name.Length > 0;

    private static CsMacroStep[] NewSteps()
    {
        var a = new CsMacroStep[CsLimits.MaxMacroSteps];
        for (int i = 0; i < a.Length; i++) a[i] = new CsMacroStep();
        return a;
    }

    /// <summary>The 36-byte REQ_SET_CS_MACRO payload (name + step count).</summary>
    public byte[] HeaderToBytes()
    {
        var b = new byte[HeaderWireSize];
        CsWire.WriteName(Name, b, 0);
        b[32] = StepCount;
        return b;
    }

    public static CsMacro? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        var m = new CsMacro
        {
            Name = CsWire.ReadName(d, 0),
            StepCount = d[32],
        };
        for (int i = 0; i < CsLimits.MaxMacroSteps; i++)
            m.Steps[i] = CsMacroStep.FromBytes(d, 36 + i * CsMacroStep.WireSize) ?? new CsMacroStep();
        return m;
    }

    public CsMacro Clone()
    {
        var m = (CsMacro)MemberwiseClone();
        m.Steps = new CsMacroStep[Steps.Length];
        for (int i = 0; i < Steps.Length; i++) m.Steps[i] = Steps[i].Clone();
        return m;
    }

    public bool WireEquals(CsMacro other)
    {
        if (other == null) return false;
        if (!string.Equals(Name, other.Name, StringComparison.Ordinal)) return false;
        if (StepCount != other.StepCount) return false;
        for (int i = 0; i < Steps.Length; i++)
            if (!Steps[i].WireEquals(other.Steps[i])) return false;
        return true;
    }
}

/// <summary>The 24-byte CsExtStatusPacket (REQ_GET_CS_EXT_STATUS; caps v9): the
/// group/macro companion to <see cref="CsStatusPacket"/>.</summary>
public sealed class CsExtStatusPacket
{
    public const int WireSize = 24;

    public byte MaxGroups;
    public byte MaxMacros;
    public byte MaxMacroSteps;
    public byte MacroRunning;   // running macro index; 0xFF = idle
    public byte MacroStep;      // current step index while running
    public byte[] GroupStatus = new byte[CsLimits.MaxGroups];
    public byte[] MacroStatus = new byte[CsLimits.MaxMacros];

    public bool IsMacroRunning => MacroRunning != CsLimits.MacroIdle;

    public byte GroupHealth(int idx) =>
        idx >= 0 && idx < GroupStatus.Length ? GroupStatus[idx] : (byte)0;

    public byte MacroHealth(int idx) =>
        idx >= 0 && idx < MacroStatus.Length ? MacroStatus[idx] : (byte)0;

    public static CsExtStatusPacket? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        var s = new CsExtStatusPacket
        {
            MaxGroups = d[0],
            MaxMacros = d[1],
            MaxMacroSteps = d[2],
            MacroRunning = d[3],
            MacroStep = d[4],
        };
        Array.Copy(d, 8, s.GroupStatus, 0, CsLimits.MaxGroups);
        Array.Copy(d, 16, s.MacroStatus, 0, CsLimits.MaxMacros);
        return s;
    }
}

/// <summary>One entry in the caps type table (4 bytes; CsTypeDesc).</summary>
public readonly struct CsTypeDesc
{
    public readonly ushort Actions;   // CS_ACT_BIT mask
    public readonly byte PinCount;    // 1 or 2
    public readonly CsPinClass PinClass;

    public CsTypeDesc(ushort actions, byte pinCount, CsPinClass pinClass)
    {
        Actions = actions; PinCount = pinCount; PinClass = pinClass;
    }

    public bool SupportsAction(CsAction a) => (Actions & (1u << (int)a)) != 0;
}

/// <summary>The caps header + type table (REQ_GET_CS_CAPS, wValue 0xFFFF). Length
/// is variable — the v3 tail (<see cref="MaxIrCommands"/>) sits at
/// <c>4 + 4*TypeCount</c> so a future type-table growth won't move it, and the v9
/// group/macro limits follow it in the three bytes a pre-v9 firmware reserved
/// (they read 0 there). Parse defensively rather than assuming 40 bytes.</summary>
public sealed class CsCapsHeader
{
    public byte CapsVersion;
    public byte MaxBindings;
    public byte TypeCount;
    public byte NounCount;
    public CsTypeDesc[] Types = Array.Empty<CsTypeDesc>();
    public byte MaxIrCommands;   // v3; 0 on a v2 header = IR unavailable
    public byte MaxGroups;       // v9; 0 on a pre-v9 header = groups unavailable
    public byte MaxMacros;       // v9
    public byte MaxMacroSteps;   // v9

    public bool IsValid => TypeCount > 0 && NounCount > 0;

    /// <summary>Whether this firmware serves the caps-v9 group and macro
    /// commands (0x20–0x26).</summary>
    public bool HasGroupsAndMacros => MaxGroups > 0 && MaxMacros > 0;

    public static CsCapsHeader? FromBytes(byte[] d)
    {
        if (d == null || d.Length < 4) return null;
        var h = new CsCapsHeader
        {
            CapsVersion = d[0],
            MaxBindings = d[1],
            TypeCount = d[2],
            NounCount = d[3],
        };
        int count = h.TypeCount;
        // Only parse as many type descriptors as actually fit in the buffer.
        int maxFit = (d.Length - 4) / 4;
        if (count > maxFit) count = maxFit;
        h.Types = new CsTypeDesc[count];
        for (int i = 0; i < count; i++)
        {
            int off = 4 + i * 4;
            h.Types[i] = new CsTypeDesc(
                BitConverter.ToUInt16(d, off),
                d[off + 2],
                (CsPinClass)d[off + 3]);
        }
        int irOff = 4 + h.TypeCount * 4;
        h.MaxIrCommands = irOff < d.Length ? d[irOff] : (byte)0;
        h.MaxGroups = irOff + 1 < d.Length ? d[irOff + 1] : (byte)0;
        h.MaxMacros = irOff + 2 < d.Length ? d[irOff + 2] : (byte)0;
        h.MaxMacroSteps = irOff + 3 < d.Length ? d[irOff + 3] : (byte)0;
        return h;
    }

    /// <summary>Type descriptor for a CsType, or null if out of range.</summary>
    public CsTypeDesc? DescFor(CsType t)
    {
        int i = (int)t;
        return i >= 0 && i < Types.Length ? Types[i] : null;
    }
}

/// <summary>One noun's descriptor (12 bytes; CsNounDesc), from 0x86 wValue=noun.</summary>
public sealed class CsNounDesc
{
    public const int WireSize = 12;

    public CsKind Kind;
    public byte EnumCount;
    public ushort Actions;     // accepted-action mask; 0 = unavailable on platform
    public short MinQ;         // continuous range low, unit-encoded
    public short MaxQ;         // continuous range high
    public CsUnit Unit;
    public CsTarget TargetKind;
    public byte TargetCount;   // valid targets 0..count-1; 0 = untargeted
    public byte DFlags;

    public bool IsAvailable => Actions != 0;
    public bool IsTargeted => TargetKind != CsTarget.None && TargetCount > 0;
    public bool HasBand => TargetKind == CsTarget.DspBand;
    public bool SupportsAction(CsAction a) => (Actions & (1u << (int)a)) != 0;
    public double Min => CsWire.DecodeValue(MinQ, Unit);
    public double Max => CsWire.DecodeValue(MaxQ, Unit);

    public static CsNounDesc? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsNounDesc
        {
            Kind = (CsKind)d[0],
            EnumCount = d[1],
            Actions = BitConverter.ToUInt16(d, 2),
            MinQ = BitConverter.ToInt16(d, 4),
            MaxQ = BitConverter.ToInt16(d, 6),
            Unit = (CsUnit)d[8],
            TargetKind = (CsTarget)d[9],
            TargetCount = d[10],
            DFlags = d[11],
        };
    }
}

/// <summary>The 41-byte CsStatusPacket (REQ_GET_CS_STATUS). The base v2 layout is
/// 22 bytes; the IR tail is only read when present, and moved at caps v6 (the
/// active mask widened to a uint16 for 16 sub-slots, pushing the learn state to
/// 24 and the per-sub-slot status array to 25). A v3..v5 firmware still answers
/// with the 32-byte layout, so the tail is parsed by length.</summary>
public sealed class CsStatusPacket
{
    public byte LastStatus;      // most recent deferred CS SET result
    public byte LastSlot;        // slot; 0x80|n = IR sub-slot; 0xFF = save/revert
    public byte MaxBindings;
    public bool Dirty;           // v3; live differs from flash
    public ushort ActiveMask;    // bit N = binding N live
    public byte[] SlotStatus = new byte[16];
    public ushort IrActiveMask;  // v3 (uint16 since v6); bit N = IR command N live
    public CsIrLearnState IrLearnState; // v3
    public byte[] IrCmdStatus = new byte[CsLimits.MaxIrCommands]; // v3 (16 since v6)

    public bool IsSlotActive(int slot) =>
        slot >= 0 && slot < 16 && (ActiveMask & (1 << slot)) != 0;

    public byte SlotHealth(int slot) =>
        slot >= 0 && slot < SlotStatus.Length ? SlotStatus[slot] : (byte)0;

    public bool IsIrCmdActive(int sub) =>
        sub >= 0 && sub < CsLimits.MaxIrCommands && (IrActiveMask & (1 << sub)) != 0;

    public byte IrCmdHealth(int sub) =>
        sub >= 0 && sub < IrCmdStatus.Length ? IrCmdStatus[sub] : (byte)0;

    public static CsStatusPacket? FromBytes(byte[] d)
    {
        if (d == null || d.Length < 22) return null;
        var s = new CsStatusPacket
        {
            LastStatus = d[0],
            LastSlot = d[1],
            MaxBindings = d[2],
            Dirty = d[3] != 0,
            ActiveMask = BitConverter.ToUInt16(d, 4),
        };
        Array.Copy(d, 6, s.SlotStatus, 0, 16);
        if (d.Length >= 41)
        {
            // v6 tail: 16 sub-slots.
            s.IrActiveMask = BitConverter.ToUInt16(d, 22);
            s.IrLearnState = (CsIrLearnState)d[24];
            Array.Copy(d, 25, s.IrCmdStatus, 0, 16);
        }
        else if (d.Length >= 24)
        {
            // v3..v5 tail: 8 sub-slots, byte-wide mask.
            s.IrActiveMask = d[22];
            s.IrLearnState = (CsIrLearnState)d[23];
            if (d.Length >= 32) Array.Copy(d, 24, s.IrCmdStatus, 0, 8);
        }
        return s;
    }
}

/// <summary>The 8-byte IR-learn result (REQ_CS_IR_LEARN wValue=2).</summary>
public sealed class CsIrLearnResult
{
    public const int WireSize = 8;

    public CsIrLearnState State;
    public CsIrProto Proto;
    public uint Code;   // LE

    public bool IsDone => State == CsIrLearnState.Done;
    public bool IsTimeout => State == CsIrLearnState.Timeout;

    public static CsIrLearnResult? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CsIrLearnResult
        {
            State = (CsIrLearnState)d[0],
            Proto = (CsIrProto)d[1],
            Code = BitConverter.ToUInt32(d, 4),
        };
    }
}
