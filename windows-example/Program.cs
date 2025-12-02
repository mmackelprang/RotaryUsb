// SPDX-FileCopyrightText: 2024 RotaryUsb Project
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.InteropServices;

namespace RotaryUsbExample;

/// <summary>
/// Example Windows console application demonstrating how to capture
/// keyboard events from the RotaryUsb device.
/// 
/// The RotaryUsb device sends F1-F12 keys for rotary encoder events.
/// This example shows how to intercept these keys using a low-level
/// keyboard hook.
/// </summary>
public class Program
{
    // Windows API constants for keyboard hooks
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;

    // Virtual key codes for F1-F12
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

    // Delegate for the keyboard hook callback
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Store delegate to prevent garbage collection
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

    public static void Main(string[] args)
    {
        Console.WriteLine("RotaryUsb Windows Example");
        Console.WriteLine("=========================");
        Console.WriteLine();
        Console.WriteLine("This application demonstrates how to capture keyboard events");
        Console.WriteLine("from the RotaryUsb device (Raspberry Pi Pico with rotary encoders).");
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
                
                // Here you can add custom handling logic
                // For example, control application volume, switch tabs, etc.
                HandleEncoderEvent(vkCode);
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

    private static void HandleEncoderEvent(int vkCode)
    {
        // Example custom handling logic
        // You can expand this to control your application
        switch (vkCode)
        {
            case VK_F1:
                // Encoder 1 clockwise - could increase volume
                Console.WriteLine("  -> Action: Could increase volume");
                break;
            case VK_F2:
                // Encoder 1 counter-clockwise - could decrease volume
                Console.WriteLine("  -> Action: Could decrease volume");
                break;
            case VK_F9:
                // Encoder 1 button - could mute/unmute
                Console.WriteLine("  -> Action: Could toggle mute");
                break;
            // Add more custom handlers as needed
        }
    }
}
