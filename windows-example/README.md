# RotaryUsb Windows Example

This directory contains a C# console application that demonstrates two methods to read data from the RotaryUsb device on Windows:

1. **Generic HID Mode** (Recommended) - Direct device access using HidLibrary
2. **Keyboard HID Mode** - Low-level keyboard hook for F1-F12 key events

## Choosing a Mode

| Mode | Firmware Required | Pros | Cons |
|------|-------------------|------|------|
| **Generic HID** | boot.py + code_generic_hid.py | Events only go to your app, raw encoder data | Requires firmware change |
| **Keyboard HID** | code.py only (default) | Works out of the box | Keys sent to all apps |

## Overview

### Generic HID Mode

When the RotaryUsb device is configured for Generic HID mode:
- The application opens the device directly using HidLibrary
- Reads raw encoder movement values (relative, signed)
- Reads button press/release states
- Events are **exclusive** to your application

### Keyboard HID Mode

When the device uses the default keyboard firmware:
- Uses a low-level keyboard hook to capture F1-F12 events
- Works with any application that accepts keyboard input
- Events are **global** and may affect other applications

## Requirements

- .NET 8.0 SDK or later
- Windows 10/11
- The RotaryUsb device connected via USB
- For Generic HID mode: firmware configured with boot.py + code_generic_hid.py

## Dependencies

