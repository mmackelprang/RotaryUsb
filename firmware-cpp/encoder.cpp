// SPDX-FileCopyrightText: 2024 RotaryUsb Project
// SPDX-License-Identifier: Apache-2.0

#include "encoder.h"
#include "pico/stdlib.h"
#include <cstdio>

// Quadrature state transition table
// Index: (prev_state << 2) | curr_state
// Values: +1 = CW, -1 = CCW, 0 = invalid/no change
const int8_t Encoder::TRANSITION_TABLE[16] = {
    0,  // 00 -> 00: no change
    +1, // 00 -> 01: CW
    -1, // 00 -> 10: CCW
    0,  // 00 -> 11: invalid (skip)
    -1, // 01 -> 00: CCW
    0,  // 01 -> 01: no change
    0,  // 01 -> 10: invalid (skip)
    +1, // 01 -> 11: CW
    +1, // 10 -> 00: CW
    0,  // 10 -> 01: invalid (skip)
    0,  // 10 -> 10: no change
    -1, // 10 -> 11: CCW
    0,  // 11 -> 00: invalid (skip)
    -1, // 11 -> 01: CCW
    +1, // 11 -> 10: CW
    0   // 11 -> 11: no change
};

Encoder::Encoder(uint8_t pin_a, uint8_t pin_b, uint8_t pin_sw,
                 uint8_t keycode_cw, uint8_t keycode_ccw, uint8_t keycode_btn,
                 uint8_t encoder_id)
    : pin_a_(pin_a)
    , pin_b_(pin_b)
    , pin_sw_(pin_sw)
    , keycode_cw_(keycode_cw)
    , keycode_ccw_(keycode_ccw)
    , keycode_btn_(keycode_btn)
    , last_ab_state_(0)
    , steps_(0)
    , last_button_state_(true)
    , button_pressed_(false)
    , last_button_time_(0)
    , encoder_id_(encoder_id)
{
}

void Encoder::init() {
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

uint8_t Encoder::read_ab_state() {
    // Pins are active-low, so invert the reading
    uint8_t a_val = gpio_get(pin_a_) ? 0 : 1;
    uint8_t b_val = gpio_get(pin_b_) ? 0 : 1;
    return (a_val << 1) | b_val;
}

bool Encoder::update(uint8_t& keycode) {
    keycode = 0;

    // Process encoder rotation
    uint8_t current_ab_state = read_ab_state();

    if (current_ab_state != last_ab_state_) {
        // Look up transition in table
        uint8_t index = (last_ab_state_ << 2) | current_ab_state;
        int8_t direction = TRANSITION_TABLE[index];

        if (direction != 0) {
            steps_ += direction;

            // Most encoders have 4 state changes per detent
            // Send key event when a full detent is completed
            if (steps_ >= 4) {
                keycode = keycode_cw_;
                steps_ = 0;
                printf("Encoder %d: CW -> Key 0x%02X\n", encoder_id_, keycode);
                last_ab_state_ = current_ab_state;
                return true;
            } else if (steps_ <= -4) {
                keycode = keycode_ccw_;
                steps_ = 0;
                printf("Encoder %d: CCW -> Key 0x%02X\n", encoder_id_, keycode);
                last_ab_state_ = current_ab_state;
                return true;
            }
        }

        last_ab_state_ = current_ab_state;
    }

    // Process button press with debounce
    bool current_button_state = gpio_get(pin_sw_);
    uint32_t current_time = time_us_32();

    // Button is active-low (pressed = false/low)
    if (current_button_state != last_button_state_) {
        if ((current_time - last_button_time_) >= BUTTON_DEBOUNCE_US) {
            last_button_time_ = current_time;
            last_button_state_ = current_button_state;

            // Detect falling edge (button press)
            if (!current_button_state && !button_pressed_) {
                button_pressed_ = true;
                keycode = keycode_btn_;
                printf("Encoder %d: BTN -> Key 0x%02X\n", encoder_id_, keycode);
                return true;
            }
            // Detect rising edge (button release)
            else if (current_button_state && button_pressed_) {
                button_pressed_ = false;
                printf("Encoder %d: Button released\n", encoder_id_);
            }
        }
    }

    return false;
}
