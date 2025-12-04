# SPDX-FileCopyrightText: 2024 RotaryUsb Project
# SPDX-License-Identifier: Apache-2.0
"""
CircuitPython firmware for Raspberry Pi Pico - Generic HID Mode

Reads 4 rotary encoders with push buttons and sends raw USB HID reports
to the host PC. This mode allows applications to read encoder data directly
without keyboard event interception.

PREREQUISITES:
- Copy boot.py (Generic HID version) to the CIRCUITPY drive first
- Power cycle the device before copying this file

Copy this file to the CIRCUITPY drive as code.py after installing CircuitPython
and the adafruit_hid library.

HID REPORT FORMAT (8 bytes total):
  Byte 0: Report ID (0x01 - handled by send_report)
  Byte 1: Encoder 1 movement (signed: -127 to +127, positive=CW)
  Byte 2: Encoder 2 movement (signed: -127 to +127, positive=CW)
  Byte 3: Encoder 3 movement (signed: -127 to +127, positive=CW)
  Byte 4: Encoder 4 movement (signed: -127 to +127, positive=CW)
  Byte 5: Button states (bit 0=Btn1, bit 1=Btn2, bit 2=Btn3, bit 3=Btn4)
  Byte 6: Reserved (0x00)
  Byte 7: Reserved (0x00)
"""

import time
import board
import digitalio
import usb_hid

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

# Debounce timing (in seconds)
BUTTON_DEBOUNCE_TIME = 0.020  # 20ms debounce for buttons
LOOP_DELAY = 0.001  # 1ms loop delay
REPORT_INTERVAL = 0.010  # 10ms minimum between reports

# Debug output to serial console
DEBUG_ENABLED = True

# ============================================================================
# ENCODER CLASS (Generic HID version)
# ============================================================================

class Encoder:
    """
    Handles a single rotary encoder with push button for Generic HID mode.
    
    Uses quadrature decoding to detect rotation direction and
    software debouncing for the push button. Returns raw movement
    values instead of sending keyboard events.
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
    
    def __init__(self, pin_a, pin_b, pin_sw, encoder_id=0):
        """
        Initialize an encoder instance.
        
        Args:
            pin_a: GPIO pin for encoder A (CLK)
            pin_b: GPIO pin for encoder B (DT)
            pin_sw: GPIO pin for encoder push button (SW)
            encoder_id: Identifier for debug output
        """
        self.encoder_id = encoder_id
        
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
        self.last_ab_state = self._read_ab_state()
        self.steps = 0
        self.accumulated_movement = 0
        
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
        Update encoder state.
        
        Should be called frequently in the main loop.
        Returns tuple: (movement, button_pressed)
          - movement: Signed int (-127 to +127) for rotation since last report
          - button_pressed: Boolean True if button is currently pressed
        """
        # Process encoder rotation
        current_ab_state = self._read_ab_state()
        
        if current_ab_state != self.last_ab_state:
            # Look up transition in table
            transition = (self.last_ab_state, current_ab_state)
            direction = self.TRANSITION_TABLE.get(transition, 0)
            
            if direction != 0:
                self.steps += direction
                
                # Most encoders have 4 state changes per detent
                # Accumulate movement when a full detent is completed
                if self.steps >= 4:
                    self.accumulated_movement += 1
                    self.steps = 0
                    if DEBUG_ENABLED:
                        print(f"Encoder {self.encoder_id}: CW detent")
                elif self.steps <= -4:
                    self.accumulated_movement -= 1
                    self.steps = 0
                    if DEBUG_ENABLED:
                        print(f"Encoder {self.encoder_id}: CCW detent")
            
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
                    if DEBUG_ENABLED:
                        print(f"Encoder {self.encoder_id}: Button pressed")
                # Detect rising edge (button release)
                elif current_button_state and self.button_pressed:
                    self.button_pressed = False
                    if DEBUG_ENABLED:
                        print(f"Encoder {self.encoder_id}: Button released")
        
        return self.button_pressed
    
    def get_and_clear_movement(self):
        """
        Get accumulated movement since last call and reset counter.
        
        Returns:
            Signed int clamped to -127 to +127 range
        """
        movement = max(-127, min(127, self.accumulated_movement))
        self.accumulated_movement = 0
        return movement


