# Movement Accumulator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Report knob rotation to the host even when encoder position is clamped at `min_value`/`max_value`, by adding a free-running signed movement accumulator to HID Input Report ID 0x01.

**Architecture:** Input Report ID 0x01 grows from 21 to 36 bytes; bytes 0–17 keep their exact current meaning and a new `int32[4] movement` field lands at 4-byte-aligned offset 20. Movement accumulates `detent_direction × step_size × tier_multiplier` **before** clamping, so it keeps accruing at a limit and carries acceleration. It is monotonic and wraps at 32 bits; the host differences it. Both firmwares implement the identical wire format, guarded by a new descriptor-parity test.

**Tech Stack:** C++17 (Pico SDK / TinyUSB, CMake+Ninja), CircuitPython 8.x, desktop Python 3.13 + pytest, C# .NET 8 (HidLibrary).

**Spec:** `docs/superpowers/specs/2026-08-18-movement-accumulator-design.md`

## Global Constraints

- **Bytes 0–17 of Input Report ID 0x01 must not change meaning or offset.** Only exact-length assertions may break.
- **`CONFIG_VERSION` stays `0x01`.** Config layout is unchanged; bumping it would invalidate every saved flash config.
- **No new command codes.** The five existing codes (0x01–0x05) are the complete set.
- **The accumulator resets only at power-on.** `CMD_RESET_POSITIONS` and `CMD_RESET_DEFAULTS` must NOT zero it.
- **Movement is accumulated pre-clamp, post-`reverse`.**
- **Overflow wraps, never saturates.** `uint32_t` in C++, `& 0xFFFFFFFF` in Python.
- **The two HID report descriptors must stay byte-identical** between `firmware/boot.py` and `firmware-cpp/main_generic_hid.cpp`.
- **Keyboard HID mode is out of scope.** Do not modify `firmware-cpp/main.cpp` or `firmware/code.py`.
- Every file carries the existing SPDX header:
  `# SPDX-FileCopyrightText: 2024 RotaryUsb Project` / `# SPDX-License-Identifier: Apache-2.0`
- **Baseline:** `python -m pytest tests/ -q` currently reports **36 passed**. It must never regress.

---

### Task 1: `firmware/reports.py` — report packing and wrap arithmetic

Report packing currently lives inline in the CircuitPython main loop as a bare `struct.pack`, so the wire format has no test at all. Extract it into a pure module with no CircuitPython imports, mirroring the pattern `firmware/config.py` already establishes.

`movement_delta()` is the single most important function here: it is the exact wrap-correct differencing every host integrator must perform, and unit-testing it is how that math gets pinned rather than re-derived by each consumer.

**Files:**
- Create: `firmware/reports.py`
- Test: `tests/test_reports.py`

**Interfaces:**
- Consumes: nothing (foundation task)
- Produces:
  - `POSITION_REPORT_SIZE = 36`, `DIAG_REPORT_SIZE = 56`, `NUM_ENCODERS = 4`, `MASK32 = 0xFFFFFFFF`
  - `to_signed_i32(value: int) -> int`
  - `accumulate_movement(current: int, delta: int) -> int`
  - `movement_delta(now: int, last: int) -> int`
  - `pack_position_report(positions, button_states, tier_byte, movements) -> bytes`
  - `pack_diag_report(raw_pins, steps_per_detent, edge_count, invalid_count, detent_count) -> bytes`

- [ ] **Step 1: Write the failing test**

Create `tests/test_reports.py`:

