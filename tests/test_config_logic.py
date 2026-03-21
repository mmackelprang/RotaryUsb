# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""Tests for config validation, serialization, position wrapping, and acceleration."""

import sys
import os

# Add firmware directory to path so we can import config module
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "firmware"))

from config import (
    EncoderConfig, TierConfig, DeviceConfig,
    factory_default_config, factory_default_encoder,
    validate_encoder_config, validate_config,
    clamp_position, select_acceleration_tier, compute_effective_step,
    FULL_CONFIG_SIZE,
)


# ---- Serialization round-trip ----

def test_encoder_config_roundtrip():
    enc = EncoderConfig(
        min_value=-1000, max_value=1000, step_size=10,
        wrap=True, reverse=True,
        tiers=[TierConfig(200, 3), TierConfig(100, 10), TierConfig(50, 50)],
    )
    data = enc.pack()
    assert len(data) == 26
    restored = EncoderConfig.unpack(data)
    assert restored.min_value == -1000
    assert restored.max_value == 1000
    assert restored.step_size == 10
    assert restored.wrap is True
    assert restored.reverse is True
    assert restored.tiers[0].threshold_ms == 200
    assert restored.tiers[2].multiplier == 50


def test_full_config_roundtrip():
    config = factory_default_config()
    data = config.pack()
    assert len(data) == FULL_CONFIG_SIZE
    restored = DeviceConfig.unpack(data)
    assert restored is not None
    assert restored.version == config.version
    assert len(restored.encoders) == 4
    assert restored.encoders[0].max_value == 100


def test_unpack_wrong_size_returns_none():
    assert DeviceConfig.unpack(b"\x00" * 50) is None


def test_unpack_wrong_version_returns_none():
    data = bytearray(FULL_CONFIG_SIZE)
    data[0] = 0xFF  # wrong version
    assert DeviceConfig.unpack(bytes(data)) is None


# ---- Validation ----

def test_valid_default_config():
    assert validate_config(factory_default_config())


def test_invalid_min_ge_max():
    enc = factory_default_encoder()
    enc.min_value = 100
    enc.max_value = 50
    assert not validate_encoder_config(enc)


def test_invalid_min_eq_max():
    enc = factory_default_encoder()
    enc.min_value = 50
    enc.max_value = 50
    assert not validate_encoder_config(enc)


def test_invalid_step_zero():
    enc = factory_default_encoder()
    enc.step_size = 0
    assert not validate_encoder_config(enc)


def test_invalid_step_negative():
    enc = factory_default_encoder()
    enc.step_size = -1
    assert not validate_encoder_config(enc)


def test_invalid_tier_thresholds_not_descending():
    enc = factory_default_encoder()
    enc.tiers[0].threshold_ms = 80
    enc.tiers[1].threshold_ms = 150  # tier2 > tier1, wrong
    assert not validate_encoder_config(enc)


def test_invalid_tier_multipliers_not_ascending():
    enc = factory_default_encoder()
    enc.tiers[0].multiplier = 20
    enc.tiers[1].multiplier = 10  # tier2 < tier1, wrong
    assert not validate_encoder_config(enc)


def test_disabled_middle_tier_valid():
    """Tier 2 disabled, tiers 1 and 3 enabled — should validate tier1 vs tier3."""
    enc = factory_default_encoder()
    enc.tiers[0] = TierConfig(150, 5)
    enc.tiers[1] = TierConfig(0, 0)  # disabled
    enc.tiers[2] = TierConfig(40, 50)
    assert validate_encoder_config(enc)


def test_invalid_enabled_tier_zero_multiplier():
    """Enabled tier with multiplier=0 should be invalid (would freeze encoder)."""
    enc = factory_default_encoder()
    enc.tiers[0] = TierConfig(150, 0)  # enabled threshold, but zero multiplier
    assert not validate_encoder_config(enc)


def test_invalid_enabled_tier_zero_multiplier_middle():
    """Middle tier enabled with multiplier=0 should be invalid."""
    enc = factory_default_encoder()
    enc.tiers[1] = TierConfig(80, 0)
    assert not validate_encoder_config(enc)


def test_disabled_middle_tier_invalid_threshold():
    """Tier 2 disabled, but tier3 threshold > tier1 threshold — invalid."""
    enc = factory_default_encoder()
    enc.tiers[0] = TierConfig(50, 5)
    enc.tiers[1] = TierConfig(0, 0)  # disabled
    enc.tiers[2] = TierConfig(100, 50)  # threshold > tier1
    assert not validate_encoder_config(enc)