This project uses the [HidLibrary](https://www.nuget.org/packages/hidlibrary) NuGet package for Generic HID access. It will be automatically restored when building.

## Building

```bash
cd windows-example
dotnet build
```

## Running

```bash
dotnet run
```

Or run the compiled executable:
```bash
bin\Debug\net8.0\RotaryUsbExample.exe
```

## Expected Output

### Generic HID Mode

When the device is in Generic HID mode:

```
RotaryUsb Windows Example
=========================

This application supports two modes:
  1. Generic HID Mode - Direct device access (recommended)
  2. Keyboard HID Mode - Keyboard hook for F1-F12 keys

Searching for Generic HID devices...
Found 15 HID devices total.
Found 1 vendor-defined HID devices.
  - VID:0x239A PID:0x80F4 UsagePage:0xFF00 Usage:0x01
  -> Potential RotaryUsb device found!
Generic HID device found! Starting Generic HID mode...

Generic HID Mode
================

HID Report Format:
  Byte 0: Report ID (0x01)
  Byte 1-4: Encoder 1-4 movement (signed, +CW/-CCW)
  Byte 5: Button states (bit 0-3 = buttons 1-4)
  Byte 6-7: Reserved

Press Ctrl+C to exit.

Waiting for HID reports...
------------------------------------------------------------
Device opened successfully.

[14:30:15.123] Encoder 1: CW (+1) -> Position: 1
  -> Action: Encoder 1 could control volume (up)
[14:30:15.456] Encoder 1: CCW (-1) -> Position: 0
  -> Action: Encoder 1 could control volume (down)
[14:30:16.789] Button 1: PRESSED
  -> Action: Button 1 could toggle mute
[14:30:16.890] Button 1: RELEASED
```

### Keyboard HID Mode

### Keyboard HID Mode

When Generic HID device is not found, falls back to keyboard mode:

```
RotaryUsb Windows Example
=========================

This application supports two modes:
  1. Generic HID Mode - Direct device access (recommended)
  2. Keyboard HID Mode - Keyboard hook for F1-F12 keys

Searching for Generic HID devices...
Found 15 HID devices total.
Found 0 vendor-defined HID devices.
No Generic HID device found.
Falling back to Keyboard HID mode...

Note: For Generic HID mode, ensure the firmware is configured
with boot.py and code_generic_hid.py

Keyboard HID Mode
=================

Expected key mappings from the device:
  Encoder 1: CW=F1, CCW=F2, Button=F9
  Encoder 2: CW=F3, CCW=F4, Button=F10
  Encoder 3: CW=F5, CCW=F6, Button=F11
  Encoder 4: CW=F7, CCW=F8, Button=F12

Press Ctrl+C to exit.

Waiting for keyboard events...
----------------------------------------
Keyboard hook installed successfully.

[14:30:15.123] Encoder 1: Clockwise rotation
  -> Action: Could increase volume
[14:30:15.456] Encoder 1: Counter-clockwise rotation
  -> Action: Could decrease volume
[14:30:16.789] Encoder 1: Button pressed
  -> Action: Could toggle mute
```

## Generic HID Report Format

When using Generic HID mode, the device sends 8-byte reports:

| Byte | Description | Range |
|------|-------------|-------|
| 0 | Report ID | Always 0x01 |
| 1 | Encoder 1 movement | -127 to +127 (signed, positive = CW) |
| 2 | Encoder 2 movement | -127 to +127 (signed, positive = CW) |
| 3 | Encoder 3 movement | -127 to +127 (signed, positive = CW) |
| 4 | Encoder 4 movement | -127 to +127 (signed, positive = CW) |
| 5 | Button states | Bit 0-3: Buttons 1-4 (1 = pressed) |
| 6 | Reserved | 0x00 |
| 7 | Reserved | 0x00 |

## USB Device Identification

The application searches for devices with:
- **Usage Page:** 0xFF00 (Vendor Defined)
- **Known VIDs:** 0x239A (Adafruit/CircuitPython), 0xCAFE (Development)

If your device uses different identifiers, update the `KNOWN_VIDS` and `KNOWN_PIDS` arrays in `Program.cs`.

## Customization

### Adding Custom Handlers (Generic HID Mode)

Edit the `HandleEncoderMovement` and `HandleButtonPress` methods in `Program.cs`:

```csharp
private static void HandleEncoderMovement(int encoderNumber, int movement, int position)
{
    switch (encoderNumber)
    {
        case 1:
            // Your custom action for encoder 1
            AdjustVolume(movement);
            break;
        case 2:
            // Your custom action for encoder 2
            AdjustBrightness(movement);
            break;
    }
}

private static void HandleButtonPress(int buttonNumber)
{
    switch (buttonNumber)
    {
        case 1:
            ToggleMute();
            break;
        case 2:
            ResetSettings();
            break;
    }
}
```

### Adding Custom Handlers (Keyboard HID Mode)

### Adding Custom Handlers (Keyboard HID Mode)

Edit the `HandleKeyboardEncoderEvent` method in `Program.cs`:

```csharp
private static void HandleKeyboardEncoderEvent(int vkCode)
{
    switch (vkCode)
    {
        case VK_F1:
            // Your custom action for encoder 1 clockwise
            IncreaseApplicationVolume();
            break;
        case VK_F2:
            // Your custom action for encoder 1 counter-clockwise
            DecreaseApplicationVolume();
            break;
        // ... add more cases
    }
}
```

### Suppressing Key Events

If you want to prevent the F-key events from reaching other applications, you can modify the hook callback to not call `CallNextHookEx` for those specific keys. However, be careful as this will block the keys system-wide.

```csharp
private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
{
    if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
    {
        var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int vkCode = (int)hookStruct.vkCode;

        // Block F1-F12 from other apps
        if (vkCode >= VK_F1 && vkCode <= VK_F12)
        {
            HandleEncoderEvent(vkCode);
            return (IntPtr)1; // Block the key
        }
    }

    return CallNextHookEx(_hookId, nCode, wParam, lParam);
}
```

## Using in a WPF or WinForms Application

For GUI applications, you can integrate the keyboard hook similarly. However, GUI applications already have a message loop, so you don't need the `GetMessage` loop.

Example for WPF:
```csharp
public partial class MainWindow : Window
{
    private IntPtr _hookId;
    
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        InstallKeyboardHook();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnhookWindowsHookEx(_hookId);
        base.OnClosed(e);
    }
}
```

## Troubleshooting

### Hook Not Working

- Ensure the application is running with appropriate permissions
- The hook may not capture keys when certain applications have focus (e.g., some games with anti-cheat)

### Device Not Detected (Generic HID Mode)

- Verify the firmware is configured for Generic HID mode (boot.py installed, device power cycled)
- Check Device Manager for "HID-compliant device" under "Human Interface Devices"
- Use a USB HID analyzer tool to verify the device is sending reports
- Check that your VID/PID matches the `KNOWN_VIDS` and `KNOWN_PIDS` arrays

### Device Not Detected (Keyboard HID Mode)

- Verify the RotaryUsb device is connected and recognized by Windows
- Check Device Manager for "USB Input Device" or "HID Keyboard Device"
- Open a text editor and verify the encoders send F1-F12 keys

### High CPU Usage

The keyboard hook runs on every keystroke. If you notice high CPU usage, consider adding a short `Thread.Sleep` or using async patterns for heavy processing in the event handler.

## License

This example is provided under the Apache License 2.0.
