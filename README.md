# RotaryUsb

USB Rotary Encoder Interface Using Raspberry Pi Pico

## Quick Start

This repository contains:

- **[firmware/](firmware/)** - CircuitPython firmware (easy to customize, no build required)
- **[firmware-cpp/](firmware-cpp/)** - C++ firmware (high-performance, requires Pico SDK)
- **[windows-example/](windows-example/)** - C# example for reading encoder data on Windows

### Operating Modes

RotaryUsb supports two HID modes:

| Mode | Description | Best For |
|------|-------------|----------|
| **Keyboard HID** | Sends F1-F12 key events | Quick setup, works with any app |
| **Generic HID** | Sends raw encoder data | Custom applications, precise control |

#### Keyboard HID Mode (Default)
- Device appears as a standard USB keyboard
- Encoder events trigger F1-F12 key presses  
- Works immediately with any application that accepts keyboard input
- Keys are sent globally (all applications receive them)

#### Generic HID Mode (Advanced)
- Device uses vendor-defined HID (Usage Page 0xFF00)
- Applications read raw encoder position and button states directly
- Events are exclusive to applications that open the device
- Better for precise control and custom integrations

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

**For Generic HID Mode:**
1. Also copy `firmware/boot.py` to the `CIRCUITPY` drive
2. Power cycle the device (unplug and replug)
3. Copy `firmware/code_generic_hid.py` as `code.py`

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

**For Generic HID Mode (C++):**
1. Backup `main.cpp` and replace it with `main_generic_hid.cpp`
2. Rebuild and flash

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

## Schematic and Wiring Diagram

### System Block Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              USB HOST (PC)                                  │
│                                   │                                         │
│                              USB 5V / D+ / D-                               │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         RASPBERRY PI PICO                                   │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                        POWER SECTION                                 │    │
│  │   USB VBUS (5V) ──┬──► Internal 3.3V Regulator ──► 3V3 Pin (3.3V)   │    │
│  │                   │                                                  │    │
│  │                   └──► VBUS Pin (5V out - use with caution!)        │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                        GPIO SECTION (3.3V LOGIC)                    │    │
│  │                                                                      │    │
│  │   GP2  ◄──── Encoder 1 CLK (A)     GP11 ◄──── Encoder 4 CLK (A)    │    │
│  │   GP3  ◄──── Encoder 1 DT  (B)     GP12 ◄──── Encoder 4 DT  (B)    │    │
│  │   GP4  ◄──── Encoder 1 SW          GP13 ◄──── Encoder 4 SW         │    │
│  │   GP5  ◄──── Encoder 2 CLK (A)                                      │    │
│  │   GP6  ◄──── Encoder 2 DT  (B)     Internal Pull-ups: ENABLED      │    │
│  │   GP7  ◄──── Encoder 2 SW          (~50-80kΩ to 3.3V on each GPIO) │    │
│  │   GP8  ◄──── Encoder 3 CLK (A)                                      │    │
│  │   GP9  ◄──── Encoder 3 DT  (B)                                      │    │
│  │   GP10 ◄──── Encoder 3 SW                                           │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
│   GND ◄───────────────────────────────────────────────────────► Common Ground  │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
          ┌─────────────────────────┼─────────────────────────┐
          │                         │                         │
          ▼                         ▼                         ▼
    ┌───────────┐             ┌───────────┐             ┌───────────┐
    │ Encoder 1 │             │ Encoder 2 │             │    ...    │
    │  KY-040   │             │  KY-040   │             │ Encoder 4 │
    │           │             │           │             │           │
    │ CLK DT SW │             │ CLK DT SW │             │ CLK DT SW │
    │  +   GND  │             │  +   GND  │             │  +   GND  │
    └───────────┘             └───────────┘             └───────────┘
