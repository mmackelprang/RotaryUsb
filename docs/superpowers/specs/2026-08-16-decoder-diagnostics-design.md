# Decoder Diagnostics via HID Input Report ID 0x04

## Overview

Make the RotaryUsb quadrature decoder observable from the host without changing what it decodes.

A prior debugging session left an uncommitted patch in `firmware-cpp/main_generic_hid.cpp` that
overwrote the decoded encoder position in Input Report ID 0x01 with the raw GPIO pin state
(`positions[i] = (a << 2) | (b << 1) | sw`) and force-sent that report at 10 Hz. That patch answered
the question it was built for — the wiring is good, confirmed by idle `7` (`0b111`), `6` on button
press, and `3`/`5` while turning — but it did so by destroying the very output under test. The
decoder itself (`GenericHidEncoder::update()`, `get_position()`) has still never been observed
end-to-end.

This design replaces that patch with a permanent, non-destructive diagnostic surface: a **separate
Input Report ID 0x04** carrying per-encoder raw pins and three counters, plus a host-side view that
renders them. Report ID 0x01 returns to carrying real decoded positions, byte-identical in layout, so
existing hosts keep working.

The key property: **diagnostics ship in the normal build.** There is no diagnostic firmware variant,
so the drift that caused this situation cannot recur.

Scope: C++ Generic HID firmware, its CMake build, and the Windows console app. Keyboard HID mode is
unchanged.

## Problem Statement

Three distinct problems are in scope, all traceable to the same uncommitted working tree:

1. **The decoder is unvalidated.** Report ID 0x01 carries pin state, not position. Nothing on the
   host has ever displayed `get_position()` output from this firmware.
2. **The build personality is an uncommitted local edit.** `firmware-cpp/CMakeLists.txt` has
   `add_executable` switched from `main.cpp` to `main_generic_hid.cpp` in the working tree only. The
   README documents a manual `cp main_generic_hid.cpp main.cpp` step instead. Both are ways of losing
   track of what is actually flashed.
3. **Two genuine host bug fixes are stranded** in the same uncommitted diff and would be lost to a
   `git checkout`.

## Requirements

1. Input Report ID 0x01 keeps its exact 21-byte layout and carries real decoded positions again.
2. A new Input Report ID 0x04 carries, per encoder: raw pin state, cumulative A/B edge count,
   cumulative invalid-transition count, cumulative emitted-detent count.
3. Report ID 0x04 also carries the decoder's **active** `steps_per_detent` value, so the host
   displays what the firmware is actually using rather than what the host believes it configured.
4. The host can zero all diagnostic counters without a reflash or replug.
5. The host can toggle `global_flags` bit 0 (4 ↔ 2 steps/detent) and persist it to flash.
6. Firmware personality is selected by a CMake cache variable, not a file edit.
7. The onboard-LED heartbeat and both `windows-example/Program.cs` bug fixes are preserved.
8. The host degrades legibly when no report ID 0x04 arrives — the empty state must be
   distinguishable from dead hardware.
9. No change to decoder behavior. `steps_per_detent` default stays 4.

## Key Finding: `steps_per_detent` Is Already Runtime-Switchable

The originating brief identified `steps_per_detent_ = 4` at `main_generic_hid.cpp:356` as a hardcoded
value. That line is only the constructor's member initializer. The value actually used by the decode
path is set at runtime in three places:

```cpp
int8_t spd = (device_config.global_flags & 0x01) ? 2 : 4;   // lines 624, 639
int8_t steps_per_detent = (device_config.global_flags & 0x01) ? 2 : 4;  // line 746
```

and applied via `encoders[i]->apply_config(&device_config.encoders[i], spd)`.

`docs/superpowers/specs/2026-03-21-runtime-config-design.md` already documents `global_flags` bit 0
as exactly this switch: *"0 = 4 steps/detent for KY-040, 1 = 2 steps/detent for bare EC11."*

`windows-example/Program.cs` serializes `GlobalFlags` into the config payload (`data[1] = GlobalFlags`)
but **contains no code path that ever sets it**. The capability shipped; the control was never built.

Consequence for this design: exposing that toggle is not a speculative fix. Firmware behavior and the
default are unchanged; the host merely surfaces a switch that already exists, and the user decides
based on measured evidence.

## Design

### Firmware: diagnostic counters

`GenericHidEncoder` gains three `uint32_t` counters and a raw-pin reader. Increment sites sit inside
the existing transition-decode block in `update()`:

| Counter | Increment site | Meaning |
|---------|---------------|---------|
| `edge_count_` | every time `current_ab_state != last_ab_state_` | observed A/B state changes, valid or not |
| `invalid_count_` | when `TRANSITION_TABLE[index] == 0` inside that block | illegal quadrature transitions |
| `detent_count_` | when the `steps_ >= steps_per_detent_` threshold fires | detents the decoder actually emitted |

Two properties matter and are load-bearing for the test plan:

- **`invalid_count_` is unambiguous.** The transition table contains zeros at indices where
  `last == current` (0, 5, 10, 15) and where both A and B changed at once (3, 6, 9, 12). Because the
  enclosing `if` already guarantees `current != last`, a zero result inside that block can *only*
  mean a simultaneous A+B change — physically impossible in clean quadrature. It is bounce, a
  marginal connection, or a missed poll. Never a decoder logic error.
- **`detent_count_` is independent of clamping.** It increments before the position math, so it
  counts emitted detents even when `clamp_position()` pins the value at `min_value` or `max_value`.
  Counting works from anywhere in the range, in either direction.

`detent_count_` is a monotonic total across both directions. Direction is not tracked; the test plan
turns in one direction so the arithmetic stays clean.

### Firmware: raw pin semantics

`read_raw_pins()` returns `(A << 2) | (B << 1) | SW` using **literal GPIO levels, uninverted** —
matching the semantics of the DIAG patch the user already validated, so idle reads `7`.

This deliberately differs from the private `read_ab_state()`, which inverts to active-high for the
transition table. Two functions, two conventions, one file. Documented at both definitions because it
is an easy trap.

### Firmware: Input Report ID 0x04 layout (56 bytes)

Offsets are payload bytes after the Report ID byte.

| Offset | Type | Field | Description |
|--------|------|-------|-------------|
| 0-3 | uint8[4] | `raw_pins` | Per encoder: `(A<<2)\|(B<<1)\|SW`, literal GPIO levels. Idle = 7 |
| 4 | uint8 | `steps_per_detent` | Threshold the decoder is actively using (2 or 4) |
| 5-7 | uint8[3] | `reserved` | 0x00. Keeps the uint32 arrays 4-byte aligned |
| 8-23 | uint32[4] LE | `edge_count` | Cumulative observed A/B state changes |
| 24-39 | uint32[4] LE | `invalid_count` | Cumulative illegal transitions (subset of `edge_count`) |
| 40-55 | uint32[4] LE | `detent_count` | Cumulative detents emitted by the decoder |

56 bytes fits a single 64-byte full-speed interrupt packet. `CFG_TUD_HID_EP_BUFSIZE` is already 128,
so no `tusb_config.h` change is needed.

Report ID 0x04 is free: the descriptor declares only IDs 1, 2, and 3
(`main_generic_hid.cpp:261, 293, 310`), matching `firmware/boot.py`.

Counters are `uint32`, not `uint16`. At a sustained 80 edges/sec a `uint16` wraps in ~13 minutes,
which is inside a plausible debugging session; `uint32` costs 24 bytes and removes the concern.

### Firmware: send scheduling

`hid_task()` runs on its existing 10 ms tick and sends at most one report per tick, by priority:

1. **Config readback** (ID 0x02) if pending — one-shot, the host is blocking on it.
2. **Position report** (ID 0x01) if changed — restored `memcmp` gate, no force-send.
3. **Diagnostics** (ID 0x04) on a 100 ms deadline.

Positions outrank diagnostics, so a position change defers the diagnostic heartbeat by one 10 ms
tick. Since positions only change on detents, contention is negligible. 10 Hz × 57 bytes ≈ 570 B/s.

### Firmware: reset command

`CMD_RESET_DIAG = 0x05` on the existing Output Report ID 0x03 zeroes all counters on all encoders.
Required for the counted-detent measurement — without it, every measurement would need a replug.

### Build: `FIRMWARE_MODE`

A cache variable replaces both the uncommitted edit and the documented `cp` step:

```
cmake -DFIRMWARE_MODE=generic_hid ..   # default
cmake -DFIRMWARE_MODE=keyboard ..
```

An unrecognized value is a `FATAL_ERROR`, and the selected mode is echoed via `message(STATUS ...)`
so the build log records what was built.

**The default changes from `keyboard` to `generic_hid`.** The uncommitted edit was already the
de-facto default; this makes it official and version-controlled. Because the old default is
documented in two READMEs, both get an explicit "this changed" note — a reader who knows the old
behavior must be told, not left to infer it from a flag table.

CMake caches the value, so switching modes requires re-running `cmake` with the flag, not passing it
on every build.

### Host: diagnostics view

New `[D] Diagnostics` screen in `windows-example/Program.cs`:

```
Encoder Diagnostics                    (updated 0.1s ago)
=========================================================
Firmware steps/detent: 4    (GlobalFlags bit 0 = 0)

Enc    A  B  SW      Edges   Invalid   Detents   Edges/Detent
  1    1  1   1         80         0        20           4.00
  2    1  1   1          0         0         0            n/a
  3    1  1   1          0         0         0            n/a
  4    1  1   0          0         0         0            n/a

[Z] Zero counters   [T] Toggle steps/detent (4<->2)   [S] Save to flash
[B] Back
```

`[D]` currently means "Reset to defaults". That moves to `[F]` (Factory defaults) — a clearer mnemonic
that also removes a genuine hazard: a mis-pressed `D` now opens a read-only screen instead of wiping
the device config.

The `[T]` toggle flips `GlobalFlags` bit 0 and writes the full config via the existing Output Report
ID 0x02 path. It applies immediately but is **not** persisted until `[S]`. The view labels this.

### Host: graceful degradation

