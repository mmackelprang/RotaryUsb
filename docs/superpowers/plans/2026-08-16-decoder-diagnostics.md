# Decoder Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore real decoded encoder positions to HID Input Report ID 0x01 and add a non-destructive diagnostic Input Report ID 0x04 that makes the quadrature decoder observable from the host, so the true steps-per-detent of the installed encoders can be measured rather than guessed.

**Architecture:** `GenericHidEncoder` gains three monotonic counters (edges, invalid transitions, emitted detents) incremented inside the existing decode path, plus an uninverted raw-pin reader. A new 56-byte Input Report ID 0x04 carries those per encoder at 10 Hz alongside the untouched Report ID 0x01. The Windows console app gains a `[D] Diagnostics` view that renders the table and can zero the counters and toggle `global_flags` bit 0. Diagnostics ship in the normal build — there is no diagnostic variant to drift.

**Tech Stack:** Pico C SDK + TinyUSB (C++17), CMake, C# .NET 8 with HidLibrary

**Spec:** `docs/superpowers/specs/2026-08-16-decoder-diagnostics-design.md`

## Global Constraints

- Input Report ID 0x01 keeps its exact 21-byte layout. Byte-identical, no exceptions — existing hosts depend on it.
- Decoder behavior does not change. `steps_per_detent` default stays 4; only the host's ability to *see* and *switch* it changes.
- `DiagReport` is 56 bytes, guarded by `static_assert`. It must fit one 64-byte full-speed interrupt packet.
- Report ID 0x04 and command 0x05 are the only new protocol identifiers. IDs 1/2/3 and commands 0x01–0x04 are unchanged.
- Branch policy: all work on `feat/decoder-diagnostics`, merged via PR. Never commit to `main`.
- `raw_pins` uses **literal, uninverted** GPIO levels — idle reads `7`. This is deliberately the opposite convention from the private `read_ab_state()`.
- CircuitPython (`firmware/`) and `tools/encoder-monitor/` are out of scope.

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `firmware-cpp/CMakeLists.txt` | Modify | `FIRMWARE_MODE` cache variable replaces the file-swap build step |
| `firmware-cpp/main_generic_hid.cpp` | Modify | Restore report 1; add counters, `DiagReport`, descriptor entry, `CMD_RESET_DIAG`, send scheduling |
| `windows-example/Program.cs` | Modify | Commit stranded fixes; parse report 4; diagnostics view; `[D]`→`[F]` remap |
| `firmware-cpp/README.md` | Modify | Build modes + default-changed note; report 0x04; command 0x05 |
| `README.md` | Modify | Build modes + default-changed note |

`firmware-cpp/tusb_config.h` is **not** modified — `CFG_TUD_HID_EP_BUFSIZE` is already 128.

## A Note on Verification

This repo has no firmware test harness. `tests/` covers desktop Python config logic for the
CircuitPython firmware, which is out of scope here. Verification for firmware tasks is therefore:
compile-time `static_assert`, a successful cross-compile, and a byte-level `git diff` assertion that
the protected Report ID 0x01 block did not change. The behavioral questions are physical and are
answered by the hardware Test Plan in Task 10.

Do not invent a test framework for these tasks. Do not skip the `git diff` assertions — they are the
automated half of the verification.

---

## Task 1: Preserve the stranded host bug fixes

The working tree contains two real `Program.cs` fixes from the debugging session. They are unrelated
to diagnostics and must land as their own commit **before** anything else touches the file, so they
survive independently and are reviewable on their own.

**Files:**
- Modify: `windows-example/Program.cs:350-358` (already modified in the working tree — commit as-is)

**Interfaces:**
- Consumes: nothing
- Produces: a clean working tree for `windows-example/Program.cs`

- [ ] **Step 1: Create the working branch**

```bash
cd /d/prj/RotaryUsb
git checkout -b feat/decoder-diagnostics
```

- [ ] **Step 2: Confirm the two fixes are exactly what is in the working tree**

```bash
git diff -- windows-example/Program.cs
```

Expected: exactly two hunks — `OpenDevice` gaining explicit `DeviceMode.Overlapped` /
`ShareMode.ShareRead | ShareMode.ShareWrite`, and `MonitorDeviceEvents` wrapped in a
`try`/`catch (PlatformNotSupportedException)`. If you see anything else, stop and report it.

- [ ] **Step 3: Commit only this file**

```bash
git add windows-example/Program.cs
git commit -m "fix(windows-example): open HID device overlapped and tolerate missing WMI

OpenDevice() with default flags could fail to establish a readable handle
alongside other openers; request Overlapped mode with shared read/write
explicitly. MonitorDeviceEvents throws PlatformNotSupportedException when WMI
is unavailable, which killed device setup outright.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J"
```

- [ ] **Step 4: Verify the file is clean**

```bash
git status --porcelain -- windows-example/Program.cs
```

Expected: no output. `firmware-cpp/CMakeLists.txt` and `firmware-cpp/main_generic_hid.cpp` remain
modified — that is correct, they are handled in Tasks 2 and 3.

---

## Task 2: Replace the file-swap build step with a `FIRMWARE_MODE` option

**Files:**
- Modify: `firmware-cpp/CMakeLists.txt:14-20`

**Interfaces:**
- Consumes: nothing
- Produces: CMake cache variable `FIRMWARE_MODE` (values `generic_hid` | `keyboard`, default `generic_hid`) and internal variable `FIRMWARE_MAIN`

- [ ] **Step 1: Replace the `add_executable` block**

In `firmware-cpp/CMakeLists.txt`, replace this (note the working tree currently has
`main_generic_hid.cpp` here as an uncommitted edit):

```cmake
# main.cpp is the active firmware entry point.
# For Generic HID mode: copy main_generic_hid.cpp to main.cpp before building.
# For Keyboard HID mode: use the original main.cpp (default).
add_executable(rotary_usb
    main_generic_hid.cpp
    encoder.cpp
)
```

with:

```cmake
# Firmware personality. Both entry points share encoder.cpp and are mutually
# exclusive — each defines its own main() and its own TinyUSB descriptor callbacks.
#
#   generic_hid -> main_generic_hid.cpp   vendor HID, runtime config, diagnostics
#   keyboard    -> main.cpp               F1-F12 keyboard HID
#
# NOTE: the default changed from 'keyboard' to 'generic_hid'. A bare `cmake ..`
# now builds the Generic HID firmware. This replaces the old (and error-prone)
# "copy main_generic_hid.cpp over main.cpp" step.
#
# CMake caches this value, so switching modes means re-running cmake with the
# flag — not passing it on every build:
#     cmake -DFIRMWARE_MODE=keyboard ..
set(FIRMWARE_MODE "generic_hid" CACHE STRING
    "Firmware personality: generic_hid or keyboard")
set_property(CACHE FIRMWARE_MODE PROPERTY STRINGS generic_hid keyboard)

if(FIRMWARE_MODE STREQUAL "generic_hid")
    set(FIRMWARE_MAIN main_generic_hid.cpp)
elseif(FIRMWARE_MODE STREQUAL "keyboard")
    set(FIRMWARE_MAIN main.cpp)
else()
    message(FATAL_ERROR
        "FIRMWARE_MODE must be 'generic_hid' or 'keyboard', got '${FIRMWARE_MODE}'")
endif()

message(STATUS "RotaryUsb firmware mode: ${FIRMWARE_MODE} (${FIRMWARE_MAIN})")

add_executable(rotary_usb
    ${FIRMWARE_MAIN}
    encoder.cpp
)
```

- [ ] **Step 2: Verify the default builds Generic HID**

```bash
cd /d/prj/RotaryUsb/firmware-cpp
rm -rf build-generic && mkdir build-generic && cd build-generic
cmake .. 2>&1 | grep "RotaryUsb firmware mode"
```

Expected: `-- RotaryUsb firmware mode: generic_hid (main_generic_hid.cpp)`

- [ ] **Step 3: Build it**

```bash
make -j4
```

Expected: builds clean, produces `rotary_usb.uf2`.

- [ ] **Step 4: Verify the keyboard mode still compiles**

`main.cpp` has not been built since March and may have bit-rotted. Finding that out now is the point
of this step — if it fails, report the errors rather than silently leaving the mode broken.

```bash
cd /d/prj/RotaryUsb/firmware-cpp
rm -rf build-keyboard && mkdir build-keyboard && cd build-keyboard
cmake -DFIRMWARE_MODE=keyboard .. 2>&1 | grep "RotaryUsb firmware mode"
make -j4
```