```

### Detailed Wiring Schematic

```
                    RASPBERRY PI PICO (Top View)
                    ┌─────────────────────────────┐
                    │         [USB PORT]          │
                    │            ┌─┐              │
                    │            └─┘              │
            GP0  ───┤ 1                        40 ├─── VBUS (5V from USB)
            GP1  ───┤ 2                        39 ├─── VSYS
            GND  ───┤ 3                        38 ├─── GND
  Enc1 CLK  GP2  ───┤ 4                        37 ├─── 3V3_EN
  Enc1 DT   GP3  ───┤ 5                        36 ├─── 3V3 (OUT) ◄── Power for encoders
  Enc1 SW   GP4  ───┤ 6                        35 ├─── ADC_VREF
  Enc2 CLK  GP5  ───┤ 7                        34 ├─── GP28
  Enc2 DT   GP6  ───┤ 8                        33 ├─── GND
  Enc2 SW   GP7  ───┤ 9                        32 ├─── GP27
  Enc3 CLK  GP8  ───┤ 10                       31 ├─── GP26
  Enc3 DT   GP9  ───┤ 11                       30 ├─── RUN
  Enc3 SW   GP10 ───┤ 12                       29 ├─── GP22
  Enc4 CLK  GP11 ───┤ 13                       28 ├─── GND
  Enc4 DT   GP12 ───┤ 14                       27 ├─── GP21
  Enc4 SW   GP13 ───┤ 15                       26 ├─── GP20
            GND  ───┤ 16                       25 ├─── GP19
            GP14 ───┤ 17                       24 ├─── GP18
            GP15 ───┤ 18                       23 ├─── GND
            GP16 ───┤ 19                       22 ├─── GP17
            GP17 ───┤ 20                       21 ├─── GP16
                    └─────────────────────────────┘

               ENCODER MODULE (KY-040 Style)
              ┌─────────────────────────────┐
              │         ┌───────┐           │
              │         │ENCODER│           │
              │         │ KNOB  │           │
              │         └───────┘           │
              │                             │
              │  [CLK] [DT] [SW] [+] [GND]  │
              └───┬─────┬────┬───┬────┬────┘
                  │     │    │   │    │
                  │     │    │   │    └──────► Pico GND (Pin 3, 8, 13, 18, 23, 28, 33, 38)
                  │     │    │   │
                  │     │    │   └───────────► Pico 3V3 (Pin 36) OR leave NC*
                  │     │    │
                  │     │    └───────────────► Pico GPIO (SW pin)
                  │     │
                  │     └────────────────────► Pico GPIO (DT/B pin)
                  │
                  └──────────────────────────► Pico GPIO (CLK/A pin)

              * NC = Not Connected (recommended when using internal pull-ups)
