# Integrating RotaryUsb Into Your Application

A complete guide to consuming the RotaryUsb device from a host application. Written for
C#/.NET on Windows, with the wire protocol documented well enough to implement in any
language.

**You should not need to read firmware source to use this device.** If you find yourself
doing so, that is a bug in this document — please file it.

---

## Contents

1. [What this device is](#1-what-this-device-is)
2. [Setup: firmware and hardware](#2-setup-firmware-and-hardware)
3. [Discovery and feature detection](#3-discovery-and-feature-detection)
4. [Wire protocol reference](#4-wire-protocol-reference)
5. [Reference implementation](#5-reference-implementation)
6. [Mapping recipes](#6-mapping-recipes)
7. [Configuring the device from the host](#7-configuring-the-device-from-the-host)
8. [Diagnostics and troubleshooting](#8-diagnostics-and-troubleshooting)
9. [Gotchas checklist](#9-gotchas-checklist)
10. [Integration checklist](#10-integration-checklist)

---

## 1. What this device is

A Raspberry Pi Pico exposing **four rotary encoders, each with a push button**, over USB
as a vendor-defined HID device. It is not a keyboard and not a mouse — it speaks a small
binary protocol on HID report IDs, so no driver, no elevation, and no key-hook is needed.

The device does real work on your behalf:

- **Quadrature decoding** with a transition table, so you get clean detents, not edges.
- **Absolute position tracking** per encoder, clamped or wrapped to a configurable range.
- **Acceleration**, in up to three configurable tiers based on how fast you turn.
- **Runtime configuration** from the host, persisted to flash.

### `position` versus `movement` — pick one per control

Every Input Report 0x01 carries both. They answer different questions.

| Your control | Use | Why |
|---|---|---|
| Volume, squelch, any bounded setting | `position` | The device owns the range; clamping at the ends is the behavior you want |
| VFO frequency, or any value whose range exceeds what the device can hold | `movement` delta | The host owns the range; the device's own range is irrelevant |
| Menu or preset selector that should cycle | `movement` delta, or `position` with `wrap = 1` | Wrap at the ends with no host-side modulo |
| Momentary action, mode toggle | `button_states` bit | — |

**The rule of thumb:** if the knob can reach a point where turning it further should still
do something, you want `movement`.

### Why `movement` exists

`position` is clamped to `[min_value, max_value]`. Hold the knob against a limit and
position stops changing — and because the device only transmits a report when its contents
change, **it stops transmitting entirely**. The knob goes silent even though the operator is
still turning it.

`movement` is a free-running signed accumulator updated *before* clamping. It keeps accruing
at a limit, which both gives you the motion signal and makes the device transmit again.

It is measured in the **same units as position** — `step_size × tier_multiplier` — so
acceleration is already applied. You add the delta to your own value and get device-identical
feel without reimplementing the tier logic.

---

## 2. Setup: firmware and hardware

### Which firmware

Two implementations of the identical wire protocol:

| | C++ (`firmware-cpp/`) | CircuitPython (`firmware/`) |
|---|---|---|
| Recommended for production | **Yes** | Prototyping |
| Poll rate | ~µs loop | ~1 ms loop |
| Missed detents when spun fast | Very unlikely | Possible |
| Flashing | Drag one `.uf2` | Copy `.py` files, power cycle |

A regression test (`tests/test_descriptor_parity.py`) asserts the two HID report descriptors
are byte-identical, so **a host written against one works unchanged against the other**.

### Flashing the C++ firmware

```bash
cd firmware-cpp && mkdir -p build && cd build
cmake ..          # generic_hid is the default
make -j4
```

Hold BOOTSEL while plugging the Pico in, then copy `rotary_usb.uf2` to the `RPI-RP2` drive.

> **Do not flash keyboard mode.** `cmake -DFIRMWARE_MODE=keyboard ..` builds a firmware that
> types F1–F12 and speaks none of this protocol. The configure step echoes which mode it built —
> check the log if unsure.

### Flashing the CircuitPython firmware

Copy `firmware/boot.py` → `boot.py`, `firmware/config.py` and `firmware/reports.py` as-is, and
`firmware/code_generic_hid.py` → `code.py` onto the `CIRCUITPY` drive, then **power cycle**
(a soft reset is not enough — `boot.py` only runs at power-on).

### Steps per detent — get this right first

Encoders differ in how many quadrature edges they emit per physical click. Set it wrong and
every click counts double, or every second click is ignored.

| Encoder | Steps/detent | `global_flags` bit 0 |
|---|---|---|
| KY-040 module | 4 | `0` (default) |
| Bare EC11 | 2 | `1` |

**Measure it rather than guessing** — the device will tell you:

1. Send command `0x05` (reset diagnostics) to zero the counters.
2. Turn one encoder exactly 10 physical clicks, in one direction, at a moderate speed.
3. Read `edge_count` for that encoder from Input Report 0x04.
4. `edge_count / 10` is your steps-per-detent: **4** or **2**.

**Check `invalid_count` first.** If it is not near zero, you have contact bounce or a marginal
connection — that inflates `edge_count` and can make a 2-step encoder read as 4. Fix the wiring
before trusting the ratio.

---

## 3. Discovery and feature detection

### Identifiers

| | C++ firmware | CircuitPython firmware |
|---|---|---|
| Vendor ID | `0xCAFE` | `0x239A` |
| Product ID | `0x4005` | `0x80F4` |
| Usage Page | `0xFF00` (vendor-defined) | `0xFF00` |
| Usage | `0x01` | `0x01` |

`0xCAFE` is a development placeholder, not a registered vendor ID. Match on usage page as well
as VID/PID so you do not bind to an unrelated device.

```csharp
using HidLibrary;

static readonly int[] KnownVids = { 0x239A, 0xCAFE };
static readonly int[] KnownPids = { 0x80F4, 0x4005 };

static HidDevice? FindDevice() =>
    HidDevices.Enumerate()
        .Where(d => KnownVids.Contains(d.Attributes.VendorId)
                 && KnownPids.Contains(d.Attributes.ProductId)
                 && d.Capabilities.UsagePage == unchecked((short)0xFF00))
        .FirstOrDefault();
```

### Feature detection

Detect the movement accumulator by **report length**, not by any version field:

| Firmware | `InputReportByteLength` | Movement available |
|---|---|---|
| Current | 37 (36 payload + report ID) | Yes |
| Pre-accumulator | 22 (21 payload + report ID) | No |

```csharp
bool hasMovement = device.Capabilities.InputReportByteLength >= 37;
```

Payload bytes 0–17 are identical in both, so position, buttons, and tiers parse the same way
against either. Only code that asserts an *exact* report length breaks.

---

## 4. Wire protocol reference

All multi-byte values are **little-endian**. All offsets below are **payload** offsets,
counted after the report ID byte.

> **HidLibrary prepends the report ID** to `report.Data`, so in that library
> `buffer index = payload offset + 1`. Other bindings differ — check yours.

### Report summary

| Report | Direction | Payload | When |
|---|---|---|---|
| Input `0x01` | Device → Host | 36 B | **Only when contents change** |
| Input `0x02` | Device → Host | 106 B | Only in response to command `0x04` |
| Input `0x04` | Device → Host | 56 B | Every 100 ms (10 Hz) |
| Output `0x02` | Host → Device | 106 B | When you write config |
| Output `0x03` | Host → Device | 2 B | When you send a command |

### Input Report `0x01` — positions, buttons, tiers, movement (36 bytes)

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0–3 | int32 | Encoder 1 position | Clamped to `[min_value, max_value]` |
| 4–7 | int32 | Encoder 2 position | |
| 8–11 | int32 | Encoder 3 position | |
| 12–15 | int32 | Encoder 4 position | |
| 16 | uint8 | Button states | Bit *n* = encoder *n+1*; 1 = pressed |
| 17 | uint8 | Active tiers | 2 bits per encoder: `(byte >> (i*2)) & 0x03` |
| 18–19 | uint8[2] | Reserved | `0x00` |
| 20–23 | int32 | Encoder 1 movement | Free-running accumulator |
| 24–27 | int32 | Encoder 2 movement | |
| 28–31 | int32 | Encoder 3 movement | |
| 32–35 | int32 | Encoder 4 movement | |

**Tier values:** `0` = no acceleration, `1`–`3` = acceleration tier.

**Movement semantics — read this carefully:**

- It is a **running total since power-on**, not a per-report delta. You must difference it.
- It **wraps** at 32 bits rather than saturating. Difference it with wrapping arithmetic
  (`unchecked` in C#) and the wrap is invisible. A saturating design would have frozen the
  control after ~119 hours of continuous fast spinning.
- It is updated **before clamping**, so it accrues at `min_value`/`max_value`.
- It already includes **acceleration** (`step_size × tier_multiplier`).
- It respects the **`reverse`** flag, so its sign always agrees with position.
- It is **not reset** by `Reset positions` (`0x03`) or `Reset defaults` (`0x02`). Only a
  power cycle zeroes it. This is deliberate: zeroing an odometer because the dial was
  re-zeroed would inject a phantom delta into every host that differences it.
- Under `wrap = 1`, position wraps while movement continues monotonically — so you can tell
  how many full turns were made.

**Reports are sent only when contents change.** An idle device is silent. Do not treat
silence as a disconnect; use it as the natural idle state.

### Input Report `0x04` — decoder diagnostics (56 bytes, 10 Hz)

| Offset | Type | Field |
|---|---|---|
| 0–3 | uint8[4] | Raw pins per encoder: `(A<<2)\|(B<<1)\|SW`, **literal levels**. Idle = `7` |
| 4 | uint8 | `steps_per_detent` the decoder is *actually* using (2 or 4) |
| 5–7 | uint8[3] | Reserved (`0x00`) |
| 8–23 | uint32[4] | `edge_count` — A/B state changes observed |
| 24–39 | uint32[4] | `invalid_count` — illegal transitions (a subset of `edge_count`) |
| 40–55 | uint32[4] | `detent_count` — detents the decoder emitted |

Counters are monotonic across both directions and are zeroed by command `0x05`.
`detent_count` increments *before* the position math, so it counts even at a limit.

Byte 4 is the value the firmware is **using**, not what you asked for — use it to confirm a
config write actually took effect.

**Raw pins are sampled at 10 Hz**, and an encoder rests at a detent with both contacts open,
so this field reads `7` at rest *even while you are spinning it*. It is for at-rest checks
(idle `7`, button pressed `6`, a stuck pin); rotation shows up in the counters.

### Input Report `0x02` / Output Report `0x02` — configuration (106 bytes)

| Offset | Type | Field |
|---|---|---|
| 0 | uint8 | Config version — currently `0x01` |
| 1 | uint8 | Global flags. Bit 0: `0` = 4 steps/detent, `1` = 2 steps/detent |
| 2–27 | — | Encoder 1 block (26 bytes) |
| 28–53 | — | Encoder 2 block |
| 54–79 | — | Encoder 3 block |
| 80–105 | — | Encoder 4 block |

Each 26-byte encoder block:

| Offset | Type | Field |
|---|---|---|
| 0–3 | int32 | `min_value` |
| 4–7 | int32 | `max_value` |
| 8–11 | int32 | `step_size` |
| 12 | uint8 | `wrap` (0/1) |
| 13 | uint8 | `reverse` (0/1) |
| 14–15 | uint16 | Tier 1 `threshold_ms` |
| 16–17 | uint16 | Tier 1 `multiplier` |
| 18–19 | uint16 | Tier 2 `threshold_ms` |
| 20–21 | uint16 | Tier 2 `multiplier` |
| 22–23 | uint16 | Tier 3 `threshold_ms` |
| 24–25 | uint16 | Tier 3 `multiplier` |

A tier with `threshold_ms = 0` is disabled. "Turn faster than `threshold_ms` between detents
and each detent moves `step_size × multiplier`."

### Output Report `0x03` — commands (2 bytes)

Byte 0 is the command; byte 1 is reserved, send `0x00`.

| Code | Command | Effect |
|---|---|---|
| `0x01` | Save config | Persist current config to flash |
| `0x02` | Reset defaults | Factory config **and** reset positions. Movement untouched |
| `0x03` | Reset positions | Every position to its `min_value`. Movement untouched |
| `0x04` | Read config | Triggers one Input Report `0x02` |
| `0x05` | Reset diagnostics | Zero all report `0x04` counters |

---

## 5. Reference implementation

Drop-in C# class. Requires the `hidlibrary` NuGet package.

```csharp
using System;
using System.Linq;
using System.Threading;
using HidLibrary;

/// <summary>
/// Host-side interface to a RotaryUsb device.
///
/// Raises EncoderMoved with a signed delta in device units (acceleration already
/// applied) and ButtonChanged on press/release. Movement deltas keep arriving when
/// the knob is held against its configured limit.
/// </summary>
public sealed class RotaryUsbDevice : IDisposable
{
    public const int NumEncoders = 4;

    private static readonly int[] KnownVids = { 0x239A, 0xCAFE };
    private static readonly int[] KnownPids = { 0x80F4, 0x4005 };

    private const byte ReportIdPositions = 0x01;
    private const byte ReportIdConfig    = 0x02;
    private const byte ReportIdCommand   = 0x03;
    private const byte ReportIdDiag      = 0x04;

    private const int PositionPayloadSize       = 36;
    private const int LegacyPositionPayloadSize = 21;

    private HidDevice? _device;
    private Thread? _reader;
    private volatile bool _running;

    private readonly int[]  _movementLast = new int[NumEncoders];
    private readonly int[]  _positions    = new int[NumEncoders];
    private bool _baselined;
    private byte _buttons;

    /// <summary>Signed movement since the previous report, in device units.</summary>
    public event Action<int, int>? EncoderMoved;      // (encoderIndex, delta)

    /// <summary>Button press (true) or release (false).</summary>
    public event Action<int, bool>? ButtonChanged;    // (encoderIndex, isPressed)

    /// <summary>True if the firmware sends the 36-byte report with movement.</summary>
    public bool HasMovement { get; private set; }

    public bool IsConnected => _device?.IsConnected ?? false;

    public int GetPosition(int index)
    {
        lock (_positions) { return _positions[index]; }
    }

    public bool Open()
    {
        _device = HidDevices.Enumerate()
            .Where(d => KnownVids.Contains(d.Attributes.VendorId)
                     && KnownPids.Contains(d.Attributes.ProductId)
                     && d.Capabilities.UsagePage == unchecked((short)0xFF00))
            .FirstOrDefault();

        if (_device == null) return false;

        _device.OpenDevice();
        HasMovement = _device.Capabilities.InputReportByteLength >= PositionPayloadSize + 1;

        // Re-baseline on every (re)connect: the device's accumulator kept running
        // while we were not listening, and that history is not ours to replay.
        _baselined = false;

        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true };
        _reader.Start();
        return true;
    }

    private void ReadLoop()
    {
        while (_running && _device != null && _device.IsConnected)
        {
            var report = _device.Read(100);
            if (report.Status != HidDeviceData.ReadStatus.Success || report.Data.Length < 2)
                continue;   // a timeout just means the device is idle - not an error

            if (report.Data[0] != ReportIdPositions) continue;
            if (report.Data.Length < LegacyPositionPayloadSize + 1) continue;

            // HidLibrary prepends the report ID, so index = payload offset + 1.
            lock (_positions)
            {
                for (int i = 0; i < NumEncoders; i++)
                    _positions[i] = BitConverter.ToInt32(report.Data, 1 + i * 4);
            }

            byte buttons = report.Data[17];        // payload 16
            if (buttons != _buttons)
            {
                for (int i = 0; i < NumEncoders; i++)
                {
                    bool was = (_buttons & (1 << i)) != 0;
                    bool now = (buttons  & (1 << i)) != 0;
                    if (was != now) ButtonChanged?.Invoke(i, now);
                }
                _buttons = buttons;
            }

            if (!HasMovement || report.Data.Length < PositionPayloadSize + 1) continue;

            for (int i = 0; i < NumEncoders; i++)
            {
                int now = BitConverter.ToInt32(report.Data, 21 + i * 4);   // payload 20-35

                if (_baselined)
                {
                    // unchecked: the accumulator wraps at 32 bits by design, and
                    // two's-complement subtraction gives the correct signed delta
                    // straight across the boundary.
                    int delta = unchecked(now - _movementLast[i]);
                    if (delta != 0) EncoderMoved?.Invoke(i, delta);
                }
                _movementLast[i] = now;
            }
            _baselined = true;
        }
    }

    public void SendCommand(byte command)
    {
        if (_device == null) return;
        _device.Write(new byte[] { ReportIdCommand, command, 0x00 });
    }

    public void SendConfig(byte[] config106)
    {
        if (_device == null) return;
        if (config106.Length != 106)
            throw new ArgumentException("config must be exactly 106 bytes", nameof(config106));

        var data = new byte[107];
        data[0] = ReportIdConfig;
        Array.Copy(config106, 0, data, 1, 106);
        _device.Write(data);
    }

    public void Dispose()
    {
        _running = false;
        _reader?.Join(500);
        _device?.CloseDevice();
        _device = null;
    }
}
```

### Using it

```csharp
using var knobs = new RotaryUsbDevice();
if (!knobs.Open())
    throw new InvalidOperationException("RotaryUsb device not found");

if (!knobs.HasMovement)
    Console.WriteLine("Warning: firmware predates the movement accumulator; " +
                      "unbounded controls will stall at the device's limits.");

long vfoHz = 14_200_000;

knobs.EncoderMoved += (i, delta) =>
{
    if (i == 0) vfoHz += delta * 10;         // encoder 1 tunes in 10 Hz units
};

knobs.ButtonChanged += (i, pressed) =>
{
    if (i == 0 && pressed) vfoHz = 14_200_000;   // press to return to centre
};
```

---

## 6. Mapping recipes

### Bounded control — use `position`

Volume, squelch, anything where the device's own limits are the right limits.

```csharp
int volume = knobs.GetPosition(1);   // already clamped to [min, max]
```

Configure the range once with `SendConfig`, then save it to flash so it survives a replug.

### Unbounded control — use the `movement` delta

A VFO, or any value whose range the device cannot hold.

```csharp
long vfoHz = 14_200_000;
knobs.EncoderMoved += (i, delta) => { if (i == 0) vfoHz += delta * 10; };
```

Configure that encoder with a **wide** range and `step_size = 1` so acceleration still has
room to work; you are ignoring `position` entirely, but the tiers still scale the delta.

### Stepped selector — use `wrap`

A mode or preset selector that should cycle past the end.

Configure `min_value = 0`, `max_value = n - 1`, `step_size = 1`, `wrap = 1`, and disable all
acceleration tiers (`threshold_ms = 0`) — acceleration on a discrete selector makes it
uncontrollable. Then read `position` directly as the index.

### Buttons

```csharp
knobs.ButtonChanged += (i, pressed) => { if (pressed) Toggle(i); };
```

Debounce is already handled in firmware (20 ms, first-edge-latch). Do not add your own.

---

## 7. Configuring the device from the host

Write the full 106-byte config with Output Report `0x02`, then send command `0x01` to persist
it. Config applies immediately; without the save it is lost at power-off.

### Validation rules the firmware enforces

**A config failing any of these is rejected silently and the previous config stays active.**
Read it back with command `0x04` to confirm what actually took.

For every encoder:

- `min_value < max_value`
- `step_size > 0`
- For each *enabled* tier (`threshold_ms > 0`): `multiplier != 0`
- Across enabled tiers, in index order: `threshold_ms` **strictly descending**
- Across enabled tiers, in index order: `multiplier` **strictly ascending**

The tier ordering rule expresses "faster turning must mean a bigger step." Tier 1 is the
gentlest, tier 3 the most aggressive.

A working example — the factory default:

| Tier | `threshold_ms` | `multiplier` |
|---|---|---|
| 1 | 150 | 5 |
| 2 | 80 | 15 |
| 3 | 40 | 50 |

Also: config version must be `0x01`, or the whole write is rejected.

### Verifying a write

1. Send the config (Output `0x02`).
2. Send command `0x04` (read config).
3. Compare the returned Input Report `0x02` against what you sent.
4. If it matches and you want it permanent, send command `0x01` (save).

For `global_flags` bit 0 specifically, byte 4 of the diagnostics report is the more
trustworthy check — it reports the value the decoder is *actually* using.

---

## 8. Diagnostics and troubleshooting

| Symptom | Likely cause | Check |
|---|---|---|
| No device found | Keyboard-mode firmware flashed | Rebuild without `-DFIRMWARE_MODE=keyboard` |
| Device found, no reports | Device is idle — reports are change-only | Turn a knob; also check the LED heartbeat (2 Hz) |
| Every click counts twice | `steps_per_detent` is 4, encoder needs 2 | Measure with `edge_count`; set `global_flags` bit 0 |
| Every second click ignored | `steps_per_detent` is 2, encoder needs 4 | Same, clear the bit |
| Jumpy / erratic counts | Contact bounce or marginal wiring | `invalid_count` climbing in report `0x04` |
| Knob dead at the ends | Using `position` for an unbounded control | Switch to the `movement` delta |
| Movement jumps hugely on connect | Not baselining the first sample | Set `last = now` on the first report, emit no delta |
| Movement delta occasionally enormous | Signed overflow on the wrap | Use `unchecked` subtraction |
| Direction inverted | Encoder A/B swapped | Set `reverse = 1` rather than rewiring |
| Config write ignored | Failed validation | See §7; read back with command `0x04` |

**The onboard LED blinks at 2 Hz** whenever the main loop is running. If it is dark, the
firmware is not running at all — the problem is upstream of anything on the host.

---

## 9. Gotchas checklist

- [ ] **HidLibrary prepends the report ID.** Buffer index = payload offset + 1. Other
      bindings may not — verify against yours.
- [ ] **Reports are sent only when contents change.** Silence is idle, not disconnect. A
      read timeout is normal.
- [ ] **Baseline the accumulator on the first report and on every reconnect.** Otherwise the
      device's entire pre-connection history arrives as one delta.
- [ ] **Use wrapping (`unchecked`) subtraction** for the delta.
- [ ] **The accumulator survives `Reset positions` and `Reset defaults`.** Only a power cycle
      zeroes it. If you re-zero a dial, do not expect a movement reset.
- [ ] **Raw pins read `7` at rest even mid-spin** — they are sampled at 10 Hz and encoders
      rest with contacts open. Use the counters for rotation.
- [ ] **`step_size` and acceleration both apply to movement.** A tier-3 detent at
      `step_size = 1` yields 50, not 1.
- [ ] **Feature-detect by report length**, not by a version field.
- [ ] **Config writes are validated and silently rejected.** Always read back.
- [ ] **Do not flash keyboard mode** and expect this protocol.

---

## 10. Integration checklist

For the team wiring this into an application:

1. [ ] Decide `position` versus `movement` **per encoder** and write it down — this drives
       everything else.
2. [ ] Flash the C++ Generic HID firmware; confirm the build log says `generic_hid`.
3. [ ] Measure steps-per-detent with the `edge_count` procedure (§2); set `global_flags`
       bit 0 if needed and save to flash.
4. [ ] Add discovery with the usage-page filter, plus the `InputReportByteLength` feature
       check (§3).
5. [ ] Implement the read loop with baselining and `unchecked` differencing (§5).
6. [ ] Configure each encoder's range, step, wrap, reverse, and tiers; read back to verify;
       save to flash (§7).
7. [ ] Handle disconnect and reconnect — re-open, and **re-baseline**.
8. [ ] Test the limit case explicitly: drive a bounded control to `max_value`, keep turning,
       and confirm your unbounded control still moves.
9. [ ] Test acceleration: a fast spin should cover far more ground than the same number of
       slow clicks.
10. [ ] Decide what a missing device means for your app — hard failure, or degrade to
        keyboard/mouse control.

---

## Related documentation

| Document | Covers |
|---|---|
| `firmware-cpp/README.md` | Building and flashing the C++ firmware |
| `firmware/README.md` | CircuitPython setup |
| `windows-example/README.md` | A working reference application |
| `README.md` | Hardware, wiring, and encoder selection |
| `docs/superpowers/specs/2026-08-18-movement-accumulator-design.md` | Why the accumulator is designed the way it is |
