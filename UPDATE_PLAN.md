# DSPiConsole-Windows — Firmware Catch-Up Plan

_Generated 2026-07-10 from a survey of the firmware (`~/Projects/DSPi-Firmware`),
the macOS reference app (`~/Projects/DSPi-Console-Mac`), and this Windows port._

## Headline

The firmware has advanced **9 wire-format versions** past the Windows port:

| | Windows port (now) | Firmware / macOS ref (V20) |
|---|---|---|
| Wire format version | **V11** | **V20** |
| Bulk params size | 3664 B | **5876 B** |
| Channel model | 11 ch (2 master inputs + 9 outputs) | **17 ch unified** (up to 8 first-class inputs + 9 outputs) |
| Crossover filter types | 8–39 | **32–63** |

**Critical:** firmware **V16 was a compat-breaking rewrite with no migration**.
`bulk_params_apply()` now rejects any payload where `format_version != 20` OR
`length != 5876`. The Windows app requests 3664 B and parses a V11 layout, so
against current firmware the connect-time state load **mis-parses into garbage**
(every section offset moved; inputs are now first-class channels). Bulk sync is
effectively broken against current firmware — this is not graceful degradation.
Individual GET/SET opcodes mostly still STALL-degrade, but whole-state load does not.
**Phase 0 is a hard prerequisite for everything else.**

---

## Already implemented on Windows (out of scope — only deltas noted below)

Matrix Mixer, Crossfeed, Loudness, Volume Leveller, Stats, Bulk Monitor, AutoEQ
browser, LG Sound Sync, DAC HW mute, master-volume & output-config persistence
modes, per-channel preamp, user volume, bootloader (`0xF0`) + factory reset
(`0x53`), settings shell. Only the **multichannel deltas** to these are missing.

---

## Missing filter types

| Item | Windows now | Firmware V20 | Notes |
|---|---|---|---|
| Crossover type numbering | 8–39 | **32–63** | `FILTER_XOVER_FIRST=32`; moved at wire V13. Windows enum values are now wrong. Touches `FilterType` enum, `CrossoverFilter.cs`, bulk parser. |
| `AllPass1` (1st-order all-pass) | missing | **type 8** | Added wire V13 |
| `LowShelf1` | missing | **type 9** | Added wire V14 |
| `HighShelf1` | missing | **type 10** | Added wire V14 |
| Crossover band 5-bit wValue | gated on V11+ | correct | Re-verify semantics at V13+ |

Value space partition (firmware `config.h`): **0–10 PEQ, 11–31 reserved,
32–63 crossover, 64+ reserved.** `filter_is_peq_type(t) == (t < 32)`.

Crossover map (32–63): LR2/4/6/8 = 32–39 · Butterworth ord 1–8 = 40–55 ·
Bessel 2/4/6/8 = 56–63 (each pair LP=even, HP=odd).

---

## Missing USB commands / features

| Opcode(s) | Feature | Wire ver |
|---|---|---|
| `0xA2/0xA3` | **Chunked bulk get/set** (mandatory — 5876 B > WinUSB 4 KB cap) | — |
| — | **17-channel unified model** (inputs as first-class EQ channels, up to 8) | V16 |
| `0xDE/0xDF` | Volume Leveller detector/apply channel masks | V18 |
| `0xFA/0xFB` | Loudness per-output mask | V19 |
| `0xFC/0xFD` | Crossfeed output-pair mask | V20 |
| `0xE4/0xE5` (indexed) + `0xE9/0xEF` | Multiple selectable SPDIF inputs (×3) | — |
| `0xF3/0xF4` | Multichannel I2S input (2/4/6/8 ch) | — |
| `0xA4–0xA8` | Test Signal Generator (15 signal types) | — |
| `0x84–0x8F`, `0x9D/0x9E` | Control Surfaces + IR remote (GPIO bindings, IR learn) | — |
| `0xF5–0xF9` | UART / I2C control interfaces | — |
| `0xCA–0xCE` | ADAT bulk output (RP2350-only) | V17 |
| — | Phase graphing + graph pop-out window | — |

---

## Phased plan

### Phase 0 — Foundation (BLOCKING)
- Rewrite `BulkParamsParser` for the **V20 5876-byte layout**. Section breakdown
  (firmware `bulk_params.h`): header 16 · global 16 · crossfeed 16 · legacy 16 ·
  delays 68 (17ch) · crosspoints 576 (8×9×8) · outputs 108 (9×12) · pins 8 ·
  eq 3264 (17×12×16) · channel_names 544 (17×32) · i2s_config 16 · leveller 20 ·
  preamp 32 (8 inputs) · master_volume 16 · input_config 16 · lg_sound_sync 16 ·
  user_volume 16 · dac_hw_mute 16 · crossovers 1088 (17×4×16) · adat_config 8.
- Implement **chunked bulk transfer** `0xA2/0xA3` (wValue=offset, wLength=chunk;
  replace the single-transfer `GetAllParams`). UART/I2C use plain 0xA0/A1.
- Refactor `Channel` model + `MainViewModel` + sidebar to the **inputs-as-channels**
  model (INPUTS/OUTPUTS split, up to 8 inputs, 17 wire channels).
- Compat policy: firmware now hard-requires exact V20 for bulk — drop the legacy
  V2–V12 size-anchor logic for bulk (individual opcodes still STALL-degrade).

### Phase 1 — Filter correctness
- Renumber crossover **8–39 → 32–63** (`FilterType`, `CrossoverFilter.cs`, parser).
- Add PEQ types **8 AllPass1, 9 LowShelf1, 10 HighShelf1** with firmware-version
  gating in the type picker.

### Phase 2 — Multichannel DSP masks (extend existing windows)
- Leveller detector/apply masks (`0xDE/0xDF`).
- Loudness per-output mask (`0xFA/0xFB`).
- Crossfeed output-pair mask (`0xFC/0xFD`).

### Phase 3 — Input expansion
- Multiple SPDIF inputs (indexed `0xE4/0xE5` + enable/config `0xE9/0xEF`).
- Multichannel I2S input channel count (`0xF3/0xF4`).

### Phase 4 — New feature windows (each self-contained)
- Test Signal Generator (`0xA4–0xA8`).
- ADAT output (`0xCA–0xCE`, RP2350 gating).
- Control Surfaces + IR remote (`0x84–0x8F`, `0x9D/0x9E`).
- UART / I2C control interfaces (`0xF5–0xF9`).

### Phase 5 — Graph / UX polish
- Phase graphing on the response graph.
- Graph pop-out window.

---

## Reference specs (firmware repo)

`Documentation/Features/` — `crossover_filters_spec.md`, `bulk_params_chunking.md`,
`volume_leveller_spec.md`, `loudness_compensation_spec.md`, `SPDIF_input_spec.md`,
`control_surfaces_spec.md`, `test_signals_spec.md`, `adat_output_spec.md`,
`control_interfaces_spec.md`, `i2s_multi_input.md`, `usb_8ch_input_spec.md`,
`notification_protocol_v2_spec.md`. Running notes: `Documentation/current_architecture.md`.

macOS reference impl: `Constants.swift` (opcodes), `Commands.swift` (USB impls),
`DSPMath.swift` (`FilterType`), `ContentView.swift` (PEQ/XO tabs), `DSPi_ConsoleApp.swift`
(settings, siggen, control surfaces).
