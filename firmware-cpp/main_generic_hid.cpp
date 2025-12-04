// SPDX-FileCopyrightText: 2024 RotaryUsb Project
// SPDX-License-Identifier: Apache-2.0

/**
 * @file main_generic_hid.cpp
 * @brief C++ firmware for Raspberry Pi Pico - Generic HID Mode
 * 
 * This firmware reads 4 rotary encoders with push buttons and sends
 * raw USB HID reports using a vendor-defined HID descriptor.
 * This allows applications to read encoder data directly without
 * keyboard event interception.
 * 
 * HID REPORT FORMAT (8 bytes total):
 *   Byte 0: Report ID (0x01)
 *   Byte 1: Encoder 1 movement (signed: -127 to +127, positive=CW)
 *   Byte 2: Encoder 2 movement (signed: -127 to +127, positive=CW)
 *   Byte 3: Encoder 3 movement (signed: -127 to +127, positive=CW)
 *   Byte 4: Encoder 4 movement (signed: -127 to +127, positive=CW)
 *   Byte 5: Button states (bit 0=Btn1, bit 1=Btn2, bit 2=Btn3, bit 3=Btn4)
 *   Byte 6: Reserved (0x00)
 *   Byte 7: Reserved (0x00)
 * 
 * BUILD INSTRUCTIONS:
 *   1. Rename this file to main.cpp (backup the original)
 *   2. Rebuild the firmware: cmake .. && make
 *   3. Flash the resulting .uf2 file to the Pico
 */

#include <cstdio>
#include <cstring>
#include "pico/stdlib.h"
#include "pico/bootrom.h"
#include "tusb.h"
#include "encoder.h"

// ============================================================================
// USB HID CONFIGURATION - GENERIC HID MODE
// ============================================================================

// Vendor-defined HID Report Descriptor
// Usage Page: 0xFF00 (Vendor Defined)
// Report Size: 8 bytes (1 byte report ID + 7 bytes data)
static const uint8_t hid_report_descriptor[] = {
    0x06, 0x00, 0xFF,  // Usage Page (Vendor Defined 0xFF00)
    0x09, 0x01,        // Usage (Vendor Usage 1)
    0xA1, 0x01,        // Collection (Application)
    0x85, 0x01,        //   Report ID (1)
    
    // Encoder values (4 signed bytes for relative movement)
    0x09, 0x02,        //   Usage (Vendor Usage 2 - Encoder Data)
    0x15, 0x81,        //   Logical Minimum (-127)
    0x25, 0x7F,        //   Logical Maximum (127)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x04,        //   Report Count (4 encoders)
    0x81, 0x06,        //   Input (Data, Variable, Relative)
    
    // Button states (1 byte with 4 button bits + 4 padding bits)
    0x09, 0x03,        //   Usage (Vendor Usage 3 - Button Data)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x01,        //   Logical Maximum (1)
    0x75, 0x01,        //   Report Size (1 bit)
    0x95, 0x04,        //   Report Count (4 buttons)
    0x81, 0x02,        //   Input (Data, Variable, Absolute)
    0x75, 0x01,        //   Report Size (1 bit)
    0x95, 0x04,        //   Report Count (4 padding bits)
    0x81, 0x03,        //   Input (Constant, Variable, Absolute) - Padding
    
    // Reserved bytes (2 bytes for future use)
    0x09, 0x04,        //   Usage (Vendor Usage 4 - Reserved)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x02,        //   Report Count (2 reserved bytes)
    0x81, 0x02,        //   Input (Data, Variable, Absolute)
    
    0xC0               // End Collection
};

// ============================================================================
// ENCODER CONFIGURATION
// ============================================================================

// GPIO Pin mapping for 4 encoders
// Encoder 1: A=GP2, B=GP3, SW=GP4
// Encoder 2: A=GP5, B=GP6, SW=GP7
// Encoder 3: A=GP8, B=GP9, SW=GP10
// Encoder 4: A=GP11, B=GP12, SW=GP13

struct EncoderPinConfig {
    uint8_t pin_a;
    uint8_t pin_b;
    uint8_t pin_sw;
};

static const EncoderPinConfig ENCODER_PIN_CONFIGS[4] = {
    { 2,  3,  4  },  // Encoder 1
    { 5,  6,  7  },  // Encoder 2
    { 8,  9,  10 },  // Encoder 3
    { 11, 12, 13 },  // Encoder 4
};

// Number of encoders
constexpr size_t NUM_ENCODERS = 4;

// ============================================================================
// GENERIC HID ENCODER CLASS
// ============================================================================

/**
 * @brief Encoder class modified for Generic HID mode
 * Accumulates movement instead of sending individual key events
 */
