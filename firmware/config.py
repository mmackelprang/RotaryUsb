# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
Configuration data structures, defaults, validation, and serialization
for RotaryUsb Generic HID mode.

This module has no CircuitPython dependencies and can be tested on desktop Python.
"""

import struct

# Config binary format version
CONFIG_VERSION = 0x01

# Struct format for one encoder config block (26 bytes, little-endian)
# int32 min, int32 max, int32 step, uint8 wrap, uint8 reverse,
# 3x (uint16 threshold, uint16 multiplier)
ENCODER_CONFIG_STRUCT = struct.Struct("<iiiBBHHHHHH")
assert ENCODER_CONFIG_STRUCT.size == 26

# Full config: 2-byte header + 4 x 26-byte encoder blocks = 106 bytes
FULL_CONFIG_SIZE = 106

# Number of encoders
NUM_ENCODERS = 4

# Number of acceleration tiers
NUM_TIERS = 3


class TierConfig:
    """Single acceleration tier configuration."""
    __slots__ = ("threshold_ms", "multiplier")

    def __init__(self, threshold_ms=0, multiplier=1):
        self.threshold_ms = threshold_ms
        self.multiplier = multiplier


class EncoderConfig:
    """Configuration for a single encoder."""
    __slots__ = ("min_value", "max_value", "step_size", "wrap", "reverse", "tiers")

    def __init__(self, min_value=0, max_value=100, step_size=1,
                 wrap=False, reverse=False, tiers=None):
        self.min_value = min_value
        self.max_value = max_value
        self.step_size = step_size
        self.wrap = wrap
        self.reverse = reverse
        if tiers is None:
            self.tiers = [TierConfig(), TierConfig(), TierConfig()]
        else:
            self.tiers = tiers

    def pack(self):
        """Pack into 26-byte binary."""
        return ENCODER_CONFIG_STRUCT.pack(
            self.min_value, self.max_value, self.step_size,
            1 if self.wrap else 0,
            1 if self.reverse else 0,
            self.tiers[0].threshold_ms, self.tiers[0].multiplier,
            self.tiers[1].threshold_ms, self.tiers[1].multiplier,
            self.tiers[2].threshold_ms, self.tiers[2].multiplier,
        )

    @classmethod
    def unpack(cls, data):
        """Unpack from 26-byte binary."""
        vals = ENCODER_CONFIG_STRUCT.unpack(data)
        return cls(
            min_value=vals[0], max_value=vals[1], step_size=vals[2],
            wrap=bool(vals[3]), reverse=bool(vals[4]),
            tiers=[
                TierConfig(vals[5], vals[6]),
                TierConfig(vals[7], vals[8]),
                TierConfig(vals[9], vals[10]),
            ],
        )


class DeviceConfig:
    """Full device configuration (4 encoders + global flags)."""

    def __init__(self, steps_per_detent_mode=0, encoders=None):
        self.version = CONFIG_VERSION
        self.steps_per_detent_mode = steps_per_detent_mode  # 0=4 steps, 1=2 steps
        if encoders is None:
            self.encoders = [factory_default_encoder() for _ in range(NUM_ENCODERS)]
        else:
            self.encoders = encoders

    def pack(self):
        """Pack into 106-byte binary."""
        data = struct.pack("BB", self.version, self.steps_per_detent_mode)
        for enc in self.encoders:
            data += enc.pack()
        assert len(data) == FULL_CONFIG_SIZE
        return data

    @classmethod
    def unpack(cls, data):
        """Unpack from 106-byte binary."""
        if len(data) != FULL_CONFIG_SIZE:
            return None
        version, flags = struct.unpack("BB", data[0:2])
        if version != CONFIG_VERSION:
            return None
        encoders = []
        for i in range(NUM_ENCODERS):
            offset = 2 + i * ENCODER_CONFIG_STRUCT.size
            enc_data = data[offset:offset + ENCODER_CONFIG_STRUCT.size]
            encoders.append(EncoderConfig.unpack(enc_data))
        return cls(steps_per_detent_mode=flags & 0x01, encoders=encoders)


def factory_default_encoder():
    """Return factory default encoder config."""
    return EncoderConfig(
        min_value=0, max_value=100, step_size=1,
        wrap=False, reverse=False,
        tiers=[
            TierConfig(150, 5),
            TierConfig(80, 15),
            TierConfig(40, 50),
        ],
    )


def factory_default_config():
    """Return full factory default config."""
    return DeviceConfig()


def validate_encoder_config(enc):
    """
    Validate a single encoder config. Returns True if valid.
    """
    if enc.min_value >= enc.max_value:
        return False
    if enc.step_size <= 0:
        return False

    # Collect enabled tiers
    enabled = [(i, enc.tiers[i]) for i in range(NUM_TIERS)
               if enc.tiers[i].threshold_ms > 0]

    # Check enabled tier thresholds are strictly descending
    # and multipliers are strictly ascending
    for j in range(len(enabled) - 1):
        idx_a, tier_a = enabled[j]
        idx_b, tier_b = enabled[j + 1]
        if tier_a.threshold_ms <= tier_b.threshold_ms:
            return False
        if tier_a.multiplier >= tier_b.multiplier:
            return False

    return True


def validate_config(config):
    """Validate a full DeviceConfig. Returns True if valid."""
    if config is None:
        return False
    if config.version != CONFIG_VERSION:
        return False
    if len(config.encoders) != NUM_ENCODERS:
        return False
    return all(validate_encoder_config(enc) for enc in config.encoders)


def clamp_position(position, min_value, max_value, wrap):
    """Clamp or wrap a position to [min_value, max_value]."""
    if not wrap:
        return max(min_value, min(max_value, position))

    range_size = max_value - min_value + 1  # computed as int64 conceptually
    if position > max_value:
        position = min_value + ((position - min_value) % range_size)
    elif position < min_value:
        position = max_value - ((min_value - 1 - position) % range_size)
    return position


def select_acceleration_tier(interval_ms, tiers):
    """
    Select acceleration tier based on time between detents.
    Returns (tier_index, multiplier) where tier_index 0 = normal speed.
    """
    # Check from fastest (tier 3) to slowest (tier 1)
    for i in range(NUM_TIERS - 1, -1, -1):
        if tiers[i].threshold_ms > 0 and interval_ms < tiers[i].threshold_ms:
            return (i + 1, tiers[i].multiplier)
    return (0, 1)


def compute_effective_step(step_size, multiplier):
    """Compute effective step with overflow protection."""
    result = step_size * multiplier  # Python handles big ints natively
    # Clamp to int32 range
    return max(-2147483648, min(2147483647, result))
