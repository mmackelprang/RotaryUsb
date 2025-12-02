# RotaryUsb

# Project Plan: USB Rotary Encoder Interface Using Raspberry Pi Pico

## Overview

This project interfaces four rotary encoders, each with a push‑button shaft, to a Windows PC using a Raspberry Pi Pico. The Pico reads the rotary and button inputs and emulates a USB HID device (keyboard/media control) so Windows sees it as a standard input device[web:37][web:40][web:50].

---

## Hardware Components

- Raspberry Pi Pico (or Pico W)[web:37].
- 4× rotary encoders with push‑button shafts (e.g., KY‑040 or similar 5‑pin modules)[web:37][web:55].
- Breadboard and jumper wires.
- Optional: 10 kΩ resistors (external pull‑ups if desired, often not needed when using internal pull‑ups)[web:43][web:49].
- USB micro‑B cable to connect Pico to PC.

Notes:

- Typical “5 V” KY‑040 modules are safe to use at 3.3 V with the Pico; they are just mechanical switches plus resistors[web:49][web:54][web:55].
- You can omit the encoder “+” pin entirely if you rely on the Pico’s internal pull‑ups and treat the encoder as just switches to ground[web:49][web:53].

---

## Encoder Pinout And Pico Wiring

### Typical encoder pins

For a common 5‑pin rotary encoder module (KY‑040‑style)[web:37][web:54][web:55]:

- A (CLK) – Quadrature signal 1.
- B (DT) – Quadrature signal 2.
- SW – Push‑button output.
- + – Optional VCC (often labeled 5V but workable at 3.3 V)[web:49][web:54][web:55].
- GND – Ground.

Internally, A/B/SW are just switches that connect to ground when activated; the microcontroller provides pull‑ups[web:49][web:53][web:56].

### GPIO assignment

Use 4 encoders × (A, B, SW) = 12 GPIO pins. All encoders share 3V3 and GND rails[web:37][web:54][web:55].

| Encoder | A (CLK) | B (DT) | SW (Button) | VCC (+)      | GND       |
|---------|---------|--------|-------------|--------------|-----------|
| 1       | GP2     | GP3    | GP4         | Pico 3V3 or NC | Pico GND |
| 2       | GP5     | GP6    | GP7         | Pico 3V3 or NC | Pico GND |
| 3       | GP8     | GP9    | GP10        | Pico 3V3 or NC | Pico GND |
| 4       | GP11    | GP12   | GP13        | Pico 3V3 or NC | Pico GND |

Wiring rules:

- Connect all encoder GND pins to any Pico GND pins (tie grounds together on the breadboard)[web:37][web:54][web:55].
- Either:
  - Connect all “+” pins to Pico 3V3, **or**
  - Leave “+” unconnected and enable internal pull‑ups on A/B/SW lines in software (recommended and commonly used)[web:43][web:49].
- Each A/B/SW pin goes directly to its assigned Pico GPIO, no level shifting needed because the encoder only pulls to ground[web:49][web:53][web:56].

Debounce and direction:

- Use software debouncing for the SW button.
- Use a proper quadrature decoding routine or library for A/B (either interrupt‑based or fast polling) to avoid miscounts[web:37][web:53][web:55].

---

## Development Environment And Firmware Stack

### Choice of firmware

The plan assumes CircuitPython on the Pico because:

- It has built‑in `usb_hid` support and an easy HID API via Adafruit HID library[web:40][web:44][web:50].
- There is a reference project implementing **4 encoders + buttons as a USB keyboard** using a Pico (`usb_keyboard_button_box_pico`)[web:37][web:51].

You can later port to C/C++ with the Pico SDK if you want lower‑level control.

### Setup steps

1. **Install CircuitPython on Pico**  
   - Download the latest Raspberry Pi Pico `.uf2` for CircuitPython from Adafruit.  
   - Hold BOOTSEL, plug Pico into USB, copy the `.uf2` to the exposed drive; the board will reboot into CircuitPython and appear as `CIRCUITPY`[web:40][web:41][web:44].

2. **Prepare development tools**  
   - Install VS Code or Thonny and point it at the `CIRCUITPY` drive.  
   - Optionally install the CircuitPython extension in VS Code for easy upload and REPL access[web:41][web:44].

3. **Install required CircuitPython libraries**  
   - From the Adafruit CircuitPython bundle, copy at least:  
     - `adafruit_hid` (for keyboard/media HID)[web:40][web:44][web:50].  
     - `rotaryio` if your CircuitPython build supports it for direct encoder reading, or implement your own A/B logic as in existing Pico rotary examples[web:40][web:41][web:52][web:53].  

