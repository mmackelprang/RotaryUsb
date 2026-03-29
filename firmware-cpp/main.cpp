// SPDX-FileCopyrightText: 2024 RotaryUsb Project
// SPDX-License-Identifier: Apache-2.0

/**
 * @file main.cpp
 * @brief C++ firmware for Raspberry Pi Pico rotary encoder USB HID device
 * 
 * This firmware reads 4 rotary encoders with push buttons and sends
 * USB HID keyboard events (F1-F12 keys) to the host PC.
 * 
 * More performant than CircuitPython version due to:
 * - Native C++ with direct hardware access
 * - No interpreter overhead
 * - Optimized polling loop
 * - Lower latency USB HID responses
 */

#include <cstdio>
#include <cstring>
#include "pico/stdlib.h"
#include "pico/bootrom.h"
#include "tusb.h"
#include "encoder.h"

// ============================================================================
// USB HID CONFIGURATION
// ============================================================================

// HID Report Descriptor for a standard keyboard
static const uint8_t hid_report_descriptor[] = {
    0x05, 0x01,        // Usage Page (Generic Desktop)
    0x09, 0x06,        // Usage (Keyboard)
    0xA1, 0x01,        // Collection (Application)
    0x05, 0x07,        //   Usage Page (Key Codes)
    0x19, 0xE0,        //   Usage Minimum (224)
    0x29, 0xE7,        //   Usage Maximum (231)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x01,        //   Logical Maximum (1)
    0x75, 0x01,        //   Report Size (1)
    0x95, 0x08,        //   Report Count (8)
    0x81, 0x02,        //   Input (Data, Variable, Absolute) - Modifier byte
    0x95, 0x01,        //   Report Count (1)
    0x75, 0x08,        //   Report Size (8)
    0x81, 0x01,        //   Input (Constant) - Reserved byte
    0x95, 0x06,        //   Report Count (6)
    0x75, 0x08,        //   Report Size (8)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x65,        //   Logical Maximum (101)
    0x05, 0x07,        //   Usage Page (Key Codes)
    0x19, 0x00,        //   Usage Minimum (0)
    0x29, 0x65,        //   Usage Maximum (101)
    0x81, 0x00,        //   Input (Data, Array) - Key array (6 keys)
    0xC0               // End Collection
};

// HID keycodes (HID_KEY_F1 through HID_KEY_F12) are provided by TinyUSB's
// hid.h header — no local definitions needed.

// ============================================================================
// ENCODER CONFIGURATION
// ============================================================================

// GPIO Pin mapping for 4 encoders
// Encoder 1: A=GP2, B=GP3, SW=GP4
// Encoder 2: A=GP5, B=GP6, SW=GP7
// Encoder 3: A=GP8, B=GP9, SW=GP10
// Encoder 4: A=GP11, B=GP12, SW=GP13

struct EncoderConfig {
    uint8_t pin_a;
    uint8_t pin_b;
    uint8_t pin_sw;
    uint8_t key_cw;
    uint8_t key_ccw;
    uint8_t key_btn;
};

static const EncoderConfig ENCODER_CONFIGS[4] = {
    { 2,  3,  4, HID_KEY_F1, HID_KEY_F2, HID_KEY_F9  },  // Encoder 1
    { 5,  6,  7, HID_KEY_F3, HID_KEY_F4, HID_KEY_F10 },  // Encoder 2
    { 8,  9, 10, HID_KEY_F5, HID_KEY_F6, HID_KEY_F11 },  // Encoder 3
    {11, 12, 13, HID_KEY_F7, HID_KEY_F8, HID_KEY_F12 },  // Encoder 4
};

// Number of encoders
constexpr size_t NUM_ENCODERS = 4;

// Number of quadrature state changes per physical detent.
// KY-040 modules (full-cycle): 4
// Many bare EC11 encoders (half-cycle): 2
static constexpr int8_t STEPS_PER_DETENT = 4;

// Set true to swap CW/CCW direction for all encoders.
// Useful if your encoder's A/B pins are wired in reverse.
static constexpr bool REVERSE_DIRECTION = false;

// Encoder instances (allocated in main)
static Encoder* encoders[NUM_ENCODERS];

