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