```python
# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""Tests for HID report packing and movement-accumulator arithmetic."""

import struct
import sys
import os

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "firmware"))

from reports import (
    POSITION_REPORT_SIZE, DIAG_REPORT_SIZE,
    to_signed_i32, accumulate_movement, movement_delta,
    pack_position_report, pack_diag_report,
)


# ---- Sizes ----

def test_position_report_is_36_bytes():
    data = pack_position_report([0, 0, 0, 0], 0, 0, [0, 0, 0, 0])
    assert len(data) == 36
    assert POSITION_REPORT_SIZE == 36


def test_diag_report_is_56_bytes():
    data = pack_diag_report([7, 7, 7, 7], 4, [0] * 4, [0] * 4, [0] * 4)
    assert len(data) == 56
    assert DIAG_REPORT_SIZE == 56


# ---- Field offsets: the protocol table, asserted ----

def test_position_report_field_offsets():
    """Every field must land at the offset documented in the spec."""
    data = pack_position_report(
        positions=[0x11111111, 0x22222222, 0x33333333, 0x44444444],
        button_states=0x0F,
        tier_byte=0xAA,
        movements=[0x55555555, 0x66666666, 0x77777777, 0x0BADF00D],
    )
    # 0-15: positions
    assert struct.unpack_from("<i", data, 0)[0] == 0x11111111
    assert struct.unpack_from("<i", data, 4)[0] == 0x22222222
    assert struct.unpack_from("<i", data, 8)[0] == 0x33333333
    assert struct.unpack_from("<i", data, 12)[0] == 0x44444444
    # 16-17: buttons, tiers
    assert data[16] == 0x0F
    assert data[17] == 0xAA
    # 18-19: reserved, must be zero
    assert data[18] == 0x00
    assert data[19] == 0x00
    # 20-35: movement
    assert struct.unpack_from("<i", data, 20)[0] == 0x55555555
    assert struct.unpack_from("<i", data, 24)[0] == 0x66666666
    assert struct.unpack_from("<i", data, 28)[0] == 0x77777777
    assert struct.unpack_from("<i", data, 32)[0] == 0x0BADF00D


def test_diag_report_field_offsets():
    data = pack_diag_report(
        raw_pins=[7, 6, 5, 3],
        steps_per_detent=4,
        edge_count=[100, 200, 300, 400],
        invalid_count=[1, 2, 3, 4],
        detent_count=[25, 50, 75, 100],
    )
    assert list(data[0:4]) == [7, 6, 5, 3]
    assert data[4] == 4
    assert list(data[5:8]) == [0, 0, 0]
    assert struct.unpack_from("<I", data, 8)[0] == 100
    assert struct.unpack_from("<I", data, 20)[0] == 400
    assert struct.unpack_from("<I", data, 24)[0] == 1
    assert struct.unpack_from("<I", data, 40)[0] == 25
    assert struct.unpack_from("<I", data, 52)[0] == 100


# ---- Signed reinterpretation ----

def test_to_signed_i32_boundaries():
    assert to_signed_i32(0) == 0
    assert to_signed_i32(0x7FFFFFFF) == 2147483647
    assert to_signed_i32(0x80000000) == -2147483648
    assert to_signed_i32(0xFFFFFFFF) == -1


# ---- Accumulation wraps, never saturates ----

def test_accumulate_movement_basic():
    assert accumulate_movement(0, 50) == 50
    assert accumulate_movement(50, -20) == 30


def test_accumulate_movement_wraps_upward():
    assert accumulate_movement(0xFFFFFFFF, 1) == 0
    assert accumulate_movement(0xFFFFFF00, 0x200) == 0x100


def test_accumulate_movement_wraps_downward():
    assert accumulate_movement(0, -1) == 0xFFFFFFFF


def test_accumulate_movement_never_saturates():
    """A saturating accumulator would freeze here; a wrapping one keeps moving."""
    acc = 0x7FFFFFFF
    acc = accumulate_movement(acc, 1)
    assert acc == 0x80000000
    assert to_signed_i32(acc) == -2147483648


# ---- The differencing math every host must implement ----

def test_movement_delta_simple():
    assert movement_delta(150, 100) == 50
    assert movement_delta(100, 150) == -50
    assert movement_delta(42, 42) == 0


def test_movement_delta_across_positive_wrap():
    """0x7FFFFFFF -> 0x80000000 is +1, not -4294967295."""
    assert movement_delta(to_signed_i32(0x80000000), to_signed_i32(0x7FFFFFFF)) == 1


def test_movement_delta_across_zero_wrap():
    assert movement_delta(to_signed_i32(0x00000000), to_signed_i32(0xFFFFFFFF)) == 1
    assert movement_delta(to_signed_i32(0xFFFFFFFF), to_signed_i32(0x00000000)) == -1


def test_movement_delta_round_trip_through_accumulator():
    """Accumulate a burst across the wrap boundary; the delta must equal the burst."""
    acc = 0xFFFFFF00
    last = to_signed_i32(acc)
    for _ in range(10):
        acc = accumulate_movement(acc, 50)
    assert movement_delta(to_signed_i32(acc), last) == 500


# ---- Acceleration is carried in the movement value ----

def test_movement_carries_acceleration():
    """One tier-3 detent at step_size=1, multiplier=50 accumulates 50, not 1."""
    step_size, multiplier, direction = 1, 50, 1
    assert accumulate_movement(0, direction * step_size * multiplier) == 50


def test_movement_sign_follows_direction():
    assert to_signed_i32(accumulate_movement(0, -1 * 1 * 50)) == -50


# ---- Packing accepts raw unsigned accumulator values ----

def test_pack_accepts_unsigned_accumulator_values():
    """Encoders hold accumulators as unsigned; packing must reinterpret, not raise."""
    data = pack_position_report([0] * 4, 0, 0, [0xFFFFFFFF, 0x80000000, 0, 1])
    assert struct.unpack_from("<i", data, 20)[0] == -1
    assert struct.unpack_from("<i", data, 24)[0] == -2147483648
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/test_reports.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'reports'`

- [ ] **Step 3: Write the implementation**

Create `firmware/reports.py`:

```python
# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
HID report packing and movement-accumulator arithmetic for RotaryUsb Generic HID mode.

This module has no CircuitPython dependencies and can be tested on desktop Python,
the same way config.py is.
"""

import struct

NUM_ENCODERS = 4

POSITION_REPORT_SIZE = 36
DIAG_REPORT_SIZE = 56

MASK32 = 0xFFFFFFFF

# Input Report ID 0x01 (36 bytes). Offsets are payload bytes, after the Report ID.
#   0-15  int32[4]  positions       clamped to [min_value, max_value]
#   16    uint8     button_states   bits 0-3
#   17    uint8     active_tiers    packed 2 bits per encoder
#   18-19 uint8[2]  reserved        0x00
#   20-35 int32[4]  movement        free-running accumulator, wraps
#
# movement sits at offset 20 so it is 4-byte aligned: the RP2040 is a Cortex-M0+,
# which HardFaults on unaligned word access.
POSITION_REPORT_STRUCT = struct.Struct("<iiiiBB2xiiii")
assert POSITION_REPORT_STRUCT.size == POSITION_REPORT_SIZE

# Input Report ID 0x04 (56 bytes).
#   0-3   uint8[4]  raw_pins           (A<<2)|(B<<1)|SW, literal levels, idle = 7
#   4     uint8     steps_per_detent   threshold the decoder is actually using
#   5-7   uint8[3]  reserved           keeps the uint32 arrays 4-byte aligned
#   8-23  uint32[4] edge_count
#   24-39 uint32[4] invalid_count
#   40-55 uint32[4] detent_count
DIAG_REPORT_STRUCT = struct.Struct("<4BB3x4I4I4I")
assert DIAG_REPORT_STRUCT.size == DIAG_REPORT_SIZE


def to_signed_i32(value):
    """Reinterpret the low 32 bits of value as signed two's-complement int32."""
    value &= MASK32
    return value - 0x100000000 if value & 0x80000000 else value


def accumulate_movement(current, delta):
    """
    Add delta to a 32-bit movement accumulator, wrapping rather than saturating.

    Saturation would silently freeze the control after roughly 119 hours of
    continuous fast spinning. Wrapping never misbehaves, because the host
    differences the value with movement_delta(), which is wrap-correct.
    """
    return (current + delta) & MASK32


def movement_delta(now, last):
    """
    Signed movement between two accumulator samples, correct across the 32-bit wrap.

    This is the exact arithmetic a host integrator must perform. The C# equivalent
    is `unchecked(now - last)` on int, which gives the identical result under
    two's complement.
    """
    return to_signed_i32((now - last) & MASK32)


def pack_position_report(positions, button_states, tier_byte, movements):
    """Pack Input Report ID 0x01 (36 bytes). Accumulators may be unsigned."""
    return POSITION_REPORT_STRUCT.pack(
        positions[0], positions[1], positions[2], positions[3],
        button_states & 0xFF,
        tier_byte & 0xFF,
        to_signed_i32(movements[0]), to_signed_i32(movements[1]),
        to_signed_i32(movements[2]), to_signed_i32(movements[3]),
    )


def pack_diag_report(raw_pins, steps_per_detent, edge_count, invalid_count, detent_count):
    """Pack Input Report ID 0x04 (56 bytes)."""
    return DIAG_REPORT_STRUCT.pack(
        raw_pins[0] & 0xFF, raw_pins[1] & 0xFF, raw_pins[2] & 0xFF, raw_pins[3] & 0xFF,
        steps_per_detent & 0xFF,
        edge_count[0] & MASK32, edge_count[1] & MASK32,
        edge_count[2] & MASK32, edge_count[3] & MASK32,
        invalid_count[0] & MASK32, invalid_count[1] & MASK32,
        invalid_count[2] & MASK32, invalid_count[3] & MASK32,
        detent_count[0] & MASK32, detent_count[1] & MASK32,
        detent_count[2] & MASK32, detent_count[3] & MASK32,
    )
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python -m pytest tests/test_reports.py -q`
Expected: PASS (all tests)

