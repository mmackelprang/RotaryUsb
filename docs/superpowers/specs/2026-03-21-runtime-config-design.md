# Runtime Configuration for Generic HID Mode

## Overview

Add runtime configuration to the RotaryUsb Generic HID mode, enabling host applications to configure encoder behavior (min/max values, step size, acceleration) without recompiling firmware. Encoders shift from relative movement reporting to absolute position tracking with configurable bounds.

Scope: Generic HID mode only (CircuitPython and C++ firmware, Windows console app). Keyboard HID mode is unchanged.

## Requirements

1. Each encoder tracks an absolute position (`int32`) with configurable min/max bounds
2. Step size per detent is configurable per encoder (`int32`, supports MHz-range values)
3. Three configurable acceleration tiers per encoder: faster rotation applies larger step multipliers
4. Encoders can wrap around at bounds or clamp, configurable per encoder
5. Direction (CW/CCW) is reversible per encoder
6. Configuration is sent from host to device via HID Output Reports at runtime
7. Configuration persists to device flash and loads on boot
8. Host verifies config acceptance by reading back after writing
9. Windows console app provides an interactive config menu with built-in presets

## HID Report Protocol

All offset tables describe payload bytes **after** the Report ID byte. The Report ID is transmitted on the wire but is not included in offset counts. In CircuitPython, `send_report()` prepends the Report ID automatically; `get_last_received_report()` returns payload only. In TinyUSB (C++), the Report ID is passed as a separate parameter in callbacks.

### Why Output Reports instead of Feature Reports

HID Feature Reports (GET/SET) travel over the USB control endpoint (EP0). CircuitPython's `usb_hid` module does not expose Feature Report access to Python code — it only supports `send_report()` (Input Reports) and `get_last_received_report()` (Output Reports). To keep both firmware implementations using the same protocol, all host-to-device communication uses Output Reports and all device-to-host communication uses Input Reports. The C++ firmware (TinyUSB) supports both mechanisms, but uses Output/Input Reports for protocol consistency.

### Input Report (ID 0x01) — 21 bytes, Device to Host

Reports current encoder positions, button states, and active acceleration tiers. Sent continuously at the report interval (~10ms).

| Offset | Type | Description |
|--------|------|-------------|
| 0-3 | int32 LE | Encoder 1 absolute position |
| 4-7 | int32 LE | Encoder 2 absolute position |
| 8-11 | int32 LE | Encoder 3 absolute position |
| 12-15 | int32 LE | Encoder 4 absolute position |
| 16 | uint8 | Button states (bit 0-3 = buttons 1-4, 1 = pressed) |
| 17 | uint8 | Acceleration tier used for each encoder's most recent detent, packed: enc1 = bits 0-1, enc2 = bits 2-3, enc3 = bits 4-5, enc4 = bits 6-7. Values: 0 = normal, 1 = tier 1, 2 = tier 2, 3 = tier 3. Initialized to 0 on boot and on position reset. Value remains set until the next detent event on that encoder. |
| 18-20 | uint8[3] | Reserved (0x00) |

### Input Report (ID 0x02) — 106 bytes, Device to Host

Config readback. Sent once by the device in response to a "read config" command (Output Report ID 0x03, command 0x04). The host uses this to verify config was accepted after writing.

Layout is identical to the Output Report ID 0x02 config structure defined below.

### Output Report (ID 0x02) — 106 bytes, Host to Device

Full device configuration. Host writes to update config. Device applies immediately if validation passes; retains current config if validation fails.

#### Header (2 bytes)

| Offset | Type | Description |
|--------|------|-------------|
| 0 | uint8 | Config version (0x01) |
| 1 | uint8 | Global flags. Bit 0: steps_per_detent mode (0 = 4 steps/detent for KY-040, 1 = 2 steps/detent for bare EC11). Bits 1-7: reserved |

#### Per-Encoder Config (26 bytes each, offsets relative to encoder block start)