// ============================================================================
// USB HID KEYBOARD REPORT
// ============================================================================

// Standard 8-byte keyboard report
struct KeyboardReport {
    uint8_t modifier;     // Modifier keys (Ctrl, Shift, Alt, GUI)
    uint8_t reserved;     // Reserved byte
    uint8_t keycodes[6];  // Up to 6 simultaneous keys
};

static KeyboardReport keyboard_report;

// Ring buffer for queued key events (prevents dropped keys during rapid rotation)
static constexpr size_t KEY_QUEUE_SIZE = 8;
static uint8_t key_queue[KEY_QUEUE_SIZE];
static size_t key_queue_head = 0;  // Next write position
static size_t key_queue_tail = 0;  // Next read position

static bool key_queue_empty() {
    return key_queue_head == key_queue_tail;
}

static bool key_queue_full() {
    return ((key_queue_head + 1) % KEY_QUEUE_SIZE) == key_queue_tail;
}

/**
 * @brief Queue a keyboard key press for sending
 */
static void send_key(uint8_t keycode) {
    if (keycode == 0) return;
    if (key_queue_full()) return;  // Queue full — drop incoming key

    key_queue[key_queue_head] = keycode;
    key_queue_head = (key_queue_head + 1) % KEY_QUEUE_SIZE;
}

static uint8_t key_queue_peek() {
    return key_queue[key_queue_tail];
}

static void key_queue_pop() {
    key_queue_tail = (key_queue_tail + 1) % KEY_QUEUE_SIZE;
}

// ============================================================================
// TINYUSB CALLBACKS (extern "C" required — TinyUSB is compiled as C)
// ============================================================================
extern "C" {

// Device descriptor
static const tusb_desc_device_t device_descriptor = {
    .bLength            = sizeof(tusb_desc_device_t),
    .bDescriptorType    = TUSB_DESC_DEVICE,
    .bcdUSB             = 0x0200,
    .bDeviceClass       = 0x00,
    .bDeviceSubClass    = 0x00,
    .bDeviceProtocol    = 0x00,
    .bMaxPacketSize0    = CFG_TUD_ENDPOINT0_SIZE,
    // Placeholder VID/PID for development - for commercial products,
    // obtain official IDs from USB-IF or use Raspberry Pi Foundation's VID
    .idVendor           = 0xCAFE,
    .idProduct          = 0x4004,
    .bcdDevice          = 0x0100,
    .iManufacturer      = 0x01,
    .iProduct           = 0x02,
    .iSerialNumber      = 0x03,
    .bNumConfigurations = 0x01
};

// HID interface descriptor
enum {
    ITF_NUM_HID,
    ITF_NUM_TOTAL
};

#define CONFIG_TOTAL_LEN  (TUD_CONFIG_DESC_LEN + TUD_HID_DESC_LEN)
#define EPNUM_HID   0x81

static const uint8_t configuration_descriptor[] = {
    // Configuration descriptor
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL, 0, CONFIG_TOTAL_LEN, TUSB_DESC_CONFIG_ATT_REMOTE_WAKEUP, 100),
    // HID descriptor
    // EP max packet size must be <= 64 for full-speed USB (RP2040)
    TUD_HID_DESCRIPTOR(ITF_NUM_HID, 0, HID_ITF_PROTOCOL_KEYBOARD, sizeof(hid_report_descriptor), EPNUM_HID, 16, 10)
};

// String descriptors
static const char* string_descriptors[] = {
    (const char[]) { 0x09, 0x04 },  // 0: Language (English)
    "RotaryUsb",                     // 1: Manufacturer
    "Rotary Encoder HID",            // 2: Product
    "123456",                        // 3: Serial
};

// Invoked when received GET DEVICE DESCRIPTOR
const uint8_t* tud_descriptor_device_cb(void) {
    return (const uint8_t*)&device_descriptor;
}

// Invoked when received GET CONFIGURATION DESCRIPTOR
const uint8_t* tud_descriptor_configuration_cb(uint8_t index) {
    (void)index;
    return configuration_descriptor;
}

// Invoked when received GET HID REPORT DESCRIPTOR
const uint8_t* tud_hid_descriptor_report_cb(uint8_t instance) {
    (void)instance;
    return hid_report_descriptor;
}