Run: `python -m pytest tests/ -q`
Expected: PASS — 36 pre-existing + the new ones, no regressions.

- [ ] **Step 5: Commit**

```bash
git add firmware/reports.py tests/test_reports.py
git commit -m "feat(firmware): add reports module with wrap-correct movement math"
```

---

### Task 2: Descriptor parity test — close the pre-existing CircuitPython/C++ divergence

The HID report descriptor is duplicated across `firmware-cpp/main_generic_hid.cpp` and `firmware/boot.py`, plus the `in_report_lengths`/`out_report_lengths` tuples in that same `boot.py`. Nothing verifies they agree.

**They do not agree today.** `boot.py`'s descriptor is 97 bytes and ends at End Collection; the C++ descriptor is 112 bytes and carries an extra 15-byte Input Report ID 0x04 section that CircuitPython never received. So this test fails on a real, already-shipped bug before it guards anything new.

This task writes the test, watches it fail on that divergence, then closes the divergence by giving `boot.py` the report 0x04 declaration. Declaring a report the CircuitPython firmware does not yet send is harmless — Task 4 makes it send one.

**Files:**
- Create: `tests/test_descriptor_parity.py`
- Modify: `firmware/boot.py`

**Interfaces:**
- Consumes: `POSITION_REPORT_SIZE`, `DIAG_REPORT_SIZE` from `firmware/reports.py` (Task 1)
- Produces: `extract_descriptor_bytes(path, pattern, comment_token)` — reused by no other task, but the test module is the reference for how descriptors are parsed.

- [ ] **Step 1: Write the failing test**

Create `tests/test_descriptor_parity.py`:

```python
# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
The HID report descriptor is duplicated in three places that must agree:
the C++ firmware, boot.py's descriptor, and boot.py's report-length tuples.

They agreed only by review until this test existed. This is also the only
automated guard the C++ firmware has at all.
"""

import ast
import os
import re
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "firmware"))

from reports import POSITION_REPORT_SIZE, DIAG_REPORT_SIZE

REPO = os.path.join(os.path.dirname(__file__), "..")
BOOT_PY = os.path.join(REPO, "firmware", "boot.py")
MAIN_CPP = os.path.join(REPO, "firmware-cpp", "main_generic_hid.cpp")


def _strip_comments(text, token):
    return "\n".join(line.split(token, 1)[0] for line in text.splitlines())


def extract_descriptor_bytes(path, pattern, comment_token):
    """
    Pull a descriptor's byte literals out of source.

    Comments MUST be stripped before scanning for hex: both files contain values
    like 0xFF00 and "Report ID 0x01" inside comments, which would otherwise be
    scraped as descriptor bytes.
    """
    with open(path, encoding="utf-8") as f:
        text = f.read()
    match = re.search(pattern, text, re.S)
    assert match is not None, f"descriptor not found in {path}"
    body = _strip_comments(match.group(1), comment_token)
    return [int(h, 16) for h in re.findall(r"0x([0-9A-Fa-f]{2})\b", body)]


def python_descriptor():
    return extract_descriptor_bytes(
        BOOT_PY, r"GENERIC_HID_REPORT_DESCRIPTOR\s*=\s*bytes\(\[(.*?)\]\)", "#")


def cpp_descriptor():
    return extract_descriptor_bytes(
        MAIN_CPP, r"hid_report_descriptor\[\]\s*=\s*\{(.*?)\n\};", "//")


def walk_descriptor(desc):
    """
    Walk HID short items and derive payload size in bytes per (direction, report_id).

    Only short-item encoding is handled; every item in these descriptors is short.
    """
    sizes = {}
    report_id = 0
    report_size = 0
    report_count = 0
    i = 0
    while i < len(desc):
        prefix = desc[i]
        length = prefix & 0x03
        if length == 3:
            length = 4
        tag = prefix & 0xFC
        data = 0
        for k in range(length):
            data |= desc[i + 1 + k] << (8 * k)

        if tag == 0x84:      # Report ID (global)
            report_id = data
        elif tag == 0x74:    # Report Size (global)
            report_size = data
        elif tag == 0x94:    # Report Count (global)
            report_count = data
        elif tag == 0x80:    # Input (main)
            key = ("in", report_id)
            sizes[key] = sizes.get(key, 0) + report_size * report_count
        elif tag == 0x90:    # Output (main)
            key = ("out", report_id)
            sizes[key] = sizes.get(key, 0) + report_size * report_count

        i += 1 + length

    for key, bits in sizes.items():
        assert bits % 8 == 0, f"{key} is not a whole number of bytes: {bits} bits"
    return {key: bits // 8 for key, bits in sizes.items()}


def boot_py_tuple(name):
    """Parse a literal tuple assigned in the usb_hid.Device(...) call in boot.py."""
    with open(BOOT_PY, encoding="utf-8") as f:
        text = _strip_comments(f.read(), "#")
    match = re.search(name + r"\s*=\s*(\([^)]*\))", text)
    assert match is not None, f"{name} not found in boot.py"
    return ast.literal_eval(match.group(1))


# ---- The parity guarantee ----

def test_descriptors_are_byte_identical():
    py = python_descriptor()
    cpp = cpp_descriptor()
    assert py == cpp, (
        f"descriptor divergence: boot.py has {len(py)} bytes, "
        f"main_generic_hid.cpp has {len(cpp)}"
    )


def test_descriptor_is_not_trivially_empty():
    """Guard the extraction itself: a broken regex must not silently pass."""
    assert len(cpp_descriptor()) > 50


# ---- Derived sizes agree with every other declaration of them ----

def test_derived_report_sizes():
    sizes = walk_descriptor(cpp_descriptor())
    assert sizes[("in", 1)] == POSITION_REPORT_SIZE
    assert sizes[("in", 2)] == 106
    assert sizes[("in", 4)] == DIAG_REPORT_SIZE
    assert sizes[("out", 2)] == 106
    assert sizes[("out", 3)] == 2


def test_boot_py_report_ids_cover_descriptor():
    sizes = walk_descriptor(python_descriptor())
    declared = set(boot_py_tuple("report_ids"))
    used = {rid for _, rid in sizes}
    assert used <= declared, f"descriptor uses report IDs not declared: {used - declared}"


def test_boot_py_in_report_lengths_match_descriptor():
    sizes = walk_descriptor(python_descriptor())
    report_ids = boot_py_tuple("report_ids")
    lengths = boot_py_tuple("in_report_lengths")
    assert len(lengths) == len(report_ids)
    for rid, declared in zip(report_ids, lengths):
        assert declared == sizes.get(("in", rid), 0), f"in_report_lengths wrong for ID {rid}"


def test_boot_py_out_report_lengths_match_descriptor():
    sizes = walk_descriptor(python_descriptor())
    report_ids = boot_py_tuple("report_ids")
    lengths = boot_py_tuple("out_report_lengths")
    assert len(lengths) == len(report_ids)
    for rid, declared in zip(report_ids, lengths):
        assert declared == sizes.get(("out", rid), 0), f"out_report_lengths wrong for ID {rid}"
```