Expected: `-- RotaryUsb firmware mode: keyboard (main.cpp)` then a clean build.

- [ ] **Step 5: Verify an invalid mode fails loudly**

```bash
cd /d/prj/RotaryUsb/firmware-cpp
rm -rf build-bogus && mkdir build-bogus && cd build-bogus
cmake -DFIRMWARE_MODE=nonsense .. ; echo "exit=$?"
```

Expected: `CMake Error ... FIRMWARE_MODE must be 'generic_hid' or 'keyboard', got 'nonsense'` and a
non-zero exit.

- [ ] **Step 6: Clean up the scratch build directories and commit**

```bash
cd /d/prj/RotaryUsb/firmware-cpp
rm -rf build-generic build-keyboard build-bogus
cd /d/prj/RotaryUsb
git add firmware-cpp/CMakeLists.txt
git commit -m "build: select firmware personality with -DFIRMWARE_MODE

Replaces the manual 'cp main_generic_hid.cpp main.cpp' step and the
uncommitted local add_executable edit that kept drifting. Default is
generic_hid, which is what every recent change targets.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J"
```

---

## Task 3: Restore Report ID 0x01 to real decoded positions

This is the regression fix. It is deliberately its own task and its own commit: after it, the
firmware is correct, and everything that follows is purely additive.

**Files:**
- Modify: `firmware-cpp/main_generic_hid.cpp:702-729` (the `[DIAG]` block in `hid_task`)
- Modify: `firmware-cpp/main_generic_hid.cpp:769-776, 790-797` (LED heartbeat — keep, de-`[DIAG]`)

**Interfaces:**
- Consumes: existing `GenericHidEncoder::get_position()`, `get_active_tier()`
- Produces: `hid_task()` sending Report ID 0x01 with decoded positions, gated on `memcmp`

- [ ] **Step 1: Replace the destructive DIAG block in `hid_task`**

Replace this entire block:

```cpp
    // [DIAG] Report each encoder's RAW pin state instead of its decoded position
    // so we can see electrical/wiring activity directly. Each value packs the 3
    // pins as (A<<2)|(B<<1)|SW. With the internal pull-ups and nothing pressed,
    // an idle/connected pin reads HIGH, so a healthy idle encoder shows 7.
    // Turning toggles A/B (value bounces among 1/3/5/7); a button press clears
    // SW (bit 0) giving an even value (e.g. 6).
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        const EncoderPinConfig& p = ENCODER_PIN_CONFIGS[i];
        int32_t a  = gpio_get(p.pin_a)  ? 1 : 0;
        int32_t b  = gpio_get(p.pin_b)  ? 1 : 0;
        int32_t sw = gpio_get(p.pin_sw) ? 1 : 0;
        current_report.positions[i] = (a << 2) | (b << 1) | sw;
    }
    current_report.button_states = cached_button_states;
    current_report.active_tiers = 0;
    memset(current_report.reserved, 0, sizeof(current_report.reserved));

    // [DIAG] Force a send ~10x/sec so the host always shows the live pin states
    // even when nothing is changing.
    static uint32_t diag_send_ms = 0;
    uint32_t diag_now = to_ms_since_boot(get_absolute_time());
    bool diag_force = (diag_now - diag_send_ms >= 100);
    if (diag_force) diag_send_ms = diag_now;

    if (diag_force || memcmp(&current_report, &last_report, sizeof(PositionReport)) != 0) {
        tud_hid_report(1, &current_report, sizeof(PositionReport));
        memcpy(&last_report, &current_report, sizeof(PositionReport));
    }
```

with the original, restored verbatim:

```cpp
    // Build position report
    uint8_t tier_byte = 0;
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        current_report.positions[i] = encoders[i]->get_position();
        tier_byte |= (encoders[i]->get_active_tier() & 0x03) << (i * 2);
    }
    current_report.button_states = cached_button_states;
    current_report.active_tiers = tier_byte;
    memset(current_report.reserved, 0, sizeof(current_report.reserved));

    // Only send if something changed
    if (memcmp(&current_report, &last_report, sizeof(PositionReport)) != 0) {
        tud_hid_report(1, &current_report, sizeof(PositionReport));
        memcpy(&last_report, &current_report, sizeof(PositionReport));
    }
```

- [ ] **Step 2: Keep the LED heartbeat, but drop the `[DIAG]` framing**

It is a genuine liveness signal and stays permanently. Replace this block in `main()`:

```cpp
    // [DIAG] Onboard-LED heartbeat: proves the main loop is alive even if USB
    // reporting is broken. (Pico W defines no PICO_DEFAULT_LED_PIN, so skipped.)
#ifdef PICO_DEFAULT_LED_PIN
    gpio_init(PICO_DEFAULT_LED_PIN);
    gpio_set_dir(PICO_DEFAULT_LED_PIN, GPIO_OUT);
    uint32_t diag_led_ms = 0;
    bool diag_led_on = false;
#endif
```

with:

```cpp
    // Onboard-LED heartbeat at 2 Hz: proves the main loop is alive even when USB
    // reporting is broken, which is the first thing you want to know when the host
    // sees nothing. (Pico W defines no PICO_DEFAULT_LED_PIN, so this is skipped there.)
#ifdef PICO_DEFAULT_LED_PIN
    gpio_init(PICO_DEFAULT_LED_PIN);
    gpio_set_dir(PICO_DEFAULT_LED_PIN, GPIO_OUT);
    uint32_t led_ms = 0;
    bool led_on = false;
#endif
```

and replace this block inside the `while (true)` loop:

```cpp
#ifdef PICO_DEFAULT_LED_PIN
        uint32_t diag_led_now = to_ms_since_boot(get_absolute_time());
        if (diag_led_now - diag_led_ms >= 250) {
            diag_led_ms = diag_led_now;
            diag_led_on = !diag_led_on;
            gpio_put(PICO_DEFAULT_LED_PIN, diag_led_on);
        }
#endif
```

with:

```cpp
#ifdef PICO_DEFAULT_LED_PIN
        uint32_t led_now = to_ms_since_boot(get_absolute_time());
        if (led_now - led_ms >= 250) {
            led_ms = led_now;
            led_on = !led_on;
            gpio_put(PICO_DEFAULT_LED_PIN, led_on);
        }
#endif
```

- [ ] **Step 3: Assert the report path is byte-identical to the last committed version**

This is the automated guard for the "Report ID 0x01 must not change" constraint.

```bash
cd /d/prj/RotaryUsb
git diff HEAD -- firmware-cpp/main_generic_hid.cpp
```

Expected: the **only** hunks are the LED heartbeat additions in `main()`. There must be **zero**
diff inside `hid_task()`. If any line of `hid_task()` shows as changed, the restore is not faithful —
fix it before continuing.

- [ ] **Step 4: Verify it builds**

```bash
cd /d/prj/RotaryUsb/firmware-cpp
rm -rf build && mkdir build && cd build
cmake .. && make -j4
```

Expected: clean build, `rotary_usb.uf2` produced.

- [ ] **Step 5: Commit**

```bash
cd /d/prj/RotaryUsb
git add firmware-cpp/main_generic_hid.cpp
git commit -m "fix(firmware): report decoded positions again on HID report ID 0x01

A debugging patch overwrote positions[] with raw GPIO pin state and
force-sent at 10 Hz, which meant the decoder output never reached the host.
Restore the original position/tier build and the change-gated send. The
onboard-LED heartbeat from the same patch is kept — it is a genuine liveness
signal — with the [DIAG] framing removed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J"
```

---

## Task 4: Add diagnostic counters to `GenericHidEncoder`

**Files:**
- Modify: `firmware-cpp/main_generic_hid.cpp:342-513` (the `GenericHidEncoder` class)

**Interfaces:**
- Consumes: existing `TRANSITION_TABLE`, `steps_per_detent_`, `pin_a_`/`pin_b_`/`pin_sw_`
- Produces, for Task 5:
  - `uint32_t GenericHidEncoder::get_edge_count() const`
  - `uint32_t GenericHidEncoder::get_invalid_count() const`
  - `uint32_t GenericHidEncoder::get_detent_count() const`
  - `int8_t   GenericHidEncoder::get_steps_per_detent() const`
  - `uint8_t  GenericHidEncoder::read_raw_pins() const`
  - `void     GenericHidEncoder::reset_diagnostics()`

- [ ] **Step 1: Add the counters to the constructor initializer list**

Order matters — GCC warns under `-Wreorder` if the initializer list diverges from declaration order.
Append these at the **end** of the list. Replace:

```cpp
        , debounce_start_(0)
        , debounce_active_(false)
    {}
```

with:

```cpp
        , debounce_start_(0)
        , debounce_active_(false)
        , edge_count_(0)
        , invalid_count_(0)
        , detent_count_(0)
    {}
```

- [ ] **Step 2: Add the accessors**

Immediately after the existing `get_active_tier()` line:

```cpp
    int32_t get_position() const { return position_; }
    uint8_t get_active_tier() const { return active_tier_; }
```

insert:

```cpp
    // ---- Decoder diagnostics (Input Report ID 0x04) ----

    uint32_t get_edge_count()       const { return edge_count_; }
    uint32_t get_invalid_count()    const { return invalid_count_; }
    uint32_t get_detent_count()     const { return detent_count_; }
    int8_t   get_steps_per_detent() const { return steps_per_detent_; }

    void reset_diagnostics() {
        edge_count_ = 0;
        invalid_count_ = 0;
        detent_count_ = 0;
    }

    // Literal GPIO levels, NOT inverted: (A<<2)|(B<<1)|SW.
    // With internal pull-ups and nothing pressed this reads 7 (0b111); a held
    // button clears bit 0 giving 6.
    //
    // WARNING: this is the opposite convention from the private read_ab_state()
    // below, which inverts to active-high for the quadrature transition table.
    // Two readers, two conventions, on purpose. Do not substitute one for the other.
    uint8_t read_raw_pins() const {
        uint8_t a  = gpio_get(pin_a_)  ? 1 : 0;
        uint8_t b  = gpio_get(pin_b_)  ? 1 : 0;
        uint8_t sw = gpio_get(pin_sw_) ? 1 : 0;
        return (uint8_t)((a << 2) | (b << 1) | sw);
    }
```

- [ ] **Step 3: Increment the counters inside the decode path**

Replace the transition-decode block in `update()`:

```cpp
        if (current_ab_state != last_ab_state_) {
            uint8_t index = (last_ab_state_ << 2) | current_ab_state;
            int8_t direction = TRANSITION_TABLE[index];

            if (direction != 0) {
                if (config_->reverse) direction = -direction;
                steps_ += direction;

                if (steps_ >= steps_per_detent_ || steps_ <= -steps_per_detent_) {
                    int8_t detent_direction = (steps_ > 0) ? 1 : -1;
                    steps_ = 0;
```

with:

```cpp
        if (current_ab_state != last_ab_state_) {
            edge_count_++;

            uint8_t index = (last_ab_state_ << 2) | current_ab_state;
            int8_t direction = TRANSITION_TABLE[index];

            if (direction != 0) {
                if (config_->reverse) direction = -direction;
                steps_ += direction;

                if (steps_ >= steps_per_detent_ || steps_ <= -steps_per_detent_) {
                    // Counted before the position math, so clamping at min_value or
                    // max_value does not hide emitted detents. Counting works from
                    // anywhere in the range, in either direction.
                    detent_count_++;

                    int8_t detent_direction = (steps_ > 0) ? 1 : -1;
                    steps_ = 0;
```

Then replace the `else` arm:

```cpp
            } else {
                steps_ = 0;
            }
```

with:

```cpp
            } else {
                // TRANSITION_TABLE yields 0 here only for a simultaneous A+B change
                // (indices 3, 6, 9, 12) — the last==current entries (0, 5, 10, 15)
                // are already excluded by the enclosing if. A simultaneous change is
                // physically impossible in clean quadrature, so this counts contact
                // bounce, a marginal connection, or a missed poll. Never a decoder
                // logic error.
                invalid_count_++;
                steps_ = 0;
            }
```

- [ ] **Step 4: Cross-reference the inversion in `read_ab_state()`**

Replace:

```cpp
    uint8_t read_ab_state() {
        uint8_t a_val = gpio_get(pin_a_) ? 0 : 1;
        uint8_t b_val = gpio_get(pin_b_) ? 0 : 1;
        return (a_val << 1) | b_val;
    }
```

with:

```cpp
    // Inverted to active-high (pins are active-low with pull-ups) because the
    // quadrature TRANSITION_TABLE is indexed in that space. See read_raw_pins()
    // above for the uninverted view used by diagnostics.
    uint8_t read_ab_state() {
        uint8_t a_val = gpio_get(pin_a_) ? 0 : 1;
        uint8_t b_val = gpio_get(pin_b_) ? 0 : 1;
        return (a_val << 1) | b_val;
    }
```

- [ ] **Step 5: Add the member declarations**

Replace:

```cpp
    static constexpr uint32_t BUTTON_DEBOUNCE_US = 20000;  // 20ms
    static const int8_t TRANSITION_TABLE[16];
```

with:

```cpp
    // Decoder diagnostics. Monotonic totals across both directions; the host
    // zeroes them via Output Report ID 0x03, command CMD_RESET_DIAG.
    // uint32 rather than uint16: at a sustained 80 edges/sec a uint16 wraps in
    // about 13 minutes, which is inside a plausible debugging session.
    uint32_t edge_count_;
    uint32_t invalid_count_;
    uint32_t detent_count_;

    static constexpr uint32_t BUTTON_DEBOUNCE_US = 20000;  // 20ms
    static const int8_t TRANSITION_TABLE[16];
```

- [ ] **Step 6: Verify it builds**

```bash
cd /d/prj/RotaryUsb/firmware-cpp/build
make -j4
```

Expected: clean build, no `-Wreorder` warning.

- [ ] **Step 7: Verify Report ID 0x01 is still untouched**

```bash
cd /d/prj/RotaryUsb
git diff -- firmware-cpp/main_generic_hid.cpp | grep -E "^[-+].*(positions\[|active_tiers|button_states|tud_hid_report\(1)"
```

Expected: no output. This task must not touch the report-1 build or send path.

- [ ] **Step 8: Commit**

```bash
git add firmware-cpp/main_generic_hid.cpp
git commit -m "feat(firmware): count edges, invalid transitions and emitted detents

Adds three monotonic counters to GenericHidEncoder plus an uninverted
read_raw_pins(). Nothing consumes them yet. detent_count_ increments before
the position math so clamping does not hide detents; invalid_count_ is
unambiguous because the enclosing if already excludes the no-change table
entries, leaving only physically impossible simultaneous A+B changes.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J"
```

---

## Task 5: Add Input Report ID 0x04 and the reset command

**Files:**
- Modify: `firmware-cpp/main_generic_hid.cpp:69-82` (struct + command constants)
- Modify: `firmware-cpp/main_generic_hid.cpp:255-319` (HID report descriptor)
- Modify: `firmware-cpp/main_generic_hid.cpp:518-522` (report state globals)
- Modify: `firmware-cpp/main_generic_hid.cpp:632-654` (`tud_hid_set_report_cb`)
- Modify: `firmware-cpp/main_generic_hid.cpp:685-730` (`hid_task`)

**Interfaces:**
- Consumes: `get_edge_count()`, `get_invalid_count()`, `get_detent_count()`, `get_steps_per_detent()`, `read_raw_pins()`, `reset_diagnostics()` from Task 4
- Produces, for Tasks 6–7: wire format of Input Report ID 0x04 (56-byte payload) and `CMD_RESET_DIAG = 0x05` on Output Report ID 0x03

- [ ] **Step 1: Add the `DiagReport` struct**

After the existing `static_assert(sizeof(PositionReport) == 21, ...)` line, insert:

```cpp
// Input Report ID 0x04: decoder diagnostics (56 bytes)
//
// Non-destructive companion to Report ID 0x01. This ships in the normal build —
// there is deliberately no separate "diagnostic firmware", because a variant build
// is exactly how the decoder output got silently replaced by pin state before.
//
// Offsets below are payload bytes, after the Report ID byte.
struct DiagReport {
    uint8_t  raw_pins[NUM_ENCODERS];        // 0-3   (A<<2)|(B<<1)|SW, literal levels, idle = 7
    uint8_t  steps_per_detent;              // 4     threshold the decoder is actually using
    uint8_t  reserved[3];                   // 5-7   0x00; keeps the uint32 arrays 4-byte aligned
    uint32_t edge_count[NUM_ENCODERS];      // 8-23  observed A/B state changes
    uint32_t invalid_count[NUM_ENCODERS];   // 24-39 illegal transitions (a subset of edge_count)
    uint32_t detent_count[NUM_ENCODERS];    // 40-55 detents the decoder emitted
} __attribute__((packed));

static_assert(sizeof(DiagReport) == 56, "DiagReport must be 56 bytes");
```

