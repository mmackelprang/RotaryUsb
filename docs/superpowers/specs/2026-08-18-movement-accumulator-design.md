# Movement Accumulator in HID Input Report 0x01

## Overview

Make knob rotation observable on the host **even when the encoder position is pinned at
`min_value` or `max_value`**, by adding a free-running signed movement accumulator to Input
Report ID 0x01.

Today, a knob turned against a limit is completely silent on the wire. The consuming project
(`RTest`, a radio console) needs to map controls whose useful range is wider than any range the
firmware can hold — a VFO frequency, for example. Such a control must keep receiving motion after
the device's own bounded position saturates.

This design also brings the CircuitPython firmware to full parity with the C++ firmware by
backporting the decoder diagnostics report (ID 0x04), and adds an integration guide plus a
descriptor-parity regression test.

Scope: both Generic HID firmwares, the shared Python config/report modules, the Windows example,
the test suite, and documentation. Keyboard HID mode (`firmware-cpp/main.cpp`) is unchanged.

## Problem Statement

### The knob goes silent at its limits

Three mechanisms combine to produce the silence. In the C++ firmware:

1. `position_ = clamp_position(...)` (`firmware-cpp/main_generic_hid.cpp:502`) pins the value at
   `min_value` or `max_value`.
2. `hid_task()` gates every send on
   `memcmp(&current_report, &last_report, sizeof(PositionReport)) != 0`
   (`firmware-cpp/main_generic_hid.cpp:808`).
3. Therefore, at a limit, positions do not change, buttons do not change, and **no Input Report
   0x01 is emitted at all.**

The only field that can still change is byte 17, `active_tiers`, which flips as turn speed crosses
a tier threshold. That is incidental, not a motion signal: turn at a steady speed against the stop
and the host observes nothing whatsoever.

The CircuitPython firmware has the identical suppression at
`firmware/code_generic_hid.py:388` (`if report != last_report:`).

### The existing partial workaround is not a control path

`detent_count` in Input Report ID 0x04 *does* keep incrementing at a limit — it is deliberately
counted before the position math (`firmware-cpp/main_generic_hid.cpp:481`) — and ships at 10 Hz.
A host could difference it to infer "still turning."

It is not adequate as a control path:

- **10 Hz.** Visibly laggy for a continuously-tuned control.
- **No direction.** It is a monotonic total across both rotation directions.
- **No acceleration.** It counts detents, not the effective step the device would have applied.
- **It is documented as a debug channel.** Building a production control path on it invites a
  future contributor to compile it out or change its cadence.

### CircuitPython has no diagnostics at all

Report ID 0x04 does not exist in `firmware/boot.py` or `firmware/code_generic_hid.py`. A user who
flashes the CircuitPython firmware and follows the C++ documentation finds a field silently absent.

### The wire format has no test coverage

Report packing lives inline in the CircuitPython main loop as a bare `struct.pack` call
(`firmware/code_generic_hid.py:379`). The HID report descriptor is duplicated across
`firmware-cpp/main_generic_hid.cpp`, `firmware/boot.py`, and the `in_report_lengths` /
`out_report_lengths` tuples in that same `boot.py`. Nothing verifies that any of these agree. They
agree today by review alone, and this change roughly doubles the divergence surface.

## Requirements

1. Input Report ID 0x01 carries a signed, per-encoder movement value alongside the existing
   absolute position, **in the same report**, so the two are always mutually consistent.
2. Movement continues to accrue when position is clamped at `min_value` or `max_value`.
3. Movement is expressed in the **same units as position** — `step_size × tier_multiplier` — so a
   host reproduces device-identical feel, including acceleration, with no knowledge of the tier
   configuration.
4. Movement is **drop-resistant**: a report lost above the firmware (OS buffer, slow host read)
   must not permanently lose motion.
5. Bytes 0–17 of Report ID 0x01 keep their exact current meaning and offsets.
6. Both firmwares implement the same wire format.
7. The CircuitPython firmware gains Input Report ID 0x04 with the same layout and semantics as the
   C++ firmware.
8. An automated test fails if the two report descriptors diverge.
9. The Windows example displays movement and demonstrates the host-side unbounded-value pattern.
10. An integration guide documents the complete protocol and a reference C#/.NET implementation.
11. No change to decoder behavior, config layout, command codes, or `CONFIG_VERSION`.

## Design Decisions

These four were settled during brainstorming and are load-bearing for everything below.

### Movement is measured in effective step units, not raw detents

`movement` accumulates `detent_direction × step_size × tier_multiplier` — precisely the quantity
that would have been added to position had there been no limit.

