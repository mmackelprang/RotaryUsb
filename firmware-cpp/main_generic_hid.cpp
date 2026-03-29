// SPDX-FileCopyrightText: 2024 RotaryUsb Project
// SPDX-License-Identifier: Apache-2.0

/**
 * @file main_generic_hid.cpp
 * @brief C++ firmware for Raspberry Pi Pico - Generic HID Mode
 *        Runtime Configuration with Absolute Position Tracking
 *
 * Reads 4 rotary encoders with push buttons and sends absolute position data
 * via USB HID reports. Supports runtime configuration from the host via
 * Output Reports, with flash persistence.
 *
 * HID REPORT FORMAT:
 *   Input Report ID 0x01 (21 bytes): Encoder positions + buttons + tiers
 *   Input Report ID 0x02 (106 bytes): Config readback
 *   Output Report ID 0x02 (106 bytes): Config write
 *   Output Report ID 0x03 (2 bytes): Device commands
 *
 * BUILD INSTRUCTIONS:
 *   1. Rename this file to main.cpp (backup the original)
 *   2. Rebuild the firmware: cmake .. && make
 *   3. Flash the resulting .uf2 file to the Pico
 */

#include <cstdio>
#include <cstring>
#include <cstdint>
#include "pico/stdlib.h"
#include "pico/bootrom.h"
#include "hardware/flash.h"
#include "hardware/sync.h"
#include "tusb.h"
#include "encoder.h"

// ============================================================================
// CONFIG DATA STRUCTURES
// ============================================================================

static constexpr uint8_t CONFIG_VERSION = 0x01;
static constexpr size_t NUM_ENCODERS = 4;
static constexpr size_t NUM_TIERS = 3;
static constexpr size_t FULL_CONFIG_SIZE = 106;

struct TierConfig {
    uint16_t threshold_ms;
    uint16_t multiplier;
} __attribute__((packed));

struct EncoderConfig {
    int32_t min_value;
    int32_t max_value;
    int32_t step_size;
    uint8_t wrap;
    uint8_t reverse;
    TierConfig tiers[NUM_TIERS];
} __attribute__((packed));

struct DeviceConfig {
    uint8_t version;
    uint8_t global_flags;
    EncoderConfig encoders[NUM_ENCODERS];
} __attribute__((packed));

static_assert(sizeof(TierConfig) == 4, "TierConfig must be 4 bytes");
static_assert(sizeof(EncoderConfig) == 26, "EncoderConfig must be 26 bytes");
static_assert(sizeof(DeviceConfig) == FULL_CONFIG_SIZE, "DeviceConfig must be 106 bytes");

// Input Report: absolute positions + buttons + tiers
struct PositionReport {
    int32_t positions[NUM_ENCODERS];
    uint8_t button_states;
    uint8_t active_tiers;  // packed 2-bit fields
    uint8_t reserved[3];
} __attribute__((packed));

static_assert(sizeof(PositionReport) == 21, "PositionReport must be 21 bytes");

// Command codes (Output Report ID 0x03)
static constexpr uint8_t CMD_SAVE_CONFIG     = 0x01;
static constexpr uint8_t CMD_RESET_DEFAULTS  = 0x02;
static constexpr uint8_t CMD_RESET_POSITIONS = 0x03;
static constexpr uint8_t CMD_READ_CONFIG     = 0x04;

// ============================================================================
// FACTORY DEFAULTS
// ============================================================================

static DeviceConfig factory_default_config() {
    DeviceConfig cfg;
    cfg.version = CONFIG_VERSION;
    cfg.global_flags = 0;
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        cfg.encoders[i].min_value = 0;
        cfg.encoders[i].max_value = 100;
        cfg.encoders[i].step_size = 1;
        cfg.encoders[i].wrap = 0;
        cfg.encoders[i].reverse = 0;
        cfg.encoders[i].tiers[0] = {150, 5};
        cfg.encoders[i].tiers[1] = {80, 15};
        cfg.encoders[i].tiers[2] = {40, 50};
    }
    return cfg;
}

// ============================================================================
// CONFIG VALIDATION
// ============================================================================