- [ ] **Step 2: Run test to verify it fails on the real divergence**

Run: `python -m pytest tests/test_descriptor_parity.py -q`
Expected: FAIL — `test_descriptors_are_byte_identical` reports
`descriptor divergence: boot.py has 97 bytes, main_generic_hid.cpp has 112`.
`test_boot_py_report_ids_cover_descriptor` also fails once boot.py gains ID 4.

This failure is the pre-existing bug. Confirm the message names the byte counts before proceeding — that proves the extraction works rather than silently matching nothing.

- [ ] **Step 3: Add the missing report 0x04 section to `firmware/boot.py`**

In `firmware/boot.py`, insert immediately **before** the closing `0xC0` line of `GENERIC_HID_REPORT_DESCRIPTOR` (after the Output Report ID 0x03 block), matching the C++ file's ordering exactly:

```python
    # ---- Input Report ID 0x04: Decoder Diagnostics (56 bytes) ----
    0x85, 0x04,        #   Report ID (4)
    0x09, 0x08,        #   Usage (Vendor Usage 8 - Decoder Diagnostics)
    0x15, 0x00,        #   Logical Minimum (0)
    0x26, 0xFF, 0x00,  #   Logical Maximum (255)
    0x75, 0x08,        #   Report Size (8 bits)
    0x95, 0x38,        #   Report Count (56 bytes)
    0x81, 0x02,        #   Input (Data, Variable, Absolute)

```

- [ ] **Step 4: Update the device declaration in `firmware/boot.py`**

Replace the `usb_hid.Device(...)` tuples and the comment above them:

```python
# Create the Generic HID device descriptor
# report_ids: all report IDs used across Input and Output reports
# in_report_lengths / out_report_lengths: indexed by POSITION in report_ids,
#   not by report ID value.
#   ID 1 = 21B in / no out    ID 2 = 106B in / 106B out
#   ID 3 = no in / 2B out     ID 4 = 56B in / no out
GENERIC_HID_DEVICE = usb_hid.Device(
    report_descriptor=GENERIC_HID_REPORT_DESCRIPTOR,
    usage_page=0xFF00,                      # Vendor Defined
    usage=0x01,                             # Vendor Usage 1
    report_ids=(1, 2, 3, 4),                # All report IDs
    in_report_lengths=(21, 106, 0, 56),     # Input Report sizes per ID
    out_report_lengths=(0, 106, 2, 0),      # Output Report sizes per ID
)
```

Also update the module docstring, the descriptor comment block, and the closing `print()` lines to mention Input Report ID 0x04 (56 bytes, decoder diagnostics).

- [ ] **Step 5: Run tests to verify parity is restored**

Run: `python -m pytest tests/test_descriptor_parity.py -q`
Expected: PASS, **except** `test_derived_report_sizes`, which still fails asserting
`sizes[("in", 1)] == POSITION_REPORT_SIZE` (36) while the descriptor still declares 21.

That is correct and expected at this point: Task 1 already declared the 36-byte target, and Task 3 changes the descriptors to match. To keep the tree green between tasks, temporarily mark only that one test:

```python
@pytest.mark.xfail(reason="descriptor still declares the 21-byte report 0x01; Task 3 resizes it")
def test_derived_report_sizes():
```

Task 3 Step 6 removes this decorator. Do not xfail anything else.

Run: `python -m pytest tests/ -q`
Expected: PASS with one xfail.

- [ ] **Step 6: Commit**

```bash
git add tests/test_descriptor_parity.py firmware/boot.py
git commit -m "test: add HID descriptor parity guard, close report 0x04 divergence

The CircuitPython descriptor was 15 bytes shorter than the C++ one - it
never received the Input Report ID 0x04 declaration. Nothing detected
this. The new test walks both descriptors, derives report sizes, and
cross-checks them against boot.py's length tuples and reports.py."
```

---

### Task 3: 36-byte wire format — both descriptors and the C++ accumulator

Resize Input Report ID 0x01 to 36 bytes in **both** descriptors and implement the accumulator in the C++ firmware. Descriptors move together so the parity test from Task 2 stays green and actively guards the change.

**Files:**
- Modify: `firmware-cpp/main_generic_hid.cpp`
- Modify: `firmware/boot.py`
- Modify: `tests/test_descriptor_parity.py` (remove the xfail)

**Interfaces:**
- Consumes: the parity test and `POSITION_REPORT_SIZE = 36` from Tasks 1–2
- Produces: `GenericHidEncoder::get_movement() -> int32_t`; `PositionReport.movement[4]` at offset 20

- [ ] **Step 1: Resize the Report ID 0x01 section in the C++ descriptor**