The alternative, raw detent counts, would require the host to replicate the firmware's tier logic
to match its feel, and to read the tier byte to do so. Effective step units make the host's job a
single addition and keep acceleration behavior in exactly one place.

### Movement is a monotonic accumulator, not a self-clearing per-report delta

The field is a free-running signed total since power-on. The host computes
`delta = movement - lastSeenMovement`.

A self-clearing delta would be simpler on the host — no state to keep — but a report lost above the
firmware is motion gone for good, undetectably. For a VFO, that means the tuned frequency silently
drifts from what the operator actually turned. With an accumulator, the very next report
re-synchronizes and no motion is lost.

### The accumulator resets only at power-on

`CMD_RESET_POSITIONS` (0x03) and `CMD_RESET_DEFAULTS` (0x02) explicitly do **not** zero it.

Position and movement answer different questions. Position is "where is the dial." Movement is an
odometer. Zeroing the odometer because the dial was re-zeroed injects a phantom delta into every
host that is differencing the field. Re-plugging the device re-enumerates it, and the host
re-baselines naturally on the first report it receives.

No `CMD_RESET_MOVEMENT` command is added. There is no established need for one, and adding it would
create exactly the phantom-delta trap the previous paragraph avoids.

### Overflow wraps; it does not saturate

The accumulator is stored as `uint32_t` in C++ (signed overflow is undefined behavior; unsigned
wrapping is well defined) and masked to 32 bits in Python. The bit pattern is transmitted as
`int32`. The host differences with wrapping arithmetic — `unchecked` in C# — so wrap is invisible.

Saturation would silently freeze the control after roughly 119 hours of continuous fast spinning.
Wrapping never misbehaves, and the differencing math is identical either way.

## Wire Protocol

### Input Report ID 0x01 — 36 bytes (was 21)

Offsets are payload bytes, after the Report ID byte.

| Offset | Type | Field | Description |
|--------|------|-------|-------------|
| 0-15 | int32[4] LE | `position` | Absolute position, clamped to `[min_value, max_value]`, wrap applied. **Unchanged** |
| 16 | uint8 | `button_states` | Bits 0-3 = buttons 1-4. **Unchanged** |
| 17 | uint8 | `active_tiers` | Packed 2-bit field per encoder. **Unchanged** |
| 18-19 | uint8[2] | `reserved` | 0x00. Was 3 bytes at 18-20 |
| 20-35 | int32[4] LE | `movement` | Free-running signed accumulator. **NEW** |

`movement` is placed at offset 20 so it is 4-byte aligned. This matters: the RP2040 is a
Cortex-M0+, which fires a HardFault on unaligned word access. The struct is `packed`, so GCC would
emit byte-wise accesses regardless, but alignment costs one reserved byte and removes the hazard
entirely.

**Backward compatibility.** Bytes 0–17 are byte-identical to the previous layout. A host built
against the 21-byte layout, running against new firmware, parses positions, buttons and tiers
correctly; it simply receives a longer buffer and ignores the tail. Only a host that asserts an
*exact* report length breaks.

**Movement semantics.** Accumulated **pre-clamp and post-`reverse`**, from the same expression that
feeds the position update. Consequences:

- It carries acceleration, because `effective_step` already includes the tier multiplier.
- It agrees in sign with position, because `reverse` has already been applied to `direction`.
- It keeps accruing at a limit, because clamping happens afterwards and only to `position`.
- Under `wrap`, position wraps while movement continues monotonically, so a host can determine how
  many full turns were made.

### Input Report ID 0x04 — 56 bytes

Layout, semantics, and 10 Hz cadence unchanged. Newly implemented in the CircuitPython firmware;
see "CircuitPython Firmware" below.

### Unchanged

Output Report ID 0x02 (config write, 106 bytes), Output Report ID 0x03 (commands, 2 bytes), Input
Report ID 0x02 (config readback, 106 bytes), all five command codes, the config binary layout, and
`CONFIG_VERSION` (stays `0x01`).

`CONFIG_VERSION` deliberately does **not** change. It gates flash config validity — bumping it
would invalidate every user's saved configuration to signal a change in an unrelated structure.

### Host feature detection

A host distinguishes new firmware from old by the HID-advertised input report length, not by any
version field:

| Firmware | `InputReportByteLength` |
|----------|------------------------|
| With movement accumulator | 37 (36 payload + report ID) |
| Without | 22 (21 payload + report ID) |

### HID report descriptor changes

Vendor Usages 0x02–0x08 are already assigned. Movement takes **Vendor Usage 0x09**.

Two edits inside the Report ID 0x01 section, identical in both firmwares:

1. The Usage-4 item (`tier + reserved`) changes Report Count from `4` to `3`, covering bytes 17–19.
2. A new item follows it:

```
0x09, 0x09,        //   Usage (Vendor Usage 9 - Movement Accumulators)
0x15, 0x00,        //   Logical Minimum (0)
0x26, 0xFF, 0x00,  //   Logical Maximum (255)
0x75, 0x08,        //   Report Size (8 bits)
0x95, 0x10,        //   Report Count (16 bytes = 4x int32)
0x81, 0x02,        //   Input (Data, Variable, Absolute)
```

Byte accounting for Report ID 0x01: 16 (positions) + 1 (4 button bits + 4 padding bits) + 3 (tier +
2 reserved) + 16 (movement) = **36**.

As elsewhere in these descriptors, the new item re-declares its global items rather than inheriting
them. This is redundant but matches the existing style in both files and keeps each item readable in
isolation.

## C++ Firmware

File: `firmware-cpp/main_generic_hid.cpp`

### Report struct

```cpp
struct PositionReport {
    int32_t positions[NUM_ENCODERS];   // 0-15
    uint8_t button_states;             // 16
    uint8_t active_tiers;              // 17
    uint8_t reserved[2];               // 18-19
    int32_t movement[NUM_ENCODERS];    // 20-35
} __attribute__((packed));

static_assert(sizeof(PositionReport) == 36, "PositionReport must be 36 bytes");
```

### Accumulator

`GenericHidEncoder` gains one member:

```cpp
uint32_t movement_;   // unsigned: signed overflow is UB; unsigned wrapping is defined
```

Initialized to `0` in the constructor initializer list.

The increment goes inside the existing detent block in `update()`, immediately after
`effective_step` is computed and **before** the clamp at `main_generic_hid.cpp:502`:

```cpp
// Accumulated pre-clamp, so motion is still reported when position_ is pinned
// at min_value or max_value. int64 intermediate because compute_effective_step()
// can return INT32_MIN, and -1 * INT32_MIN is undefined behavior in int32.
movement_ += (uint32_t)((int64_t)detent_direction * (int64_t)effective_step);
```

The `int64_t` intermediate is not defensive padding. `compute_effective_step()` explicitly clamps
to `INT32_MIN`, and negating that value in `int32` arithmetic is undefined behavior.

### Accessor

```cpp
int32_t get_movement() const {
    int32_t out;
    memcpy(&out, &movement_, sizeof(out));
    return out;
}
```

`memcpy` rather than a cast: converting an out-of-range `uint32_t` to `int32_t` is
implementation-defined before C++20, and this project builds with C++17.

### What does not change

`reset_position()` does **not** touch `movement_`, and carries a comment explaining why (see
"The accumulator resets only at power-on").

`hid_task()` gains only the field fill:

```cpp
current_report.movement[i] = encoders[i]->get_movement();
```

**The send-gating logic at `main_generic_hid.cpp:808` is untouched.** This is the elegant part of
the design: `movement` changing is precisely what makes the report differ from `last_report` while
turning at a limit, so the existing change-detection sends it with no special case. The reported
bug is fixed by adding a field, not by adding a code path.

## CircuitPython Firmware

### `firmware/boot.py`

- Report descriptor mirrors the C++ descriptor **byte for byte**, including the Report ID 0x04
  section, which is added at the same position the C++ file uses (after the Output ID 0x03 item,
  immediately before End Collection).
- Device declaration:

```python
report_ids=(1, 2, 3, 4),
in_report_lengths=(36, 106, 0, 56),
out_report_lengths=(0, 106, 2, 0),
```

- Module docstring and the closing `print()` lines updated to describe all four reports.

### `firmware/code_generic_hid.py`

**Movement.** `Encoder` gains `self.movement = 0`, incremented in the detent block before the
`clamp_position()` call at `code_generic_hid.py:213`, using
`reports.accumulate_movement()` for the 32-bit wrap.

**Diagnostics backport.** `Encoder` gains `edge_count`, `invalid_count`, `detent_count`,
`read_raw_pins()`, and `reset_diagnostics()`, mirroring the C++ implementation. Two properties from
the C++ file are load-bearing and carry over verbatim, with their comments:

- **`detent_count` increments before the position math**, so it counts emitted detents even at a
  limit.
- **`read_raw_pins()` returns uninverted GPIO levels** — `(A<<2)|(B<<1)|SW`, idle reads 7. This is
  the opposite convention from the private `_read_ab_state()`, which inverts to active-high for the
  transition table. Two readers, two conventions, on purpose.