```

### Breadboard Wiring Example

```
 ENCODER 1        ENCODER 2        ENCODER 3        ENCODER 4
 ┌───────┐        ┌───────┐        ┌───────┐        ┌───────┐
 │ KY040 │        │ KY040 │        │ KY040 │        │ KY040 │
 │  ┌─┐  │        │  ┌─┐  │        │  ┌─┐  │        │  ┌─┐  │
 │  │○│  │        │  │○│  │        │  │○│  │        │  │○│  │
 │  └─┘  │        │  └─┘  │        │  └─┘  │        │  └─┘  │
 │C D S + G│      │C D S + G│      │C D S + G│      │C D S + G│
 └┬─┬─┬─┬─┬┘      └┬─┬─┬─┬─┬┘      └┬─┬─┬─┬─┬┘      └┬─┬─┬─┬─┬┘
  │ │ │ │ │        │ │ │ │ │        │ │ │ │ │        │ │ │ │ │
  │ │ │ │ │        │ │ │ │ │        │ │ │ │ │        │ │ │ │ │
  │ │ │ NC│        │ │ │ NC│        │ │ │ NC│        │ │ │ NC│
  │ │ │   │        │ │ │   │        │ │ │   │        │ │ │   │
  │ │ │   └────────┴─┴─┴───┴────────┴─┴─┴───┴────────┴─┴─┴───┴──► GND Rail
  │ │ │            │ │ │            │ │ │            │ │ │
  │ │ └──GP4       │ │ └──GP7       │ │ └──GP10      │ │ └──GP13
  │ └────GP3       │ └────GP6       │ └────GP9       │ └────GP12
  └──────GP2       └──────GP5       └──────GP8       └──────GP11

                        BREADBOARD LAYOUT
  ┌──────────────────────────────────────────────────────────────┐
  │  ═══════════════════════════════════════════════════════════ │◄─ Power Rail (+)
  │  ═══════════════════════════════════════════════════════════ │◄─ GND Rail (-)
  │                                                              │
  │   [ENC1]    [ENC2]    [ENC3]    [ENC4]     [PICO]           │
  │    ○ ○ ○     ○ ○ ○     ○ ○ ○     ○ ○ ○     ┌─────┐          │
  │    │ │ │     │ │ │     │ │ │     │ │ │     │ USB │          │
  │    │ │ │     │ │ │     │ │ │     │ │ │     │     │          │
  │    │ │ └─────┼─┼─┼─────┼─┼─┼─────┼─┼─┼─────┤GP4  │          │
  │    │ └───────┼─┼─┼─────┼─┼─┼─────┼─┼─┼─────┤GP3  │          │
  │    └─────────┼─┼─┼─────┼─┼─┼─────┼─┼─┼─────┤GP2  │          │
  │              │ │ └─────┼─┼─┼─────┼─┼─┼─────┤GP7  │          │
  │              │ └───────┼─┼─┼─────┼─┼─┼─────┤GP6  │          │
  │              └─────────┼─┼─┼─────┼─┼─┼─────┤GP5  │          │
  │                        │ │ └─────┼─┼─┼─────┤GP10 │          │
  │                        │ └───────┼─┼─┼─────┤GP9  │          │
  │                        └─────────┼─┼─┼─────┤GP8  │          │
  │                                  │ │ └─────┤GP13 │          │
  │                                  │ └───────┤GP12 │          │
  │                                  └─────────┤GP11 │          │
  │                                            │     │          │
  │  ═══════════════════════════════════════════════════════════ │◄─ Connect to Pico GND
  └──────────────────────────────────────────────────────────────┘
```

### Voltage Levels and Level Shifting

#### Pico GPIO Voltage Characteristics

| Parameter | Value | Notes |
|-----------|-------|-------|
| GPIO Logic Level | 3.3V | **All Pico GPIO pins are 3.3V only!** |
| GPIO Input High (VIH) | ≥2.0V | Minimum voltage to read as HIGH |
| GPIO Input Low (VIL) | ≤0.8V | Maximum voltage to read as LOW |
| GPIO Output High (VOH) | ~3.3V | Output when driving HIGH |
| GPIO Output Low (VOL) | ~0V | Output when driving LOW |
| **Absolute Maximum** | **3.63V** | **Exceeding this WILL damage the Pico!** |
| Internal Pull-up | ~50-80kΩ | Pulls GPIO to 3.3V when enabled |

#### Why Level Shifting is NOT Required for KY-040 Encoders

Despite being labeled as "5V" modules, KY-040 encoders work safely with the Pico because:

1. **Open-Drain/Open-Collector Operation**: The encoder outputs (CLK, DT, SW) are mechanical switches that connect to GND when activated. They do not output voltage - they only pull the line LOW.

2. **Pull-up Resistor Location**: When using internal pull-ups (recommended), the Pico's 3.3V internal pull-ups define the HIGH voltage level. The encoder module's onboard pull-ups (typically 10kΩ to VCC) are either:
   - Not connected (if you leave the `+` pin unconnected)
   - Connected to 3.3V (if you wire `+` to Pico 3V3)

3. **Safe Signal Flow**:
   ```
   With internal pull-ups (RECOMMENDED):
   
   Pico GPIO ◄────┬──── ~50-80kΩ ────► 3.3V (internal pull-up)
                  │
                  └──── Encoder Switch ────► GND
   
   Switch OPEN:  GPIO reads HIGH (3.3V from internal pull-up)
   Switch CLOSED: GPIO reads LOW (connected to GND through switch)
   ```

#### ⚠️ CRITICAL: Voltage Warnings

| DO ✓ | DON'T ✗ |
|------|---------|
| Connect encoder `+` to Pico 3V3 (36) | Connect encoder `+` to 5V/VBUS (40) |
| Leave encoder `+` unconnected (NC) | Apply >3.63V to any GPIO pin |
| Use Pico's internal pull-ups | Mix 5V and 3.3V logic without level shifters |
| Share common GND between all devices | Float GND connections |

#### If Using Active 5V Sensors or Modules

For other components that output 5V logic signals (NOT applicable to standard rotary encoders), you would need level shifting:

```
5V Device Output ────┬──── 10kΩ ────► 3.3V
                     │
                     └──── To Pico GPIO (now safe 3.3V max)
   
   OR use a dedicated level shifter IC (e.g., TXS0108E, BSS138-based)
