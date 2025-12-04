# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
CircuitPython boot.py for Generic HID Mode

This boot.py configures the Raspberry Pi Pico to expose a vendor-defined
Generic HID device instead of a standard keyboard. This allows applications
to read raw encoder/button data directly via HID reports.

INSTALLATION:
1. Copy this file to the CIRCUITPY drive as boot.py
2. Copy code_generic_hid.py to the CIRCUITPY drive as code.py
3. Power cycle the device

To switch back to Keyboard mode:
- Delete boot.py from the CIRCUITPY drive
- Copy code.py (the original keyboard version) to the drive
"""

import usb_hid

# Vendor-defined HID report descriptor for RotaryUsb Generic HID device
# Usage Page: 0xFF00 (Vendor Defined)
# Usage: 0x01 (Vendor Usage 1)
# Report Size: 8 bytes
#   Byte 0: Report ID (0x01)
#   Byte 1: Encoder 1 position (signed 8-bit, relative movement)
#   Byte 2: Encoder 2 position (signed 8-bit, relative movement)
#   Byte 3: Encoder 3 position (signed 8-bit, relative movement)
#   Byte 4: Encoder 4 position (signed 8-bit, relative movement)
#   Byte 5: Button states (bit 0-3 for buttons 1-4, 1=pressed)
#   Byte 6: Reserved
#   Byte 7: Reserved

GENERIC_HID_REPORT_DESCRIPTOR = bytes([
    0x06, 0x00, 0xFF,  # Usage Page (Vendor Defined 0xFF00)
    0x09, 0x01,        # Usage (Vendor Usage 1)
    0xA1, 0x01,        # Collection (Application)
    0x85, 0x01,        #   Report ID (1)
    
    # Encoder values (4 signed bytes for relative movement)
    0x09, 0x02,        #   Usage (Vendor Usage 2 - Encoder Data)
    0x15, 0x81,        #   Logical Minimum (-127)
    0x25, 0x7F,        #   Logical Maximum (127)
    0x75, 0x08,        #   Report Size (8 bits)
    0x95, 0x04,        #   Report Count (4 encoders)
    0x81, 0x06,        #   Input (Data, Variable, Relative)
    
    # Button states (1 byte with 4 button bits + 4 padding bits)
    0x09, 0x03,        #   Usage (Vendor Usage 3 - Button Data)
    0x15, 0x00,        #   Logical Minimum (0)
    0x25, 0x01,        #   Logical Maximum (1)
    0x75, 0x01,        #   Report Size (1 bit)
    0x95, 0x04,        #   Report Count (4 buttons)
    0x81, 0x02,        #   Input (Data, Variable, Absolute)
    0x75, 0x01,        #   Report Size (1 bit)
    0x95, 0x04,        #   Report Count (4 padding bits)
    0x81, 0x03,        #   Input (Constant, Variable, Absolute) - Padding
    
    # Reserved bytes (2 bytes for future use)
    0x09, 0x04,        #   Usage (Vendor Usage 4 - Reserved)
    0x15, 0x00,        #   Logical Minimum (0)
    0x26, 0xFF, 0x00,  #   Logical Maximum (255)
    0x75, 0x08,        #   Report Size (8 bits)
    0x95, 0x02,        #   Report Count (2 reserved bytes)
    0x81, 0x02,        #   Input (Data, Variable, Absolute)
    
    0xC0               # End Collection
])

# Create the Generic HID device descriptor
GENERIC_HID_DEVICE = usb_hid.Device(
    report_descriptor=GENERIC_HID_REPORT_DESCRIPTOR,
    usage_page=0xFF00,       # Vendor Defined
    usage=0x01,              # Vendor Usage 1
    report_ids=(1,),         # Report ID 1
    in_report_lengths=(7,),  # 7 bytes per report (excluding report ID)
    out_report_lengths=(0,), # No output reports
)

# Enable only the Generic HID device (disable default keyboard/mouse)
usb_hid.enable((GENERIC_HID_DEVICE,))

print("RotaryUsb Generic HID mode enabled")
print("Report format: [ReportID][Enc1][Enc2][Enc3][Enc4][Buttons][Rsv1][Rsv2]")