**`invalid_count` parity note.** The C++ decoder increments `invalid_count_` whenever the
transition table yields 0 for a genuine state change, and resets `steps_` in that branch. The Python
decoder has the same `else` branch at `code_generic_hid.py:219` but currently only resets `steps`.
The counter increment is added there so the two firmwares report identical numbers for identical
physical input.

**New command.** `CMD_RESET_DIAG = 0x05`, handled alongside the existing commands.

**Report 0x04 emission.** Sent at 10 Hz at the **lowest** priority, mirroring the C++ ordering:
config readback first, then the position report if it changed, then diagnostics. A `last_diag_time`
variable parallels the existing `last_report_time`.

## New Module: `firmware/reports.py`

Report packing currently lives inline in the CircuitPython main loop, so the wire format has no
test. Extract it into a module with no CircuitPython dependencies, testable on desktop Python —
the same pattern `firmware/config.py` already establishes.

```python
POSITION_REPORT_SIZE = 36
DIAG_REPORT_SIZE = 56

POSITION_REPORT_STRUCT = struct.Struct("<iiiiBB2xiiii")   # 36 bytes
DIAG_REPORT_STRUCT     = struct.Struct("<4BB3x4I4I4I")    # 56 bytes

def to_signed_i32(value: int) -> int: ...
def accumulate_movement(current: int, delta: int) -> int: ...   # wraps to 32 bits
def movement_delta(now: int, last: int) -> int: ...             # wrap-correct difference
def pack_position_report(positions, button_states, tier_byte, movements) -> bytes: ...
def pack_diag_report(raw_pins, steps_per_detent, edge, invalid, detent) -> bytes: ...
```

`movement_delta()` is **the exact wrap-correct differencing every host must perform**:

```python
def movement_delta(now, last):
    return to_signed_i32((now - last) & 0xFFFFFFFF)
```

Its C# equivalent is `unchecked(now - last)` on `int`, which produces the identical result under
two's complement. Unit-testing it here is how that math gets pinned and documented rather than
re-derived by each integrator.

`pack_position_report()` converts each accumulator to signed via `to_signed_i32()` before packing
with the `i` format code, so the struct format string is self-documenting against the protocol table.

`code_generic_hid.py` imports from this module instead of calling `struct.pack` inline.

## Testing

### `tests/test_reports.py` (new)

- Both struct sizes are exactly 36 and 56.
- Field offsets round-trip: pack a report with distinguishable values, then assert each field
  appears at the offset the protocol table specifies. This is what catches a layout drift.
- `accumulate_movement()` wraps at the 32-bit boundary in both directions.
- `movement_delta()` returns the correct signed delta **across** the wrap boundary — e.g. from
  `0x7FFFFFFF` to `0x80000000` is `+1`, not `-4294967295`.
- `to_signed_i32()` round-trips at `0`, `INT32_MAX`, `INT32_MIN`, `0xFFFFFFFF`.
- Movement accumulation carries acceleration: `step_size × multiplier` for a tier-3 detent.

### `tests/test_descriptor_parity.py` (new)

The highest-value test in this change, and the only automated guard the C++ firmware has at all.

1. Extract the descriptor byte array from `firmware/boot.py` (between `bytes([` and `])`) and from
   `firmware-cpp/main_generic_hid.cpp` (the `hid_report_descriptor[]` initializer). **Strip comments
   before extracting hex literals** — both files contain values like `0xFF00` inside comments.
2. Assert the two byte sequences are identical.
3. Walk the descriptor with a minimal HID parser tracking Report ID (0x85), Report Size (0x75),
   Report Count (0x95), Input (0x81) and Output (0x91), accumulating bits per report ID and
   direction.
4. Assert the derived sizes match `in_report_lengths` / `out_report_lengths` parsed from `boot.py`,
   and match `POSITION_REPORT_SIZE` / `DIAG_REPORT_SIZE` from `firmware/reports.py`.

All items in these descriptors are short items, so the parser handles only short-item encoding.

### `tests/test_config_logic.py` (existing)

Must remain green and unmodified. The config layout does not change.

### Firmware build

Both CMake modes configure and build cleanly:

```
cmake .. && make -j4                          # generic_hid (default)
cmake -DFIRMWARE_MODE=keyboard .. && make -j4 # keyboard, must remain unaffected
```

### Hardware UAT

Per firmware (C++ and CircuitPython):

1. Flash, launch `windows-example`, open the live monitor.
2. Turn an encoder mid-range: position advances, movement advances by the same amount.
3. Turn to `max_value` and **keep turning**: position holds at max, **movement keeps climbing**, and
   the host-accumulated unbounded value tracks it. This is the acceptance criterion for the feature.