- [ ] **Step 2: Add the reset command constant**

Replace:

```cpp
static constexpr uint8_t CMD_READ_CONFIG     = 0x04;
```

with:

```cpp
static constexpr uint8_t CMD_READ_CONFIG     = 0x04;
static constexpr uint8_t CMD_RESET_DIAG      = 0x05;
```

- [ ] **Step 3: Add the descriptor entry for Report ID 0x04**

In `hid_report_descriptor[]`, replace:

```cpp
    0xC0               // End Collection
};
```

with:

```cpp
    // ---- Input Report ID 0x04: Decoder Diagnostics (56 bytes) ----
    0x85, 0x04,        //   Report ID (4)
    0x09, 0x08,        //   Usage (Vendor Usage 8 - Decoder Diagnostics)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x38,        //   Report Count (56 bytes)
    0x81, 0x02,        //   Input (Data, Variable, Absolute)

    0xC0               // End Collection
};
```

`0x38` is 56. This block is appended after the Report ID 0x03 output block, so it does not disturb
the existing Output Report ID 0x02 entry, which relies on inheriting report ID 2 from the preceding
input declaration.

- [ ] **Step 4: Add the report state global**

Replace:

```cpp
// Report state
static PositionReport current_report;
static PositionReport last_report;
```

with:

```cpp
// Report state
static PositionReport current_report;
static PositionReport last_report;
static DiagReport diag_report;
```

- [ ] **Step 5: Handle `CMD_RESET_DIAG`**

In `tud_hid_set_report_cb`, replace:

```cpp
        } else if (command == CMD_READ_CONFIG) {
            pending_config_readback = true;
        }
```

with:

```cpp
        } else if (command == CMD_READ_CONFIG) {
            pending_config_readback = true;
        } else if (command == CMD_RESET_DIAG) {
            for (size_t i = 0; i < NUM_ENCODERS; i++) {
                encoders[i]->reset_diagnostics();
            }
            printf("Diagnostic counters reset\n");
        }
```

- [ ] **Step 6: Add the diagnostic send interval constant**

Immediately before `static void hid_task() {`, insert:

```cpp
// Diagnostic heartbeat cadence. 10 Hz x 57 bytes on the wire is ~570 B/s, which is
// negligible on a full-speed interrupt endpoint.
static constexpr uint32_t DIAG_INTERVAL_MS = 100;
```

- [ ] **Step 7: Make the position send yield the interval**

The position report must return after sending so at most one report goes out per 10 ms tick.
Replace:

```cpp
    // Only send if something changed
    if (memcmp(&current_report, &last_report, sizeof(PositionReport)) != 0) {
        tud_hid_report(1, &current_report, sizeof(PositionReport));
        memcpy(&last_report, &current_report, sizeof(PositionReport));
    }
}
```

with:

```cpp
    // Only send if something changed
    if (memcmp(&current_report, &last_report, sizeof(PositionReport)) != 0) {
        tud_hid_report(1, &current_report, sizeof(PositionReport));
        memcpy(&last_report, &current_report, sizeof(PositionReport));
        return;  // One report per interval
    }

    // Diagnostics (ID 0x04) are lowest priority: a position change defers the
    // heartbeat by one 10 ms tick. Positions only change on detents, so the 10 Hz
    // cadence holds in practice.
    static uint32_t diag_ms = 0;
    uint32_t now_ms = to_ms_since_boot(get_absolute_time());
    if (now_ms - diag_ms >= DIAG_INTERVAL_MS) {
        diag_ms = now_ms;
        for (size_t i = 0; i < NUM_ENCODERS; i++) {
            diag_report.raw_pins[i]      = encoders[i]->read_raw_pins();
            diag_report.edge_count[i]    = encoders[i]->get_edge_count();
            diag_report.invalid_count[i] = encoders[i]->get_invalid_count();
            diag_report.detent_count[i]  = encoders[i]->get_detent_count();
        }
        diag_report.steps_per_detent = (uint8_t)encoders[0]->get_steps_per_detent();
        memset(diag_report.reserved, 0, sizeof(diag_report.reserved));
        tud_hid_report(4, &diag_report, sizeof(DiagReport));
    }
}
```

- [ ] **Step 8: Verify it builds, including the size assertion**

```bash
cd /d/prj/RotaryUsb/firmware-cpp/build
make -j4
```

Expected: clean build. A failure on `"DiagReport must be 56 bytes"` means the struct was edited
away from the wire format the host in Task 6 parses — fix the struct, not the assertion.

- [ ] **Step 9: Verify Report ID 0x01's payload construction is still untouched**

```bash
cd /d/prj/RotaryUsb
git diff -- firmware-cpp/main_generic_hid.cpp | grep -E "^-.*(positions\[i\] = encoders|active_tiers = tier_byte|tud_hid_report\(1)"
```

Expected: no output — no line of the report-1 build or send was removed. (The `return;` addition is
a `+` line and correctly does not appear.)

- [ ] **Step 10: Commit**

```bash
git add firmware-cpp/main_generic_hid.cpp
git commit -m "feat(firmware): add decoder diagnostics on HID input report ID 0x04

56-byte report carrying per-encoder raw pins, the active steps_per_detent,
and cumulative edge/invalid/detent counts, sent at 10 Hz below the position
report in priority. Adds command 0x05 to zero the counters without a replug.
Report ID 0x01 is unchanged.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J"
```

---

## Task 6: Parse Report ID 0x04 on the host

**Files:**
- Modify: `windows-example/Program.cs:36-45` (report ID and command constants)
- Modify: `windows-example/Program.cs:202-208` (state)
- Modify: `windows-example/Program.cs:410-448` (`ReportReaderLoop`)

**Interfaces:**
- Consumes: Report ID 0x04 wire format from Task 5
- Produces, for Task 7: `_diagRawPins`, `_diagEdgeCount`, `_diagInvalidCount`, `_diagDetentCount`, `_diagStepsPerDetent`, `_diagLastSeenUtc`, and the constants `REPORT_ID_DIAG`, `CMD_RESET_DIAG`, `DIAG_PAYLOAD_SIZE`

- [ ] **Step 1: Add the constants**

Replace:

```csharp
    // Report IDs
    private const byte REPORT_ID_POSITIONS = 0x01;
    private const byte REPORT_ID_CONFIG = 0x02;
    private const byte REPORT_ID_COMMAND = 0x03;

    // Commands
    private const byte CMD_SAVE_CONFIG = 0x01;
    private const byte CMD_RESET_DEFAULTS = 0x02;
    private const byte CMD_RESET_POSITIONS = 0x03;
    private const byte CMD_READ_CONFIG = 0x04;
```

with:

```csharp
    // Report IDs
    private const byte REPORT_ID_POSITIONS = 0x01;
    private const byte REPORT_ID_CONFIG = 0x02;
    private const byte REPORT_ID_COMMAND = 0x03;
    private const byte REPORT_ID_DIAG = 0x04;

    // Input Report ID 0x04 payload size, in bytes after the report ID byte.
    private const int DIAG_PAYLOAD_SIZE = 56;

    // Commands
    private const byte CMD_SAVE_CONFIG = 0x01;
    private const byte CMD_RESET_DEFAULTS = 0x02;
    private const byte CMD_RESET_POSITIONS = 0x03;
    private const byte CMD_READ_CONFIG = 0x04;
    private const byte CMD_RESET_DIAG = 0x05;
```

- [ ] **Step 2: Add the diagnostic state fields**

Replace:

```csharp
    private static HidDevice? _hidDevice;
    private static bool _configReceived;
    private static readonly object _lock = new();
```

with:

```csharp
    private static HidDevice? _hidDevice;
    private static bool _configReceived;
    private static readonly object _lock = new();

    // Decoder diagnostics (Input Report ID 0x04). All guarded by _lock.
    private static readonly byte[] _diagRawPins = new byte[NUM_ENCODERS];
    private static readonly uint[] _diagEdgeCount = new uint[NUM_ENCODERS];
    private static readonly uint[] _diagInvalidCount = new uint[NUM_ENCODERS];
    private static readonly uint[] _diagDetentCount = new uint[NUM_ENCODERS];
    private static byte _diagStepsPerDetent;
    private static DateTime _diagLastSeenUtc = DateTime.MinValue;
```

- [ ] **Step 3: Parse the report**

In `ReportReaderLoop`, replace:

