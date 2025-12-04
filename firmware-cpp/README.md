# Raspberry Pi Pico Rotary Encoder Firmware (C++)

This directory contains a high-performance C++ firmware for the Raspberry Pi Pico that reads 4 rotary encoders with push buttons. Two modes are supported:

| Mode | File | Description |
|------|------|-------------|
| **Keyboard HID** | `main.cpp` | Sends F1-F12 key events (default) |
| **Generic HID** | `main_generic_hid.cpp` | Sends raw encoder data via vendor-defined HID |

## Choosing a Mode

### Keyboard HID Mode (Default)
- Device appears as a standard USB keyboard
- Encoder events trigger F1-F12 key presses
- Works immediately with any application that accepts keyboard input
- Build using the default `main.cpp`

### Generic HID Mode
- Device uses vendor-defined HID (Usage Page 0xFF00)
- Applications can read raw encoder position and button states directly
- Requires custom application code to read HID reports
- To build: rename `main_generic_hid.cpp` to `main.cpp` (backup the original first)

## Why C++?

This C++ implementation offers several advantages over the CircuitPython version:

| Feature | CircuitPython | C++ |
|---------|---------------|-----|
| Startup time | ~3-5 seconds | <100ms |
| Polling latency | ~1-5ms | <100µs |
| Memory usage | ~100KB+ | ~20KB |
| CPU overhead | High (interpreter) | Minimal |
| USB response | Good | Excellent |

**Choose C++ when you need:**
- Lowest possible latency
- Fast startup (no boot delay)
- Minimal resource usage
- Production-ready firmware

**Choose CircuitPython when you need:**
- Easy customization without recompiling
- Rapid prototyping
- No development toolchain setup

## Hardware Setup

Same wiring as the CircuitPython version:

| Encoder | A (CLK) | B (DT) | SW (Button) | VCC (+)   | GND |
|---------|---------|--------|-------------|-----------|-----|
| 1       | GP2     | GP3    | GP4         | 3V3 or NC | GND |
| 2       | GP5     | GP6    | GP7         | 3V3 or NC | GND |
| 3       | GP8     | GP9    | GP10        | 3V3 or NC | GND |
| 4       | GP11    | GP12   | GP13        | 3V3 or NC | GND |

## Prerequisites

### 1. Install Pico SDK

**Linux (Ubuntu/Debian):**
```bash
sudo apt update
sudo apt install cmake gcc-arm-none-eabi libnewlib-arm-none-eabi build-essential

# Clone the Pico SDK
cd ~
git clone https://github.com/raspberrypi/pico-sdk.git
cd pico-sdk
git submodule update --init

# Set environment variable (add to ~/.bashrc for persistence)
export PICO_SDK_PATH=~/pico-sdk
```

**macOS:**
```bash
brew install cmake armmbed/formulae/arm-none-eabi-gcc

# Clone the Pico SDK
cd ~
git clone https://github.com/raspberrypi/pico-sdk.git
cd pico-sdk
git submodule update --init

# Set environment variable (add to ~/.zshrc for persistence)
export PICO_SDK_PATH=~/pico-sdk
```

**Windows:**