When no report ID 0x04 has ever arrived, the view must not render an empty table — an empty table is
indistinguishable from dead hardware, which is precisely the ambiguity this work exists to remove.
Instead:

```
No diagnostic reports received (Input Report ID 0x04).

  * This firmware build may predate report ID 0x04 - reflash from this branch.
  * Or the keyboard-HID personality was flashed; rebuild with
    -DFIRMWARE_MODE=generic_hid.

Positions and config still work; only diagnostics are unavailable.
```

A separate staleness path covers reports that started and stopped (device hung or unplugged): if the
last report is older than 2 seconds, the header shows the age instead of a live timestamp.

## Explicit Deviations

- **CircuitPython parity is out of scope.** `firmware/boot.py` and `firmware/code_generic_hid.py`
  implement the same protocol and will not gain report ID 0x04. That personality is not under test
  here, and adding it roughly doubles the work. The host's graceful-degradation path covers a
  CircuitPython device correctly — it reports "firmware predates report ID 0x04", which is accurate.
  Tracked as a clean follow-up.
- **`tools/encoder-monitor/` is out of scope.** It is a separate probe utility with its own report
  parsing. Not touched.
- **`steps_per_detent` is not changed.** Firmware default stays 4. The host gains a toggle; the user
  decides from measured evidence.

## Testing Strategy

There is no test harness for firmware in this repo (`tests/` covers desktop Python config logic only),
and the questions at hand are physical. Verification is therefore split:

**Build-time, automatable:**
- Both `FIRMWARE_MODE` values compile. `keyboard` has not been built since March and may have
  bit-rotted; that is worth discovering now rather than during a future emergency.
- The restored Report ID 0x01 block is byte-identical to `git show HEAD:firmware-cpp/main_generic_hid.cpp`.
- `static_assert(sizeof(DiagReport) == 56)` guards the wire format at compile time.

**Hardware UAT:** a scripted sequence, detailed in the plan's Test Plan section, that establishes in
order: (1) the new report path works at all, (2) decoded positions are real again, (3) signal
integrity is clean enough to trust a count, (4) the true steps-per-detent of the installed encoders.

### The discriminating measurement

Worth stating precisely, because the obvious ratio is the wrong one.

`edge_count / detent_count` **does not measure the encoder.** By construction the decoder emits one
detent per `steps_per_detent` valid edges, so that ratio simply reports the firmware's own threshold
back — it reads ≈4 on a 4-step encoder *and* on a 2-step encoder. It is a useful self-check on the
decoder, not a measurement of the hardware.

The measurement that discriminates is **edges per physically counted click**: turn the knob exactly
N tactile detents and read `edge_count / N`.

| Measurement | 4-step encoder (KY-040 class) | 2-step encoder (bare EC11 class) |
|---|---|---|
| `edge_count / N` | 4 | 2 |
| `detent_count / N` (with spd=4) | 1 | 0.5 |
| Symptom | correct | position advances once per **two** clicks |

Note the symptom direction: a 2-step encoder on `spd=4` advances at **half** rate, not double. Double
rate would come from the opposite error — `spd=2` on a 4-step encoder.

`invalid_count` must be checked *before* trusting the count, not after: bounce inflates `edge_count`,
and enough of it can make a 2-step encoder read as ≈4 edges/click. It is a precondition, not a
footnote.

## Known UAT Traps

Each of these would otherwise read as "still broken":

1. **`raw_pins` shows 7 while spinning.** Sampled at 10 Hz; encoders rest at a detent with both
   contacts open. The sub-millisecond transients are invisible at that rate. `raw_pins` is for
   at-rest checks — idle 7, SW press 6, stuck values — while the **counters** capture rotation.
2. **Positions start clamped at the minimum.** Factory default is `min=0, max=100, wrap=off`, and
   `reset_position()` sets position to `min_value`. Turning CCW from a fresh device does nothing.
   Turn CW.
3. **`[T]` does not persist.** The toggle applies immediately but survives a replug only after `[S]`.
4. **`steps_` is not reset by `apply_config()`.** A toggle mid-rotation can leave up to 3 unconsumed
   steps, making the first detent after a switch land early or late by one. Zero the counters after
   toggling, before re-measuring.
5. **A partial first detent costs at most one count.** Starting mid-detent leaves up to
   `steps_per_detent - 1` steps unconsumed, so `detent_count` can read one low. `edge_count` has no
   such slack and is the stronger signal.

## Files Affected

| File | Action |
|------|--------|
| `firmware-cpp/CMakeLists.txt` | `FIRMWARE_MODE` cache variable |
| `firmware-cpp/main_generic_hid.cpp` | Restore report 1; add counters, `DiagReport`, descriptor entry, `CMD_RESET_DIAG`, send scheduling |
| `windows-example/Program.cs` | Commit stranded fixes; report 4 parsing; diagnostics view; `[D]`→`[F]` remap |
| `firmware-cpp/README.md` | Build modes + default-changed note; report 0x04; command 0x05 |
| `README.md` | Build modes + default-changed note |

`firmware-cpp/tusb_config.h` needs no change — the 128-byte EP buffer already accommodates 56 bytes.