```csharp
            else if (reportId == REPORT_ID_CONFIG && report.Data.Length >= FULL_CONFIG_SIZE + 1)
            {
                // Input Report ID 0x02: 106 bytes config readback
                var configData = new byte[FULL_CONFIG_SIZE];
                Array.Copy(report.Data, 1, configData, 0, FULL_CONFIG_SIZE);
                var parsed = DeviceConfig.Deserialize(configData);
                if (parsed != null)
                {
                    lock (_lock)
                    {
                        _deviceConfig = parsed;
                        _configReceived = true;
                    }
                }
            }
```

with:

```csharp
            else if (reportId == REPORT_ID_CONFIG && report.Data.Length >= FULL_CONFIG_SIZE + 1)
            {
                // Input Report ID 0x02: 106 bytes config readback
                var configData = new byte[FULL_CONFIG_SIZE];
                Array.Copy(report.Data, 1, configData, 0, FULL_CONFIG_SIZE);
                var parsed = DeviceConfig.Deserialize(configData);
                if (parsed != null)
                {
                    lock (_lock)
                    {
                        _deviceConfig = parsed;
                        _configReceived = true;
                    }
                }
            }
            else if (reportId == REPORT_ID_DIAG && report.Data.Length >= DIAG_PAYLOAD_SIZE + 1)
            {
                // Input Report ID 0x04: 56 bytes of decoder diagnostics.
                // HidLibrary prepends the report ID, so buffer index = payload offset + 1.
                lock (_lock)
                {
                    for (int i = 0; i < NUM_ENCODERS; i++)
                        _diagRawPins[i] = report.Data[1 + i];            // payload 0-3
                    _diagStepsPerDetent = report.Data[5];                // payload 4
                                                                          // payload 5-7 reserved
                    for (int i = 0; i < NUM_ENCODERS; i++)
                    {
                        _diagEdgeCount[i]    = BitConverter.ToUInt32(report.Data, 9  + i * 4);  // payload 8-23
                        _diagInvalidCount[i] = BitConverter.ToUInt32(report.Data, 25 + i * 4);  // payload 24-39
                        _diagDetentCount[i]  = BitConverter.ToUInt32(report.Data, 41 + i * 4);  // payload 40-55
                    }
                    _diagLastSeenUtc = DateTime.UtcNow;
                }
            }
```

- [ ] **Step 4: Verify it compiles**

```bash
cd /d/prj/RotaryUsb/windows-example
dotnet build
```

Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Commit**

```bash
cd /d/prj/RotaryUsb
git add windows-example/Program.cs
git commit -m "feat(windows-example): parse decoder diagnostics from report ID 0x04

Nothing renders it yet. Buffer offsets are payload offset + 1 because
HidLibrary prepends the report ID byte.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J"
```

---

## Task 7: Add the host diagnostics view

**Files:**
- Modify: `windows-example/Program.cs:512-549` (main menu text and switch)
- Modify: `windows-example/Program.cs` (new `RunDiagnostics` and `WriteLinePadded` methods, after `RunMonitor`)

**Interfaces:**
- Consumes: all state and constants produced by Task 6; existing `SendCommand(byte)` and `SendConfig(DeviceConfig)`
- Produces: `RunDiagnostics(HidDevice, CancellationToken)` reachable from the main menu via `[D]`

- [ ] **Step 1: Remap the menu and add the Diagnostics entry**

`[D]` currently means "Reset to defaults". Move that to `[F]` (Factory reset) — a clearer mnemonic
that also removes a hazard, since a mis-pressed `D` now opens a read-only screen instead of wiping
the device config. Replace:

```csharp
            Console.WriteLine("[M] Monitor - Live display of encoder values");
            Console.WriteLine("[C] Configure encoder");
            Console.WriteLine("[S] Save config to device flash");
            Console.WriteLine("[D] Reset to defaults");
            Console.WriteLine("[R] Reset positions");
            Console.WriteLine("[Q] Quit");
```

with:

```csharp
            Console.WriteLine("[M] Monitor - Live display of encoder values");
            Console.WriteLine("[C] Configure encoder");
            Console.WriteLine("[D] Diagnostics - Decoder counters and raw pin state");
            Console.WriteLine("[S] Save config to device flash");
            Console.WriteLine("[F] Factory reset (restore default config)");
            Console.WriteLine("[R] Reset positions");
            Console.WriteLine("[Q] Quit");
```

- [ ] **Step 2: Update the switch to match**

Replace:

```csharp
                case 'D':
                    SendCommand(CMD_RESET_DEFAULTS);
                    Console.WriteLine("\nReset to defaults command sent.");
                    Thread.Sleep(500);
                    SendCommand(CMD_READ_CONFIG);
                    Thread.Sleep(500);
                    break;
```

with:

```csharp
                case 'D':
                    RunDiagnostics(device, cts.Token);
                    break;
                case 'F':
                    SendCommand(CMD_RESET_DEFAULTS);
                    Console.WriteLine("\nFactory reset command sent.");
                    Thread.Sleep(500);
                    SendCommand(CMD_READ_CONFIG);
                    Thread.Sleep(500);
                    break;
```

- [ ] **Step 3: Add the diagnostics view**

Insert this immediately after the closing brace of `RunMonitor`, before the
`// CONFIGURE ENCODER` banner comment:

```csharp
    // ========================================================================
    // DECODER DIAGNOSTICS
    // ========================================================================

    private static void WriteLinePadded(string s) => Console.WriteLine(s.PadRight(78));

    private static void RunDiagnostics(HidDevice device, CancellationToken ct)
    {
        Console.Clear();
        bool? lastHadData = null;

        while (!ct.IsCancellationRequested && device.IsConnected)
        {
            bool everSeen;
            DateTime lastSeen;
            byte spd;
            byte globalFlags;
            var rawPins = new byte[NUM_ENCODERS];
            var edges = new uint[NUM_ENCODERS];
            var invalid = new uint[NUM_ENCODERS];
            var detents = new uint[NUM_ENCODERS];

            lock (_lock)
            {
                lastSeen = _diagLastSeenUtc;
                everSeen = _diagLastSeenUtc != DateTime.MinValue;
                spd = _diagStepsPerDetent;
                globalFlags = _deviceConfig.GlobalFlags;
                Array.Copy(_diagRawPins, rawPins, NUM_ENCODERS);
                Array.Copy(_diagEdgeCount, edges, NUM_ENCODERS);
                Array.Copy(_diagInvalidCount, invalid, NUM_ENCODERS);
                Array.Copy(_diagDetentCount, detents, NUM_ENCODERS);
            }

            // Only clear on a layout change; otherwise redraw in place to avoid flicker
            // while the user is counting detents.
            if (lastHadData != everSeen)
            {
                Console.Clear();
                lastHadData = everSeen;
            }
            Console.SetCursorPosition(0, 0);

            WriteLinePadded("Encoder Diagnostics");
            WriteLinePadded("===================");
            WriteLinePadded("");

            if (!everSeen)
            {
                WriteLinePadded("No diagnostic reports received (Input Report ID 0x04).");
                WriteLinePadded("");
                WriteLinePadded("  * This firmware build may predate report ID 0x04 - reflash the");
                WriteLinePadded("    .uf2 built from this branch.");
                WriteLinePadded("  * Or the keyboard-HID personality was flashed; rebuild with");
                WriteLinePadded("    cmake -DFIRMWARE_MODE=generic_hid ..");
                WriteLinePadded("");
                WriteLinePadded("Positions and config still work; only diagnostics are unavailable.");
                WriteLinePadded("");
                WriteLinePadded("");
                WriteLinePadded("");
            }
            else
            {
                double ageSec = (DateTime.UtcNow - lastSeen).TotalSeconds;
                string ageNote = ageSec > 2.0
                    ? $"STALE - last report {ageSec:F1}s ago (device hung or unplugged?)"
                    : $"updated {ageSec:F1}s ago";

                WriteLinePadded($"Firmware steps/detent: {spd}   (GlobalFlags bit 0 = {globalFlags & 0x01})   {ageNote}");
                WriteLinePadded("");
                WriteLinePadded("Enc    A  B  SW       Edges   Invalid   Detents   Edges/Detent");
                for (int i = 0; i < NUM_ENCODERS; i++)
                {
                    int a = (rawPins[i] >> 2) & 1;
                    int b = (rawPins[i] >> 1) & 1;
                    int sw = rawPins[i] & 1;
                    string ratio = detents[i] > 0
                        ? ((double)edges[i] / detents[i]).ToString("F2")
                        : "n/a";
                    WriteLinePadded($"  {i + 1}    {a}  {b}   {sw}   {edges[i],11}{invalid[i],10}{detents[i],10}{ratio,15}");
                }
                WriteLinePadded("");
                WriteLinePadded("Raw pins are sampled at 10 Hz: expect 7 (A=1 B=1 SW=1) at rest, even");
                WriteLinePadded("while spinning. Rotation shows up in the counters, not the pin bits.");
            }

            WriteLinePadded("");
            WriteLinePadded("[Z] Zero counters    [T] Toggle steps/detent (4<->2)");
            WriteLinePadded("[S] Save config to flash (persists the toggle)    [B] Back");

            // Poll for a keypress for ~400ms, then redraw.
            var deadline = DateTime.UtcNow.AddMilliseconds(400);
            while (DateTime.UtcNow < deadline)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    switch (char.ToUpper(key.KeyChar))
                    {
                        case 'Z':
                            SendCommand(CMD_RESET_DIAG);
                            Thread.Sleep(200);
                            break;
                        case 'T':
                            lock (_lock)
                            {
                                _deviceConfig.GlobalFlags ^= 0x01;
                                SendConfig(_deviceConfig);
                            }
                            Thread.Sleep(200);
                            SendCommand(CMD_READ_CONFIG);
                            Thread.Sleep(300);
                            // apply_config() does not reset the encoder's partial-step
                            // accumulator, so the first detent after a switch can land
                            // early or late by one. Zero the counters for a clean measurement.
                            SendCommand(CMD_RESET_DIAG);
                            Thread.Sleep(200);
                            break;
                        case 'S':
                            SendCommand(CMD_SAVE_CONFIG);
                            Thread.Sleep(500);
                            break;
                        case 'B':
                            return;
                    }
                    break;
                }
                Thread.Sleep(25);
            }
        }
    }
```