Four encoder config blocks at absolute offsets 2, 28, 54, 80.

| Offset | Type | Description |
|--------|------|-------------|
| 0-3 | int32 LE | min_value — minimum encoder position |
| 4-7 | int32 LE | max_value — maximum encoder position |
| 8-11 | int32 LE | step_size — base value change per detent |
| 12 | uint8 | wrap — 0 = clamp at min/max, 1 = wrap around |
| 13 | uint8 | reverse — 0 = normal direction, 1 = swap CW/CCW |
| 14-15 | uint16 LE | accel_tier1_threshold_ms — detent interval below which tier 1 activates |
| 16-17 | uint16 LE | accel_tier1_multiplier — step_size multiplier for tier 1 |
| 18-19 | uint16 LE | accel_tier2_threshold_ms |
| 20-21 | uint16 LE | accel_tier2_multiplier |
| 22-23 | uint16 LE | accel_tier3_threshold_ms |
| 24-25 | uint16 LE | accel_tier3_multiplier |

### Output Report (ID 0x03) — 2 bytes, Host to Device

Device commands.

| Offset | Type | Description |
|--------|------|-------------|
| 0 | uint8 | Command: 0x01 = save config to flash, 0x02 = reset to defaults, 0x03 = reset all positions to min_value, 0x04 = send current config as Input Report ID 0x02 |
| 1 | uint8 | Reserved (0x00) |

## Encoder Processing Pipeline

Each encoder processes detents through this pipeline:

```
GPIO read
  -> Quadrature decode (existing)
  -> Measure time since last detent
  -> Select acceleration tier based on time
  -> Compute effective_step = step_size * multiplier (as int64, then clamp to int32)
  -> Update position: position += direction * effective_step
  -> Clamp to [min_value, max_value] or wrap
  -> Store position for next Input Report
```

### Acceleration Tier Selection

On each detent:
1. Compute `interval_ms` = time since previous detent
2. Check tiers in order from fastest (tier 3) to slowest (tier 1):
   - If `interval_ms < tier3_threshold_ms` and tier 3 is enabled (threshold > 0): use tier 3 multiplier
   - Else if `interval_ms < tier2_threshold_ms` and tier 2 is enabled (threshold > 0): use tier 2 multiplier
   - Else if `interval_ms < tier1_threshold_ms` and tier 1 is enabled (threshold > 0): use tier 1 multiplier
   - Else: use multiplier of 1 (normal speed)
3. A tier is disabled when its threshold is 0. Disabled tiers are simply skipped in the check order above. This means if tier 2 is disabled but tiers 1 and 3 are enabled, the algorithm checks tier 3 first, skips tier 2, then checks tier 1.
4. Store the selected tier index (0-3) in the active tier field of the Input Report. This value persists until the next detent event on that encoder.

### Effective Step Computation

`effective_step = (int64)step_size * (int64)multiplier`

The multiplication is performed in 64-bit to avoid overflow. The result is then clamped to `INT32_MIN..INT32_MAX` before being applied to the position. This ensures that large `step_size` values (MHz-range) combined with large multipliers do not produce undefined behavior.

### Position Clamping and Wrapping

If `wrap == false`:
```
position = clamp(position, min_value, max_value)
```

If `wrap == true`:
```
range = (int64)max_value - (int64)min_value + 1
if position > max_value:
    position = min_value + ((position - min_value) % range)
if position < min_value:
    position = max_value - ((min_value - 1 - position) % range)
```

The `range` computation uses 64-bit arithmetic to avoid overflow when `max_value - min_value` spans a large portion of the int32 range.

### Initial Position

On boot (whether loading from flash or using factory defaults) and on the "reset positions" command (0x03), all encoder positions are initialized to `min_value`.

## Default Configuration

### General Purpose (factory defaults)