static bool validate_encoder_config(const EncoderConfig& enc) {
    if (enc.min_value >= enc.max_value) return false;
    if (enc.step_size <= 0) return false;

    // Collect enabled tiers and validate ordering
    uint16_t prev_threshold = 0;
    uint16_t prev_multiplier = 0;
    bool has_prev = false;

    for (size_t i = 0; i < NUM_TIERS; i++) {
        if (enc.tiers[i].threshold_ms > 0) {
            if (enc.tiers[i].multiplier == 0) return false;
            if (has_prev) {
                if (enc.tiers[i].threshold_ms >= prev_threshold) return false;
                if (enc.tiers[i].multiplier <= prev_multiplier) return false;
            }
            prev_threshold = enc.tiers[i].threshold_ms;
            prev_multiplier = enc.tiers[i].multiplier;
            has_prev = true;
        }
    }
    return true;
}

static bool validate_config(const DeviceConfig& cfg) {
    if (cfg.version != CONFIG_VERSION) return false;
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        if (!validate_encoder_config(cfg.encoders[i])) return false;
    }
    return true;
}

// ============================================================================
// ACCELERATION AND POSITION LOGIC
// ============================================================================

struct TierResult {
    uint8_t tier_index;  // 0 = normal, 1-3 = acceleration tier
    uint16_t multiplier;
};

static TierResult select_acceleration_tier(uint32_t interval_ms, const TierConfig tiers[NUM_TIERS]) {
    // Check from fastest (tier 3) to slowest (tier 1)
    for (int i = NUM_TIERS - 1; i >= 0; i--) {
        if (tiers[i].threshold_ms > 0 && interval_ms < tiers[i].threshold_ms) {
            return {static_cast<uint8_t>(i + 1), tiers[i].multiplier};
        }
    }
    return {0, 1};
}

static int32_t compute_effective_step(int32_t step_size, uint16_t multiplier) {
    int64_t result = (int64_t)step_size * (int64_t)multiplier;
    if (result > INT32_MAX) return INT32_MAX;
    if (result < INT32_MIN) return INT32_MIN;
    return (int32_t)result;
}

static int32_t clamp_position(int32_t position, int32_t min_value, int32_t max_value, bool wrap) {
    if (!wrap) {
        if (position < min_value) return min_value;
        if (position > max_value) return max_value;
        return position;
    }

    int64_t range = (int64_t)max_value - (int64_t)min_value + 1;
    if (position > max_value) {
        int64_t offset = (int64_t)position - (int64_t)min_value;
        position = (int32_t)(min_value + (offset % range));
    } else if (position < min_value) {
        int64_t offset = (int64_t)min_value - 1 - (int64_t)position;
        position = (int32_t)(max_value - (offset % range));
    }
    return position;
}

// ============================================================================
// FLASH PERSISTENCE
// ============================================================================

// Flash storage at last sector
#define FLASH_CONFIG_OFFSET (PICO_FLASH_SIZE_BYTES - FLASH_SECTOR_SIZE)
#define FLASH_MAGIC 0x52554342  // "RUCB"

struct FlashHeader {
    uint32_t magic;
    DeviceConfig config;
    uint16_t crc;
} __attribute__((packed));

static uint16_t crc16_ccitt(const uint8_t* data, size_t len) {
    uint16_t crc = 0xFFFF;
    for (size_t i = 0; i < len; i++) {
        crc ^= (uint16_t)data[i] << 8;
        for (int j = 0; j < 8; j++) {
            if (crc & 0x8000)
                crc = (crc << 1) ^ 0x1021;
            else
                crc <<= 1;
        }
    }
    return crc;
}

// Global device config
static DeviceConfig device_config;

static void load_config_from_flash() {
    const uint8_t* flash_addr = (const uint8_t*)(XIP_BASE + FLASH_CONFIG_OFFSET);
    const FlashHeader* header = (const FlashHeader*)flash_addr;
    if (header->magic == FLASH_MAGIC) {
        uint16_t computed_crc = crc16_ccitt(
            (const uint8_t*)&header->config, sizeof(DeviceConfig));
        if (computed_crc == header->crc && validate_config(header->config)) {
            memcpy(&device_config, &header->config, sizeof(DeviceConfig));
            printf("Config loaded from flash\n");
            return;
        }
    }
    device_config = factory_default_config();
    printf("Using factory default config\n");
}