- [ ] **Step 4: Verify it compiles**

```bash
cd /d/prj/RotaryUsb/windows-example
dotnet build
```

Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Verify the empty state renders without hardware**

Run the app with no RotaryUsb device attached. It falls through to Keyboard HID mode, which confirms
the build runs; press Ctrl+C to exit. The diagnostics empty state itself is exercised in Task 10.

```bash
cd /d/prj/RotaryUsb/windows-example
dotnet run
```

Expected: reaches either the config menu (device attached) or "Keyboard HID Mode" (not attached),
with no unhandled exception.

- [ ] **Step 6: Commit**

```bash
cd /d/prj/RotaryUsb
git add windows-example/Program.cs
git commit -m "feat(windows-example): add [D] decoder diagnostics view

Renders per-encoder raw pins and edge/invalid/detent counters, with [Z] to
zero them and [T] to toggle GlobalFlags bit 0 (4<->2 steps per detent).
Degrades to an explicit 'no report ID 0x04' message rather than an empty
table, which would be indistinguishable from dead hardware.

'Reset to defaults' moves from [D] to [F] to free the mnemonic; the remap
also means a mis-pressed key opens a read-only screen instead of wiping config.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J"
```

---

## Task 8: Document the protocol and build changes in `firmware-cpp/README.md`

**Files:**
- Modify: `firmware-cpp/README.md:5-22` (mode table and mode sections)
- Modify: `firmware-cpp/README.md:316-326` (project structure)
- Modify: `firmware-cpp/README.md:332-350` (build instructions)
- Modify: `firmware-cpp/README.md:385-392` (protocol table)

**Interfaces:**
- Consumes: the wire format from Task 5 and the CMake option from Task 2
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Update the mode table and mode descriptions**

Replace:

```markdown
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
```

with:

```markdown
| Mode | `-DFIRMWARE_MODE=` | File | Description |
|------|--------------------|------|-------------|
| **Generic HID** | `generic_hid` (default) | `main_generic_hid.cpp` | Sends raw encoder data via vendor-defined HID |
| **Keyboard HID** | `keyboard` | `main.cpp` | Sends F1-F12 key events |

> **⚠️ The default changed.** A bare `cmake ..` used to build **Keyboard HID**. It now builds
> **Generic HID**. If you were relying on the old default, pass `-DFIRMWARE_MODE=keyboard`.
> The old `cp main_generic_hid.cpp main.cpp` step is gone — never copy files to switch modes.

## Choosing a Mode

### Generic HID Mode (Default)
- Device uses vendor-defined HID (Usage Page 0xFF00)
- Applications can read raw encoder position and button states directly
- Runtime configurable, with decoder diagnostics on Input Report ID 0x04
- Requires custom application code to read HID reports
- Build with `cmake -DFIRMWARE_MODE=generic_hid ..` (or just `cmake ..`)

### Keyboard HID Mode
- Device appears as a standard USB keyboard
- Encoder events trigger F1-F12 key presses
- Works immediately with any application that accepts keyboard input
- Build with `cmake -DFIRMWARE_MODE=keyboard ..`
```

- [ ] **Step 2: Update the project structure listing**

Replace:

```
firmware-cpp/
├── CMakeLists.txt      # Build configuration
├── main.cpp            # Main program and USB HID implementation
├── encoder.h           # Encoder class header
├── encoder.cpp         # Encoder class implementation
├── tusb_config.h       # TinyUSB configuration
└── README.md           # This file
```

with:

```
firmware-cpp/
├── CMakeLists.txt          # Build configuration; selects FIRMWARE_MODE
├── main_generic_hid.cpp    # Generic HID entry point (default)
├── main.cpp                # Keyboard HID entry point
├── encoder.h               # Encoder class header
├── encoder.cpp             # Encoder class implementation
├── tusb_config.h           # TinyUSB configuration
└── README.md               # This file
```

- [ ] **Step 3: Replace the file-copy build instructions**

Replace:

```markdown
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
```

with:

```markdown
### Building Generic HID Firmware

Generic HID is the default, so a plain build produces it:

```bash
cd firmware-cpp
mkdir -p build && cd build
cmake ..
make -j4
```

To build the Keyboard HID firmware instead:

```bash
cmake -DFIRMWARE_MODE=keyboard ..
make -j4
```

CMake caches `FIRMWARE_MODE`, so plain `make` keeps whatever mode the build directory was
configured with. Re-run `cmake` with the flag to switch. The configure step echoes the
selection, so check the build log if you are unsure what you flashed:

```
-- RotaryUsb firmware mode: generic_hid (main_generic_hid.cpp)
```
```

- [ ] **Step 4: Extend the protocol table and document report 0x04**

Replace:

```markdown
| Report | Direction | Size | Description |
|--------|-----------|------|-------------|
| Input ID 0x01 | Device → Host | 21 bytes | Absolute positions + buttons + tiers |
| Input ID 0x02 | Device → Host | 106 bytes | Config readback |
| Output ID 0x02 | Host → Device | 106 bytes | Full config write |
| Output ID 0x03 | Host → Device | 2 bytes | Commands |
```

with:

```markdown
| Report | Direction | Size | Description |
|--------|-----------|------|-------------|
| Input ID 0x01 | Device → Host | 21 bytes | Absolute positions + buttons + tiers |
| Input ID 0x02 | Device → Host | 106 bytes | Config readback |
| Input ID 0x04 | Device → Host | 56 bytes | Decoder diagnostics (10 Hz) |
| Output ID 0x02 | Host → Device | 106 bytes | Full config write |
| Output ID 0x03 | Host → Device | 2 bytes | Commands |

#### Commands (Output Report ID 0x03, byte 0)

| Code | Command | Effect |
|------|---------|--------|
| 0x01 | Save config | Write current config to flash |
| 0x02 | Reset defaults | Restore factory config and reset positions |
| 0x03 | Reset positions | Set every encoder position to its `min_value` |
| 0x04 | Read config | Trigger an Input Report ID 0x02 readback |
| 0x05 | Reset diagnostics | Zero all report ID 0x04 counters |

#### Decoder Diagnostics (Input Report ID 0x04) — 56 bytes

Sent at 10 Hz. Ships in the normal build; there is no separate diagnostic firmware.

| Offset | Type | Description |
|--------|------|-------------|
| 0-3 | uint8[4] | Raw pin state per encoder: `(A<<2)\|(B<<1)\|SW`, literal GPIO levels. Idle = 7 |
| 4 | uint8 | `steps_per_detent` the decoder is actively using (2 or 4) |
| 5-7 | uint8[3] | Reserved (0x00) |
| 8-23 | uint32[4] LE | Cumulative A/B state changes observed, per encoder |
| 24-39 | uint32[4] LE | Cumulative illegal quadrature transitions (a subset of the above) |
| 40-55 | uint32[4] LE | Cumulative detents emitted by the decoder |

Counters are monotonic across both rotation directions and are zeroed by command 0x05.
`detent_count` increments before position clamping, so it counts emitted detents even at
`min_value` or `max_value`.

**Interpreting the counters.** `edge_count / detent_count` only reports the firmware's own
threshold back and does not identify the encoder. To measure the encoder, zero the counters, turn
the knob a counted number of physical clicks, and divide: `edge_count / clicks` is 4 for a KY-040
class encoder and 2 for a bare-EC11 class one. Check `invalid_count` first — contact bounce inflates
`edge_count` and can make a 2-step encoder read as 4.

**Raw pins are sampled at 10 Hz** and encoders rest at a detent with both contacts open, so this
field reads 7 at rest even during a spin. It is for at-rest checks (idle 7, button press 6, stuck
values); rotation shows up in the counters.
```