def test_all_tiers_disabled_valid():
    enc = factory_default_encoder()
    enc.tiers = [TierConfig(0, 0), TierConfig(0, 0), TierConfig(0, 0)]
    assert validate_encoder_config(enc)


def test_large_mhz_values_valid():
    enc = EncoderConfig(
        min_value=88000, max_value=108000, step_size=100,
        wrap=True, reverse=False,
        tiers=[TierConfig(150, 10), TierConfig(80, 100), TierConfig(40, 1000)],
    )
    assert validate_encoder_config(enc)


# ---- Position clamping and wrapping ----

def test_clamp_within_range():
    assert clamp_position(50, 0, 100, wrap=False) == 50


def test_clamp_above_max():
    assert clamp_position(150, 0, 100, wrap=False) == 100


def test_clamp_below_min():
    assert clamp_position(-10, 0, 100, wrap=False) == 0


def test_wrap_above_max():
    # 0-9 range (10 values), position=10 wraps to 0
    assert clamp_position(10, 0, 9, wrap=True) == 0


def test_wrap_above_max_by_two():
    assert clamp_position(11, 0, 9, wrap=True) == 1


def test_wrap_below_min():
    # position=-1 wraps to 9
    assert clamp_position(-1, 0, 9, wrap=True) == 9


def test_wrap_below_min_by_two():
    assert clamp_position(-2, 0, 9, wrap=True) == 8


def test_wrap_exact_range_below():
    # position=-10 wraps to 0 (full cycle)
    assert clamp_position(-10, 0, 9, wrap=True) == 0


def test_wrap_negative_range():
    # min=-50, max=50, position=51 wraps to -50
    assert clamp_position(51, -50, 50, wrap=True) == -50


def test_wrap_large_mhz_values():
    # Radio tuner: 88000-108000, step over max
    assert clamp_position(108001, 88000, 108000, wrap=True) == 88000
    assert clamp_position(87999, 88000, 108000, wrap=True) == 108000


# ---- Acceleration tier selection ----

def test_normal_speed():
    tiers = [TierConfig(150, 5), TierConfig(80, 15), TierConfig(40, 50)]
    tier_idx, mult = select_acceleration_tier(200, tiers)
    assert tier_idx == 0
    assert mult == 1


def test_tier1_speed():
    tiers = [TierConfig(150, 5), TierConfig(80, 15), TierConfig(40, 50)]
    tier_idx, mult = select_acceleration_tier(100, tiers)
    assert tier_idx == 1
    assert mult == 5


def test_tier2_speed():
    tiers = [TierConfig(150, 5), TierConfig(80, 15), TierConfig(40, 50)]
    tier_idx, mult = select_acceleration_tier(60, tiers)
    assert tier_idx == 2
    assert mult == 15


def test_tier3_speed():
    tiers = [TierConfig(150, 5), TierConfig(80, 15), TierConfig(40, 50)]
    tier_idx, mult = select_acceleration_tier(30, tiers)
    assert tier_idx == 3
    assert mult == 50


def test_disabled_middle_tier_skipped():
    tiers = [TierConfig(150, 5), TierConfig(0, 0), TierConfig(40, 50)]
    # 60ms: tier3 disabled check (40ms, 60>40 so no), tier2 disabled, tier1 (150ms, 60<150 so yes)
    tier_idx, mult = select_acceleration_tier(60, tiers)
    assert tier_idx == 1
    assert mult == 5


def test_all_tiers_disabled():
    tiers = [TierConfig(0, 0), TierConfig(0, 0), TierConfig(0, 0)]
    tier_idx, mult = select_acceleration_tier(10, tiers)
    assert tier_idx == 0
    assert mult == 1


# ---- Effective step computation ----

def test_effective_step_normal():
    assert compute_effective_step(100, 10) == 1000


def test_effective_step_overflow_clamps():
    assert compute_effective_step(2000000000, 2) == 2147483647  # INT32_MAX


def test_effective_step_negative_overflow():
    assert compute_effective_step(-2000000000, 2) == -2147483648  # INT32_MIN


if __name__ == "__main__":
    import pytest
    pytest.main([__file__, "-v"])
