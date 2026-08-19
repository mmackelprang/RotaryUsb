# RotaryUsb

USB Rotary Encoder Interface Using Raspberry Pi Pico

## Quick Start

This repository contains:

- **[firmware/](firmware/)** - CircuitPython firmware (easy to customize, no build required)
- **[firmware-cpp/](firmware-cpp/)** - C++ firmware (high-performance, requires Pico SDK)
- **[windows-example/](windows-example/)** - C# example for reading encoder data on Windows

> **Integrating RotaryUsb into your own application?** Start with
> **[docs/INTEGRATION.md](docs/INTEGRATION.md)** — the complete host-side guide: wire
> protocol, discovery, a drop-in C# reference class, mapping recipes, and gotchas. It is
> written to be sufficient without reading firmware source.

### Operating Modes

RotaryUsb supports two HID modes:

| Mode | Description | Best For |
|------|-------------|----------|
| **Keyboard HID** | Sends F1-F12 key events | Quick setup, works with any app |
| **Generic HID** | Sends raw encoder data | Custom applications, precise control |

#### Keyboard HID Mode
- Device appears as a standard USB keyboard
- Encoder events trigger F1-F12 key presses  
- Works immediately with any application that accepts keyboard input
- Keys are sent globally (all applications receive them)

#### Generic HID Mode (Default for the C++ firmware)
- Device uses vendor-defined HID (Usage Page 0xFF00)
- Applications read raw encoder position and button states directly
- Events are exclusive to applications that open the device
- Better for precise control and custom integrations
- **Runtime configurable:** Encoder bounds, step size, acceleration, and wrapping can be configured at runtime from the Windows app and saved to device flash

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

**Selecting the firmware mode (C++):**

> **⚠️ The default changed.** A bare `cmake ..` used to build **Keyboard HID**. It now builds
> **Generic HID**. If you were relying on the old default, pass `-DFIRMWARE_MODE=keyboard`.

```bash
cmake -DFIRMWARE_MODE=generic_hid ..   # default: vendor HID, runtime config, diagnostics
cmake -DFIRMWARE_MODE=keyboard ..      # F1-F12 keyboard HID
```

