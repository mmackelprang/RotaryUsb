# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
CircuitPython firmware for Raspberry Pi Pico
Reads 4 rotary encoders with push buttons and sends USB HID keyboard events.

Copy this file to the CIRCUITPY drive as code.py after installing CircuitPython
and the adafruit_hid library.
"""

import time
import board
import digitalio
import usb_hid
from adafruit_hid.keyboard import Keyboard
from adafruit_hid.keycode import Keycode

# ============================================================================
# CONFIGURATION
# ============================================================================

# GPIO Pin mapping for 4 encoders
# Each encoder has: A (CLK), B (DT), SW (Button)
ENCODER_PINS = [
    {"a": board.GP2, "b": board.GP3, "sw": board.GP4},   # Encoder 1
    {"a": board.GP5, "b": board.GP6, "sw": board.GP7},   # Encoder 2
    {"a": board.GP8, "b": board.GP9, "sw": board.GP10},  # Encoder 3
    {"a": board.GP11, "b": board.GP12, "sw": board.GP13}, # Encoder 4
]

# Key mappings for each encoder
# cw_key: Keycode sent on clockwise rotation
# ccw_key: Keycode sent on counter-clockwise rotation
# btn_key: Keycode sent on button press
KEY_MAPPINGS = [
    {"cw_key": Keycode.F1, "ccw_key": Keycode.F2, "btn_key": Keycode.F9},   # Encoder 1
    {"cw_key": Keycode.F3, "ccw_key": Keycode.F4, "btn_key": Keycode.F10},  # Encoder 2
    {"cw_key": Keycode.F5, "ccw_key": Keycode.F6, "btn_key": Keycode.F11},  # Encoder 3
    {"cw_key": Keycode.F7, "ccw_key": Keycode.F8, "btn_key": Keycode.F12},  # Encoder 4
]

# Debounce timing (in seconds)
BUTTON_DEBOUNCE_TIME = 0.020  # 20ms debounce for buttons
LOOP_DELAY = 0.001  # 1ms loop delay

# Debug output to serial console
DEBUG_ENABLED = True

# ============================================================================
# ENCODER CLASS
# ============================================================================

class Encoder:
    """
    Handles a single rotary encoder with push button.
    
    Uses quadrature decoding to detect rotation direction and
    software debouncing for the push button.
    """
    
    # Quadrature state transition table
    # Maps (previous_state, current_state) to direction
    # +1 = clockwise, -1 = counter-clockwise, 0 = no valid transition
    TRANSITION_TABLE = {
        (0b00, 0b01): +1,
        (0b01, 0b11): +1,
        (0b11, 0b10): +1,
        (0b10, 0b00): +1,
        (0b00, 0b10): -1,
        (0b10, 0b11): -1,
        (0b11, 0b01): -1,
        (0b01, 0b00): -1,
    }
    
    def __init__(self, pin_a, pin_b, pin_sw, cw_key, ccw_key, btn_key, keyboard, encoder_id=0):
        """
        Initialize an encoder instance.
        
        Args:
            pin_a: GPIO pin for encoder A (CLK)
            pin_b: GPIO pin for encoder B (DT)
            pin_sw: GPIO pin for encoder push button (SW)
            cw_key: Keycode to send on clockwise rotation
            ccw_key: Keycode to send on counter-clockwise rotation
            btn_key: Keycode to send on button press
            keyboard: Keyboard instance for sending HID events
            encoder_id: Identifier for debug output
        """
        self.encoder_id = encoder_id
        self.keyboard = keyboard
        
        # Key mappings
        self.cw_key = cw_key
        self.ccw_key = ccw_key
        self.btn_key = btn_key
        
        # Initialize pin A
        self.pin_a = digitalio.DigitalInOut(pin_a)
        self.pin_a.direction = digitalio.Direction.INPUT
        self.pin_a.pull = digitalio.Pull.UP
        
        # Initialize pin B
        self.pin_b = digitalio.DigitalInOut(pin_b)
        self.pin_b.direction = digitalio.Direction.INPUT
        self.pin_b.pull = digitalio.Pull.UP
        
        # Initialize button pin
        self.pin_sw = digitalio.DigitalInOut(pin_sw)
        self.pin_sw.direction = digitalio.Direction.INPUT
        self.pin_sw.pull = digitalio.Pull.UP
        
        # Encoder state tracking
        # Read initial state (inverted because active-low)
        self.last_ab_state = self._read_ab_state()
        # Accumulated steps - most encoders have 4 state changes per detent
        # (adjust threshold in update() if your encoder behaves differently)
        self.steps = 0
        
        # Button state tracking
        self.last_button_state = self.pin_sw.value  # True = not pressed (pull-up)
        self.button_pressed = False
        self.last_button_time = time.monotonic()
    
    def _read_ab_state(self):
        """Read current A/B state as 2-bit value."""
        # Pins are active-low, so invert the reading
        a_val = 0 if self.pin_a.value else 1
        b_val = 0 if self.pin_b.value else 1
        return (a_val << 1) | b_val
    
    def update(self):
        """
        Update encoder state and send HID events if needed.
        
        Should be called frequently in the main loop.
        Returns True if an event was sent.
        """
        event_sent = False
        
        # Process encoder rotation
        current_ab_state = self._read_ab_state()
        
        if current_ab_state != self.last_ab_state:
            # Look up transition in table
            transition = (self.last_ab_state, current_ab_state)
            direction = self.TRANSITION_TABLE.get(transition, 0)
            
            if direction != 0:
                self.steps += direction
                
                # Most encoders have 4 state changes per detent
                # Send key event when a full detent is completed
                if self.steps >= 4:
                    self._send_key(self.cw_key, "CW")
                    self.steps = 0
                    event_sent = True
                elif self.steps <= -4:
                    self._send_key(self.ccw_key, "CCW")
                    self.steps = 0
                    event_sent = True
            
            self.last_ab_state = current_ab_state
        
        # Process button press with debounce
        current_button_state = self.pin_sw.value
        current_time = time.monotonic()
        
        # Button is active-low (pressed = False/low)
        if current_button_state != self.last_button_state:
            if (current_time - self.last_button_time) >= BUTTON_DEBOUNCE_TIME:
                self.last_button_time = current_time
                self.last_button_state = current_button_state
                
                # Detect falling edge (button press)
                if not current_button_state and not self.button_pressed:
                    self.button_pressed = True
                    self._send_key(self.btn_key, "BTN")
                    event_sent = True
                # Detect rising edge (button release)
                elif current_button_state and self.button_pressed:
                    self.button_pressed = False
                    if DEBUG_ENABLED:
                        print(f"Encoder {self.encoder_id}: Button released")
        
        return event_sent
    
    def _send_key(self, keycode, event_type):
        """Send a keyboard key press and release."""
        try:
            self.keyboard.send(keycode)
            if DEBUG_ENABLED:
                print(f"Encoder {self.encoder_id}: {event_type} -> Key sent")
        except Exception as e:
            if DEBUG_ENABLED:
                print(f"Encoder {self.encoder_id}: Error sending key: {e}")


# ============================================================================
# MAIN PROGRAM
# ============================================================================

def main():
    """Main entry point."""
    print("RotaryUsb Encoder Firmware Starting...")
    print(f"Debug output: {'Enabled' if DEBUG_ENABLED else 'Disabled'}")
    
    # Initialize USB HID Keyboard
    try:
        keyboard = Keyboard(usb_hid.devices)
        print("USB HID Keyboard initialized")
    except Exception as e:
        print(f"Failed to initialize keyboard: {e}")
        print("Make sure usb_hid is enabled in boot.py")
        return
    
    # Create encoder instances
    encoders = []
    for i, (pins, keys) in enumerate(zip(ENCODER_PINS, KEY_MAPPINGS)):
        encoder = Encoder(
            pin_a=pins["a"],
            pin_b=pins["b"],
            pin_sw=pins["sw"],
            cw_key=keys["cw_key"],
            ccw_key=keys["ccw_key"],
            btn_key=keys["btn_key"],
            keyboard=keyboard,
            encoder_id=i + 1
        )
        encoders.append(encoder)
        print(f"Encoder {i + 1} initialized: A={pins['a']}, B={pins['b']}, SW={pins['sw']}")
    
    print("All encoders initialized. Starting main loop...")
    print("-" * 40)
    
    # Main loop
    while True:
        for encoder in encoders:
            encoder.update()
        
        # Small delay to prevent CPU hogging
        time.sleep(LOOP_DELAY)


# Run the main program
if __name__ == "__main__":
    main()