1. Download and install [CMake](https://cmake.org/download/)
2. Download and install [ARM GCC Toolchain](https://developer.arm.com/downloads/-/arm-gnu-toolchain-downloads)
3. Clone the Pico SDK:
   ```cmd
   cd %USERPROFILE%
   git clone https://github.com/raspberrypi/pico-sdk.git
   cd pico-sdk
   git submodule update --init
   ```
4. Set environment variable:
   ```cmd
   setx PICO_SDK_PATH %USERPROFILE%\pico-sdk
   ```

## Building

### 1. Create Build Directory

```bash
cd firmware-cpp
mkdir build
cd build
```

### 2. Configure with CMake

```bash
cmake ..
```

### 3. Build

```bash
make -j4
```

Or on Windows with Visual Studio:
```cmd
cmake --build . --config Release
```

After successful build, you'll find `rotary_usb.uf2` in the build directory.

## Installation

### Method 1: BOOTSEL Mode (Recommended)

1. **Disconnect** the Pico from USB
2. **Hold** the BOOTSEL button on the Pico
3. **While holding BOOTSEL**, connect the Pico to USB
4. **Release** BOOTSEL - the Pico appears as a USB drive named `RPI-RP2`
5. **Copy** `rotary_usb.uf2` to the `RPI-RP2` drive
6. The Pico will automatically reboot and start running the firmware

### Method 2: Using picotool

If you have [picotool](https://github.com/raspberrypi/picotool) installed:

```bash
# Put Pico in BOOTSEL mode, then:
picotool load rotary_usb.uf2
picotool reboot
```

### Method 3: SWD Debug Probe

For development, you can use another Pico as a debug probe:

```bash
openocd -f interface/cmsis-dap.cfg -f target/rp2040.cfg -c "program rotary_usb.elf verify reset exit"
```

## Usage

Once installed:

1. The Pico will appear as a USB keyboard device named "Rotary Encoder HID"
2. Rotate encoders to send F1-F8 keys
3. Press encoder buttons to send F9-F12 keys

### Default Key Mappings

| Encoder | Clockwise | Counter-CW | Button |
|---------|-----------|------------|--------|
| 1       | F1        | F2         | F9     |
| 2       | F3        | F4         | F10    |
| 3       | F5        | F6         | F11    |
| 4       | F7        | F8         | F12    |

### Debug Output

Debug messages are sent via UART on GP0 (TX) and GP1 (RX) at 115200 baud.

Connect a USB-to-Serial adapter to view debug output:
```bash
# Linux/macOS
screen /dev/ttyUSB0 115200

# Or use minicom, picocom, PuTTY, etc.
```

## Customization

### Changing Key Mappings

Edit the `ENCODER_CONFIGS` array in `main.cpp`:

```cpp
static const EncoderConfig ENCODER_CONFIGS[4] = {
    { 2,  3,  4, HID_KEY_F1, HID_KEY_F2, HID_KEY_F9  },  // Encoder 1
    { 5,  6,  7, HID_KEY_F3, HID_KEY_F4, HID_KEY_F10 },  // Encoder 2
    { 8,  9, 10, HID_KEY_F5, HID_KEY_F6, HID_KEY_F11 },  // Encoder 3
    {11, 12, 13, HID_KEY_F7, HID_KEY_F8, HID_KEY_F12 },  // Encoder 4
};
```

### Available HID Keycodes

Common keycodes (defined in `main.cpp`):

```cpp
// Letters (A-Z): 0x04 - 0x1D
// Numbers (1-0): 0x1E - 0x27
// F1-F12: 0x3A - 0x45
// Arrow keys: 0x4F (Right), 0x50 (Left), 0x51 (Down), 0x52 (Up)
// Media keys require Consumer Control HID (not included in basic implementation)
```

See the [USB HID Usage Tables](https://usb.org/document-library/hid-usage-tables-15) for a complete list.

### Changing GPIO Pins

Modify the pin assignments in `ENCODER_CONFIGS`:

```cpp
static const EncoderConfig ENCODER_CONFIGS[4] = {
    { PIN_A, PIN_B, PIN_SW, KEY_CW, KEY_CCW, KEY_BTN },
    // ...
};
```

### Adjusting Debounce Timing

Edit `encoder.h`:

```cpp
static constexpr uint32_t BUTTON_DEBOUNCE_US = 20000; // 20ms
```

### Changing Detent Sensitivity

In `encoder.cpp`, modify the step threshold:

```cpp
// Default: 4 steps per detent (common for most encoders)
if (steps_ >= 4) {  // Increase for less sensitive, decrease for more
    keycode = keycode_cw_;
    steps_ = 0;
    // ...
}
```

## Troubleshooting

### Build Errors

**"PICO_SDK_PATH not set"**
- Ensure you've set the environment variable correctly
- Try: `export PICO_SDK_PATH=/path/to/pico-sdk` before running cmake

**"arm-none-eabi-gcc not found"**
- Install the ARM GCC toolchain
- Ensure it's in your PATH

### Device Not Recognized

**Windows:**
- Check Device Manager for "Rotary Encoder HID" under "Keyboards"
- Try a different USB port or cable

**Linux:**
- Check `dmesg` output after connecting
- Verify with `lsusb` - look for VID:PID `cafe:4004`

### Encoder Not Working

1. Check wiring connections
2. Verify GPIO assignments match your wiring
3. Connect UART to view debug output
4. Test pins individually with a simple GPIO test program

### Double/Missing Events

- Adjust debounce timing in `encoder.h`
- Check for loose connections
- Some encoders have different detent counts - adjust step threshold

## Project Structure

```
firmware-cpp/
├── CMakeLists.txt      # Build configuration
├── main.cpp            # Main program and USB HID implementation
├── encoder.h           # Encoder class header
├── encoder.cpp         # Encoder class implementation
├── tusb_config.h       # TinyUSB configuration
└── README.md           # This file
```

## License

Apache License 2.0

## Generic HID Mode Details

### Building Generic HID Firmware

To build the Generic HID version instead of the Keyboard version:

```bash
# Backup the original main.cpp
cd firmware-cpp
cp main.cpp main_keyboard.cpp

# Use the Generic HID version
cp main_generic_hid.cpp main.cpp

# Build as normal
mkdir -p build && cd build
cmake ..
make -j4
```

### HID Report Format (Generic HID Mode)

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

### USB Identifiers (Generic HID Mode)

- **Vendor ID (VID):** 0xCAFE (development placeholder)
- **Product ID (PID):** 0x4005
- **Usage Page:** 0xFF00 (Vendor Defined)
- **Usage:** 0x01

### Reading Generic HID Reports

To read the Generic HID reports from your application:

1. **Windows:** Use HidLibrary, HidSharp, or Windows.Devices.HumanInterfaceDevice
2. **Linux:** Use hidraw device or libhidapi
3. **Cross-platform:** Use hidapi bindings for your language

See the `windows-example/` directory for a C# example using HidLibrary.