In `firmware-cpp/main_generic_hid.cpp`, in the Usage-4 (tier + reserved) item, change the Report Count from 4 to 3 and add the movement item after it:

```c
    // Acceleration tier byte + 2 reserved bytes (3 bytes)
    0x09, 0x04,        //   Usage (Vendor Usage 4 - Tier + Reserved)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x03,        //   Report Count (3: tier byte + 2 reserved)
    0x81, 0x02,        //   Input (Data, Variable, Absolute)

    // Movement accumulators (16 bytes = 4x int32)
    // Free-running signed totals; the host differences them. Keeps accruing when
    // position is clamped at min_value/max_value, which is the entire point.
    0x09, 0x09,        //   Usage (Vendor Usage 9 - Movement Accumulators)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x10,        //   Report Count (16 bytes = 4x int32)
    0x81, 0x02,        //   Input (Data, Variable, Absolute)
```

- [ ] **Step 2: Make the identical edit in `firmware/boot.py`, and resize its length tuple**

Same two descriptor changes, Python comment syntax (`#` instead of `//`), same position in the descriptor. The byte sequence must match exactly — that is what the parity test verifies.

**Then update the declared input length for report ID 1 from 21 to 36**, which is a separate declaration the descriptor edit does not touch:

```python
    in_report_lengths=(36, 106, 0, 56),     # Input Report sizes per ID
```

Update the comment above it (`ID 1 = 21B in` → `36B in`), the module docstring, and the closing `print()` lines, all of which still say 21 bytes.

- [ ] **Step 3: Resize `PositionReport` in the C++ firmware**

Replace the struct and its static assert:

```cpp
// Input Report: absolute positions + buttons + tiers + movement accumulators
struct PositionReport {
    int32_t positions[NUM_ENCODERS];   // 0-15
    uint8_t button_states;             // 16
    uint8_t active_tiers;              // 17
    uint8_t reserved[2];               // 18-19
    // Free-running signed accumulators. Placed at a 4-byte-aligned offset: the
    // RP2040 is a Cortex-M0+, which HardFaults on unaligned word access.
    int32_t movement[NUM_ENCODERS];    // 20-35
} __attribute__((packed));

static_assert(sizeof(PositionReport) == 36, "PositionReport must be 36 bytes");
static_assert(offsetof(PositionReport, movement) == 20, "movement must be 4-byte aligned at offset 20");
```

Add `#include <cstddef>` alongside the existing `<cstdint>` include for `offsetof`.

- [ ] **Step 4: Add the accumulator to `GenericHidEncoder`**

Add the member declaration next to the diagnostics counters in the private section:

```cpp
    // Free-running signed movement accumulator, in the same units as position_.
    // uint32_t because signed overflow is undefined behavior in C++ and this value
    // is expected to wrap; the wire reinterprets the bit pattern as int32.
    // Deliberately NOT reset by reset_position(): position and movement answer
    // different questions, and zeroing an odometer because the dial was re-zeroed
    // injects a phantom delta into every host that is differencing it.
    uint32_t movement_;
```

Add `, movement_(0)` to the constructor initializer list, after `detent_count_(0)`.

Add the accessor next to `get_position()`:

```cpp
    int32_t get_movement() const {
        // memcpy, not a cast: converting an out-of-range uint32_t to int32_t is
        // implementation-defined before C++20, and this project builds C++17.
        int32_t out;
        memcpy(&out, &movement_, sizeof(out));
        return out;
    }
```

- [ ] **Step 5: Accumulate before clamping in `update()`**

In the detent block, immediately after `effective_step` is computed and **before** the `position_ = clamp_position(...)` call:

```cpp
                    // Accumulated pre-clamp, so motion is still reported when
                    // position_ is pinned at min_value or max_value. int64
                    // intermediate because compute_effective_step() can return
                    // INT32_MIN, and negating that in int32 is undefined behavior.
                    movement_ += (uint32_t)((int64_t)detent_direction * (int64_t)effective_step);
```

Then in `hid_task()`, inside the existing per-encoder loop that fills `positions[i]`, add:

```cpp
        current_report.movement[i] = encoders[i]->get_movement();
```

**Do not touch the send-gating `memcmp` in `hid_task()`.** `movement` changing is exactly what makes the report differ from `last_report` while turning at a limit, so the existing change-detection sends it with no special case. That is the fix.

Also update the file header comment: Input Report ID 0x01 is now 36 bytes.

- [ ] **Step 6: Remove the xfail from Task 2**

Delete the `@pytest.mark.xfail(...)` decorator on `test_derived_report_sizes` in `tests/test_descriptor_parity.py`. If `pytest` is now an unused import, leave it — other tests may use it later; if the linter objects, remove it.

- [ ] **Step 7: Run tests**

Run: `python -m pytest tests/ -q`
Expected: PASS, **zero xfail**. `test_derived_report_sizes` now asserts `in/1 == 36` against both descriptors, and `test_descriptors_are_byte_identical` proves the two edits match byte for byte.

- [ ] **Step 8: Build the firmware**

Run:
```bash
cd firmware-cpp && rm -rf build && mkdir -p build && cd build && cmake .. && make -j4
```
Expected: builds clean. The `static_assert`s on `sizeof(PositionReport) == 36` and `offsetof(..., movement) == 20` are compile-time proof of the layout.

If the Pico SDK is unavailable in this environment, record that the build was not run and flag it in the final report — do not silently skip it.

Also confirm keyboard mode still builds:
```bash
cd firmware-cpp/build && cmake -DFIRMWARE_MODE=keyboard .. && make -j4 && cmake -DFIRMWARE_MODE=generic_hid .. && make -j4
```

- [ ] **Step 9: Commit**

```bash
git add firmware-cpp/main_generic_hid.cpp firmware/boot.py tests/test_descriptor_parity.py
git commit -m "feat(firmware): add movement accumulator to HID report 0x01

Report 0x01 grows 21 -> 36 bytes; bytes 0-17 keep their exact meaning.
The accumulator is updated before clamp_position(), so a knob held
against min_value/max_value keeps reporting motion - which also means
the existing memcmp send-gate now fires at the limit with no change."
```

---

### Task 4: CircuitPython runtime — movement plus the diagnostics backport

Bring `code_generic_hid.py` to full parity with the C++ firmware: the movement accumulator, the three decoder diagnostic counters, `CMD_RESET_DIAG`, and the 10 Hz Input Report ID 0x04 emission. Report packing moves to `firmware/reports.py` from Task 1.