```

### Power Supply Requirements

#### Power Budget Analysis

| Component | Typical Current | Max Current | Voltage |
|-----------|----------------|-------------|---------|
| Raspberry Pi Pico | 25-50mA | 100mA (active) | 5V via USB or 1.8-5.5V via VSYS |
| KY-040 Encoder (×4) | ~0.5mA each | 2mA each | 3.3V |
| Total System | ~30mA typical | ~110mA max | - |

#### Power Supply Options

**Option 1: USB Power (Recommended)**
```
PC USB Port ────► Pico USB ────► Pico 3V3 Regulator ────► Encoders & GPIO
     │
     └── Provides: 5V @ 500mA (USB 2.0) or 900mA (USB 3.0)
         More than sufficient for this project
```

**Option 2: External 5V Supply via VSYS**
```
External 5V PSU ────► VSYS (Pin 39) ────► Pico 3V3 Regulator ────► Encoders
                 │
                 └── Also connect GND to Pico GND
                     Useful for standalone/embedded applications
```

**Option 3: External 3.3V Supply via 3V3 Pin**
```
External 3.3V Regulated PSU ────► 3V3 (Pin 36) ────► Pico & Encoders
                            │
                            └── Bypass internal regulator
                                Use regulated 3.3V only (not 3.7V!)
                                Connect GND to Pico GND
```

#### Power Supply Recommendations

1. **For Development/Normal Use**: USB power is sufficient and safest
2. **For Standalone Applications**: Use a quality 5V regulated supply via VSYS
3. **Current Capacity**: Minimum 200mA recommended (provides headroom)
4. **USB Cable Quality**: Use a data-capable USB cable with adequate wire gauge (24 AWG or better for power)

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

## GitHub Copilot Agent Prompt

The following prompt can be used with GitHub Copilot to implement or extend RotaryUsb functionality:

```
Implement support for reading RotaryUsb rotary encoder input as a custom Generic HID device (not as a keyboard):

1. **Firmware:**
   - In `boot.py`, set the device to use a Vendor-Defined HID report descriptor for 4 encoders and buttons (suggest Usage Page 0xFF00, input report 4-8 bytes).
   - In `code.py`, modify main loop to send encoder/button state with send_report() calls (not Keyboard.send()).
   - Document the HID report bytes layout, and provide instructions for modifying report size if additional encoders/buttons are needed.

2. **Windows C# Example:**
   - Use the HidLibrary NuGet package to find the device by VID/PID and UsagePage and continuously read incoming HID reports.
   - Parse bytes to extract each encoder/button state.
   - Provide commented code in `windows-example/Program.cs` that supports live printing and optionally, application handling logic.
   - Document how to build and run the sample, and how the encoder/button values map to bytes.

3. **Documentation:**
   - Update all firmware and example READMEs to explain both modes: HID Keyboard mode and Generic HID mode.
   - Include detailed instructions for setting up, flashing, and coding against the Generic HID report.
   - Give troubleshooting guidance for HID access on Windows and Linux.

Include a section outlining the difference between Keyboard and Generic HID operation, and recommended use cases for each.
```

---



