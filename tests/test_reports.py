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