class GenericHidEncoder {
public:
    GenericHidEncoder(uint8_t pin_a, uint8_t pin_b, uint8_t pin_sw, uint8_t encoder_id)
        : pin_a_(pin_a)
        , pin_b_(pin_b)
        , pin_sw_(pin_sw)
        , encoder_id_(encoder_id)
        , last_ab_state_(0)
        , steps_(0)
        , accumulated_movement_(0)
        , last_button_state_(true)
        , button_pressed_(false)
        , last_button_time_(0)
    {}

    void init() {
        // Initialize pin A with pull-up
        gpio_init(pin_a_);
        gpio_set_dir(pin_a_, GPIO_IN);
        gpio_pull_up(pin_a_);

        // Initialize pin B with pull-up
        gpio_init(pin_b_);
        gpio_set_dir(pin_b_, GPIO_IN);
        gpio_pull_up(pin_b_);

        // Initialize button pin with pull-up
        gpio_init(pin_sw_);
        gpio_set_dir(pin_sw_, GPIO_IN);
        gpio_pull_up(pin_sw_);

        // Read initial state
        last_ab_state_ = read_ab_state();
        last_button_state_ = gpio_get(pin_sw_);

        printf("Encoder %d initialized: A=GP%d, B=GP%d, SW=GP%d\n",
               encoder_id_, pin_a_, pin_b_, pin_sw_);
    }

    /**
     * @brief Update encoder state
     * @return Current button pressed state
     */
    bool update() {
        // Process encoder rotation
        uint8_t current_ab_state = read_ab_state();

        if (current_ab_state != last_ab_state_) {
            uint8_t index = (last_ab_state_ << 2) | current_ab_state;
            int8_t direction = TRANSITION_TABLE[index];

            if (direction != 0) {
                steps_ += direction;

                // Most encoders have 4 state changes per detent
                if (steps_ >= 4) {
                    accumulated_movement_++;
                    steps_ = 0;
                    printf("Encoder %d: CW detent\n", encoder_id_);
                } else if (steps_ <= -4) {
                    accumulated_movement_--;
                    steps_ = 0;
                    printf("Encoder %d: CCW detent\n", encoder_id_);
                }
            }

            last_ab_state_ = current_ab_state;
        }

        // Process button press with debounce
        bool current_button_state = gpio_get(pin_sw_);
        uint32_t current_time = time_us_32();

        if (current_button_state != last_button_state_) {
            if ((current_time - last_button_time_) >= BUTTON_DEBOUNCE_US) {
                last_button_time_ = current_time;
                last_button_state_ = current_button_state;

                if (!current_button_state && !button_pressed_) {
                    button_pressed_ = true;
                    printf("Encoder %d: Button pressed\n", encoder_id_);
                } else if (current_button_state && button_pressed_) {
                    button_pressed_ = false;
                    printf("Encoder %d: Button released\n", encoder_id_);
                }
            }
        }

        return button_pressed_;
    }

    /**
     * @brief Get accumulated movement and reset counter
     * @return Signed movement value clamped to -127 to +127
     */
    int8_t get_and_clear_movement() {
        int8_t movement = accumulated_movement_;
        if (movement > 127) movement = 127;
        if (movement < -127) movement = -127;
        accumulated_movement_ = 0;
        return movement;
    }

private:
    uint8_t read_ab_state() {
        uint8_t a_val = gpio_get(pin_a_) ? 0 : 1;
        uint8_t b_val = gpio_get(pin_b_) ? 0 : 1;
        return (a_val << 1) | b_val;
    }

    // Pin assignments
    uint8_t pin_a_;
    uint8_t pin_b_;
    uint8_t pin_sw_;
    uint8_t encoder_id_;

    // Encoder state
    uint8_t last_ab_state_;
    int8_t steps_;
    int16_t accumulated_movement_;

    // Button state
    bool last_button_state_;
    bool button_pressed_;
    uint32_t last_button_time_;

    // Constants
    static constexpr uint32_t BUTTON_DEBOUNCE_US = 20000;  // 20ms

    // Quadrature state transition table
    static const int8_t TRANSITION_TABLE[16];
};

// Static transition table initialization
const int8_t GenericHidEncoder::TRANSITION_TABLE[16] = {
    0,  // 00 -> 00: no change
    +1, // 00 -> 01: CW
    -1, // 00 -> 10: CCW
    0,  // 00 -> 11: invalid
    -1, // 01 -> 00: CCW
    0,  // 01 -> 01: no change
    0,  // 01 -> 10: invalid
    +1, // 01 -> 11: CW
    +1, // 10 -> 00: CW
    0,  // 10 -> 01: invalid
    0,  // 10 -> 10: no change
    -1, // 10 -> 11: CCW
    0,  // 11 -> 00: invalid
    -1, // 11 -> 01: CCW
    +1, // 11 -> 10: CW
    0   // 11 -> 11: no change
};

// Encoder instances
static GenericHidEncoder* encoders[NUM_ENCODERS];

// ============================================================================
// GENERIC HID REPORT
// ============================================================================