The old "backup `main.cpp` and replace it with `main_generic_hid.cpp`" step is gone — never copy
files to switch modes. CMake caches `FIRMWARE_MODE`, so re-run `cmake` with the flag to switch;
plain `make` keeps the cached mode. The configure step prints which mode it selected.

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
- 4× rotary encoders with push‑button shafts — either type works:
  - **Bare encoders** (e.g., [Cylewet CYT1100](https://www.amazon.com/Cylewet-Encoder-Digital-Potentiometer-Arduino/dp/B07DM2YMT4) — 3+2 pin, no PCB)
  - **Module encoders** — 5‑pin PCB with onboard pull‑ups:
    - [Cylewet KY‑040 CYT1062 (5‑pack)](https://www.amazon.com/Cylewet-Encoder-15%C3%9716-5-Arduino-CYT1062/dp/B06XQTHDRR)
    - [WMYCONGCONG KY‑040 (8‑pack with knob caps)](https://www.amazon.com/gp/product/B07B68H6R8)
- Breadboard and jumper wires.
- USB micro‑B cable to connect Pico to PC.

Notes:

- **No external pull‑up resistors are required** with either encoder type. The firmware enables the Pico’s internal pull‑ups (~50–80 kΩ) on all encoder GPIO pins.
- Typical “5 V” KY‑040 modules are safe to use at 3.3 V with the Pico; they are just mechanical switches plus resistors.
- ⚠️ **KY‑040 modules must have their “+” pin wired to Pico 3V3 (pin 36).** The module’s three onboard 10 kΩ pull-ups all tie to that pin. Leaving it floating shorts CLK, DT and SW together through a floating node, producing missed detents and phantom button presses — see [Why the KY-040 Plus Pin Must Be Connected](#why-the-ky-040-plus-pin-must-be-connected).
- Bare encoders have no onboard resistors and no “+” pin — the Pico’s internal pull-ups are all they need.

---

## Schematic and Wiring Diagram

### System Block Diagram

```mermaid
block-beta
    columns 3

    block:host:3
        columns 1
        USB_HOST["USB HOST (PC)"]
        usb_signals["USB 5V / D+ / D-"]
    end

    space:3

    block:pico:3
        columns 1
        pico_title["RASPBERRY PI PICO"]

        block:power:1
            columns 1
            power_title["POWER SECTION"]
            vbus_in["USB VBUS 5V"]
            reg["Internal 3.3V Regulator → 3V3 Pin"]
            vbus_out["VBUS Pin (5V out — use with caution)"]
        end

        block:gpio:1
            columns 1
            gpio_title["GPIO SECTION — 3.3V LOGIC\nInternal Pull-ups ENABLED (~50–80kΩ to 3.3V)"]
            enc1["GP2 ← Enc1 CLK · GP3 ← Enc1 DT · GP4 ← Enc1 SW"]
            enc2["GP5 ← Enc2 CLK · GP6 ← Enc2 DT · GP7 ← Enc2 SW"]
            enc3["GP8 ← Enc3 CLK · GP9 ← Enc3 DT · GP10 ← Enc3 SW"]
            enc4["GP11 ← Enc4 CLK · GP12 ← Enc4 DT · GP13 ← Enc4 SW"]
        end

        gnd["GND ↔ Common Ground"]
    end

    space:3

    block:encoders:3
        columns 4
        e1["Encoder 1\nCLK DT SW\n+ GND"]
        e2["Encoder 2\nCLK DT SW\n+ GND"]
        e3["Encoder 3\nCLK DT SW\n+ GND"]
        e4["Encoder 4\nCLK DT SW\n+ GND"]
    end

    usb_signals --> pico_title
    gnd --> e1
    gnd --> e2
    gnd --> e3
    gnd --> e4
```

### Wiring Diagram — CYT1100 → Pico (pin-accurate)

> **CYT1100 (bare EC11):** the 3-pin side is **A — C — B** (the **center pin C is the
> common**); the 2-pin side is an **independent push switch (S1/S2)**. Each encoder
> therefore needs **two ground wires** — its center **C** *and* one switch terminal
> **S2** — both to the GND rail. A and B may be swapped (that only flips CW/CCW, and
> is fixable with the per-encoder *Reverse* option). No resistors or "+/VCC" wire are
> needed; the Pico's internal pull-ups hold each input HIGH.

```mermaid
flowchart LR
    %% CYT1100: A C B on the 3-pin rotary side; S1 S2 = independent push switch
    subgraph E1["Encoder 1 (CYT1100)"]
        direction LR
        E1A["A (CLK)"]
        E1B["B (DT)"]
        E1C["C center = common"]
        E1S1["SW S1"]
        E1S2["SW S2"]
    end
    subgraph E2["Encoder 2 (CYT1100)"]
        direction LR
        E2A["A (CLK)"]
        E2B["B (DT)"]
        E2C["C center = common"]
        E2S1["SW S1"]
        E2S2["SW S2"]
    end
    subgraph E3["Encoder 3 (CYT1100)"]
        direction LR
        E3A["A (CLK)"]
        E3B["B (DT)"]
        E3C["C center = common"]
        E3S1["SW S1"]
        E3S2["SW S2"]
    end
    subgraph E4["Encoder 4 (CYT1100)"]
        direction LR
        E4A["A (CLK)"]
        E4B["B (DT)"]
        E4C["C center = common"]
        E4S1["SW S1"]
        E4S2["SW S2"]
    end

    subgraph PICO["Raspberry Pi Pico (left header)"]
        direction TB
        GP2["GP2 - pin 4"]
        GP3["GP3 - pin 5"]
        GP4["GP4 - pin 6"]
        GP5["GP5 - pin 7"]
        GP6["GP6 - pin 9"]
        GP7["GP7 - pin 10"]
        GP8["GP8 - pin 11"]
        GP9["GP9 - pin 12"]
        GP10["GP10 - pin 14"]
        GP11["GP11 - pin 15"]
        GP12["GP12 - pin 16"]
        GP13["GP13 - pin 17"]
        GND{{"GND rail - pins 3 / 8 / 13 / 18"}}
    end

    E1A --- GP2
    E1B --- GP3
    E1S1 --- GP4
    E2A --- GP5
    E2B --- GP6
    E2S1 --- GP7
    E3A --- GP8
    E3B --- GP9
    E3S1 --- GP10
    E4A --- GP11
    E4B --- GP12
    E4S1 --- GP13

    E1C --- GND
    E1S2 --- GND
    E2C --- GND
    E2S2 --- GND
    E3C --- GND
    E3S2 --- GND
    E4C --- GND
    E4S2 --- GND

    classDef gndclass fill:#222,stroke:#000,color:#fff;
    class GND gndclass;
```

### Wiring Diagram — KY‑040 Module → Pico (pin-accurate)

> **KY‑040 (5-pin module):** pins are labelled **CLK — DT — SW — + — GND**. The encoder's common
> and one push-button terminal are already tied to GND on the PCB, so each module needs only **one
> ground wire**. The **`+` pin must go to Pico 3V3 (pin 36)** — the board's three 10 kΩ pull-ups all
> reference it, and leaving it floating couples CLK/DT/SW together. CLK and DT may be swapped (that
> only flips CW/CCW, and is fixable with the per-encoder *Reverse* option).
> ⚠️ Never wire `+` to VBUS (pin 40).

```mermaid
flowchart LR
    subgraph E1["Encoder 1 (KY-040)"]
        direction LR
        E1CLK["CLK"]
        E1DT["DT"]
        E1SW["SW"]
        E1V["+"]
        E1G["GND"]
    end
    subgraph E2["Encoder 2 (KY-040)"]
        direction LR
        E2CLK["CLK"]
        E2DT["DT"]
        E2SW["SW"]
        E2V["+"]
        E2G["GND"]
    end
    subgraph E3["Encoder 3 (KY-040)"]
        direction LR
        E3CLK["CLK"]
        E3DT["DT"]
        E3SW["SW"]
        E3V["+"]
        E3G["GND"]
    end
    subgraph E4["Encoder 4 (KY-040)"]
        direction LR
        E4CLK["CLK"]
        E4DT["DT"]
        E4SW["SW"]
        E4V["+"]
        E4G["GND"]
    end

    subgraph PICO["Raspberry Pi Pico"]
        direction TB
        GP2["GP2 - pin 4"]
        GP3["GP3 - pin 5"]
        GP4["GP4 - pin 6"]
        GP5["GP5 - pin 7"]
        GP6["GP6 - pin 9"]
        GP7["GP7 - pin 10"]
        GP8["GP8 - pin 11"]
        GP9["GP9 - pin 12"]
        GP10["GP10 - pin 14"]
        GP11["GP11 - pin 15"]
        GP12["GP12 - pin 16"]
        GP13["GP13 - pin 17"]
        V33{{"3V3 rail - pin 36"}}
        GND2{{"GND rail - pins 3 / 8 / 13 / 18"}}
    end

    E1CLK --- GP2
    E1DT --- GP3
    E1SW --- GP4
    E2CLK --- GP5
    E2DT --- GP6
    E2SW --- GP7
    E3CLK --- GP8
    E3DT --- GP9
    E3SW --- GP10
    E4CLK --- GP11
    E4DT --- GP12
    E4SW --- GP13

    E1V --- V33
    E2V --- V33
    E3V --- V33
    E4V --- V33

    E1G --- GND2
    E2G --- GND2
    E3G --- GND2
    E4G --- GND2

    classDef gndclass fill:#222,stroke:#000,color:#fff;
    classDef v33class fill:#7a2d2d,stroke:#000,color:#fff;
    class GND2 gndclass;
    class V33 v33class;
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
            GND  ───┤ 8                        33 ├─── GND
  Enc2 DT   GP6  ───┤ 9                        32 ├─── GP27
  Enc2 SW   GP7  ───┤ 10                       31 ├─── GP26
  Enc3 CLK  GP8  ───┤ 11                       30 ├─── RUN
  Enc3 DT   GP9  ───┤ 12                       29 ├─── GP22
            GND  ───┤ 13                       28 ├─── GND
  Enc3 SW   GP10 ───┤ 14                       27 ├─── GP21
  Enc4 CLK  GP11 ───┤ 15                       26 ├─── GP20
  Enc4 DT   GP12 ───┤ 16                       25 ├─── GP19
  Enc4 SW   GP13 ───┤ 17                       24 ├─── GP18
            GND  ───┤ 18                       23 ├─── GND
            GP14 ───┤ 19                       22 ├─── GP17
            GP15 ───┤ 20                       21 ├─── GP16
                    └─────────────────────────────┘

               ENCODER TYPE A: KY-040 MODULE (5-pin PCB)
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
                  │     │    │   └───────────► Pico 3V3 (Pin 36) — REQUIRED*
                  │     │    │
                  │     │    └───────────────► Pico GPIO (SW pin)
                  │     │
                  │     └────────────────────► Pico GPIO (DT/B pin)
                  │
                  └──────────────────────────► Pico GPIO (CLK/A pin)

              * The KY-040's three onboard 10kΩ pull-ups all tie to "+". Leaving it
                floating shorts CLK/DT/SW together and the encoder misbehaves.
                Never connect "+" to 5V/VBUS (Pin 40) — that would damage the Pico.


               ENCODER TYPE B: BARE ENCODER (3+2 pin, no PCB)

                      ┌─────────┐
                      │  KNOB   │
                      │  SHAFT  │
                 ┌────┴─────────┴────┐
                 │   ROTARY ENCODER   │
                 └─┬──────┬──────┬───┘
                   │      │      │         3-pin side (rotary quadrature)
                   │      │      │
                   │      │      └─────────► Pico GPIO (B/DT pin)
                   │      │
                   │      └────────────────► Pico GND (center pin = common ground)
                   │
                   └───────────────────────► Pico GPIO (A/CLK pin)

                 └───┬──────┬───┘
                     │      │              2-pin side (push button)
                     │      │
                     │      └──────────────► Pico GND
                     │
                     └─────────────────────► Pico GPIO (SW pin)
```

### Breadboard Wiring Examples

#### Using KY‑040 Modules (5‑pin PCB)

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
  │ │ │ │ └────────┼─┼─┼─┼─┴────────┼─┼─┼─┼─┴────────┼─┼─┼─┼─┴──► GND Rail
  │ │ │ │          │ │ │ │          │ │ │ │          │ │ │ │
  │ │ │ └──────────┼─┼─┼─┴──────────┼─┼─┼─┴──────────┼─┼─┼─┴──► 3V3 Rail (Pico Pin 36)
  │ │ │            │ │ │            │ │ │            │ │ │
  │ │ └──GP4       │ │ └──GP7       │ │ └──GP10      │ │ └──GP13
  │ └────GP3       │ └────GP6       │ └────GP9       │ └────GP12
  └──────GP2       └──────GP5       └──────GP8       └──────GP11
```

#### Using Bare Encoders (3+2 pin, no PCB)

```
 ENCODER 1            ENCODER 2            ENCODER 3            ENCODER 4
 ┌─────────┐          ┌─────────┐          ┌─────────┐          ┌─────────┐
 │  ┌───┐  │          │  ┌───┐  │          │  ┌───┐  │          │  ┌───┐  │
 │  │ ○ │  │          │  │ ○ │  │          │  │ ○ │  │          │  │ ○ │  │
 │  └───┘  │          │  └───┘  │          │  └───┘  │          │  └───┘  │
 │A  GND  B│          │A  GND  B│          │A  GND  B│          │A  GND  B│
 └┬───┬───┬┘          └┬───┬───┬┘          └┬───┬───┬┘          └┬───┬───┬┘
  │   │   │            │   │   │            │   │   │            │   │   │
  │   │   │            │   │   │            │   │   │            │   │   │
  │   └───┼────────────┼───┴───┼────────────┼───┴───┼────────────┼───┴───┼──► GND Rail
  │       │            │       │            │       │            │       │
  │       └──GP3       │       └──GP6       │       └──GP9       │       └──GP12
  └──────────GP2       └──────────GP5       └──────────GP8       └──────────GP11

  (Push-button pins, 2-pin side of each encoder)
  SW1  SW2            SW1  SW2            SW1  SW2            SW1  SW2
  ┬─────┬              ┬─────┬              ┬─────┬              ┬─────┬
  │     │              │     │              │     │              │     │
  │     └──► GND Rail  │     └──► GND Rail  │     └──► GND Rail  │     └──► GND Rail
  │                    │                    │                    │
  └──GP4               └──GP7               └──GP10              └──GP13
```

#### Breadboard Layout (either encoder type)

```
                        BREADBOARD LAYOUT
  ┌──────────────────────────────────────────────────────────────┐
  │  ═══════════════════════════════════════════════════════════ │◄─ 3V3 Rail (+) — KY-040 "+" pins
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

  Note: For bare encoders, the center pin on the 3-pin side and one
  push-button pin must also connect to the GND rail; bare encoders have
  no "+" pin, so the 3V3 rail goes unused. For KY-040 modules, connect
  GND to the GND rail and "+" to the 3V3 rail (Pico Pin 36) — never to
  VBUS (Pin 40).
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

#### Why Level Shifting and External Pull‑Ups are NOT Required

Both KY‑040 modules and bare encoders work safely with the Pico without any external resistors because:

1. **Open-Drain/Open-Collector Operation**: All rotary encoder outputs (CLK/A, DT/B, SW) are mechanical switches that connect to GND when activated. They do not output voltage — they only pull the line LOW. This is true for both bare encoders and module encoders.

2. **Pull-up Resistor Source**: The Pico's internal pull‑ups (~50–80 kΩ to 3.3 V) define the HIGH voltage level. No external pull‑ups are needed:
   - **Bare encoders** have no onboard resistors at all — the Pico's internal pull‑ups are sufficient.
   - **KY‑040 modules** have onboard 10 kΩ pull‑ups on CLK, DT *and* SW, all tied to the `+` pin. Wire `+` to Pico 3V3 so they pull up to a real 3.3 V rail; they then sit in parallel with the internal pull‑ups, which is harmless. **Do not leave `+` floating** — see below.

3. **Safe Signal Flow (both encoder types)**:
   ```mermaid
   flowchart LR
       V3["3.3V"] -- "~50–80kΩ\n(internal pull-up)" --> GPIO["Pico GPIO"]
       GPIO --- SW["Encoder Switch"]
       SW --> GND["GND"]
   ```

   - **Switch OPEN:** GPIO reads HIGH (3.3V from internal pull-up)
   - **Switch CLOSED:** GPIO reads LOW (connected to GND through switch)

#### Why the KY-040 Plus Pin Must Be Connected

This applies to **KY‑040 modules only** — bare encoders have no `+` pin and are unaffected.

A KY‑040 carries three 10 kΩ pull‑up resistors — on CLK, DT **and** SW — and all three tie to the
`+` pin. The encoder's common terminal and one side of the push‑button are tied to GND on the PCB.
Leaving `+` unconnected does **not** take those resistors out of circuit: it leaves them joined at a
floating node, which shorts the three signal lines together through 20 kΩ.

Whenever a contact closes, that floating node is dragged toward GND and pulls the *other* two lines
down with it. The Pico's ~50–80 kΩ internal pull‑up cannot win against a 20 kΩ path to ground:

| Contact state | Other lines settle at | Pico reads (VIL ≤ 0.8 V, VIH ≥ 2.0 V) |
|---|---|---|
| One contact closed (turning) | ~1.1–1.2 V | Indeterminate — sits on the Schmitt trigger threshold |
| Two contacts closed (mid‑detent) | ~0.7–0.8 V | **LOW** — reads as a button press that never happened |

The observable symptoms are missed and doubled detents, direction jitter, and the button appearing
pressed whenever the knob is turned.

**The fix is one wire:** connect each KY‑040's `+` pin to Pico **3V3 (Pin 36)**. The onboard pull‑ups
then reference a real 3.3 V rail, the crosstalk path disappears, and the stronger combined pull‑up
(~8.6 kΩ with the internal one in parallel) gives faster edges and better noise immunity.

```mermaid
flowchart TB
    subgraph BAD["BROKEN: '+' floating - lines are coupled"]
        direction TB
        BV["'+' node (floating)"]
        BV -- "10k" --> BCLK["CLK - contact closed, 0V"]
        BV -- "10k" --> BDT["DT - dragged to ~1.1V"]
        BV -- "10k" --> BSW["SW - dragged to ~1.1V"]
        BCLK --> BGND["GND"]
    end

    subgraph GOOD["CORRECT: '+' to Pico 3V3 - lines are independent"]
        direction TB
        GV["3V3 (Pico Pin 36)"]
        GV -- "10k" --> GCLK["CLK - contact closed, 0V"]
        GV -- "10k" --> GDT["DT - stays HIGH at 3.3V"]
        GV -- "10k" --> GSW["SW - stays HIGH at 3.3V"]
        GCLK --> GGND["GND"]
    end

    classDef bad fill:#5a1d1d,stroke:#a33,color:#fff;
    classDef good fill:#1d4a24,stroke:#3a3,color:#fff;
    class BCLK,BDT,BSW bad;
    class GCLK,GDT,GSW good;
```

#### ⚠️ CRITICAL: Voltage Warnings

| DO ✓ | DON'T ✗ |
|------|---------|
| Connect KY‑040 `+` to Pico 3V3 (Pin 36) | Connect encoder `+` to 5V/VBUS (Pin 40) |
| Use Pico's internal pull-ups | **Leave a KY‑040 `+` pin floating** |
| Share common GND between all devices | Apply >3.63V to any GPIO pin |
| Keep encoder wiring on 3.3V logic only | Mix 5V and 3.3V logic, or float GND |

#### If Using Active 5V Sensors or Modules

For other components that output 5V logic signals (NOT applicable to standard rotary encoders), you would need level shifting:

```mermaid
flowchart LR
    DEV["5V Device Output"] -- "10kΩ" --> V3["3.3V"]
    DEV --> GPIO["Pico GPIO\n(now safe ≤3.3V)"]
```

Alternatively, use a dedicated level shifter IC (e.g., TXS0108E, BSS138-based).

### Power Supply Requirements

#### Power Budget Analysis

| Component | Typical Current | Max Current | Voltage |
|-----------|----------------|-------------|---------|
| Raspberry Pi Pico | 25-50mA | 100mA (active) | 5V via USB or 1.8-5.5V via VSYS |
| KY-040 Encoder (×4) | ~0.5mA each | 2mA each | 3.3V |
| Total System | ~30mA typical | ~110mA max | - |

#### Power Supply Options

**Option 1: USB Power (Recommended)**

```mermaid
flowchart LR
    PC["PC USB Port\n5V @ 500mA (USB 2.0)\nor 900mA (USB 3.0)"] --> PICO["Pico USB"] --> REG["Pico 3V3 Regulator"] --> ENC["Encoders & GPIO"]
```

**Option 2: External 5V Supply via VSYS**

```mermaid
flowchart LR
    PSU["External 5V PSU"] --> VSYS["VSYS (Pin 39)"] --> REG["Pico 3V3 Regulator"] --> ENC["Encoders"]
    PSU -. "GND" .-> GND["Pico GND"]
```

Useful for standalone/embedded applications.

**Option 3: External 3.3V Supply via 3V3 Pin**

```mermaid
flowchart LR
    PSU["External 3.3V\nRegulated PSU"] --> PIN["3V3 (Pin 36)"] --> DEV["Pico & Encoders"]
    PSU -. "GND" .-> GND["Pico GND"]
```

Bypasses internal regulator. Use regulated 3.3V only (not 3.7V).

#### Power Supply Recommendations

1. **For Development/Normal Use**: USB power is sufficient and safest
2. **For Standalone Applications**: Use a quality 5V regulated supply via VSYS
3. **Current Capacity**: Minimum 200mA recommended (provides headroom)
4. **USB Cable Quality**: Use a data-capable USB cable with adequate wire gauge (24 AWG or better for power)

---

## Encoder Pinout And Pico Wiring

### Compatible Encoder Types

This project supports two common encoder form factors. Both are mechanical switches internally and work with the Pico's internal pull‑ups — **no external pull‑up resistors are needed**.

#### Type A: KY‑040 Module (5‑pin PCB)

A rotary encoder soldered onto a small breakout board with labeled pins and onboard 10 kΩ pull‑up resistors.

Verified examples:

- [Cylewet KY‑040 CYT1062 (5‑pack)](https://www.amazon.com/Cylewet-Encoder-15%C3%9716-5-Arduino-CYT1062/dp/B06XQTHDRR)
- [WMYCONGCONG KY‑040 (8‑pack with knob caps)](https://www.amazon.com/gp/product/B07B68H6R8)

KY‑040 variants are electrically identical: the same EC11 mechanical encoder on a breakout carrying
three 10 kΩ pull‑ups. They all produce **4 quadrature steps per detent**, which is the firmware
default (`STEPS_PER_DETENT = 4`), so no firmware change is needed to use one.

```
  KY-040 MODULE (top view)
  ┌─────────────────────┐
  │      ┌───────┐      │
  │      │ENCODER│      │
  │      │ KNOB  │      │
  │      └───────┘      │
  │  [R1]  [R2]  [R3]   │  ← Onboard 10kΩ pull-up resistors
  │                      │
  │ [CLK] [DT] [SW] [+] [GND]
  └──┬─────┬────┬───┬────┬──┘
     │     │    │   │    │
     A     B   SW  VCC  GND
```

**Pins:** CLK (A), DT (B), SW (button), + (VCC → Pico 3V3, **required**), GND

#### Type B: Bare Encoder (3+2 pin, no PCB)

A standalone encoder component with no breakout board and no onboard resistors. Pins are split across two sides of the encoder body.

Example: [Cylewet CYT1100 (5‑pack with knob caps)](https://www.amazon.com/Cylewet-Encoder-Digital-Potentiometer-Arduino/dp/B07DM2YMT4)

```
  BARE ENCODER (side view)

       ┌─────────┐
       │  KNOB   │
       │  SHAFT  │
  ┌────┴─────────┴────┐
  │                    │
  │   ROTARY ENCODER   │
  │                    │
  └─┬──────┬──────┬───┘
    │      │      │        ← 3-pin side (rotary)
    A    GND(C)   B

  └───┬──────┬───┘
      │      │             ← 2-pin side (push button)
     SW1    SW2
```

**3‑pin side:** A (CLK), C (common/GND), B (DT)
**2‑pin side:** SW1, SW2 (push button — normally open, either pin can be GND)

### Typical encoder pins

For a common 5‑pin rotary encoder module (KY‑040‑style):

- A (CLK) – Quadrature signal 1.
- B (DT) – Quadrature signal 2.
- SW – Push‑button output.
- + – VCC (often labeled 5V, but wire it to the Pico’s 3.3 V). **Required on KY‑040 modules** — the onboard pull‑ups reference this pin.
- GND – Ground.

For a bare encoder (3+2 pin):

- A – Quadrature signal 1 (3‑pin side, outer pin).
- C – Common/ground (3‑pin side, center pin).
- B – Quadrature signal 2 (3‑pin side, outer pin).
- SW1/SW2 – Push‑button terminals (2‑pin side, normally open).

Internally, all encoder types are just switches that connect to ground when activated; the microcontroller provides pull‑ups.

### GPIO assignment

Use 4 encoders × (A, B, SW) = 12 GPIO pins. All encoders share GND rails.

#### Wiring: KY‑040 Module (5‑pin)

| Encoder | CLK → | DT → | SW → | + (VCC) → | GND → |
|---------|-------|------|------|-----------|-------|
| 1       | GP2   | GP3  | GP4  | Pico 3V3 (Pin 36) | Pico GND |
| 2       | GP5   | GP6  | GP7  | Pico 3V3 (Pin 36) | Pico GND |
| 3       | GP8   | GP9  | GP10 | Pico 3V3 (Pin 36) | Pico GND |
| 4       | GP11  | GP12 | GP13 | Pico 3V3 (Pin 36) | Pico GND |

#### Wiring: Bare Encoder (3+2 pin)

| Encoder | A → | B → | C (center) → | SW1 → | SW2 → |
|---------|-----|-----|--------------|-------|-------|
| 1       | GP2 | GP3 | Pico GND     | GP4   | Pico GND |
| 2       | GP5 | GP6 | Pico GND     | GP7   | Pico GND |
| 3       | GP8 | GP9 | Pico GND     | GP10  | Pico GND |
| 4       | GP11| GP12| Pico GND     | GP13  | Pico GND |

Wiring rules:

- Connect all encoder GND/common pins to any Pico GND pins (tie grounds together on the breadboard).
- For KY‑040 modules: connect the “+” pin to Pico **3V3 (Pin 36)**. This is required — the module's onboard 10 kΩ pull‑ups all reference it, and leaving it floating couples CLK/DT/SW together.
- For bare encoders: connect the center pin (C) to GND. Connect one push‑button pin to GPIO and the other to GND.
- Each signal pin (A/B/SW) goes directly to its assigned Pico GPIO — no level shifting or external pull‑up resistors needed.

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
  - For KY‑040 modules: wire encoder “+” to 3.3 V (Pin 36) only — never to 5 V (VBUS), and never leave it floating (see [Why the KY-040 Plus Pin Must Be Connected](#why-the-ky-040-plus-pin-must-be-connected)).
  - For bare encoders: ensure signal pins (A, B, SW) connect only to Pico GPIO pins, and common/ground pins connect only to Pico GND.

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