static void save_config_to_flash() {
    FlashHeader header;
    header.magic = FLASH_MAGIC;
    memcpy(&header.config, &device_config, sizeof(DeviceConfig));
    header.crc = crc16_ccitt(
        (const uint8_t*)&header.config, sizeof(DeviceConfig));

    // Pad to FLASH_PAGE_SIZE (256 bytes)
    uint8_t page[FLASH_PAGE_SIZE] = {0};
    memcpy(page, &header, sizeof(FlashHeader));

    uint32_t ints = save_and_disable_interrupts();
    flash_range_erase(FLASH_CONFIG_OFFSET, FLASH_SECTOR_SIZE);
    flash_range_program(FLASH_CONFIG_OFFSET, page, FLASH_PAGE_SIZE);
    restore_interrupts(ints);
    printf("Config saved to flash\n");
}

// ============================================================================
// USB HID CONFIGURATION - GENERIC HID MODE
// ============================================================================

// Vendor-defined HID Report Descriptor with runtime config support
static const uint8_t hid_report_descriptor[] = {
    0x06, 0x00, 0xFF,  // Usage Page (Vendor Defined 0xFF00)
    0x09, 0x01,        // Usage (Vendor Usage 1)
    0xA1, 0x01,        // Collection (Application)

    // ---- Input Report ID 0x01: Encoder Positions (21 bytes) ----
    0x85, 0x01,        //   Report ID (1)

    // 4 encoder positions as 32-bit values (16 raw bytes)
    // Logical min/max are nominal; actual int32 values are parsed by the host
    // app from the raw vendor-defined bytes, not by the HID driver.
    0x09, 0x02,        //   Usage (Vendor Usage 2 - Encoder Positions)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x10,        //   Report Count (16 bytes = 4x int32)
    0x81, 0x02,        //   Input (Data, Variable, Absolute)

    // Button states (1 byte: bits 0-3 = buttons, bits 4-7 = padding)
    0x09, 0x03,        //   Usage (Vendor Usage 3 - Button Data)
    0x15, 0x00,        //   Logical Minimum (0)
    0x25, 0x01,        //   Logical Maximum (1)
    0x75, 0x01,        //   Report Size (1 bit)
    0x95, 0x04,        //   Report Count (4 buttons)
    0x81, 0x02,        //   Input (Data, Variable, Absolute)
    0x75, 0x01,        //   Report Size (1 bit)
    0x95, 0x04,        //   Report Count (4 padding bits)
    0x81, 0x03,        //   Input (Constant, Variable, Absolute)

    // Acceleration tier byte + 3 reserved bytes (4 bytes)
    0x09, 0x04,        //   Usage (Vendor Usage 4 - Tier + Reserved)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x04,        //   Report Count (4: tier byte + 3 reserved)
    0x81, 0x02,        //   Input (Data, Variable, Absolute)

    // ---- Input Report ID 0x02: Config Readback (106 bytes) ----
    0x85, 0x02,        //   Report ID (2)
    0x09, 0x05,        //   Usage (Vendor Usage 5 - Config Readback)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x6A,        //   Report Count (106 bytes)
    0x81, 0x02,        //   Input (Data, Variable, Absolute)

    // ---- Output Report ID 0x02: Config Write (106 bytes) ----
    0x09, 0x06,        //   Usage (Vendor Usage 6 - Config Write)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x6A,        //   Report Count (106 bytes)
    0x91, 0x02,        //   Output (Data, Variable, Absolute)

    // ---- Output Report ID 0x03: Commands (2 bytes) ----
    0x85, 0x03,        //   Report ID (3)
    0x09, 0x07,        //   Usage (Vendor Usage 7 - Commands)
    0x15, 0x00,        //   Logical Minimum (0)
    0x26, 0xFF, 0x00,  //   Logical Maximum (255)
    0x75, 0x08,        //   Report Size (8 bits)
    0x95, 0x02,        //   Report Count (2 bytes)
    0x91, 0x02,        //   Output (Data, Variable, Absolute)

    0xC0               // End Collection
};

// ============================================================================
// ENCODER CONFIGURATION
// ============================================================================

struct EncoderPinConfig {
    uint8_t pin_a;
    uint8_t pin_b;
    uint8_t pin_sw;
};