4. **Clone or reference example project**  
   - Look at `usb_keyboard_button_box_pico` for a working 4‑encoder + button HID keyboard implementation and wiring style on Pico[web:37][web:42][web:51].

---

## Firmware Design

### Functional goals

- Enumerate as a USB HID keyboard (or composite HID including consumer control for media keys)[web:37][web:40][web:44][web:50].
- For each encoder:
  - Clockwise step → send configurable key (e.g., `F1`/`F3`/`F5`/`F7` or media volume up)[web:37][web:40].
  - Counter‑clockwise step → send another key (e.g., `F2`/`F4`/`F6`/`F8` or media volume down)[web:37][web:40].
  - Button press → send a third key (e.g., `F9–F12` or play/pause/mute)[web:37][web:40].
- Debounce and rate‑limit events so holding or fast spins do predictable things (e.g., repeat every N detents).

### High‑level structure

1. **Board and HID initialization**
   - Import `board`, `digitalio`, optionally `rotaryio`, and `usb_hid` / `adafruit_hid.keyboard.Keyboard` plus keycodes[web:40][web:44][web:50].
   - Initialize a `Keyboard` or `ConsumerControl` instance bound to `usb_hid.devices`[web:40][web:44][web:50].
   - Configure GPIO for all A/B/SW pins as inputs with pull‑ups enabled[web:43][web:54][web:55].

2. **Encoder abstraction**
   - Create an `Encoder` class holding:
     - Pins A/B (and optionally an internal count / last_state variable).
     - Button pin with debounce tracking.
     - Keycodes for CW, CCW, and button press actions.
   - Implement `update()` that:
     - Checks the current A/B state vs last state to detect a detent and direction.
     - On CW: send HID event (e.g., `keyboard.send(Keycode.F1)`).
     - On CCW: send HID event (e.g., `Keycode.F2`)[web:37][web:40][web:50].
     - Debounces and edge‑detects button press and sends its keycode on press.

3. **Main loop**
   - Run a tight loop calling `update()` for each encoder.
   - Insert a small delay (e.g., 1–2 ms) to balance CPU use and responsiveness.
   - Optionally print debug output via `print()` for each event and monitor via REPL/serial console.

4. **Config and mapping**
   - Store key mappings in a `dict` or a simple config section at top of the file.
   - Example default mapping (similar to the reference button box)[web:37]:
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
     - Verify correct direction and detent counts[web:52][web:53][web:55].

2. **Add USB HID keyboard behavior**
   - Add `usb_hid` + `adafruit_hid.keyboard` to your script[web:40][web:44][web:50].
   - Map CW to one key (e.g., Right Arrow) and CCW to another (Left Arrow) to test in a text editor.
   - Map the button press to Space or Enter to verify on Windows[web:40].

3. **Scale to 4 encoders**
   - Duplicate encoder abstraction for four instances with the full GPIO map (GP2–GP13)[web:37][web:54][web:55].
   - Ensure the update loop handles all four encoders every iteration.
   - Confirm no cross‑talk or missed steps by spinning multiple encoders simultaneously.

4. **Debounce tuning and robustness**
   - Implement:
     - Button debounce (e.g., ignore changes within 10–20 ms).
     - Optional detent filtering for encoder using state machine or threshold on change rate[web:37][web:41][web:53].
   - Test with fast spins and rapid clicking.

5. **Windows testing and integration**
   - Plug the Pico directly into a Windows PC USB port.
   - Verify:
     - Device shows as “USB Keyboard” or your custom HID name in Device Manager[web:37][web:40][web:44].
     - Keys appear correctly in a key tester or text editor.
   - Map the keys in your target application (DAW, CAD, IDE hotkeys, etc.).

6. **Optional enhancements**
   - Make key mappings configurable via a small config file on the `CIRCUITPY` drive.
   - Add a mode switch button to swap profiles (e.g., IDE profile vs DAW profile).
   - Add RGB LEDs or a small display to show current mode[web:41].

---

## Risks And Mitigations

- **Noisy or bouncy encoders**  
  - Use proper software debounce and a quadrature state machine; consider using `rotaryio.Encoder` if supported in your CircuitPython version[web:40][web:41][web:53][web:55].

- **Wrong wiring (5 V risk)**  
  - Ensure encoder “+” is never tied to 5 V (VBUS) when wired to Pico GPIO; use 3.3 V or omit “+” entirely and use pull‑ups[web:49][web:54][web:55].

- **HID not appearing**  
  - Ensure CircuitPython build has HID enabled and `usb_hid` is imported and not disabled in `boot.py`[web:40][web:44][web:50].

---