4. Reverse at the limit: movement decrements, position stays at max until the accumulated motion
   brings it back inside range.
5. Spin fast: delta magnitude reflects the acceleration tier.
6. Confirm Report ID 0x04 still arrives (the `[D]` diagnostics view is populated) on **both**
   firmwares.
7. Confirm an idle device sends nothing — no report spam from the new field.

## Windows Example

File: `windows-example/Program.cs`

- `POSITION_PAYLOAD_SIZE = 36`; the length guard becomes `report.Data.Length >= 37`.
- Parse movement at `BitConverter.ToInt32(report.Data, 21 + i * 4)` (payload offset 20, plus one for
  the report ID that HidLibrary prepends).
- New state, guarded by `_lock`: `_movementRaw[]`, `_movementLast[]`, and `_hostAccumulated[]`.
- The reader thread applies the differencing pattern the integration guide prescribes, so the
  example demonstrates the exact code an integrator copies.
- `RunMonitor()` gains a **Movement** column and a **host-accumulated unbounded value** column. The
  second column is the visible proof of the feature: at a limit, position stops and the unbounded
  value keeps moving.
- **Old-firmware tolerance:** if `device.Capabilities.InputReportByteLength < 37`, render the new
  columns as `n/a` rather than misparsing a short buffer.

## Integration Guide

File: `docs/INTEGRATION.md`

Audience: an agentic team integrating this device into `RTest`, a C#/.NET application on Windows.
The guide must be sufficient without reading firmware source.

1. **What this device is** — 30-second summary, and a decision table for *when to use `position`
   versus `movement`*.
2. **Hardware and firmware setup** — which firmware to choose and why, flashing steps, and the
   steps-per-detent decision including the Report 0x04 measurement procedure for identifying an
   encoder.
3. **Discovery and feature detection** — VID/PID, usage page, the C# discovery snippet, and the
   `InputReportByteLength` check.
4. **Wire protocol reference** — every report, every offset, endianness, cadence, and precisely
   when reports are and are not sent.
5. **Reference implementation** — a drop-in `RotaryUsbDevice` class: read loop, wrap-correct
   differencing, events.
6. **Mapping recipes** — bounded control (use `position`), unbounded / VFO control (use `movement`
   delta), detent-stepped selector, button handling.
7. **Configuring the device from the host** — config write, the validation rules the firmware
   enforces, save-to-flash, and all five commands.
8. **Diagnostics and troubleshooting.**
9. **Gotchas checklist** — HidLibrary prepends the report ID; reports are sent only on change;
   re-baseline the accumulator on reconnect; use wrapping arithmetic for the delta; the accumulator
   survives `CMD_RESET_POSITIONS`; CircuitPython-versus-C++ differences.
10. **Integration checklist** for the agent team.

## Documentation Updates

| File | Change |
|------|--------|
| `firmware-cpp/README.md` | Report 0x01 table → 36 bytes; movement semantics; feature detection |
| `firmware/README.md` | Same, plus newly-supported Report ID 0x04 and `CMD_RESET_DIAG` |
| `windows-example/README.md` | New monitor columns |
| `README.md` | Link to `docs/INTEGRATION.md` |
| `firmware/boot.py` | Docstring and startup `print()` lines cover all four reports |
| `firmware-cpp/main_generic_hid.cpp` | File header comment: report sizes |

## Out of Scope

- **Keyboard HID mode** (`firmware-cpp/main.cpp`, `firmware/code.py`) — untouched.
- **A `CMD_RESET_MOVEMENT` command** — see "The accumulator resets only at power-on."
- **Per-encoder movement enable/disable** — the field costs 16 bytes on an endpoint with ample
  headroom; a config knob would add validation surface for no measured benefit.
- **Changing `CONFIG_VERSION` or the config layout.**

## Risks

| Risk | Mitigation |
|------|-----------|
| The two descriptors drift apart | `tests/test_descriptor_parity.py` fails the build |
| A host asserts an exact 21-byte report length | Documented in the integration guide; bytes 0-17 unchanged so only exact-length assertions break |
| CircuitPython loop slows under the added 10 Hz diagnostics report | One 56-byte `struct.pack` per 100 ms; measured during UAT step 7 |
| Unaligned 32-bit access faults on Cortex-M0+ | `movement` placed at 4-byte-aligned offset 20; struct is `packed` so GCC emits byte-wise access regardless |
| Signed overflow UB in the accumulator | Stored `uint32_t`, accumulated through `int64_t`, read back via `memcpy` |