| Parameter | Value |
|-----------|-------|
| min_value | 0 |
| max_value | 100 |
| step_size | 1 |
| wrap | false |
| reverse | false |
| Tier 1 | threshold: 150ms, multiplier: 5 |
| Tier 2 | threshold: 80ms, multiplier: 15 |
| Tier 3 | threshold: 40ms, multiplier: 50 |

### Built-in Presets (Windows App)

**Radio Tuner (kHz):**
min=88000, max=108000, step=100, wrap=true, reverse=false.
Tiers: 150ms/10x, 80ms/100x, 40ms/1000x.

**Audio Mixer (%):**
min=0, max=100, step=1, wrap=false, reverse=false.
Tiers: 150ms/2x, 80ms/5x, 40ms/10x.

**Fine Control:**
min=0, max=10000, step=1, wrap=false, reverse=false.
Tiers: 150ms/5x, 80ms/25x, 40ms/100x.

## Config Validation (Device-Side)

The device validates incoming config before applying. If validation fails, the current config is retained unchanged. The host detects rejection by sending command 0x04 ("read config") and comparing the returned Input Report ID 0x02 against what was sent.

Rules:
- `min_value < max_value`
- `step_size > 0`
- Enabled tier thresholds must be strictly descending: among all tiers where threshold > 0, `tier1_threshold > tier2_threshold > tier3_threshold`. Validation checks only pairs where both tiers are enabled. Example: if tier 2 is disabled (threshold=0) but tiers 1 and 3 are enabled, the rule checks `tier1_threshold > tier3_threshold`.
- Enabled tier multipliers must be strictly ascending: among all tiers where threshold > 0, `tier1_multiplier < tier2_multiplier < tier3_multiplier`. Same pairing rules as thresholds.
- A tier with threshold = 0 is disabled. Its multiplier value is ignored during validation.
- Config version byte must match expected version (0x01)

## Config Persistence

### CircuitPython

`boot.py` calls `storage.remount("/", readonly=False)` to allow code to write to the filesystem. This makes the CIRCUITPY drive read-only from the host PC, which is acceptable since all config is managed via HID Output Reports.

Config is stored as `config.bin` on the CIRCUITPY filesystem — a raw 106-byte binary dump matching the Output Report ID 0x02 layout. Validity is checked by verifying the config version byte (0x01) and running the same validation rules used for incoming config.

On boot:
1. If `config.bin` exists, read it, and if it passes validation, load it
2. Otherwise, use factory defaults
3. Set all encoder positions to `min_value`

On save command (0x01):
1. Write current config to `config.bin`

### C++

Config is stored in the last flash sector (4096 bytes) of the Pico's onboard flash using `hardware_flash` API.

Layout in flash: 4-byte magic number (`0x52554342` = "RUCB"), followed by the 106-byte config, followed by a 2-byte CRC16-CCITT (polynomial 0x1021, initial value 0xFFFF, no output reflection).

On boot:
1. Read flash sector, verify magic number and CRC16-CCITT
2. If valid, load config and run validation rules
3. If either CRC or validation fails, use factory defaults
4. Set all encoder positions to `min_value`

On save command (0x01):
1. Compute CRC16-CCITT over the 106-byte config
2. Erase sector, write magic + config + CRC

Flash writes briefly disable interrupts (~2ms). At most one detent may be slightly delayed.

## File Changes

### Modified Files

