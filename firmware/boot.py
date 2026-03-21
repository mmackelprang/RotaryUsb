# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
CircuitPython boot.py for Generic HID Mode — Runtime Configuration

This boot.py configures the Raspberry Pi Pico to expose a vendor-defined
Generic HID device with runtime configuration support. The device uses:

  Input Report ID 0x01 (21 bytes): Absolute encoder positions + buttons + tiers
  Input Report ID 0x02 (106 bytes): Config readback
  Output Report ID 0x02 (106 bytes): Config write
  Output Report ID 0x03 (2 bytes): Device commands

INSTALLATION:
1. Copy this file to the CIRCUITPY drive as boot.py
2. Copy code_generic_hid.py to the CIRCUITPY drive as code.py
3. Power cycle the device

NOTE: storage.remount() makes CIRCUITPY read-only from the PC so the
firmware can write config.bin to flash. To edit files on the drive again,
hold a button during boot or enter the REPL and run storage.remount("/", readonly=True).
"""

import usb_hid
import storage

# Allow firmware to write to the filesystem (for config.bin persistence)
storage.remount("/", readonly=False)

# Vendor-defined HID report descriptor for RotaryUsb Generic HID device
# Usage Page: 0xFF00 (Vendor Defined)
# Usage: 0x01 (Vendor Usage 1)
#
# Input Report ID 0x01 — 21 bytes: encoder positions + buttons + tiers
# Input Report ID 0x02 — 106 bytes: config readback
# Output Report ID 0x02 — 106 bytes: config write
# Output Report ID 0x03 — 2 bytes: device commands

GENERIC_HID_REPORT_DESCRIPTOR = bytes([
    0x06, 0x00, 0xFF,  # Usage Page (Vendor Defined 0xFF00)
    0x09, 0x01,        # Usage (Vendor Usage 1)
    0xA1, 0x01,        # Collection (Application)

    # ---- Input Report ID 0x01: Encoder Positions (21 bytes) ----
    0x85, 0x01,        #   Report ID (1)

    # 4 encoder positions as 32-bit signed values (16 raw bytes)
    # Logical min/max are nominal; actual int32 values are parsed by the host app
    # from the raw vendor-defined bytes, not by the HID driver.
    0x09, 0x02,        #   Usage (Vendor Usage 2 - Encoder Positions)
    0x15, 0x00,        #   Logical Minimum (0)
    0x26, 0xFF, 0x00,  #   Logical Maximum (255)
    0x75, 0x08,        #   Report Size (8 bits)
    0x95, 0x10,        #   Report Count (16 bytes = 4x int32)
    0x81, 0x02,        #   Input (Data, Variable, Absolute)

    # Button states (1 byte: bits 0-3 = buttons 1-4, bits 4-7 = padding)
    0x09, 0x03,        #   Usage (Vendor Usage 3 - Button Data)
    0x15, 0x00,        #   Logical Minimum (0)
    0x25, 0x01,        #   Logical Maximum (1)
    0x75, 0x01,        #   Report Size (1 bit)
    0x95, 0x04,        #   Report Count (4 buttons)
    0x81, 0x02,        #   Input (Data, Variable, Absolute)
    0x75, 0x01,        #   Report Size (1 bit)
    0x95, 0x04,        #   Report Count (4 padding bits)
    0x81, 0x03,        #   Input (Constant, Variable, Absolute) - Padding

    # Acceleration tier byte + 3 reserved bytes (4 bytes)
    0x09, 0x04,        #   Usage (Vendor Usage 4 - Tier + Reserved)
    0x15, 0x00,        #   Logical Minimum (0)
    0x26, 0xFF, 0x00,  #   Logical Maximum (255)
    0x75, 0x08,        #   Report Size (8 bits)
    0x95, 0x04,        #   Report Count (4: tier byte + 3 reserved)
    0x81, 0x02,        #   Input (Data, Variable, Absolute)

    # ---- Input Report ID 0x02: Config Readback (106 bytes) ----
    0x85, 0x02,        #   Report ID (2)
    0x09, 0x05,        #   Usage (Vendor Usage 5 - Config Readback)
    0x15, 0x00,        #   Logical Minimum (0)
    0x26, 0xFF, 0x00,  #   Logical Maximum (255)
    0x75, 0x08,        #   Report Size (8 bits)
    0x95, 0x6A,        #   Report Count (106 bytes)
    0x81, 0x02,        #   Input (Data, Variable, Absolute)

    # ---- Output Report ID 0x02: Config Write (106 bytes) ----
    0x09, 0x06,        #   Usage (Vendor Usage 6 - Config Write)
    0x15, 0x00,        #   Logical Minimum (0)
    0x26, 0xFF, 0x00,  #   Logical Maximum (255)
    0x75, 0x08,        #   Report Size (8 bits)
    0x95, 0x6A,        #   Report Count (106 bytes)
    0x91, 0x02,        #   Output (Data, Variable, Absolute)

    # ---- Output Report ID 0x03: Commands (2 bytes) ----
    0x85, 0x03,        #   Report ID (3)
    0x09, 0x07,        #   Usage (Vendor Usage 7 - Commands)
    0x15, 0x00,        #   Logical Minimum (0)
    0x26, 0xFF, 0x00,  #   Logical Maximum (255)
    0x75, 0x08,        #   Report Size (8 bits)
    0x95, 0x02,        #   Report Count (2 bytes)
    0x91, 0x02,        #   Output (Data, Variable, Absolute)

    0xC0               # End Collection
])

# Create the Generic HID device descriptor
# report_ids: all report IDs used across Input and Output reports
# in_report_lengths: indexed by report ID — size of Input Report for each ID
#   ID 1 = 21 bytes, ID 2 = 106 bytes, ID 3 = no input (0)
# out_report_lengths: indexed by report ID — size of Output Report for each ID
#   ID 1 = no output (0), ID 2 = 106 bytes, ID 3 = 2 bytes
GENERIC_HID_DEVICE = usb_hid.Device(
    report_descriptor=GENERIC_HID_REPORT_DESCRIPTOR,
    usage_page=0xFF00,                    # Vendor Defined
    usage=0x01,                           # Vendor Usage 1
    report_ids=(1, 2, 3),                 # All report IDs
    in_report_lengths=(21, 106, 0),       # Input Report sizes per ID
    out_report_lengths=(0, 106, 2),       # Output Report sizes per ID
)

# Enable only the Generic HID device (disable default keyboard/mouse)
usb_hid.enable((GENERIC_HID_DEVICE,))

print("RotaryUsb Generic HID mode enabled (runtime config)")
print("Input Reports: ID1=positions(21B), ID2=config_readback(106B)")
print("Output Reports: ID2=config_write(106B), ID3=commands(2B)")