- [ ] **Step 5: Verify no stale copy instructions remain**

```bash
cd /d/prj/RotaryUsb
grep -n "cp main_generic_hid.cpp\|rename this file to main.cpp\|Rename this file" firmware-cpp/README.md firmware-cpp/main_generic_hid.cpp
```

Expected: one hit, in `main_generic_hid.cpp`'s file header comment. Fix it — replace:

```cpp
 * BUILD INSTRUCTIONS:
 *   1. Rename this file to main.cpp (backup the original)
 *   2. Rebuild the firmware: cmake .. && make
 *   3. Flash the resulting .uf2 file to the Pico
 */
```

with:

```cpp
 * BUILD INSTRUCTIONS:
 *   1. cd firmware-cpp && mkdir -p build && cd build
 *   2. cmake ..            (generic_hid is the default; -DFIRMWARE_MODE=keyboard for main.cpp)
 *   3. make -j4
 *   4. Flash the resulting rotary_usb.uf2 to the Pico
 */
```

Also update the report list in that same header comment. Replace:

```cpp
 * HID REPORT FORMAT:
 *   Input Report ID 0x01 (21 bytes): Encoder positions + buttons + tiers
 *   Input Report ID 0x02 (106 bytes): Config readback
 *   Output Report ID 0x02 (106 bytes): Config write
 *   Output Report ID 0x03 (2 bytes): Device commands
```

with:

```cpp
 * HID REPORT FORMAT:
 *   Input Report ID 0x01 (21 bytes): Encoder positions + buttons + tiers
 *   Input Report ID 0x02 (106 bytes): Config readback
 *   Input Report ID 0x04 (56 bytes): Decoder diagnostics, 10 Hz
 *   Output Report ID 0x02 (106 bytes): Config write
 *   Output Report ID 0x03 (2 bytes): Device commands
```

- [ ] **Step 6: Rebuild to confirm the header edit did not break the file**

```bash
cd /d/prj/RotaryUsb/firmware-cpp/build
make -j4
```

Expected: clean build.

- [ ] **Step 7: Commit**

```bash
cd /d/prj/RotaryUsb
git add firmware-cpp/README.md firmware-cpp/main_generic_hid.cpp
git commit -m "docs(firmware-cpp): document FIRMWARE_MODE and report ID 0x04

Records that the default build personality changed from keyboard to
generic_hid, removes the file-copy build step, and documents the diagnostic
report layout including how to interpret the counters.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J"
```

---

## Task 9: Update the root `README.md`

**Files:**
- Modify: `README.md:22-33` (mode headings)
- Modify: `README.md:57-71` (C++ build instructions)

**Interfaces:**
- Consumes: the CMake option from Task 2
- Produces: nothing consumed by later tasks

- [ ] **Step 1: Swap which mode is labelled default**

Replace:

```markdown
#### Keyboard HID Mode (Default)
- Device appears as a standard USB keyboard
- Encoder events trigger F1-F12 key presses  
- Works immediately with any application that accepts keyboard input
- Keys are sent globally (all applications receive them)

#### Generic HID Mode (Advanced)
```

with:

```markdown
#### Keyboard HID Mode
- Device appears as a standard USB keyboard
- Encoder events trigger F1-F12 key presses  
- Works immediately with any application that accepts keyboard input
- Keys are sent globally (all applications receive them)

#### Generic HID Mode (Default for the C++ firmware)
```

- [ ] **Step 2: Replace the C++ build and mode-selection instructions**

Replace:

```markdown
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
```

with:

```markdown
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
```

- [ ] **Step 3: Verify no stale instructions remain**

```bash
cd /d/prj/RotaryUsb
grep -n "replace it with .main_generic_hid\|cp main_generic_hid" README.md
```

