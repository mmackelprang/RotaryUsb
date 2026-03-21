# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
CircuitPython firmware for Raspberry Pi Pico - Generic HID Mode
Runtime Configuration with Absolute Position Tracking

Reads 4 rotary encoders with push buttons and sends absolute position data
via USB HID reports. Supports runtime configuration from the host via
Output Reports, with flash persistence.

PREREQUISITES:
- Copy boot.py (Generic HID version) to the CIRCUITPY drive first
- Power cycle the device before copying this file

Copy this file to the CIRCUITPY drive as code.py after installing CircuitPython.

HID REPORT FORMAT:
  Input Report ID 0x01 (21 bytes):
    Bytes 0-15: 4x int32 LE encoder positions
    Byte 16: Button states (bits 0-3)
    Byte 17: Active acceleration tiers (packed 2-bit fields)
    Bytes 18-20: Reserved (0x00)

  Input Report ID 0x02 (106 bytes): Config readback
  Output Report ID 0x02 (106 bytes): Config write
  Output Report ID 0x03 (2 bytes): Commands
"""

import time
import struct
import board
import digitalio
import usb_hid

from config import (
    DeviceConfig, EncoderConfig,
    factory_default_config, validate_config,
    clamp_position, select_acceleration_tier, compute_effective_step,
    FULL_CONFIG_SIZE,
)

# ============================================================================
# CONFIGURATION
# ============================================================================

# GPIO Pin mapping for 4 encoders
# Each encoder has: A (CLK), B (DT), SW (Button)
ENCODER_PINS = [
    {"a": board.GP2, "b": board.GP3, "sw": board.GP4},   # Encoder 1
    {"a": board.GP5, "b": board.GP6, "sw": board.GP7},   # Encoder 2
    {"a": board.GP8, "b": board.GP9, "sw": board.GP10},  # Encoder 3
    {"a": board.GP11, "b": board.GP12, "sw": board.GP13}, # Encoder 4
]

# Debounce timing (in seconds)
BUTTON_DEBOUNCE_TIME = 0.020  # 20ms debounce for buttons
LOOP_DELAY = 0.001  # 1ms loop delay
REPORT_INTERVAL = 0.010  # 10ms minimum between reports

# Config file path on CIRCUITPY filesystem
CONFIG_FILE = "/config.bin"

# Debug output to serial console
DEBUG_ENABLED = True

# Command codes (Output Report ID 0x03)
CMD_SAVE_CONFIG = 0x01
CMD_RESET_DEFAULTS = 0x02
CMD_RESET_POSITIONS = 0x03
CMD_READ_CONFIG = 0x04

# ============================================================================
# FLASH PERSISTENCE
# ============================================================================

def load_config_from_flash():
    """Load config from flash. Returns DeviceConfig or None."""
    try:
        with open(CONFIG_FILE, "rb") as f:
            data = f.read()
        if len(data) != FULL_CONFIG_SIZE:
            return None
        config = DeviceConfig.unpack(data)
        if config is not None and validate_config(config):
            return config
    except OSError:
        pass
    return None


def save_config_to_flash(config):
    """Save config to flash. Returns True on success."""
    try:
        data = config.pack()
        with open(CONFIG_FILE, "wb") as f:
            f.write(data)
        return True
    except OSError as e:
        if DEBUG_ENABLED:
            print(f"Error saving config: {e}")
        return False


# ============================================================================
# ENCODER CLASS (Generic HID version with absolute position tracking)
# ============================================================================

class Encoder:
    """
    Handles a single rotary encoder with push button for Generic HID mode.

    Uses quadrature decoding to detect rotation direction. Tracks absolute
    position with configurable bounds, acceleration, and wrapping.
    """

    # Quadrature state transition table
    TRANSITION_TABLE = {
        (0b00, 0b01): +1,
        (0b01, 0b11): +1,
        (0b11, 0b10): +1,
        (0b10, 0b00): +1,
        (0b00, 0b10): -1,
        (0b10, 0b11): -1,
        (0b11, 0b01): -1,
        (0b01, 0b00): -1,
    }

    def __init__(self, pin_a, pin_b, pin_sw, encoder_id, config, steps_per_detent):
        self.encoder_id = encoder_id
        self.config = config
        self.steps_per_detent = steps_per_detent

        # Initialize pin A
        self.pin_a = digitalio.DigitalInOut(pin_a)
        self.pin_a.direction = digitalio.Direction.INPUT
        self.pin_a.pull = digitalio.Pull.UP

        # Initialize pin B
        self.pin_b = digitalio.DigitalInOut(pin_b)
        self.pin_b.direction = digitalio.Direction.INPUT
        self.pin_b.pull = digitalio.Pull.UP

        # Initialize button pin
        self.pin_sw = digitalio.DigitalInOut(pin_sw)
        self.pin_sw.direction = digitalio.Direction.INPUT
        self.pin_sw.pull = digitalio.Pull.UP

        # Encoder state tracking
        self.last_ab_state = self._read_ab_state()
        self.steps = 0

        # Absolute position tracking
        self.position = config.min_value
        self.last_detent_time = time.monotonic()
        self.active_tier = 0

        # Button state tracking (first-edge-latch debounce)
        self.last_button_state = self.pin_sw.value
        self.button_pressed = False
        self.debounce_start = None

    def _read_ab_state(self):
        """Read current A/B state as 2-bit value."""
        a_val = 0 if self.pin_a.value else 1
        b_val = 0 if self.pin_b.value else 1
        return (a_val << 1) | b_val

    def apply_config(self, config, steps_per_detent):
        """Apply new config without resetting position."""
        self.config = config
        self.steps_per_detent = steps_per_detent

    def reset_position(self):
        """Reset position to min_value."""
        self.position = self.config.min_value
        self.active_tier = 0

    def update(self):
        """
        Update encoder state. Call frequently in the main loop.
        Returns button_pressed boolean.
        """
        cfg = self.config
        current_ab_state = self._read_ab_state()

        if current_ab_state != self.last_ab_state:
            transition = (self.last_ab_state, current_ab_state)
            direction = self.TRANSITION_TABLE.get(transition, 0)

            if direction != 0:
                if cfg.reverse:
                    direction = -direction
                self.steps += direction

                if self.steps >= self.steps_per_detent or self.steps <= -self.steps_per_detent:
                    detent_direction = 1 if self.steps > 0 else -1
                    self.steps = 0

                    # Compute acceleration
                    now = time.monotonic()
                    interval_ms = int((now - self.last_detent_time) * 1000)
                    self.last_detent_time = now

                    tier_idx, multiplier = select_acceleration_tier(
                        interval_ms, cfg.tiers)
                    self.active_tier = tier_idx

                    effective_step = compute_effective_step(
                        cfg.step_size, multiplier)

                    # Update position
                    self.position += detent_direction * effective_step
                    self.position = clamp_position(
                        self.position, cfg.min_value, cfg.max_value, cfg.wrap)

                    if DEBUG_ENABLED:
                        dir_str = "CW" if detent_direction > 0 else "CCW"
                        print(f"Enc{self.encoder_id}: {dir_str} pos={self.position} tier={tier_idx}")
            else:
                self.steps = 0

            self.last_ab_state = current_ab_state

        # Process button with first-edge-latch debounce
        current_button_state = self.pin_sw.value
        current_time = time.monotonic()

        if current_button_state != self.last_button_state:
            if self.debounce_start is None:
                self.debounce_start = current_time
            elif (current_time - self.debounce_start) >= BUTTON_DEBOUNCE_TIME:
                self.last_button_state = current_button_state
                self.debounce_start = None

                if not current_button_state and not self.button_pressed:
                    self.button_pressed = True
                elif current_button_state and self.button_pressed:
                    self.button_pressed = False
        else:
            self.debounce_start = None

        return self.button_pressed


# ============================================================================
# HID DEVICE HELPER
# ============================================================================

def find_generic_hid_device():
    """Find the Generic HID device configured by boot.py."""
    for device in usb_hid.devices:
        if device.usage_page == 0xFF00 and device.usage == 0x01:
            return device
    return None


# ============================================================================
# MAIN PROGRAM
# ============================================================================

def main():
    """Main entry point."""
    print("RotaryUsb Generic HID Firmware Starting (runtime config)...")

    # Find the Generic HID device
    hid_device = find_generic_hid_device()
    if hid_device is None:
        print("ERROR: Generic HID device not found!")
        print("Make sure boot.py is installed and the device was power cycled.")
        return

    print(f"Generic HID device found: Usage Page 0x{hid_device.usage_page:04X}")

    # Load config from flash or use factory defaults
    device_config = load_config_from_flash()
    if device_config is not None:
        print("Loaded config from flash")
    else:
        device_config = factory_default_config()
        print("Using factory default config")

    # Determine steps per detent from config
    steps_per_detent = 2 if device_config.steps_per_detent_mode else 4

    # Create encoder instances
    encoders = []
    for i, pins in enumerate(ENCODER_PINS):
        encoder = Encoder(
            pin_a=pins["a"],
            pin_b=pins["b"],
            pin_sw=pins["sw"],
            encoder_id=i + 1,
            config=device_config.encoders[i],
            steps_per_detent=steps_per_detent,
        )
        encoders.append(encoder)
        print(f"Encoder {i + 1} initialized: pos={encoder.position}")

    print("All encoders initialized. Starting main loop...")

    # State
    last_report_time = time.monotonic()
    last_report = bytearray(21)
    pending_config_readback = False
    readback_retry_time = 0  # Rate-limit readback retries

    # Main loop
    while True:
        # Update all encoders
        button_states = 0
        for i, encoder in enumerate(encoders):
            btn_pressed = encoder.update()
            if btn_pressed:
                button_states |= (1 << i)

        # Check for Output Reports from host (poll each report ID separately)
        # Report ID 0x02: Config write (106 bytes payload)
        config_report = hid_device.get_last_received_report(2)
        if config_report is not None:
            if len(config_report) >= FULL_CONFIG_SIZE:
                new_config = DeviceConfig.unpack(bytes(config_report[:FULL_CONFIG_SIZE]))
                if new_config is not None and validate_config(new_config):
                    device_config = new_config
                    steps_per_detent = 2 if device_config.steps_per_detent_mode else 4
                    for i, enc in enumerate(encoders):
                        enc.apply_config(device_config.encoders[i], steps_per_detent)
                    if DEBUG_ENABLED:
                        print("Config applied from host")
                else:
                    if DEBUG_ENABLED:
                        print("Config rejected: validation failed")

        # Report ID 0x03: Commands (2 bytes payload)
        cmd_report = hid_device.get_last_received_report(3)
        if cmd_report is not None and len(cmd_report) >= 2:
            command = cmd_report[0]
            if command == CMD_SAVE_CONFIG:
                ok = save_config_to_flash(device_config)
                if DEBUG_ENABLED:
                    print(f"Save config: {'OK' if ok else 'FAILED'}")
            elif command == CMD_RESET_DEFAULTS:
                device_config = factory_default_config()
                steps_per_detent = 2 if device_config.steps_per_detent_mode else 4
                for i, enc in enumerate(encoders):
                    enc.apply_config(device_config.encoders[i], steps_per_detent)
                    enc.reset_position()
                if DEBUG_ENABLED:
                    print("Reset to factory defaults")
            elif command == CMD_RESET_POSITIONS:
                for enc in encoders:
                    enc.reset_position()
                if DEBUG_ENABLED:
                    print("All positions reset")
            elif command == CMD_READ_CONFIG:
                pending_config_readback = True

        # Send config readback if requested (rate-limited retries)
        current_time = time.monotonic()
        if pending_config_readback and current_time >= readback_retry_time:
            try:
                config_data = device_config.pack()
                hid_device.send_report(config_data, 2)  # Report ID 2
                pending_config_readback = False
                if DEBUG_ENABLED:
                    print("Config readback sent")
            except Exception as e:
                readback_retry_time = current_time + 0.1  # Retry after 100ms
                if DEBUG_ENABLED:
                    print(f"Config readback error: {e}")

        # Send position report at regular intervals
        if (current_time - last_report_time) >= REPORT_INTERVAL:
            # Build the 21-byte Input Report
            # Pack tier info: 2 bits per encoder
            tier_byte = 0
            for i, enc in enumerate(encoders):
                tier_byte |= (enc.active_tier & 0x03) << (i * 2)

            report = struct.pack("<iiiiBBxxx",
                                 encoders[0].position,
                                 encoders[1].position,
                                 encoders[2].position,
                                 encoders[3].position,
                                 button_states,
                                 tier_byte)

            # Only send if something changed
            if report != last_report:
                try:
                    hid_device.send_report(report, 1)  # Report ID 1
                except Exception as e:
                    if DEBUG_ENABLED:
                        print(f"Error sending report: {e}")

                last_report = bytearray(report)

            last_report_time = current_time

        time.sleep(LOOP_DELAY)


# Run the main program
if __name__ == "__main__":
    main()