static const EncoderPinConfig ENCODER_PIN_CONFIGS[NUM_ENCODERS] = {
    { 2,  3,  4  },  // Encoder 1
    { 5,  6,  7  },  // Encoder 2
    { 8,  9,  10 },  // Encoder 3
    { 11, 12, 13 },  // Encoder 4
};

// ============================================================================
// GENERIC HID ENCODER CLASS (with absolute position tracking)
// ============================================================================

class GenericHidEncoder {
public:
    GenericHidEncoder(uint8_t pin_a, uint8_t pin_b, uint8_t pin_sw,
                      uint8_t encoder_id)
        : pin_a_(pin_a)
        , pin_b_(pin_b)
        , pin_sw_(pin_sw)
        , encoder_id_(encoder_id)
        , last_ab_state_(0)
        , steps_(0)
        , position_(0)
        , last_detent_time_us_(0)
        , active_tier_(0)
        , config_(nullptr)
        , steps_per_detent_(4)
        , last_button_state_(true)
        , button_pressed_(false)
        , debounce_start_(0)
        , debounce_active_(false)
    {}

    void init() {
        gpio_init(pin_a_);
        gpio_set_dir(pin_a_, GPIO_IN);
        gpio_pull_up(pin_a_);

        gpio_init(pin_b_);
        gpio_set_dir(pin_b_, GPIO_IN);
        gpio_pull_up(pin_b_);

        gpio_init(pin_sw_);
        gpio_set_dir(pin_sw_, GPIO_IN);
        gpio_pull_up(pin_sw_);

        last_ab_state_ = read_ab_state();
        last_button_state_ = gpio_get(pin_sw_);
        last_detent_time_us_ = time_us_32();

        printf("Encoder %d initialized: A=GP%d, B=GP%d, SW=GP%d\n",
               encoder_id_, pin_a_, pin_b_, pin_sw_);
    }

    void apply_config(EncoderConfig* config, int8_t steps_per_detent) {
        config_ = config;
        steps_per_detent_ = steps_per_detent;
    }

    void reset_position() {
        if (config_) {
            position_ = config_->min_value;
        } else {
            position_ = 0;
        }
        active_tier_ = 0;
    }

    int32_t get_position() const { return position_; }
    uint8_t get_active_tier() const { return active_tier_; }

    bool update() {
        if (!config_) return button_pressed_;

        // Process encoder rotation
        uint8_t current_ab_state = read_ab_state();

        if (current_ab_state != last_ab_state_) {
            uint8_t index = (last_ab_state_ << 2) | current_ab_state;
            int8_t direction = TRANSITION_TABLE[index];

            if (direction != 0) {
                if (config_->reverse) direction = -direction;
                steps_ += direction;

                if (steps_ >= steps_per_detent_ || steps_ <= -steps_per_detent_) {
                    int8_t detent_direction = (steps_ > 0) ? 1 : -1;
                    steps_ = 0;

                    // Compute acceleration
                    uint32_t now = time_us_32();
                    uint32_t interval_us = now - last_detent_time_us_;
                    uint32_t interval_ms = interval_us / 1000;
                    last_detent_time_us_ = now;

                    TierResult tier = select_acceleration_tier(interval_ms, config_->tiers);
                    active_tier_ = tier.tier_index;

                    int32_t effective_step = compute_effective_step(config_->step_size, tier.multiplier);

                    // Update position
                    int64_t new_pos = (int64_t)position_ + (int64_t)detent_direction * (int64_t)effective_step;
                    if (new_pos > INT32_MAX) new_pos = INT32_MAX;
                    if (new_pos < INT32_MIN) new_pos = INT32_MIN;

                    position_ = clamp_position((int32_t)new_pos, config_->min_value,
                                               config_->max_value, config_->wrap != 0);

                    printf("Enc%d: %s pos=%ld tier=%d\n", encoder_id_,
                           detent_direction > 0 ? "CW" : "CCW",
                           (long)position_, active_tier_);
                }
            } else {
                steps_ = 0;
            }

            last_ab_state_ = current_ab_state;
        }

        // Process button with first-edge-latch debounce
        bool current_button_state = gpio_get(pin_sw_);
        uint32_t current_time = time_us_32();

        if (current_button_state != last_button_state_) {
            if (!debounce_active_) {
                debounce_start_ = current_time;
                debounce_active_ = true;
            } else if ((current_time - debounce_start_) >= BUTTON_DEBOUNCE_US) {
                last_button_state_ = current_button_state;
                debounce_active_ = false;

                if (!current_button_state && !button_pressed_) {
                    button_pressed_ = true;
                } else if (current_button_state && button_pressed_) {
                    button_pressed_ = false;
                }
            }
        } else {
            debounce_active_ = false;
        }

        return button_pressed_;
    }

private:
    uint8_t read_ab_state() {
        uint8_t a_val = gpio_get(pin_a_) ? 0 : 1;
        uint8_t b_val = gpio_get(pin_b_) ? 0 : 1;
        return (a_val << 1) | b_val;
    }

