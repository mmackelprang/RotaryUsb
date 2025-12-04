# Raspberry Pi Pico Rotary Encoder Firmware

This directory contains CircuitPython firmware for reading 4 rotary encoders with push buttons. Two modes are supported:

| Mode | Description | Best For |
|------|-------------|----------|
| **Keyboard HID** | Sends F1-F12 key events | Quick setup, works with any app |
| **Generic HID** | Sends raw encoder data via vendor-defined HID | Custom applications, precise control |

## Choosing a Mode

### Keyboard HID Mode (Default)
- Device appears as a standard USB keyboard
- Encoder events trigger F1-F12 key presses
- Works immediately with any application that accepts keyboard input
- Keys are sent globally (all apps receive them)

### Generic HID Mode
- Device uses vendor-defined HID (Usage Page 0xFF00)
- Applications can read raw encoder position and button states directly
- Requires custom application code to read HID reports
- Events are only received by applications that specifically open the device
- Better for precise control and custom integrations

## Hardware Setup

### Required Components

- Raspberry Pi Pico (or Pico W)
- 4× rotary encoder modules (KY-040 style, 5-pin)
- Breadboard and jumper wires
- USB micro-B cable

### Wiring Diagram

| Encoder | A (CLK) | B (DT) | SW (Button) | VCC (+)        | GND       |
|---------|---------|--------|-------------|----------------|-----------|
| 1       | GP2     | GP3    | GP4         | 3V3 or NC      | GND       |
| 2       | GP5     | GP6    | GP7         | 3V3 or NC      | GND       |
| 3       | GP8     | GP9    | GP10        | 3V3 or NC      | GND       |
| 4       | GP11    | GP12   | GP13        | 3V3 or NC      | GND       |

**Notes:**
- All encoder GND pins connect to Pico GND
- VCC (+) can be connected to 3V3 or left unconnected (firmware uses internal pull-ups)
- Do NOT connect VCC to 5V (VBUS) as this could damage GPIO pins

## Installation

### 1. Install CircuitPython on Pico

1. Download the latest CircuitPython `.uf2` file for Raspberry Pi Pico from [circuitpython.org](https://circuitpython.org/board/raspberry_pi_pico/)
2. Hold the **BOOTSEL** button on the Pico while plugging it into USB
3. The Pico will appear as a drive named `RPI-RP2`
4. Drag and drop the `.uf2` file onto the drive
5. The Pico will reboot and appear as a new drive named `CIRCUITPY`

### 2. Install Required Libraries

1. Download the [Adafruit CircuitPython Bundle](https://circuitpython.org/libraries)
2. Extract the bundle
3. From the `lib` folder, copy the `adafruit_hid` folder to `CIRCUITPY/lib/`

### 3. Install Firmware

#### Option A: Keyboard HID Mode (Default)

Copy `code.py` from this directory to the root of the `CIRCUITPY` drive.

```
CIRCUITPY/
├── lib/
│   └── adafruit_hid/
│       ├── __init__.py
│       ├── keyboard.py
│       ├── keycode.py
│       └── ...
└── code.py
```

#### Option B: Generic HID Mode

1. Copy `boot.py` from this directory to the root of the `CIRCUITPY` drive
2. **Power cycle the device** (unplug and replug USB)
3. Copy `code_generic_hid.py` as `code.py` to the `CIRCUITPY` drive

```
CIRCUITPY/
├── lib/
│   └── adafruit_hid/
│       └── ...
├── boot.py              # Required for Generic HID mode
└── code.py              # Use code_generic_hid.py renamed to code.py
```

**Important:** The `boot.py` file configures the USB device type at startup. You must power cycle the device after adding or modifying `boot.py` for changes to take effect.

To switch back to Keyboard mode, simply delete `boot.py` and use the original `code.py`.

### 4. Verify Operation

1. Open a serial terminal (e.g., PuTTY, Thonny, or `screen /dev/ttyACM0 115200`)
2. You should see startup messages and debug output for encoder events
3. Rotate an encoder or press a button - the corresponding key should be sent to Windows

## Default Key Mappings

| Encoder | Clockwise | Counter-CW | Button |
|---------|-----------|------------|--------|
| 1       | F1        | F2         | F9     |
| 2       | F3        | F4         | F10    |
| 3       | F5        | F6         | F11    |
| 4       | F7        | F8         | F12    |

## Customization

### Changing Key Mappings

Edit the `KEY_MAPPINGS` list in `code.py`:

```python
KEY_MAPPINGS = [
    {"cw_key": Keycode.F1, "ccw_key": Keycode.F2, "btn_key": Keycode.F9},
    {"cw_key": Keycode.F3, "ccw_key": Keycode.F4, "btn_key": Keycode.F10},
    # ... etc
]
```

Available keycodes can be found in the [adafruit_hid documentation](https://docs.circuitpython.org/projects/hid/en/latest/api.html#adafruit_hid.keycode.Keycode).

### Disabling Debug Output

Set `DEBUG_ENABLED = False` in `code.py` to disable serial console output.

### Adjusting Debounce Timing

Modify `BUTTON_DEBOUNCE_TIME` (default: 20ms) if buttons are too sensitive or unresponsive.

## Generic HID Mode Details

### HID Report Format

When using Generic HID mode, the device sends 8-byte reports:

| Byte | Description | Range |
|------|-------------|-------|
| 0 | Report ID | Always 0x01 |
| 1 | Encoder 1 movement | -127 to +127 (signed, positive = CW) |
| 2 | Encoder 2 movement | -127 to +127 (signed, positive = CW) |
| 3 | Encoder 3 movement | -127 to +127 (signed, positive = CW) |
| 4 | Encoder 4 movement | -127 to +127 (signed, positive = CW) |
| 5 | Button states | Bit 0-3: Buttons 1-4 (1 = pressed) |
| 6 | Reserved | 0x00 |
| 7 | Reserved | 0x00 |

### USB Identifiers

- **Vendor ID (VID):** Depends on CircuitPython (typically 0x239A for Adafruit)
- **Product ID (PID):** Depends on CircuitPython board
- **Usage Page:** 0xFF00 (Vendor Defined)
- **Usage:** 0x01

### Reading Generic HID Reports

To read the Generic HID reports from your application:

1. **Windows:** Use HidLibrary, HidSharp, or similar libraries
2. **Linux:** Use hidraw device or libhidapi
3. **Cross-platform:** Use hidapi bindings for your language

See the `windows-example/` directory for a C# example using HidLibrary.

## Troubleshooting

### Device Not Appearing as Keyboard

- Ensure CircuitPython is properly installed
- Check that `adafruit_hid` library is in `lib/` folder
- Verify `usb_hid` is not disabled in `boot.py`

### Encoder Not Responding

- Check wiring connections
- Verify GPIO pin assignments match your wiring
- Open serial console to see debug output

### Erratic Behavior / Double Events

- Increase `BUTTON_DEBOUNCE_TIME` for buttons
- Check for loose connections
- Some encoders have different detent counts - adjust the `steps` threshold (default: 4) in `update()` method if needed