| File | Changes |
|------|---------|
| `firmware/boot.py` | New HID descriptor with Input Report (positions) + Output Reports (config write, commands). Add `storage.remount` for filesystem write access. Two Input Report IDs (0x01 for positions, 0x02 for config readback). |
| `firmware/code_generic_hid.py` | New Encoder class with position tracking, acceleration, config struct. Output Report handler for config write and commands. Config readback via Input Report ID 0x02. Flash persistence via filesystem. New Input Report format (int32 positions). |
| `firmware-cpp/main_generic_hid.cpp` | Same encoder changes as CircuitPython. Updated GenericHidEncoder class. TinyUSB `tud_hid_set_report_cb` handles Output Reports for config/commands. Flash persistence via `hardware_flash`. New HID report descriptor with Output Reports. |
| `firmware-cpp/tusb_config.h` | Increase `CFG_TUD_HID_EP_BUFSIZE` from 16 to 128. This buffer sizes the interrupt IN endpoint used for Input Reports. Input Report ID 0x01 is 21 bytes, but Input Report ID 0x02 (config readback) is 106 bytes payload + 1 byte Report ID = 107 bytes on the wire, so the buffer must be at least 107 (128 for alignment). Output Reports (config write, commands) are received via EP0 SET_REPORT control transfers. Although the 107-byte config exceeds the EP0 packet size of 64 bytes, TinyUSB's control transfer state machine reassembles multi-packet payloads and delivers the complete buffer to `tud_hid_set_report_cb` in a single call. No change to `CFG_TUD_ENDPOINT0_SIZE` is needed. |
| `firmware-cpp/CMakeLists.txt` | Add `hardware_flash` to `target_link_libraries` for flash persistence API. |
| `windows-example/Program.cs` | Parse new Input Report format (int32 positions). Send Output Reports for config write and commands. Read config via Input Report ID 0x02. Interactive config menu. Built-in presets. |

### Unchanged Files

| File | Reason |
|------|--------|
| `firmware/code.py` | Keyboard HID mode — out of scope |
| `firmware-cpp/encoder.h` | Used by keyboard mode only |
| `firmware-cpp/encoder.cpp` | Used by keyboard mode only |
| `firmware-cpp/main.cpp` | Keyboard HID mode — out of scope |

## Windows Console App UX

### Main Menu

```
RotaryUsb Configuration
========================
Device connected: VID:0xCAFE PID:0x4005

Current encoder values:
  Enc1:       50  [0 - 100, step=1]
  Enc2:   94200  [88000 - 108000, step=100]
  Enc3:        0  [0 - 255, step=1]
  Enc4:       75  [0 - 100, step=1]

[M] Monitor - Live display of encoder values
[C] Configure encoder
[S] Save config to device flash
[D] Reset to defaults
[R] Reset positions
[Q] Quit
```

### Configure Encoder Submenu

```
Configure Encoder
=================
Select encoder [1-4]: 2

Encoder 2 current config:
  Min: 88000  Max: 108000  Step: 100  Wrap: Yes  Reverse: No
  Tier 1: <150ms -> 10x    Tier 2: <80ms -> 100x    Tier 3: <40ms -> 1000x

[1] Min value          [5] Reverse
[2] Max value          [6] Tier 1 (threshold, multiplier)
[3] Step size          [7] Tier 2
[4] Wrap on/off        [8] Tier 3
[A] Apply (send to device)
[P] Load preset (General / Radio Tuner / Audio Mixer / Fine Control)
[B] Back
```

### Live Monitor

```
Live Monitor (press any key to return)
=======================================
Enc1:       50  [0-100]
Enc2:   94,200  [88000-108000]  ** Tier 2
Enc3:        0  [0-255]
Enc4:       75  [0-100]         * Tier 1

Buttons: [ ] [ ] [X] [ ]
```

## Error Handling

- **Invalid config from host:** Device keeps current config. Host detects rejection by sending command 0x04 to request config readback and comparing against what was sent.
- **Corrupt flash on boot:** Detected via config version byte mismatch (CircuitPython) or magic number / CRC16-CCITT failure (C++). Falls back to factory defaults.
- **Device unplugged mid-config-write:** No harm — runtime config was applied but not persisted. Next boot uses previous saved config or defaults.
- **Flash write during rotation:** Brief interrupt disable (~2ms) may delay at most one detent. Acceptable.
- **Host sends unknown command ID:** Device ignores it.
- **Host sends wrong-size Output Report:** USB HID layer rejects it before firmware sees it.
- **Effective step overflow:** Multiplication uses 64-bit intermediate; result is clamped to int32 range before applying to position.
