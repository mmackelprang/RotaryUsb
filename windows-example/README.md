# RotaryUsb Windows Example

This directory contains a C# console application that demonstrates how to capture and handle keyboard events from the RotaryUsb device on Windows.

## Overview

The RotaryUsb device (Raspberry Pi Pico with rotary encoders) appears as a standard USB keyboard to Windows. It sends F1-F12 key events in response to encoder rotations and button presses.

This example shows how to:
- Use a low-level keyboard hook to capture these events
- Map the events to meaningful actions
- Handle the events without blocking other applications

## Requirements

- .NET 8.0 SDK or later
- Windows 10/11 (required for the keyboard hook APIs)
- The RotaryUsb device connected via USB

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

When you run the application and interact with the RotaryUsb device:

```
RotaryUsb Windows Example
=========================

This application demonstrates how to capture keyboard events
from the RotaryUsb device (Raspberry Pi Pico with rotary encoders).

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

## Customization

### Adding Custom Handlers

Edit the `HandleEncoderEvent` method in `Program.cs` to add your own logic:

```csharp
private static void HandleEncoderEvent(int vkCode)
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

### Device Not Detected

- Verify the RotaryUsb device is connected and recognized by Windows
- Check Device Manager for "USB Input Device" or "HID Keyboard Device"
- Open a text editor and verify the encoders send F1-F12 keys

### High CPU Usage

The keyboard hook runs on every keystroke. If you notice high CPU usage, consider adding a short `Thread.Sleep` or using async patterns for heavy processing in the event handler.

## License

This example is provided under the Apache License 2.0.