**Files:**
- Modify: `firmware/code_generic_hid.py`
- Test: `tests/test_reports.py` (extend)

**Interfaces:**
- Consumes: everything `firmware/reports.py` produces (Task 1); the boot.py declarations (Tasks 2–3)
- Produces: `Encoder.movement`, `Encoder.edge_count`, `Encoder.invalid_count`, `Encoder.detent_count`, `Encoder.read_raw_pins()`, `Encoder.reset_diagnostics()`

- [ ] **Step 1: Write the failing test**

Append to `tests/test_reports.py`. This models the exact firmware decode sequence, so it pins the semantics the CircuitPython code must implement:

```python
# ---- Semantics the firmware decode path must honor ----

def test_movement_accrues_while_position_is_clamped():
    """
    The whole point of the feature: at max_value, position stops and movement
    does not. Models the firmware's per-detent sequence.
    """
    from config import clamp_position  # sys.path already set at module top

    min_value, max_value, step_size = 0, 100, 1
    position, movement = 100, 0  # already pinned at max

    for _ in range(5):
        effective_step = step_size * 1
        movement = accumulate_movement(movement, 1 * effective_step)
        position = clamp_position(position + 1 * effective_step,
                                  min_value, max_value, False)

    assert position == 100, "position must stay clamped"
    assert to_signed_i32(movement) == 5, "movement must keep accruing at the limit"


def test_movement_reverses_at_the_limit():
    from config import clamp_position

    position, movement = 100, 0
    for _ in range(3):
        movement = accumulate_movement(movement, -1)
        position = clamp_position(position - 1, 0, 100, False)

    assert position == 97
    assert to_signed_i32(movement) == -3


def test_diag_report_packs_counters_that_survive_clamping():
    """detent_count is incremented before position math, so it counts at a limit."""
    data = pack_diag_report([7] * 4, 4, [40, 0, 0, 0], [0] * 4, [10, 0, 0, 0])
    assert struct.unpack_from("<I", data, 8)[0] == 40    # edges
    assert struct.unpack_from("<I", data, 40)[0] == 10   # detents
```

- [ ] **Step 2: Run the new tests — these are characterization tests, so they must PASS**

Run: `python -m pytest tests/test_reports.py -q`
Expected: **PASS.** Unlike Task 1's tests, these exercise `reports.py` and `config.py`, which both already exist. Their job is to lock in the clamped-position/accruing-movement semantics *before* the firmware is edited, so the CircuitPython implementation in Step 3 has an executable definition of correct to code against.

If any of them fails, the Task 1 implementation is wrong — stop and fix it before touching the firmware.

- [ ] **Step 3: Add movement and diagnostics to the `Encoder` class**

In `firmware/code_generic_hid.py`, add the import at the top with the other local imports:

```python
from reports import (
    accumulate_movement, pack_position_report, pack_diag_report,
    POSITION_REPORT_SIZE,
)
```

Add `CMD_RESET_DIAG = 0x05` to the command constants.

In `Encoder.__init__`, after `self.active_tier = 0`:

```python
        # Free-running signed movement accumulator, same units as position.
        # Held unsigned and masked to 32 bits; the wire reinterprets it as int32.
        # Deliberately NOT cleared by reset_position() - see the C++ firmware and
        # docs/superpowers/specs/2026-08-18-movement-accumulator-design.md.
        self.movement = 0

        # Decoder diagnostics (Input Report ID 0x04). Monotonic totals across both
        # directions; the host zeroes them with command 0x05.
        self.edge_count = 0
        self.invalid_count = 0
        self.detent_count = 0
```

Add these two methods to `Encoder`:

```python
    def read_raw_pins(self):
        """
        Literal GPIO levels, NOT inverted: (A<<2)|(B<<1)|SW.

        With internal pull-ups and nothing pressed this reads 7 (0b111); a held
        button clears bit 0 giving 6.

        WARNING: this is the opposite convention from _read_ab_state(), which
        inverts to active-high for the quadrature transition table. Two readers,
        two conventions, on purpose. Do not substitute one for the other.
        """
        a = 1 if self.pin_a.value else 0
        b = 1 if self.pin_b.value else 0
        sw = 1 if self.pin_sw.value else 0
        return (a << 2) | (b << 1) | sw

    def reset_diagnostics(self):
        """Zero the diagnostic counters. Does not touch movement or position."""
        self.edge_count = 0
        self.invalid_count = 0
        self.detent_count = 0
```

- [ ] **Step 4: Wire the counters and the accumulator into `update()`**

In `Encoder.update()`, inside `if current_ab_state != self.last_ab_state:`, immediately after that line:

```python
            self.edge_count += 1
```

In the detent block, immediately after `detent_direction = 1 if self.steps > 0 else -1` (i.e. before the acceleration and position math):

```python
                    # Counted before the position math, so clamping at min_value or
                    # max_value does not hide emitted detents.
                    self.detent_count += 1
```

Then, after `effective_step` is computed and **before** `self.position = clamp_position(...)`:

```python
                    # Accumulated pre-clamp, so motion is still reported when
                    # position is pinned at min_value or max_value.
                    self.movement = accumulate_movement(
                        self.movement, detent_direction * effective_step)
```

In the `else:` branch that currently only contains `self.steps = 0` (around `code_generic_hid.py:219`), add the counter so the two firmwares report identical numbers for identical physical input:

```python
            else:
                # TRANSITION_TABLE yields 0 here only for a simultaneous A+B change,
                # which is physically impossible in clean quadrature. This counts
                # contact bounce, a marginal connection, or a missed poll. Never a
                # decoder logic error.
                self.invalid_count += 1
                self.steps = 0
```

- [ ] **Step 5: Use `reports.py` for packing and add report 0x04 emission**

Replace the inline `struct.pack("<iiiiBBxxx", ...)` call in `main()` with:

```python
            report = pack_position_report(
                [enc.position for enc in encoders],
                button_states,
                tier_byte,
                [enc.movement for enc in encoders],
            )
```

Change `last_report = bytearray(21)` to `last_report = bytearray(POSITION_REPORT_SIZE)`.

Add `CMD_RESET_DIAG` handling next to the other commands:

```python
            elif command == CMD_RESET_DIAG:
                for enc in encoders:
                    enc.reset_diagnostics()
                if DEBUG_ENABLED:
                    print("Diagnostic counters reset")
```

