// SPDX-FileCopyrightText: 2024 RotaryUsb Project
// SPDX-License-Identifier: Apache-2.0

#ifndef ENCODER_H
#define ENCODER_H

#include <cstdint>

/**
 * @brief Encoder class for handling rotary encoders with push buttons
 * 
 * Uses quadrature decoding to detect rotation direction and
 * software debouncing for the push button.
 */
class Encoder {
public:
    /**
     * @brief Construct a new Encoder object
     *
     * @param pin_a GPIO pin for encoder A (CLK)
     * @param pin_b GPIO pin for encoder B (DT)
     * @param pin_sw GPIO pin for encoder push button (SW)
     * @param keycode_cw HID keycode to send on clockwise rotation
     * @param keycode_ccw HID keycode to send on counter-clockwise rotation
     * @param keycode_btn HID keycode to send on button press
     * @param encoder_id Identifier for debug output
     * @param steps_per_detent Quadrature state changes per physical detent (4 for KY-040, 2 for many bare EC11)
     * @param reverse_direction Swap CW/CCW if A/B pins are wired in reverse
     */
    Encoder(uint8_t pin_a, uint8_t pin_b, uint8_t pin_sw,
            uint8_t keycode_cw, uint8_t keycode_ccw, uint8_t keycode_btn,
            uint8_t encoder_id, int8_t steps_per_detent = 4,
            bool reverse_direction = false);

    /**
     * @brief Initialize GPIO pins
     */
    void init();

    /**
     * @brief Update encoder state and check for events
     * 
     * Should be called frequently in the main loop.
     * 
     * @param keycode Output parameter for keycode to send (0 if no event)
     * @return true if a key event should be sent
     */
    bool update(uint8_t& keycode);

private:
    uint8_t read_ab_state();

    // Pin assignments
    uint8_t pin_a_;
    uint8_t pin_b_;
    uint8_t pin_sw_;

    // Key mappings
    uint8_t keycode_cw_;
    uint8_t keycode_ccw_;
    uint8_t keycode_btn_;

    // Encoder state
    uint8_t last_ab_state_;
    int8_t steps_;
    int8_t steps_per_detent_;
    bool reverse_direction_;

    // Button state (first-edge-latch debounce)
    bool last_button_state_;
    bool button_pressed_;
    uint32_t debounce_start_;   // Time of first edge (0 = no pending debounce)
    bool debounce_active_;

    // Debug
    uint8_t encoder_id_;

    // Debounce timing: 20ms = 20,000 microseconds
    static constexpr uint32_t BUTTON_DEBOUNCE_US = 20000;

    // Quadrature state transition table
    // Maps (prev_state << 2 | curr_state) to direction
    static const int8_t TRANSITION_TABLE[16];
};

#endif // ENCODER_H