    uint8_t pin_a_;
    uint8_t pin_b_;
    uint8_t pin_sw_;
    uint8_t encoder_id_;

    uint8_t last_ab_state_;
    int16_t steps_;  // int16 to prevent overflow under rapid bounce noise

    // Absolute position tracking
    int32_t position_;
    uint32_t last_detent_time_us_;
    uint8_t active_tier_;

    // Config pointer (points into device_config.encoders[])
    EncoderConfig* config_;
    int8_t steps_per_detent_;

    // Button state
    bool last_button_state_;
    bool button_pressed_;
    uint32_t debounce_start_;
    bool debounce_active_;

    static constexpr uint32_t BUTTON_DEBOUNCE_US = 20000;  // 20ms
    static const int8_t TRANSITION_TABLE[16];
};

const int8_t GenericHidEncoder::TRANSITION_TABLE[16] = {
     0, +1, -1,  0,
    -1,  0,  0, +1,
    +1,  0,  0, -1,
     0, -1, +1,  0
};

// Encoder instances
static GenericHidEncoder* encoders[NUM_ENCODERS];

// Report state
static PositionReport current_report;
static PositionReport last_report;
static bool pending_config_readback = false;
static bool pending_save = false;

// ============================================================================
// TINYUSB DESCRIPTORS AND CALLBACKS (extern "C" required — TinyUSB is compiled as C)
// ============================================================================
extern "C" {

static const tusb_desc_device_t device_descriptor = {
    .bLength            = sizeof(tusb_desc_device_t),
    .bDescriptorType    = TUSB_DESC_DEVICE,
    .bcdUSB             = 0x0200,
    .bDeviceClass       = 0x00,
    .bDeviceSubClass    = 0x00,
    .bDeviceProtocol    = 0x00,
    .bMaxPacketSize0    = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor           = 0xCAFE,
    .idProduct          = 0x4005,
    .bcdDevice          = 0x0100,
    .iManufacturer      = 0x01,
    .iProduct           = 0x02,
    .iSerialNumber      = 0x03,
    .bNumConfigurations = 0x01
};

enum {
    ITF_NUM_HID,
    ITF_NUM_TOTAL
};

// Use INOUT descriptor for bidirectional HID (Output Reports via interrupt EP)
#define CONFIG_TOTAL_LEN  (TUD_CONFIG_DESC_LEN + TUD_HID_INOUT_DESC_LEN)
#define EPNUM_HID_IN   0x81
#define EPNUM_HID_OUT  0x01

static const uint8_t configuration_descriptor[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL, 0, CONFIG_TOTAL_LEN,
                          TUSB_DESC_CONFIG_ATT_REMOTE_WAKEUP, 100),
    // EP max packet size must be <= 64 for full-speed USB (RP2040)
    TUD_HID_INOUT_DESCRIPTOR(ITF_NUM_HID, 0, HID_ITF_PROTOCOL_NONE,
                             sizeof(hid_report_descriptor),
                             EPNUM_HID_OUT, EPNUM_HID_IN,
                             64, 10)
};

static const char* string_descriptors[] = {
    (const char[]) { 0x09, 0x04 },  // 0: Language (English)
    "RotaryUsb",                     // 1: Manufacturer
    "Rotary Encoder Generic HID",    // 2: Product
    "123456",                        // 3: Serial
};

const uint8_t* tud_descriptor_device_cb(void) {
    return (const uint8_t*)&device_descriptor;
}

const uint8_t* tud_descriptor_configuration_cb(uint8_t index) {
    (void)index;
    return configuration_descriptor;
}