# ============================================================================
# HID DEVICE HELPER
# ============================================================================

def find_generic_hid_device():
    """
    Find the Generic HID device configured by boot.py.
    
    Returns:
        The usb_hid.Device instance or None if not found
    """
    for device in usb_hid.devices:
        # Look for our vendor-defined device (Usage Page 0xFF00)
        if device.usage_page == 0xFF00 and device.usage == 0x01:
            return device
    return None


def signed_to_unsigned_byte(value):
    """
    Convert a signed integer to an unsigned byte using two's complement.
    
    Args:
        value: Signed integer in range -127 to +127
    
    Returns:
        Unsigned byte (0-255) representing the two's complement value.
        This is needed because send_report expects unsigned bytes, but we
        want to transmit signed movement values.
    """
    if value < 0:
        return (256 + value) & 0xFF
    return value & 0xFF


# ============================================================================
# MAIN PROGRAM
# ============================================================================

def main():
    """Main entry point."""
    print("RotaryUsb Generic HID Firmware Starting...")
    print(f"Debug output: {'Enabled' if DEBUG_ENABLED else 'Disabled'}")
    
    # Find the Generic HID device
    hid_device = find_generic_hid_device()
    if hid_device is None:
        print("ERROR: Generic HID device not found!")
        print("Make sure boot.py is installed and the device was power cycled.")
        print("Available devices:")
        for device in usb_hid.devices:
            print(f"  - Usage Page: 0x{device.usage_page:04X}, Usage: 0x{device.usage:02X}")
        return
    
    print(f"Generic HID device found: Usage Page 0x{hid_device.usage_page:04X}")
    
    # Create encoder instances
    encoders = []
    for i, pins in enumerate(ENCODER_PINS):
        encoder = Encoder(
            pin_a=pins["a"],
            pin_b=pins["b"],
            pin_sw=pins["sw"],
            encoder_id=i + 1
        )
        encoders.append(encoder)
        print(f"Encoder {i + 1} initialized: A={pins['a']}, B={pins['b']}, SW={pins['sw']}")
    
    print("All encoders initialized. Starting main loop...")
    print("-" * 40)
    
    # HID report buffer (7 bytes, report ID is handled separately)
    # [Enc1][Enc2][Enc3][Enc4][Buttons][Reserved][Reserved]
    report = bytearray(7)
    last_report_time = time.monotonic()
    last_report = bytearray(7)
    
    # Main loop
    while True:
        # Update all encoders
        button_states = 0
        for i, encoder in enumerate(encoders):
            btn_pressed = encoder.update()
            if btn_pressed:
                button_states |= (1 << i)
        
        current_time = time.monotonic()
        
        # Send report at regular intervals or when state changes
        if (current_time - last_report_time) >= REPORT_INTERVAL:
            # Build the HID report
            for i, encoder in enumerate(encoders):
                movement = encoder.get_and_clear_movement()
                report[i] = signed_to_unsigned_byte(movement)
            
            report[4] = button_states  # Button states
            report[5] = 0x00           # Reserved
            report[6] = 0x00           # Reserved
            
            # Only send if something changed or there's movement
            has_movement = any(report[i] != 0 for i in range(4))
            has_change = report != last_report
            
            if has_movement or has_change:
                try:
                    hid_device.send_report(report)
                    if DEBUG_ENABLED and has_movement:
                        print(f"Report: Enc[{report[0]:3d},{report[1]:3d},{report[2]:3d},{report[3]:3d}] Btn=0x{report[4]:02X}")
                except Exception as e:
                    if DEBUG_ENABLED:
                        print(f"Error sending report: {e}")
                
                last_report = bytearray(report)
            
            last_report_time = current_time
        
        # Small delay to prevent CPU hogging
        time.sleep(LOOP_DELAY)


# Run the main program
if __name__ == "__main__":
    main()