// Invoked when received GET STRING DESCRIPTOR
const uint16_t* tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
    (void)langid;
    static uint16_t str_desc[32];
    uint8_t chr_count;

    if (index == 0) {
        memcpy(&str_desc[1], string_descriptors[0], 2);
        chr_count = 1;
    } else {
        if (index >= sizeof(string_descriptors) / sizeof(string_descriptors[0])) {
            return NULL;
        }

        const char* str = string_descriptors[index];
        chr_count = strlen(str);
        if (chr_count > 31) chr_count = 31;

        for (uint8_t i = 0; i < chr_count; i++) {
            str_desc[1 + i] = str[i];
        }
    }

    str_desc[0] = (TUSB_DESC_STRING << 8) | (2 * chr_count + 2);
    return str_desc;
}

// Invoked when received SET_REPORT control request or
// received data on OUT endpoint ( Report ID = 0, Type = 0 )
void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id,
                           hid_report_type_t report_type, uint8_t const* buffer,
                           uint16_t bufsize) {
    (void)instance;
    (void)report_id;
    (void)report_type;
    (void)buffer;
    (void)bufsize;
    // Handle LED status from host if needed
}

// Invoked when received GET_REPORT control request
uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id,
                                hid_report_type_t report_type, uint8_t* buffer,
                                uint16_t reqlen) {
    (void)instance;
    (void)report_id;
    (void)report_type;
    (void)buffer;
    (void)reqlen;
    return 0;
}

} // extern "C"

// ============================================================================
// HID TASK
// ============================================================================

static enum {
    HID_STATE_IDLE,
    HID_STATE_KEY_DOWN,
    HID_STATE_KEY_UP
} hid_state = HID_STATE_IDLE;

static void hid_task() {
    // Poll rate (ms)
    const uint32_t interval_ms = 10;
    static uint32_t start_ms = 0;

    if (to_ms_since_boot(get_absolute_time()) - start_ms < interval_ms) return;
    start_ms += interval_ms;

    if (!tud_hid_ready()) return;

    switch (hid_state) {
        case HID_STATE_IDLE:
            if (!key_queue_empty()) {
                // Send key press from front of queue
                memset(&keyboard_report, 0, sizeof(keyboard_report));
                keyboard_report.keycodes[0] = key_queue_peek();
                tud_hid_keyboard_report(0, 0, keyboard_report.keycodes);
                hid_state = HID_STATE_KEY_DOWN;
            }
            break;

        case HID_STATE_KEY_DOWN:
            // Send key release and consume from queue
            memset(&keyboard_report, 0, sizeof(keyboard_report));
            tud_hid_keyboard_report(0, 0, keyboard_report.keycodes);
            key_queue_pop();
            hid_state = HID_STATE_KEY_UP;
            break;

        case HID_STATE_KEY_UP:
            hid_state = HID_STATE_IDLE;
            break;
    }
}

// ============================================================================
// MAIN
// ============================================================================

int main() {
    // Initialize stdio for debug output via UART
    stdio_init_all();

    printf("\n");
    printf("========================================\n");
    printf("RotaryUsb C++ Firmware Starting...\n");
    printf("========================================\n");

    // Initialize TinyUSB
    tusb_init();
    printf("USB HID initialized\n");

    // Create and initialize encoders
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        const auto& cfg = ENCODER_CONFIGS[i];
        encoders[i] = new Encoder(
            cfg.pin_a, cfg.pin_b, cfg.pin_sw,
            cfg.key_cw, cfg.key_ccw, cfg.key_btn,
            i + 1, STEPS_PER_DETENT, REVERSE_DIRECTION
        );
        encoders[i]->init();
    }

    printf("All encoders initialized. Starting main loop...\n");
    printf("----------------------------------------\n");

    // Main loop
    while (true) {
        // Process USB tasks
        tud_task();

        // Process HID reports
        hid_task();

        // Update all encoders
        for (size_t i = 0; i < NUM_ENCODERS; i++) {
            uint8_t keycode;
            if (encoders[i]->update(keycode)) {
                send_key(keycode);
            }
        }
    }

    return 0;
}