const uint8_t* tud_hid_descriptor_report_cb(uint8_t instance) {
    (void)instance;
    return hid_report_descriptor;
}

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

// Handle Output Reports from host
void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id,
                           hid_report_type_t report_type, uint8_t const* buffer,
                           uint16_t bufsize) {
    (void)instance;
    (void)report_type;

    if (report_id == 0x02 && bufsize >= FULL_CONFIG_SIZE) {
        // Config write
        DeviceConfig new_config;
        memcpy(&new_config, buffer, sizeof(DeviceConfig));
        if (validate_config(new_config)) {
            memcpy(&device_config, &new_config, sizeof(DeviceConfig));
            int8_t spd = (device_config.global_flags & 0x01) ? 2 : 4;
            for (size_t i = 0; i < NUM_ENCODERS; i++) {
                encoders[i]->apply_config(&device_config.encoders[i], spd);
            }
            printf("Config applied from host\n");
        } else {
            printf("Config rejected: validation failed\n");
        }
    } else if (report_id == 0x03 && bufsize >= 2) {
        // Command
        uint8_t command = buffer[0];
        if (command == CMD_SAVE_CONFIG) {
            pending_save = true;  // Defer to main loop — flash write requires non-interrupt context
        } else if (command == CMD_RESET_DEFAULTS) {
            device_config = factory_default_config();
            int8_t spd = (device_config.global_flags & 0x01) ? 2 : 4;
            for (size_t i = 0; i < NUM_ENCODERS; i++) {
                encoders[i]->apply_config(&device_config.encoders[i], spd);
                encoders[i]->reset_position();
            }
            printf("Reset to factory defaults\n");
        } else if (command == CMD_RESET_POSITIONS) {
            for (size_t i = 0; i < NUM_ENCODERS; i++) {
                encoders[i]->reset_position();
            }
            printf("All positions reset\n");
        } else if (command == CMD_READ_CONFIG) {
            pending_config_readback = true;
        }
    }
}

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
// HID TASKS
// ============================================================================

static uint8_t cached_button_states = 0;

static void encoder_poll_task() {
    uint8_t button_states = 0;
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        if (encoders[i]->update()) {
            button_states |= (1 << i);
        }
    }
    cached_button_states = button_states;
}

static void hid_task() {
    const uint32_t interval_ms = 10;
    static uint32_t start_ms = 0;

    if (to_ms_since_boot(get_absolute_time()) - start_ms < interval_ms) return;
    start_ms += interval_ms;

    if (!tud_hid_ready()) return;

    // Send config readback if requested
    if (pending_config_readback) {
        tud_hid_report(2, &device_config, sizeof(DeviceConfig));
        pending_config_readback = false;
        printf("Config readback sent\n");
        return;  // One report per interval
    }

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
}

// ============================================================================
// MAIN
// ============================================================================

int main() {
    stdio_init_all();

    printf("\n");
    printf("========================================\n");
    printf("RotaryUsb Generic HID (runtime config)\n");
    printf("========================================\n");

    // Load config from flash
    load_config_from_flash();
    int8_t steps_per_detent = (device_config.global_flags & 0x01) ? 2 : 4;

    // Initialize TinyUSB
    tusb_init();
    printf("USB Generic HID initialized\n");

    // Create and initialize encoders
    for (size_t i = 0; i < NUM_ENCODERS; i++) {
        const auto& pin_cfg = ENCODER_PIN_CONFIGS[i];
        encoders[i] = new GenericHidEncoder(
            pin_cfg.pin_a, pin_cfg.pin_b, pin_cfg.pin_sw, i + 1
        );
        encoders[i]->init();
        encoders[i]->apply_config(&device_config.encoders[i], steps_per_detent);
        encoders[i]->reset_position();
    }

    memset(&current_report, 0, sizeof(PositionReport));
    memset(&last_report, 0, sizeof(PositionReport));

    printf("All encoders initialized. Starting main loop...\n");
    printf("----------------------------------------\n");

    while (true) {
        tud_task();

        // Deferred flash write — must run outside USB interrupt context
        if (pending_save) {
            pending_save = false;
            save_config_to_flash();
        }

        encoder_poll_task();
        hid_task();
    }

    return 0;
}
