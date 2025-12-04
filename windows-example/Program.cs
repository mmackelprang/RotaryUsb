// SPDX-FileCopyrightText: 2024 RotaryUsb Project
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HidLibrary;

namespace RotaryUsbExample;

/// <summary>
/// Example Windows console application demonstrating two methods to read
/// RotaryUsb device data:
/// 
/// 1. GENERIC HID MODE (Recommended): Direct HID access using HidLibrary
///    - Requires firmware in Generic HID mode (boot.py + code_generic_hid.py)
///    - Reads raw encoder/button data directly
///    - Events only go to your application
/// 
/// 2. KEYBOARD HID MODE: Low-level keyboard hook
///    - Works with default firmware (code.py only)
///    - Intercepts F1-F12 key events
///    - Keys are sent to all applications
/// </summary>
public class Program
{
    // ========================================================================
    // GENERIC HID CONFIGURATION
    // ========================================================================
    
    // CircuitPython devices typically use Adafruit VID
    // Check Device Manager or use the enumeration to find your actual VID/PID
    private static readonly int[] KNOWN_VIDS = { 0x239A, 0xCAFE };  // Adafruit, Development
    private static readonly int[] KNOWN_PIDS = { 0x80F4, 0x4005 }; // CircuitPython, C++ Generic HID
    
    // Vendor-defined Usage Page for Generic HID mode
    // Note: HidLibrary returns UsagePage as short, so 0xFF00 becomes -256 when signed
    private const short VENDOR_USAGE_PAGE = unchecked((short)0xFF00);
    
    // ========================================================================
    // KEYBOARD HOOK CONFIGURATION (for Keyboard HID mode)
    // ========================================================================
    
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;

    private const int VK_F1 = 0x70;
    private const int VK_F2 = 0x71;
    private const int VK_F3 = 0x72;
    private const int VK_F4 = 0x73;
    private const int VK_F5 = 0x74;
    private const int VK_F6 = 0x75;
    private const int VK_F7 = 0x76;
    private const int VK_F8 = 0x77;
    private const int VK_F9 = 0x78;
    private const int VK_F10 = 0x79;
    private const int VK_F11 = 0x7A;
    private const int VK_F12 = 0x7B;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private static LowLevelKeyboardProc? _hookCallback;
    private static IntPtr _hookId = IntPtr.Zero;

    // P/Invoke declarations
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // ========================================================================
    // MAIN ENTRY POINT
    // ========================================================================

    public static void Main(string[] args)
    {
        Console.WriteLine("RotaryUsb Windows Example");
        Console.WriteLine("=========================");
        Console.WriteLine();
        Console.WriteLine("This application supports two modes:");
        Console.WriteLine("  1. Generic HID Mode - Direct device access (recommended)");
        Console.WriteLine("  2. Keyboard HID Mode - Keyboard hook for F1-F12 keys");
        Console.WriteLine();
        
        // Try Generic HID mode first
        var hidDevice = FindGenericHidDevice();
        
        if (hidDevice != null)
        {
            Console.WriteLine("Generic HID device found! Starting Generic HID mode...");
            Console.WriteLine();
            RunGenericHidMode(hidDevice);
        }
        else
        {
            Console.WriteLine("No Generic HID device found.");
            Console.WriteLine("Falling back to Keyboard HID mode...");
            Console.WriteLine();
            Console.WriteLine("Note: For Generic HID mode, ensure the firmware is configured");
            Console.WriteLine("with boot.py and code_generic_hid.py");
            Console.WriteLine();
            RunKeyboardHidMode();
        }
    }

    // ========================================================================
    // GENERIC HID MODE IMPLEMENTATION
    // ========================================================================

