# RotaryUsb

USB Rotary Encoder Interface Using Raspberry Pi Pico

## Quick Start

This repository contains:

- **[firmware/](firmware/)** - CircuitPython firmware (easy to customize, no build required)
- **[firmware-cpp/](firmware-cpp/)** - C++ firmware (high-performance, requires Pico SDK)
- **[windows-example/](windows-example/)** - C# example for capturing keyboard events on Windows

### Firmware Options

| Feature | CircuitPython | C++ |
|---------|---------------|-----|
| **Startup time** | ~3-5 seconds | <100ms |
| **Latency** | ~1-5ms | <100µs |
| **Customization** | Edit text file | Recompile required |
| **Best for** | Prototyping | Production |

### Installation

#### Option A: CircuitPython (Easy)

1. Install [CircuitPython](https://circuitpython.org/board/raspberry_pi_pico/) on your Pico
2. Copy [adafruit_hid library](https://circuitpython.org/libraries) to `CIRCUITPY/lib/`
3. Copy `firmware/code.py` to the `CIRCUITPY` drive

#### Option B: C++ (High-Performance)

1. Install [Pico SDK](https://github.com/raspberrypi/pico-sdk) and ARM GCC toolchain
2. Build the firmware:
   ```bash
   cd firmware-cpp
   mkdir build && cd build
   cmake ..
   make -j4
   ```
3. Hold BOOTSEL, connect Pico via USB, copy `rotary_usb.uf2` to the `RPI-RP2` drive

### Windows Example

```bash
cd windows-example
dotnet build
dotnet run
```

See the READMEs in each directory for detailed instructions.

---

# Project Plan: USB Rotary Encoder Interface Using Raspberry Pi Pico

## Overview

This project interfaces four rotary encoders, each with a push‑button shaft, to a Windows PC using a Raspberry Pi Pico. The Pico reads the rotary and button inputs and emulates a USB HID device (keyboard/media control) so Windows sees it as a standard input device.

---

## Hardware Components

- Raspberry Pi Pico (or Pico W).
- 4× rotary encoders with push‑button shafts (e.g., KY‑040 or similar 5‑pin modules).
- Breadboard and jumper wires.
- Optional: 10 kΩ resistors (external pull‑ups if desired, often not needed when using internal pull‑ups).
- USB micro‑B cable to connect Pico to PC.

Notes:

- Typical “5 V” KY‑040 modules are safe to use at 3.3 V with the Pico; they are just mechanical switches plus resistors.
- You can omit the encoder “+” pin entirely if you rely on the Pico’s internal pull‑ups and treat the encoder as just switches to ground.

---

## Encoder Pinout And Pico Wiring

### Typical encoder pins

For a common 5‑pin rotary encoder module (KY‑040‑style):

- A (CLK) – Quadrature signal 1.
- B (DT) – Quadrature signal 2.
- SW – Push‑button output.
- + – Optional VCC (often labeled 5V but workable at 3.3 V).
- GND – Ground.

Internally, A/B/SW are just switches that connect to ground when activated; the microcontroller provides pull‑ups.

### GPIO assignment

Use 4 encoders × (A, B, SW) = 12 GPIO pins. All encoders share 3V3 and GND rails.

| Encoder | A (CLK) | B (DT) | SW (Button) | VCC (+)      | GND       |
|---------|---------|--------|-------------|--------------|-----------|
| 1       | GP2     | GP3    | GP4         | Pico 3V3 or NC | Pico GND |
| 2       | GP5     | GP6    | GP7         | Pico 3V3 or NC | Pico GND |
| 3       | GP8     | GP9    | GP10        | Pico 3V3 or NC | Pico GND |
| 4       | GP11    | GP12   | GP13        | Pico 3V3 or NC | Pico GND |

Wiring rules:

- Connect all encoder GND pins to any Pico GND pins (tie grounds together on the breadboard).
- Either:
  - Connect all “+” pins to Pico 3V3, **or**
  - Leave “+” unconnected and enable internal pull‑ups on A/B/SW lines in software (recommended and commonly used).
- Each A/B/SW pin goes directly to its assigned Pico GPIO, no level shifting needed because the encoder only pulls to ground.

Debounce and direction:

- Use software debouncing for the SW button.
- Use a proper quadrature decoding routine or library for A/B (either interrupt‑based or fast polling) to avoid miscounts.

---

## Development Environment And Firmware Stack

### Choice of firmware

The plan assumes CircuitPython on the Pico because:

- It has built‑in `usb_hid` support and an easy HID API via Adafruit HID library.
- There is a reference project implementing **4 encoders + buttons as a USB keyboard** using a Pico (`usb_keyboard_button_box_pico`).

You can later port to C/C++ with the Pico SDK if you want lower‑level control.

### Setup steps

1. **Install CircuitPython on Pico**  
   - Download the latest Raspberry Pi Pico `.uf2` for CircuitPython from Adafruit.  
   - Hold BOOTSEL, plug Pico into USB, copy the `.uf2` to the exposed drive; the board will reboot into CircuitPython and appear as `CIRCUITPY`.

2. **Prepare development tools**  
   - Install VS Code or Thonny and point it at the `CIRCUITPY` drive.  
   - Optionally install the CircuitPython extension in VS Code for easy upload and REPL access.

3. **Install required CircuitPython libraries**  
   - From the Adafruit CircuitPython bundle, copy at least:  
     - `adafruit_hid` (for keyboard/media HID).  
     - `rotaryio` if your CircuitPython build supports it for direct encoder reading, or implement your own A/B logic as in existing Pico rotary examples.  

4. **Clone or reference example project**  
   - Look at `usb_keyboard_button_box_pico` for a working 4‑encoder + button HID keyboard implementation and wiring style on Pico.

---

## Firmware Design

### Functional goals

- Enumerate as a USB HID keyboard (or composite HID including consumer control for media keys).
- For each encoder:
  - Clockwise step → send configurable key (e.g., `F1`/`F3`/`F5`/`F7` or media volume up).
  - Counter‑clockwise step → send another key (e.g., `F2`/`F4`/`F6`/`F8` or media volume down).
  - Button press → send a third key (e.g., `F9–F12` or play/pause/mute).
- Debounce and rate‑limit events so holding or fast spins do predictable things (e.g., repeat every N detents).

### High‑level structure

1. **Board and HID initialization**
   - Import `board`, `digitalio`, optionally `rotaryio`, and `usb_hid` / `adafruit_hid.keyboard.Keyboard` plus keycodes.
   - Initialize a `Keyboard` or `ConsumerControl` instance bound to `usb_hid.devices`.
   - Configure GPIO for all A/B/SW pins as inputs with pull‑ups enabled.

2. **Encoder abstraction**
   - Create an `Encoder` class holding:
     - Pins A/B (and optionally an internal count / last_state variable).
     - Button pin with debounce tracking.
     - Keycodes for CW, CCW, and button press actions.
   - Implement `update()` that:
     - Checks the current A/B state vs last state to detect a detent and direction.
     - On CW: send HID event (e.g., `keyboard.send(Keycode.F1)`).
     - On CCW: send HID event (e.g., `Keycode.F2`).
     - Debounces and edge‑detects button press and sends its keycode on press.

3. **Main loop**
   - Run a tight loop calling `update()` for each encoder.
   - Insert a small delay (e.g., 1–2 ms) to balance CPU use and responsiveness.
   - Optionally print debug output via `print()` for each event and monitor via REPL/serial console.

4. **Config and mapping**
   - Store key mappings in a `dict` or a simple config section at top of the file.
   - Example default mapping (similar to the reference button box):
     - Enc0 CW/CCW → `F1` / `F2`.
     - Enc1 CW/CCW → `F3` / `F4`.
     - Enc2 CW/CCW → `F5` / `F6`.
     - Enc3 CW/CCW → `F7` / `F8`.
     - Buttons for enc0–enc3 → `F9`–`F12`.

---

## Step‑By‑Step Implementation Plan

1. **Bring‑up single encoder**
   - Wire encoder 1 only (A→GP2, B→GP3, SW→GP4, GND shared, plus 3V3 or no + pin as chosen).  
   - Write minimal CircuitPython script to:
     - Print encoder position and button pressed status to serial.
     - Verify correct direction and detent counts.

2. **Add USB HID keyboard behavior**
   - Add `usb_hid` + `adafruit_hid.keyboard` to your script.
   - Map CW to one key (e.g., Right Arrow) and CCW to another (Left Arrow) to test in a text editor.
   - Map the button press to Space or Enter to verify on Windows.

3. **Scale to 4 encoders**
   - Duplicate encoder abstraction for four instances with the full GPIO map (GP2–GP13).
   - Ensure the update loop handles all four encoders every iteration.
   - Confirm no cross‑talk or missed steps by spinning multiple encoders simultaneously.

4. **Debounce tuning and robustness**
   - Implement:
     - Button debounce (e.g., ignore changes within 10–20 ms).
     - Optional detent filtering for encoder using state machine or threshold on change rate.
   - Test with fast spins and rapid clicking.

5. **Windows testing and integration**
   - Plug the Pico directly into a Windows PC USB port.
   - Verify:
     - Device shows as “USB Keyboard” or your custom HID name in Device Manager.
     - Keys appear correctly in a key tester or text editor.
   - Map the keys in your target application (DAW, CAD, IDE hotkeys, etc.).

6. **Optional enhancements**
   - Make key mappings configurable via a small config file on the `CIRCUITPY` drive.
   - Add a mode switch button to swap profiles (e.g., IDE profile vs DAW profile).
   - Add RGB LEDs or a small display to show current mode.

---

## Risks And Mitigations

- **Noisy or bouncy encoders**  
  - Use proper software debounce and a quadrature state machine; consider using `rotaryio.Encoder` if supported in your CircuitPython version.

- **Wrong wiring (5 V risk)**  
  - Ensure encoder “+” is never tied to 5 V (VBUS) when wired to Pico GPIO; use 3.3 V or omit “+” entirely and use pull‑ups.

- **HID not appearing**  
  - Ensure CircuitPython build has HID enabled and `usb_hid` is imported and not disabled in `boot.py`.

---