// Report structure (matches HID descriptor)
struct GenericHidReport {
    int8_t encoder_movement[4];  // Signed movement for each encoder
    uint8_t button_states;       // Bit 0-3: buttons 1-4
    uint8_t reserved[2];         // Reserved for future use
};

static GenericHidReport current_report;
static GenericHidReport last_report;
static bool report_pending = false;

// ============================================================================
// TINYUSB CALLBACKS
// ============================================================================

// Device descriptor
static const tusb_desc_device_t device_descriptor = {
    .bLength            = sizeof(tusb_desc_device_t),
    .bDescriptorType    = TUSB_DESC_DEVICE,
    .bcdUSB             = 0x0200,
    .bDeviceClass       = 0x00,
    .bDeviceSubClass    = 0x00,
    .bDeviceProtocol    = 0x00,
    .bMaxPacketSize0    = CFG_TUD_ENDPOINT0_SIZE,
    // WARNING: Placeholder VID/PID for development only!
    // 0xCAFE is not an officially assigned Vendor ID and may conflict with other devices.
    // For production use, either:
    // 1. Obtain an official VID from USB-IF (https://www.usb.org/getting-vendor-id)
    // 2. Use Raspberry Pi Foundation's VID with a sub-licensed PID
    // 3. Use pid.codes for open source projects (https://pid.codes/)
    .idVendor           = 0xCAFE,
    .idProduct          = 0x4005,  // Different PID for Generic HID version
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
    // HID descriptor - use HID_ITF_PROTOCOL_NONE for vendor-defined
    TUD_HID_DESCRIPTOR(ITF_NUM_HID, 0, HID_ITF_PROTOCOL_NONE, sizeof(hid_report_descriptor), EPNUM_HID, CFG_TUD_HID_EP_BUFSIZE, 10)
};

// String descriptors
static const char* string_descriptors[] = {
    (const char[]) { 0x09, 0x04 },  // 0: Language (English)
    "RotaryUsb",                     // 1: Manufacturer
    "Rotary Encoder Generic HID",    // 2: Product
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

// Invoked when received SET_REPORT control request
void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id,
                           hid_report_type_t report_type, uint8_t const* buffer,
                           uint16_t bufsize) {
    (void)instance;
    (void)report_id;
    (void)report_type;
    (void)buffer;
    (void)bufsize;
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

// ============================================================================
// HID TASK
// ============================================================================

static void hid_task() {
    // Report interval (ms)
    const uint32_t interval_ms = 10;
    static uint32_t start_ms = 0;

    if (board_millis() - start_ms < interval_ms) return;
    start_ms += interval_ms;

    if (!tud_hid_ready()) return;

    // Build report
    uint8_t button_states = 0;
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        bool btn_pressed = encoders[i]->update();
        if (btn_pressed) {
            button_states |= (1 << i);
        }
    }

    // Get encoder movements
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        current_report.encoder_movement[i] = encoders[i]->get_and_clear_movement();
    }
    current_report.button_states = button_states;
    current_report.reserved[0] = 0;
    current_report.reserved[1] = 0;

    // Check if report has changed or has movement data
    bool has_movement = false;
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        if (current_report.encoder_movement[i] != 0) {
            has_movement = true;
            break;
        }
    }

    bool has_change = memcmp(&current_report, &last_report, sizeof(GenericHidReport)) != 0;

    if (has_movement || has_change) {
        // Send report with Report ID 1
        tud_hid_report(1, &current_report, sizeof(GenericHidReport));
        
        if (has_movement) {
            printf("Report: Enc[%d,%d,%d,%d] Btn=0x%02X\n",
                   current_report.encoder_movement[0],
                   current_report.encoder_movement[1],
                   current_report.encoder_movement[2],
                   current_report.encoder_movement[3],
                   current_report.button_states);
        }

        memcpy(&last_report, &current_report, sizeof(GenericHidReport));
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
    printf("RotaryUsb Generic HID Firmware Starting...\n");
    printf("========================================\n");

    // Initialize TinyUSB
    tusb_init();
    printf("USB Generic HID initialized\n");
    printf("Usage Page: 0xFF00, Usage: 0x01\n");

    // Create and initialize encoders
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        const auto& cfg = ENCODER_PIN_CONFIGS[i];
        encoders[i] = new GenericHidEncoder(
            cfg.pin_a, cfg.pin_b, cfg.pin_sw,
            i + 1
        );
        encoders[i]->init();
    }

    // Initialize report structures
    memset(&current_report, 0, sizeof(GenericHidReport));
    memset(&last_report, 0, sizeof(GenericHidReport));

    printf("All encoders initialized. Starting main loop...\n");
    printf("----------------------------------------\n");

    // Main loop
    while (true) {
        // Process USB tasks
        tud_task();

        // Process HID reports
        hid_task();
    }

    return 0;
}