    /// <summary>
    /// Find a RotaryUsb device configured for Generic HID mode.
    /// </summary>
    private static HidDevice? FindGenericHidDevice()
    {
        Console.WriteLine("Searching for Generic HID devices...");
        
        // Get all HID devices
        var allDevices = HidDevices.Enumerate().ToList();
        Console.WriteLine($"Found {allDevices.Count} HID devices total.");
        
        // Look for vendor-defined devices (Usage Page 0xFF00)
        var vendorDevices = allDevices.Where(d => 
        {
            try
            {
                var caps = d.Capabilities;
                return caps.UsagePage == VENDOR_USAGE_PAGE;
            }
            catch
            {
                return false;
            }
        }).ToList();
        
        Console.WriteLine($"Found {vendorDevices.Count} vendor-defined HID devices.");
        
        foreach (var device in vendorDevices)
        {
            try
            {
                var attrs = device.Attributes;
                var caps = device.Capabilities;
                Console.WriteLine($"  - VID:0x{attrs.VendorId:X4} PID:0x{attrs.ProductId:X4} " +
                                $"UsagePage:0x{caps.UsagePage:X4} Usage:0x{caps.Usage:X2}");
                
                // Check if this matches known RotaryUsb identifiers
                bool vidMatch = KNOWN_VIDS.Contains(attrs.VendorId);
                bool isVendorPage = caps.UsagePage == VENDOR_USAGE_PAGE;
                
                if (isVendorPage && (vidMatch || attrs.VendorId != 0))
                {
                    Console.WriteLine($"  -> Potential RotaryUsb device found!");
                    return device;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  - Error reading device: {ex.Message}");
            }
        }
        
        // Also list any devices matching known VIDs regardless of usage page
        Console.WriteLine();
        Console.WriteLine("Devices matching known VIDs:");
        foreach (var device in allDevices)
        {
            try
            {
                var attrs = device.Attributes;
                if (KNOWN_VIDS.Contains(attrs.VendorId))
                {
                    var caps = device.Capabilities;
                    Console.WriteLine($"  - VID:0x{attrs.VendorId:X4} PID:0x{attrs.ProductId:X4} " +
                                    $"UsagePage:0x{caps.UsagePage:X4} Usage:0x{caps.Usage:X2}");
                }
            }
            catch { }
        }
        
        return null;
    }

    /// <summary>
    /// Run in Generic HID mode, reading raw encoder data from the device.
    /// </summary>
    private static void RunGenericHidMode(HidDevice device)
    {
        Console.WriteLine("Generic HID Mode");
        Console.WriteLine("================");
        Console.WriteLine();
        Console.WriteLine("HID Report Format:");
        Console.WriteLine("  Byte 0: Report ID (0x01)");
        Console.WriteLine("  Byte 1-4: Encoder 1-4 movement (signed, +CW/-CCW)");
        Console.WriteLine("  Byte 5: Button states (bit 0-3 = buttons 1-4)");
        Console.WriteLine("  Byte 6-7: Reserved");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to exit.");
        Console.WriteLine();
        Console.WriteLine("Waiting for HID reports...");
        Console.WriteLine("-".PadRight(60, '-'));

        // Track encoder positions (accumulated from relative movements)
        int[] encoderPositions = new int[4];
        bool[] lastButtonStates = new bool[4];

        // Handle Ctrl+C
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down...");
            cts.Cancel();
        };

        try
        {
            device.OpenDevice();
            
            if (!device.IsConnected)
            {
                Console.WriteLine("ERROR: Failed to open device.");
                return;
            }

            Console.WriteLine("Device opened successfully.");
            Console.WriteLine();

            // Read reports continuously
            device.MonitorDeviceEvents = true;
            
            while (!cts.Token.IsCancellationRequested)
            {
                var report = device.Read(100); // 100ms timeout
                
                if (report.Status == HidDeviceData.ReadStatus.Success && report.Data.Length >= 7)
                {
                    // Parse the report
                    // HidLibrary prepends a report ID byte to the data, so the first byte is always the Report ID (0x01)
                    // Our actual data starts at index 1
                    int offset = (report.Data.Length >= 8 && report.Data[0] == 0x01) ? 1 : 0;
                    
                    // Read encoder movements (signed bytes)
                    for (int i = 0; i < 4; i++)
                    {
                        sbyte movement = (sbyte)report.Data[offset + i];
                        if (movement != 0)
                        {
                            encoderPositions[i] += movement;
                            string direction = movement > 0 ? "CW" : "CCW";
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Encoder {i + 1}: {direction} ({movement:+#;-#;0}) -> Position: {encoderPositions[i]}");
                            
                            // Example action handling
                            HandleEncoderMovement(i + 1, movement, encoderPositions[i]);
                        }
                    }
                    
                    // Read button states
                    byte buttonByte = report.Data[offset + 4];
                    for (int i = 0; i < 4; i++)
                    {
                        bool pressed = (buttonByte & (1 << i)) != 0;
                        if (pressed != lastButtonStates[i])
                        {
                            lastButtonStates[i] = pressed;
                            string state = pressed ? "PRESSED" : "RELEASED";
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Button {i + 1}: {state}");
                            
                            if (pressed)
                            {
                                HandleButtonPress(i + 1);
                            }
                        }
                    }
                }
                else if (report.Status == HidDeviceData.ReadStatus.WaitTimedOut)
                {
                    // Normal timeout, continue
                }
                else if (report.Status != HidDeviceData.ReadStatus.Success)
                {
                    Console.WriteLine($"Read error: {report.Status}");
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            device.CloseDevice();
            Console.WriteLine("Device closed. Goodbye!");
        }
    }

    /// <summary>
    /// Handle encoder movement - customize this for your application.
    /// </summary>
    private static void HandleEncoderMovement(int encoderNumber, int movement, int position)
    {
        // Example: Different actions based on encoder number
        switch (encoderNumber)
        {
            case 1:
                Console.WriteLine($"  -> Action: Encoder 1 could control volume ({(movement > 0 ? "up" : "down")})");
                break;
            case 2:
                Console.WriteLine($"  -> Action: Encoder 2 could control brightness");
                break;
            case 3:
                Console.WriteLine($"  -> Action: Encoder 3 could scroll content");
                break;
            case 4:
                Console.WriteLine($"  -> Action: Encoder 4 could adjust zoom level");
                break;
        }
    }

    /// <summary>
    /// Handle button press - customize this for your application.
    /// </summary>
    private static void HandleButtonPress(int buttonNumber)
    {
        switch (buttonNumber)
        {
            case 1:
                Console.WriteLine($"  -> Action: Button 1 could toggle mute");
                break;
            case 2:
                Console.WriteLine($"  -> Action: Button 2 could reset brightness");
                break;
            case 3:
                Console.WriteLine($"  -> Action: Button 3 could toggle scroll lock");
                break;
            case 4:
                Console.WriteLine($"  -> Action: Button 4 could reset zoom to 100%");
                break;
        }
    }

    // ========================================================================
    // KEYBOARD HID MODE IMPLEMENTATION
    // ========================================================================

    /// <summary>
    /// Run in Keyboard HID mode using a low-level keyboard hook.
    /// </summary>
    private static void RunKeyboardHidMode()
    {
        Console.WriteLine("Keyboard HID Mode");
        Console.WriteLine("=================");
        Console.WriteLine();
        Console.WriteLine("Expected key mappings from the device:");
        Console.WriteLine("  Encoder 1: CW=F1, CCW=F2, Button=F9");
        Console.WriteLine("  Encoder 2: CW=F3, CCW=F4, Button=F10");
        Console.WriteLine("  Encoder 3: CW=F5, CCW=F6, Button=F11");
        Console.WriteLine("  Encoder 4: CW=F7, CCW=F8, Button=F12");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to exit.");
        Console.WriteLine();
        Console.WriteLine("Waiting for keyboard events...");
        Console.WriteLine("-".PadRight(40, '-'));

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down...");
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
            }
            PostQuitMessage(0);
        };

        // Install the keyboard hook
        _hookCallback = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, 
            GetModuleHandle(curModule?.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            Console.WriteLine("Failed to install keyboard hook. Error: " + Marshal.GetLastWin32Error());
            return;
        }

        Console.WriteLine("Keyboard hook installed successfully.");
        Console.WriteLine();

        // Message loop - required for the hook to work
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Cleanup
        UnhookWindowsHookEx(_hookId);
        Console.WriteLine("Keyboard hook removed. Goodbye!");
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vkCode = (int)hookStruct.vkCode;

            // Handle RotaryUsb keys (F1-F12)
            string? action = GetEncoderAction(vkCode);
            if (action != null)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {action}");
                HandleKeyboardEncoderEvent(vkCode);
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static string? GetEncoderAction(int vkCode)
    {
        return vkCode switch
        {
            VK_F1 => "Encoder 1: Clockwise rotation",
            VK_F2 => "Encoder 1: Counter-clockwise rotation",
            VK_F3 => "Encoder 2: Clockwise rotation",
            VK_F4 => "Encoder 2: Counter-clockwise rotation",
            VK_F5 => "Encoder 3: Clockwise rotation",
            VK_F6 => "Encoder 3: Counter-clockwise rotation",
            VK_F7 => "Encoder 4: Clockwise rotation",
            VK_F8 => "Encoder 4: Counter-clockwise rotation",
            VK_F9 => "Encoder 1: Button pressed",
            VK_F10 => "Encoder 2: Button pressed",
            VK_F11 => "Encoder 3: Button pressed",
            VK_F12 => "Encoder 4: Button pressed",
            _ => null
        };
    }

    private static void HandleKeyboardEncoderEvent(int vkCode)
    {
        switch (vkCode)
        {
            case VK_F1:
                Console.WriteLine("  -> Action: Could increase volume");
                break;
            case VK_F2:
                Console.WriteLine("  -> Action: Could decrease volume");
                break;
            case VK_F9:
                Console.WriteLine("  -> Action: Could toggle mute");
                break;
        }
    }
}