Expected: no output.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: record the C++ firmware default mode change

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J"
```

---

## Task 10: Verification and hardware Test Plan

**Files:** none modified — this task verifies Tasks 1–9.

**Interfaces:**
- Consumes: everything above
- Produces: the evidence that settles the steps-per-detent question

### Part A — Automated pre-flight

- [ ] **Step 1: Both firmware modes build from clean**

```bash
cd /d/prj/RotaryUsb/firmware-cpp
rm -rf build build-kb
mkdir build    && (cd build    && cmake .. && make -j4)
mkdir build-kb && (cd build-kb && cmake -DFIRMWARE_MODE=keyboard .. && make -j4)
```

Expected: both succeed. `build/rotary_usb.uf2` exists and is the one to flash.

Then remove the keyboard scratch directory — `.gitignore` covers `build/` but not `build-kb/`, so
leaving it behind pollutes `git status` when you open the PR:

```bash
rm -rf /d/prj/RotaryUsb/firmware-cpp/build-kb
git -C /d/prj/RotaryUsb status --porcelain
```

Expected: only the two new `docs/superpowers/` files as untracked (they are committed in Part C),
and nothing under `firmware-cpp/`.

- [ ] **Step 2: Report ID 0x01 never changed across the whole branch**

```bash
cd /d/prj/RotaryUsb
git diff main...HEAD -- firmware-cpp/main_generic_hid.cpp | grep -E "^-" | grep -E "positions\[|active_tiers|button_states|PositionReport"
```

Expected: exactly one removed line group — the `[DIAG]` block from the uncommitted patch. There
must be **no** removal of `current_report.positions[i] = encoders[i]->get_position();`,
`current_report.active_tiers = tier_byte;`, or the `PositionReport` struct definition.

- [ ] **Step 3: The host app builds**

```bash
cd /d/prj/RotaryUsb/windows-example
dotnet build
```

Expected: `Build succeeded`, 0 warnings related to this change, 0 errors.

- [ ] **Step 4: Flash**

Hold BOOTSEL, plug in the Pico, copy `firmware-cpp/build/rotary_usb.uf2` to the `RPI-RP2` drive.
The board reboots. On a Pico (non-W), the onboard LED should blink at 2 Hz — that is the heartbeat
from Task 3 and it means the main loop is running.

### Part B — Hardware UAT script

Run `dotnet run` from `windows-example/`. Expect "Generic HID device found!" and the config menu.
If it says "Falling back to Keyboard HID mode", you flashed the wrong personality — go back to
Part A Step 1.

Follow these in order. Each stage gates the next.

---

#### Stage 1 — The diagnostic path works at all

- [ ] Press `[D]`.

**Expected:** a table with four encoder rows. Every row should read `A=1 B=1 SW=1` at rest, and the
header should show `Firmware steps/detent: 4` and `updated 0.Xs ago`.

**If you instead see "No diagnostic reports received":** the running firmware predates report ID
0x04. Reflash from Part A. This is also the exact message a CircuitPython device produces, which is
correct — CircuitPython does not implement report 0x04.

- [ ] Press and hold encoder 1's button.

**Expected:** row 1's `SW` flips to `0` while held. This reproduces the observation the original
DIAG patch made, now on the non-destructive path.

- [ ] Any row not reading `A=1 B=1 SW=1` at rest points at that encoder's wiring — most commonly a
KY-040 `+` pin left floating (see the root README's "Why the KY-040 Plus Pin Must Be Connected").
Fix that before continuing; everything downstream assumes clean pins.

---

#### Stage 2 — Decoded positions are real again

This is the regression that started all of this.

- [ ] Press `[B]` to go back, then `[R]` (Reset positions), then `[M]` (Monitor).
- [ ] Turn encoder 1 **clockwise**, slowly.

**Expected:** `Enc1` counts up — 0, 1, 2, 3... (or advances every second click; that is Stage 4's
question, not a failure here).

**PASS criterion:** the value is a small counting number that moves monotonically with rotation.

**FAIL criterion:** the value sits at a constant `7`, or bounces among 1/3/5/7. That means a build
with the DIAG patch is still flashed.

> **Trap:** positions start clamped at the minimum. The factory default is `min=0, max=100,
> wrap=off`, and `[R]` sets each position to `min_value`. **Turning counter-clockwise from a fresh
> device does nothing** — the value is already at the floor. Turn clockwise.

- [ ] Repeat for encoders 2, 3, 4. Press each button and confirm the Buttons row shows `[X]`.

---

#### Stage 3 — Signal integrity gate

Do this **before** Stage 4, not after. Bounce inflates `edge_count`, and enough of it makes a 2-step
encoder read as though it were a 4-step one. An unchecked `invalid_count` can produce a confidently
wrong answer in Stage 4.

- [ ] `[B]`, then `[D]`, then `[Z]` to zero the counters. All three count columns go to 0.
- [ ] Turn encoder 1 slowly through about 10 clicks.
- [ ] Read the `Invalid` column for row 1.

| `Invalid` | Meaning | Action |
|---|---|---|
| `0` | Clean quadrature | Proceed to Stage 4 |
| 1–4 out of ~40 edges | Occasional contact bounce; normal for inexpensive encoders | Proceed, but prefer the `Edges` reading over `Detents` |
| More than ~10% of `Edges` | Contact bounce or a marginal connection | **Stop.** Fix hardware first |
| High on one row only | That encoder specifically | Check that encoder's wiring |

**If `Invalid` is high:** check the KY-040 `+` pin to Pico 3V3 (pin 36), GND continuity, that A/B
are not swapped with SW, and lead length. Re-run this stage after fixing. Do **not** flip
GlobalFlags to compensate — that treats a wiring fault as a decoder setting.

---

#### Stage 4 — Measure the true steps per detent

**The measurement that matters is edges per *physically counted click*.** The `Edges/Detent` column
shown in the view is a self-check on the firmware, not a measurement of the encoder — by
construction the decoder emits one detent per `steps_per_detent` valid edges, so that column reads
≈4 on a 4-step encoder *and* on a 2-step one. Use it only to confirm the decoder is consuming edges
as configured.

- [ ] Press `[Z]` to zero the counters.
- [ ] Turn encoder 1 **exactly 20 clicks clockwise, slowly** — about one click per second. Count the
tactile detents with your fingers, not the screen.
- [ ] Stop turning. Read row 1.

Divide by 20:

| `Edges` (for 20 clicks) | `Detents` | Encoder is | What it means | Action |
|---|---|---|---|---|
| **80** (4/click) | 19–20 | 4-step (KY-040 class) | Firmware default is correct | Nothing to change. If a symptom remains, it is not steps-per-detent — look at the host mapping or the encoder's `min`/`max`/`step_size` config |
| **40** (2/click) | 9–10 | 2-step (bare EC11 class) | `spd=4` needs two clicks per emitted detent — positions advance at **half** rate | Go to Stage 5 |
| **160** (8/click) | 39–40 | Two cycles per click | Not covered by the GlobalFlags bit | Leave `spd=4` and halve `step_size`; file a follow-up |
| **0** | 0 | No edges reaching the decoder | Wiring on that encoder | Other rows tell you whether it is global; re-check Stage 1 |
| Anything else | — | Bounce is inflating the count | Not a clean measurement | Return to Stage 3 |

Notes on tolerance:

- `Edges` should be **exact**. It has no accumulator slack, so a clean encoder gives exactly 80 or
  exactly 40 for 20 clicks. Any drift from a multiple of 20 means bounce.
- `Detents` may read **one low**. Starting mid-detent leaves up to `steps_per_detent - 1` steps
  unconsumed, so 19 instead of 20 (or 9 instead of 10) is expected, not a fault.
- Note the direction of the symptom: a 2-step encoder on `spd=4` advances at **half** rate. Double
  rate would be the opposite error — `spd=2` on a 4-step encoder.

- [ ] Repeat for encoders 2, 3 and 4. Mixed encoder models across the four positions would show up
here as different `Edges` values, and the GlobalFlags bit is global — flag that as a follow-up if it
happens.

---

#### Stage 5 — Only if Stage 4 measured 2 edges per click

- [ ] Press `[T]`.

**Expected:** the header changes to `Firmware steps/detent: 2   (GlobalFlags bit 0 = 1)` and the
counters zero themselves (the toggle issues a reset because a partial detent can survive the switch).

If the header still reads 4, the config write was rejected — check the device's UART log for
"Config rejected: validation failed".

- [ ] Turn encoder 1 **exactly 20 clicks clockwise** again.

**Expected:** `Edges` = 40 (unchanged — the physical encoder did not change), `Detents` = 19–20.
`Detents` matching the click count is the confirmation.

- [ ] Press `[B]`, `[R]` (reset positions), `[M]`, and turn clockwise.

**Expected:** the position now advances one step per click.

- [ ] Press `[B]`, `[D]`, then `[S]` to persist to flash.

> **Trap:** `[T]` applies immediately but does **not** persist. Without `[S]`, the setting is lost
> on the next replug and the symptom returns.

- [ ] Unplug and replug the device. Re-open `[D]`.

**Expected:** `Firmware steps/detent: 2` survives the power cycle. That closes the loop.

---

### Part C — Ship it

- [ ] **Step 1: Record the Stage 4 measurement in the PR body.** The numbers are the deliverable —
whichever row of the Stage 4 table matched, and the raw `Edges` / `Invalid` / `Detents` values for
all four encoders.

- [ ] **Step 2: Push and open the PR**

```bash
cd /d/prj/RotaryUsb
git push -u origin feat/decoder-diagnostics
gh pr create --title "Decoder diagnostics via HID report ID 0x04" --body "$(cat <<'EOF'
Restores real decoded positions to HID Input Report ID 0x01 and adds a
non-destructive diagnostic Input Report ID 0x04, so the quadrature decoder can be
observed and the true steps-per-detent of the installed encoders measured rather
than guessed.

Replaces the uncommitted `[DIAG]` patch that overwrote `positions[]` with raw GPIO
pin state. That patch confirmed the wiring is good, but it did so by destroying the
output under test. Diagnostics now ship in the normal build, so that drift cannot
recur.

## Changes

- `FIRMWARE_MODE` CMake option replaces the `cp main_generic_hid.cpp main.cpp` step
  and the uncommitted `add_executable` edit. **Default changed to `generic_hid`.**
- Report ID 0x01 restored, byte-identical layout.
- New Input Report ID 0x04 (56 bytes): per-encoder raw pins, active
  `steps_per_detent`, and cumulative edge / invalid / detent counters at 10 Hz.
- New command 0x05 zeroes the counters without a replug.
- Host `[D] Diagnostics` view; `[T]` toggles `GlobalFlags` bit 0 (4↔2 steps per
  detent), a firmware capability that shipped in March but had no host control.
  "Reset to defaults" moves from `[D]` to `[F]`.
- Preserves the LED heartbeat and two stranded `Program.cs` fixes (overlapped
  `OpenDevice`, `MonitorDeviceEvents` guard) as their own commits.

No decoder behavior changes. `steps_per_detent` default stays 4.

## Docs Impact

- `README.md` — C++ default mode changed, with an explicit callout
- `firmware-cpp/README.md` — same, plus report ID 0x04 layout and the command table
- Spec and plan (`docs/superpowers/{specs,plans}/2026-08-16-decoder-diagnostics*.md`) landed on
  `main` separately as preparatory docs; they are not part of this diff

## Test Plan

Executed the hardware UAT in Task 10 Part B. Measurement results:

<!-- paste the Stage 4 numbers here -->

## Out of scope

- CircuitPython (`firmware/`) does not implement report ID 0x04. The host degrades
  with an explicit message. Follow-up.
- `tools/encoder-monitor/` untouched.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01L64bE1rsZ6rShmUQNUkj2J
EOF
)"
```

---

## Follow-ups (do not do in this PR)

1. **CircuitPython parity** — add report ID 0x04 to `firmware/boot.py` and
   `firmware/code_generic_hid.py` so both firmware personalities expose the same diagnostics.
2. **Per-encoder steps-per-detent** — `global_flags` bit 0 is global. If Stage 4 measures different
   values across the four positions, the config format needs a per-encoder field.
3. **`tools/encoder-monitor/`** — could render report 0x04 as well; currently duplicates report
   parsing with `windows-example`.
