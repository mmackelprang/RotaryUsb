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

Generic HID device found! Starting...

Reading config from device...

RotaryUsb Configuration
========================
Device connected: VID:0xCAFE PID:0x4005

Current encoder values:
  Enc1:          0  [0 - 100, step=1]
  Enc2:          0  [0 - 100, step=1]
  Enc3:          0  [0 - 100, step=1]
  Enc4:          0  [0 - 100, step=1]

  Buttons: [ ] [ ] [ ] [ ]

[M] Monitor - Live display of encoder values
[C] Configure encoder
[S] Save config to device flash
[D] Reset to defaults
[R] Reset positions
[Q] Quit
```

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

When using Generic HID mode with runtime configuration, the device sends 21-byte position reports (Input Report ID 0x01):

| Offset | Type | Description |
|--------|------|-------------|
| 0-3 | int32 LE | Encoder 1 absolute position |
| 4-7 | int32 LE | Encoder 2 absolute position |
| 8-11 | int32 LE | Encoder 3 absolute position |
| 12-15 | int32 LE | Encoder 4 absolute position |
| 16 | uint8 | Button states (bit 0-3 = buttons 1-4) |
| 17 | uint8 | Active acceleration tiers (packed 2-bit per encoder) |
| 18-20 | uint8[3] | Reserved (0x00) |

## Runtime Configuration Menu

In Generic HID mode, the application provides an interactive configuration menu:

- **[M] Monitor** - Live display of encoder positions and acceleration tiers
- **[C] Configure** - Edit per-encoder settings (min/max/step/wrap/reverse/acceleration)
- **[S] Save** - Save current config to device flash
- **[D] Defaults** - Reset device to factory defaults
- **[R] Reset positions** - Reset all encoder positions to min_value
- **[Q] Quit**

### Built-in Presets

| Preset | Min | Max | Step | Wrap | Accel Tiers |
|--------|-----|-----|------|------|-------------|
| General Purpose | 0 | 100 | 1 | No | 5x/15x/50x |
| Radio Tuner (kHz) | 88,000 | 108,000 | 100 | Yes | 10x/100x/1000x |
| Audio Mixer (%) | 0 | 100 | 1 | No | 2x/5x/10x |
| Fine Control | 0 | 10,000 | 1 | No | 5x/25x/100x |

## USB Device Identification

The application searches for devices with:
- **Usage Page:** 0xFF00 (Vendor Defined)
- **Known VIDs:** 0x239A (Adafruit/CircuitPython), 0xCAFE (Development)

If your device uses different identifiers, update the `KNOWN_VIDS` and `KNOWN_PIDS` arrays in `Program.cs`.

## Customization

The application uses an interactive menu for configuration. To add custom behavior when encoder values change, modify the `ReportReaderLoop` method in `Program.cs` where position reports are parsed.

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