Add the diagnostics heartbeat. Declare alongside `last_report_time`:

```python
    last_diag_time = time.monotonic()
    DIAG_INTERVAL = 0.100  # 10 Hz
```

And emit it at the **end** of the loop body, after the position-report block, so it stays lowest priority exactly as in the C++ firmware:

```python
        # Diagnostics (ID 0x04) are lowest priority: a position report takes
        # precedence. Positions only change on detents, so 10 Hz holds in practice.
        if (current_time - last_diag_time) >= DIAG_INTERVAL:
            last_diag_time = current_time
            try:
                diag = pack_diag_report(
                    [enc.read_raw_pins() for enc in encoders],
                    steps_per_detent,
                    [enc.edge_count for enc in encoders],
                    [enc.invalid_count for enc in encoders],
                    [enc.detent_count for enc in encoders],
                )
                hid_device.send_report(diag, 4)  # Report ID 4
            except Exception as e:
                if DEBUG_ENABLED:
                    print(f"Error sending diag report: {e}")
```

Update the module docstring's HID REPORT FORMAT block: Report ID 0x01 is 36 bytes with movement at 20–35, and Input Report ID 0x04 (56 bytes) now exists.

- [ ] **Step 6: Run tests**

Run: `python -m pytest tests/ -q`
Expected: PASS, no regressions. (`code_generic_hid.py` imports `board`/`digitalio` and is not importable on desktop; it is validated by the descriptor parity test, by `reports.py` coverage, and by hardware UAT.)

- [ ] **Step 7: Byte-compile check**

Run: `python -m py_compile firmware/code_generic_hid.py firmware/boot.py firmware/reports.py`
Expected: no output. This catches syntax and indentation errors that pytest cannot, since the module is not importable off-device.

- [ ] **Step 8: Commit**

```bash
git add firmware/code_generic_hid.py tests/test_reports.py
git commit -m "feat(firmware): movement accumulator and decoder diagnostics for CircuitPython

Brings code_generic_hid.py to parity with the C++ firmware: movement
accumulation, edge/invalid/detent counters, CMD_RESET_DIAG, and the
10 Hz report 0x04 heartbeat. Report packing moves to reports.py, which
gives the wire format its first test coverage."
```

---

### Task 5: Windows example — display movement and the host-accumulated value

Make the feature visible and demonstrate the exact pattern an integrator copies. The live monitor's second new column is the acceptance evidence: at a limit, position freezes while the unbounded value keeps climbing.

**Files:**
- Modify: `windows-example/Program.cs`
- Modify: `windows-example/README.md`

**Interfaces:**
- Consumes: the 36-byte report from Task 3
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Add constants and state**

Next to `DIAG_PAYLOAD_SIZE`:

```csharp
    // Input Report ID 0x01 payload size, in bytes after the report ID byte.
    private const int POSITION_PAYLOAD_SIZE = 36;

    // Firmware built before the movement accumulator reports 21 payload bytes.
    private const int LEGACY_POSITION_PAYLOAD_SIZE = 21;
```

Next to the existing state fields:

```csharp
    // Movement accumulator (Input Report ID 0x01, payload 20-35). All guarded by _lock.
    private static readonly int[] _movementRaw = new int[NUM_ENCODERS];
    private static readonly long[] _hostAccumulated = new long[NUM_ENCODERS];
    private static readonly int[] _movementLast = new int[NUM_ENCODERS];
    private static bool _movementBaselined;
    private static bool _firmwareHasMovement;
```

- [ ] **Step 2: Parse movement in the reader loop**

Replace the `REPORT_ID_POSITIONS` branch:

```csharp
            if (reportId == REPORT_ID_POSITIONS && report.Data.Length >= LEGACY_POSITION_PAYLOAD_SIZE + 1)
            {
                // HidLibrary prepends the report ID, so buffer index = payload offset + 1.
                lock (_lock)
                {
                    for (int i = 0; i < NUM_ENCODERS; i++)
                        _encoderPositions[i] = BitConverter.ToInt32(report.Data, 1 + i * 4);
                    _buttonStates = report.Data[17];
                    _tierByte = report.Data[18];

                    if (report.Data.Length >= POSITION_PAYLOAD_SIZE + 1)
                    {
                        _firmwareHasMovement = true;
                        for (int i = 0; i < NUM_ENCODERS; i++)
                        {
                            // payload 20-35
                            int now = BitConverter.ToInt32(report.Data, 21 + i * 4);
                            _movementRaw[i] = now;

                            if (_movementBaselined)
                            {
                                // unchecked: the accumulator wraps at 32 bits by
                                // design, and two's-complement subtraction gives the
                                // correct signed delta straight across the boundary.
                                int delta = unchecked(now - _movementLast[i]);
                                _hostAccumulated[i] += delta;
                            }
                            _movementLast[i] = now;
                        }
                        // Baseline on the first report so a device that has been
                        // spinning before we attached does not dump its whole
                        // history into the first delta.
                        _movementBaselined = true;
                    }
                }
            }
```

- [ ] **Step 3: Show it in the live monitor**

Replace the per-encoder line in `RunMonitor()`:

```csharp
                Console.WriteLine("       Position        Range   Movement   Unbounded  Tier");
                for (int i = 0; i < NUM_ENCODERS; i++)
                {
                    var enc = _deviceConfig.Encoders[i];
                    int tier = (_tierByte >> (i * 2)) & 0x03;
                    string tierStr = tier switch
                    {
                        1 => "*",
                        2 => "**",
                        3 => "***",
                        _ => ""
                    };
                    string range = $"[{enc.MinValue}-{enc.MaxValue}]";
                    string movement = _firmwareHasMovement ? _movementRaw[i].ToString() : "n/a";
                    string unbounded = _firmwareHasMovement ? _hostAccumulated[i].ToString() : "n/a";
                    Console.WriteLine(
                        $"Enc{i + 1}: {_encoderPositions[i],10}  {range,-12} {movement,10} {unbounded,11}  {tierStr,-3}"
                            .PadRight(78));
                }
```

After the buttons block, add the explanatory footer:

```csharp
                Console.WriteLine();
                if (_firmwareHasMovement)
                {
                    Console.WriteLine("Turn a knob to its limit and keep turning:".PadRight(78));
                    Console.WriteLine("Position holds; Movement and Unbounded keep moving.".PadRight(78));
                }
                else
                {
                    Console.WriteLine("Firmware predates the movement accumulator (21-byte report).".PadRight(78));
                }
```

- [ ] **Step 4: Build and run**

Run: `cd windows-example && dotnet build`
Expected: builds with no errors and no new warnings.

- [ ] **Step 5: Update `windows-example/README.md`**

Document the two new monitor columns and the `n/a` legacy-firmware case.

- [ ] **Step 6: Commit**

```bash
git add windows-example/Program.cs windows-example/README.md
git commit -m "feat(windows-example): show movement and host-accumulated unbounded value

The Unbounded column is the visible proof of the feature: at a limit
Position freezes while Unbounded keeps climbing. Degrades to n/a on
firmware that still sends the 21-byte report."
```

---

### Task 6: Integration guide and documentation

Deliver `docs/INTEGRATION.md`, the guide an agentic team uses to wire this device into the RTest radio console. It must be sufficient without reading firmware source.

**Files:**
- Create: `docs/INTEGRATION.md`
- Modify: `README.md`, `firmware-cpp/README.md`, `firmware/README.md`

**Interfaces:**
- Consumes: the finished protocol from Tasks 1–5
- Produces: the project's public integration contract

- [ ] **Step 1: Write `docs/INTEGRATION.md`**

Required sections, in order:

1. **What this device is** — one paragraph, plus a decision table for *`position` vs `movement`*:

   | Control | Use | Why |
   |---|---|---|
   | Volume, squelch, bounded setting | `position` | Device owns the range; clamping is the desired behavior |
   | VFO frequency, unbounded value | `movement` delta | Host owns the range; device range is irrelevant |
   | Menu / preset selector | `movement` delta, or `position` with `wrap=1` | Wrap at the ends without host math |
   | Momentary action | `button_states` bit | — |

2. **Setup** — firmware choice (C++ recommended; note both now implement the identical wire format), flashing, and the steps-per-detent decision including the Report 0x04 measurement procedure: zero the counters, turn a counted number of physical clicks, divide `edge_count / clicks` — 4 for KY-040 class, 2 for bare EC11 — and check `invalid_count` first, because bounce inflates `edge_count` and can make a 2-step encoder read as 4.
3. **Discovery and feature detection** — VID `0xCAFE` / PID `0x4005` (and the CircuitPython `0x239A`/`0x80F4`), usage page `0xFF00`, usage `0x01`; feature-detect via `InputReportByteLength` (37 = has movement, 22 = legacy).
4. **Wire protocol reference** — every report with a full offset table, little-endian, and explicitly: *reports are sent only when their contents change*, diagnostics at 10 Hz, config readback only on request.
5. **Reference implementation** — a complete, compilable `RotaryUsbDevice` C# class: discovery, background read loop, `unchecked` differencing, re-baselining on reconnect, and `EncoderMoved` / `ButtonChanged` events.
6. **Mapping recipes** — bounded control, unbounded VFO, detent selector, buttons.
7. **Configuring from the host** — config write layout, the validation rules the firmware enforces (`min < max`; `step_size > 0`; enabled tier thresholds strictly descending; multipliers strictly ascending; a tier with `threshold_ms > 0` must have `multiplier != 0`), save-to-flash, all five commands.
8. **Diagnostics and troubleshooting.**
9. **Gotchas checklist** — HidLibrary prepends the report ID; reports sent only on change; re-baseline the accumulator on reconnect; use `unchecked` for the delta; **the accumulator survives `CMD_RESET_POSITIONS`**; raw pins are sampled at 10 Hz so they read 7 at rest even mid-spin.
10. **Integration checklist** for the agent team.

- [ ] **Step 2: Update the firmware READMEs**

`firmware-cpp/README.md`: the Report 0x01 table becomes 36 bytes with `movement` at 20–35 and reserved at 18–19; add the movement semantics paragraph and the feature-detection note; update the report-summary table's 21 → 36.

`firmware/README.md`: the same protocol updates, plus the newly supported Input Report ID 0x04 and `CMD_RESET_DIAG`, and remove any statement that diagnostics are C++-only.

`README.md`: link `docs/INTEGRATION.md` prominently near the top.

- [ ] **Step 3: Verify every documented number against the code**

Cross-check each offset and size in the new docs against `firmware/reports.py` and the descriptor. Confirm no README still claims report 0x01 is 21 bytes:

```bash
grep -rn "21 bytes\|21-byte" README.md firmware/README.md firmware-cpp/README.md windows-example/README.md docs/INTEGRATION.md
```
Expected: only historical/compatibility mentions remain (e.g. describing legacy firmware), never a current-format claim.

- [ ] **Step 4: Run the full suite one more time**

Run: `python -m pytest tests/ -q`
Expected: all pass, zero xfail.

- [ ] **Step 5: Commit**

```bash
git add docs/INTEGRATION.md README.md firmware/README.md firmware-cpp/README.md
git commit -m "docs: add integration guide for consuming projects"
```

---

## Final Verification

- [ ] `python -m pytest tests/ -q` — all pass, zero xfail, ≥36 pre-existing tests still green
- [ ] `python -m py_compile firmware/*.py` — clean
- [ ] `cmake .. && make -j4` in a clean `firmware-cpp/build` — clean (or explicitly reported as unavailable)
- [ ] `cmake -DFIRMWARE_MODE=keyboard ..` still builds — keyboard mode unaffected
- [ ] `cd windows-example && dotnet build` — clean
- [ ] `git log --oneline` shows one commit per task
- [ ] Open a PR to `main`; do not merge — the user is performing UAT

## Hardware UAT Script (for the user)

Per firmware (C++ and CircuitPython):

1. Flash; launch `windows-example`; open the live monitor.
2. Turn an encoder mid-range → Position advances, Movement advances by the same amount.
3. **Turn to the maximum and keep turning → Position holds at 100, Movement and Unbounded keep climbing.** This is the acceptance criterion.
4. Reverse at the limit → Movement decrements; Position stays at 100 until accumulated motion re-enters the range.
5. Spin fast → delta magnitude grows with the acceleration tier.
6. Press `[D]` → diagnostics populate on **both** firmwares.
7. Leave the device idle → no report spam.
8. Send Reset Positions → Position returns to `min_value`; **Movement does not reset** (by design).
