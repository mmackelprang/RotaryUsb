// SPDX-FileCopyrightText: 2024 RotaryUsb Project
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HidLibrary;

namespace RotaryUsbExample;

/// <summary>
/// Windows console application for RotaryUsb device with runtime configuration.
///
/// Supports two modes:
/// 1. GENERIC HID MODE (Recommended): Direct HID access with config menu
/// 2. KEYBOARD HID MODE: Low-level keyboard hook for F1-F12 keys
/// </summary>
public class Program
{
    // ========================================================================
    // GENERIC HID CONFIGURATION
    // ========================================================================

    private static readonly int[] KNOWN_VIDS = { 0x239A, 0xCAFE };
    private static readonly int[] KNOWN_PIDS = { 0x80F4, 0x4005 };
    private const short VENDOR_USAGE_PAGE = unchecked((short)0xFF00);

    // Config constants
    private const byte CONFIG_VERSION = 0x01;
    private const int FULL_CONFIG_SIZE = 106;
    private const int NUM_ENCODERS = 4;
    private const int NUM_TIERS = 3;

    // Report IDs
    private const byte REPORT_ID_POSITIONS = 0x01;
    private const byte REPORT_ID_CONFIG = 0x02;
    private const byte REPORT_ID_COMMAND = 0x03;
    private const byte REPORT_ID_DIAG = 0x04;

    // Input Report ID 0x04 payload size, in bytes after the report ID byte.
    private const int DIAG_PAYLOAD_SIZE = 56;

    // Input Report ID 0x01 payload size, in bytes after the report ID byte.
    private const int POSITION_PAYLOAD_SIZE = 36;

    // Firmware built before the movement accumulator sends 21 payload bytes.
    private const int LEGACY_POSITION_PAYLOAD_SIZE = 21;

    // Commands
    private const byte CMD_SAVE_CONFIG = 0x01;
    private const byte CMD_RESET_DEFAULTS = 0x02;
    private const byte CMD_RESET_POSITIONS = 0x03;
    private const byte CMD_READ_CONFIG = 0x04;
    private const byte CMD_RESET_DIAG = 0x05;

    // ========================================================================
    // CONFIG DATA STRUCTURES
    // ========================================================================

    private class TierConfig
    {
        public ushort ThresholdMs;
        public ushort Multiplier;
    }

    private class EncoderConfig
    {
        public int MinValue;
        public int MaxValue;
        public int StepSize;
        public bool Wrap;
        public bool Reverse;
        public TierConfig[] Tiers = new TierConfig[NUM_TIERS];

        public EncoderConfig()
        {
            for (int i = 0; i < NUM_TIERS; i++)
                Tiers[i] = new TierConfig();
        }

        public EncoderConfig Clone()
        {
            var c = new EncoderConfig
            {
                MinValue = MinValue,
                MaxValue = MaxValue,
                StepSize = StepSize,
                Wrap = Wrap,
                Reverse = Reverse
            };
            for (int i = 0; i < NUM_TIERS; i++)
            {
                c.Tiers[i] = new TierConfig
                {
                    ThresholdMs = Tiers[i].ThresholdMs,
                    Multiplier = Tiers[i].Multiplier
                };
            }
            return c;
        }
    }

    private class DeviceConfig
    {
        public byte Version = CONFIG_VERSION;
        public byte GlobalFlags;
        public EncoderConfig[] Encoders = new EncoderConfig[NUM_ENCODERS];

        public DeviceConfig()
        {
            for (int i = 0; i < NUM_ENCODERS; i++)
                Encoders[i] = new EncoderConfig();
        }

        public byte[] Serialize()
        {
            var data = new byte[FULL_CONFIG_SIZE];
            data[0] = Version;
            data[1] = GlobalFlags;
            int offset = 2;
            for (int e = 0; e < NUM_ENCODERS; e++)
            {
                var enc = Encoders[e];
                BitConverter.GetBytes(enc.MinValue).CopyTo(data, offset);
                BitConverter.GetBytes(enc.MaxValue).CopyTo(data, offset + 4);
                BitConverter.GetBytes(enc.StepSize).CopyTo(data, offset + 8);
                data[offset + 12] = (byte)(enc.Wrap ? 1 : 0);
                data[offset + 13] = (byte)(enc.Reverse ? 1 : 0);
                for (int t = 0; t < NUM_TIERS; t++)
                {
                    BitConverter.GetBytes(enc.Tiers[t].ThresholdMs).CopyTo(data, offset + 14 + t * 4);
                    BitConverter.GetBytes(enc.Tiers[t].Multiplier).CopyTo(data, offset + 16 + t * 4);
                }
                offset += 26;
            }
            return data;
        }

        public static DeviceConfig? Deserialize(byte[] data)
        {
            if (data.Length < FULL_CONFIG_SIZE) return null;
            if (data[0] != CONFIG_VERSION) return null;

            var cfg = new DeviceConfig
            {
                Version = data[0],
                GlobalFlags = data[1]
            };
            int offset = 2;
            for (int e = 0; e < NUM_ENCODERS; e++)
            {
                var enc = new EncoderConfig
                {
                    MinValue = BitConverter.ToInt32(data, offset),
                    MaxValue = BitConverter.ToInt32(data, offset + 4),
                    StepSize = BitConverter.ToInt32(data, offset + 8),
                    Wrap = data[offset + 12] != 0,
                    Reverse = data[offset + 13] != 0
                };
                for (int t = 0; t < NUM_TIERS; t++)
                {
                    enc.Tiers[t] = new TierConfig
                    {
                        ThresholdMs = BitConverter.ToUInt16(data, offset + 14 + t * 4),
                        Multiplier = BitConverter.ToUInt16(data, offset + 16 + t * 4)
                    };
                }
                cfg.Encoders[e] = enc;
                offset += 26;
            }
            return cfg;
        }
    }

    // ========================================================================
    // PRESETS
    // ========================================================================

    private static readonly Dictionary<string, EncoderConfig> Presets = new()
    {
        ["General Purpose"] = CreatePreset(0, 100, 1, false, false,
            150, 5, 80, 15, 40, 50),
        ["Radio Tuner (kHz)"] = CreatePreset(88000, 108000, 100, true, false,
            150, 10, 80, 100, 40, 1000),
        ["Audio Mixer (%)"] = CreatePreset(0, 100, 1, false, false,
            150, 2, 80, 5, 40, 10),
        ["Fine Control"] = CreatePreset(0, 10000, 1, false, false,
            150, 5, 80, 25, 40, 100),
    };

    private static EncoderConfig CreatePreset(int min, int max, int step, bool wrap, bool reverse,
        ushort t1Thresh, ushort t1Mult, ushort t2Thresh, ushort t2Mult, ushort t3Thresh, ushort t3Mult)
    {
        return new EncoderConfig
        {
            MinValue = min, MaxValue = max, StepSize = step,
            Wrap = wrap, Reverse = reverse,
            Tiers = new[]
            {
                new TierConfig { ThresholdMs = t1Thresh, Multiplier = t1Mult },
                new TierConfig { ThresholdMs = t2Thresh, Multiplier = t2Mult },
                new TierConfig { ThresholdMs = t3Thresh, Multiplier = t3Mult },
            }
        };
    }

    // ========================================================================
    // STATE
    // ========================================================================

    private static DeviceConfig _deviceConfig = new();
    private static int[] _encoderPositions = new int[NUM_ENCODERS];
    private static byte _tierByte;
    private static byte _buttonStates;
    private static HidDevice? _hidDevice;
    private static bool _configReceived;
    private static readonly object _lock = new();

    // Movement accumulator (Input Report ID 0x01, payload 20-35). All guarded by _lock.
    private static readonly int[] _movementRaw = new int[NUM_ENCODERS];
    private static readonly int[] _movementLast = new int[NUM_ENCODERS];
    private static readonly long[] _hostAccumulated = new long[NUM_ENCODERS];
    private static bool _movementBaselined;
    private static bool _firmwareHasMovement;

    // Decoder diagnostics (Input Report ID 0x04). All guarded by _lock.
    private static readonly byte[] _diagRawPins = new byte[NUM_ENCODERS];
    private static readonly uint[] _diagEdgeCount = new uint[NUM_ENCODERS];
    private static readonly uint[] _diagInvalidCount = new uint[NUM_ENCODERS];
    private static readonly uint[] _diagDetentCount = new uint[NUM_ENCODERS];
    private static byte _diagStepsPerDetent;
    private static DateTime _diagLastSeenUtc = DateTime.MinValue;

    // ========================================================================
    // KEYBOARD HOOK CONFIGURATION (for Keyboard HID mode)
    // ========================================================================

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;

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
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    // ========================================================================
    // MAIN ENTRY POINT
    // ========================================================================

    public static void Main(string[] args)
    {
        Console.WriteLine("RotaryUsb Windows Example");
        Console.WriteLine("=========================");
        Console.WriteLine();

        var hidDevice = FindGenericHidDevice();

        if (hidDevice != null)
        {
            Console.WriteLine("Generic HID device found! Starting...");
            Console.WriteLine();
            RunGenericHidMode(hidDevice);
        }
        else
        {
            Console.WriteLine("No Generic HID device found.");
            Console.WriteLine("Falling back to Keyboard HID mode...");
            Console.WriteLine();
            RunKeyboardHidMode();
        }
    }

    // ========================================================================
    // GENERIC HID MODE — DEVICE DISCOVERY
    // ========================================================================

    private static HidDevice? FindGenericHidDevice()
    {
        Console.WriteLine("Searching for Generic HID devices...");
        var allDevices = HidDevices.Enumerate().ToList();
        Console.WriteLine($"Found {allDevices.Count} HID devices total.");

        var vendorDevices = allDevices.Where(d =>
        {
            try { return d.Capabilities.UsagePage == VENDOR_USAGE_PAGE; }
            catch { return false; }
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

                bool vidMatch = KNOWN_VIDS.Contains(attrs.VendorId);
                bool pidMatch = KNOWN_PIDS.Contains(attrs.ProductId);

                if (vidMatch && pidMatch)
                {
                    Console.WriteLine($"  -> RotaryUsb device found!");
                    return device;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  - Error reading device: {ex.Message}");
            }
        }

        return null;
    }

    // ========================================================================
    // GENERIC HID MODE — MAIN LOOP
    // ========================================================================

    private static void RunGenericHidMode(HidDevice device)
    {
        _hidDevice = device;

        try
        {
            device.OpenDevice(DeviceMode.Overlapped, DeviceMode.Overlapped, ShareMode.ShareRead | ShareMode.ShareWrite);
            if (!device.IsConnected)
            {
                Console.WriteLine("ERROR: Failed to open device.");
                return;
            }

            try { device.MonitorDeviceEvents = true; }
            catch (PlatformNotSupportedException) { /* WMI not available */ }

            // Request current config from device
            Console.WriteLine("Reading config from device...");
            SendCommand(CMD_READ_CONFIG);

            // Start background reader thread
            var cts = new CancellationTokenSource();
            var readerThread = new Thread(() => ReportReaderLoop(device, cts.Token))
            {
                IsBackground = true
            };
            readerThread.Start();

            // Wait briefly for config readback
            Thread.Sleep(500);
            if (!_configReceived)
            {
                Console.WriteLine("No config readback received, using defaults.");
                _deviceConfig = CreateDefaultConfig();
            }

            // Main menu loop
            RunMainMenu(device, cts);

            cts.Cancel();
            readerThread.Join(1000);
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

    private static DeviceConfig CreateDefaultConfig()
    {
        var cfg = new DeviceConfig();
        var preset = Presets["General Purpose"];
        for (int i = 0; i < NUM_ENCODERS; i++)
            cfg.Encoders[i] = preset.Clone();
        return cfg;
    }

    // ========================================================================
    // REPORT READER (background thread)
    // ========================================================================

    private static void ReportReaderLoop(HidDevice device, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && device.IsConnected)
        {
            var report = device.Read(100);
            if (report.Status != HidDeviceData.ReadStatus.Success || report.Data.Length < 2)
                continue;

            byte reportId = report.Data[0];

            if (reportId == REPORT_ID_POSITIONS
                && report.Data.Length >= LEGACY_POSITION_PAYLOAD_SIZE + 1)
            {
                // Input Report ID 0x01. HidLibrary prepends the report ID, so
                // buffer index = payload offset + 1.
                lock (_lock)
                {
                    for (int i = 0; i < NUM_ENCODERS; i++)
                        _encoderPositions[i] = BitConverter.ToInt32(report.Data, 1 + i * 4);
                    _buttonStates = report.Data[17];   // payload 16
                    _tierByte = report.Data[18];       // payload 17

                    if (report.Data.Length >= POSITION_PAYLOAD_SIZE + 1)
                    {
                        _firmwareHasMovement = true;
                        for (int i = 0; i < NUM_ENCODERS; i++)
                        {
                            int now = BitConverter.ToInt32(report.Data, 21 + i * 4); // payload 20-35
                            _movementRaw[i] = now;

                            if (_movementBaselined)
                            {
                                // unchecked: the accumulator wraps at 32 bits by
                                // design, and two's-complement subtraction yields
                                // the correct signed delta straight across the
                                // boundary. This is the pattern integrators copy.
                                int delta = unchecked(now - _movementLast[i]);
                                _hostAccumulated[i] += delta;
                            }
                            _movementLast[i] = now;
                        }
                        // Baseline on the first report so a device that was
                        // already spinning before we attached does not dump its
                        // whole history into the first delta.
                        _movementBaselined = true;
                    }
                }
            }
            else if (reportId == REPORT_ID_CONFIG && report.Data.Length >= FULL_CONFIG_SIZE + 1)
            {
                // Input Report ID 0x02: 106 bytes config readback
                var configData = new byte[FULL_CONFIG_SIZE];
                Array.Copy(report.Data, 1, configData, 0, FULL_CONFIG_SIZE);
                var parsed = DeviceConfig.Deserialize(configData);
                if (parsed != null)
                {
                    lock (_lock)
                    {
                        _deviceConfig = parsed;
                        _configReceived = true;
                    }
                }
            }
            else if (reportId == REPORT_ID_DIAG && report.Data.Length >= DIAG_PAYLOAD_SIZE + 1)
            {
                // Input Report ID 0x04: 56 bytes of decoder diagnostics.
                // HidLibrary prepends the report ID, so buffer index = payload offset + 1.
                lock (_lock)
                {
                    for (int i = 0; i < NUM_ENCODERS; i++)
                        _diagRawPins[i] = report.Data[1 + i];            // payload 0-3
                    _diagStepsPerDetent = report.Data[5];                // payload 4
                                                                          // payload 5-7 reserved
                    for (int i = 0; i < NUM_ENCODERS; i++)
                    {
                        _diagEdgeCount[i]    = BitConverter.ToUInt32(report.Data, 9  + i * 4);  // payload 8-23
                        _diagInvalidCount[i] = BitConverter.ToUInt32(report.Data, 25 + i * 4);  // payload 24-39
                        _diagDetentCount[i]  = BitConverter.ToUInt32(report.Data, 41 + i * 4);  // payload 40-55
                    }
                    _diagLastSeenUtc = DateTime.UtcNow;
                }
            }
        }
    }

    // ========================================================================
    // HID COMMUNICATION
    // ========================================================================

    private static void SendConfig(DeviceConfig config)
    {
        if (_hidDevice == null) return;
        var payload = config.Serialize();
        // HidLibrary Write: first byte is report ID
        var data = new byte[FULL_CONFIG_SIZE + 1];
        data[0] = REPORT_ID_CONFIG;
        Array.Copy(payload, 0, data, 1, FULL_CONFIG_SIZE);
        _hidDevice.Write(data);
    }

    private static void SendCommand(byte command)
    {
        if (_hidDevice == null) return;
        // Report ID + 2 bytes payload
        var data = new byte[3];
        data[0] = REPORT_ID_COMMAND;
        data[1] = command;
        data[2] = 0x00;
        _hidDevice.Write(data);
    }

    // ========================================================================
    // MAIN MENU
    // ========================================================================

    private static void RunMainMenu(HidDevice device, CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested && device.IsConnected)
        {
            Console.Clear();
            Console.WriteLine("RotaryUsb Configuration");
            Console.WriteLine("========================");

            var attrs = device.Attributes;
            Console.WriteLine($"Device connected: VID:0x{attrs.VendorId:X4} PID:0x{attrs.ProductId:X4}");
            Console.WriteLine();

            lock (_lock)
            {
                Console.WriteLine("Current encoder values:");
                for (int i = 0; i < NUM_ENCODERS; i++)
                {
                    var enc = _deviceConfig.Encoders[i];
                    int tier = (_tierByte >> (i * 2)) & 0x03;
                    string tierStr = tier > 0 ? $"  {new string('*', tier)} Tier {tier}" : "";
                    Console.WriteLine($"  Enc{i + 1}: {_encoderPositions[i],10}  [{enc.MinValue} - {enc.MaxValue}, step={enc.StepSize}]{tierStr}");
                }
                Console.WriteLine();
                Console.Write("  Buttons: ");
                for (int i = 0; i < NUM_ENCODERS; i++)
                {
                    bool pressed = (_buttonStates & (1 << i)) != 0;
                    Console.Write(pressed ? "[X] " : "[ ] ");
                }
                Console.WriteLine();
            }

            Console.WriteLine();
            Console.WriteLine("[M] Monitor - Live display of encoder values");
            Console.WriteLine("[C] Configure encoder");
            Console.WriteLine("[D] Diagnostics - Decoder counters and raw pin state");
            Console.WriteLine("[S] Save config to device flash");
            Console.WriteLine("[F] Factory reset (restore default config)");
            Console.WriteLine("[R] Reset positions");
            Console.WriteLine("[Q] Quit");
            Console.Write("\nChoice: ");

            var key = Console.ReadKey(true);
            switch (char.ToUpper(key.KeyChar))
            {
                case 'M':
                    RunMonitor(device, cts.Token);
                    break;
                case 'C':
                    RunConfigureEncoder(device);
                    break;
                case 'S':
                    SendCommand(CMD_SAVE_CONFIG);
                    Console.WriteLine("\nConfig save command sent.");
                    Thread.Sleep(1000);
                    break;
                case 'D':
                    RunDiagnostics(device, cts.Token);
                    break;
                case 'F':
                    SendCommand(CMD_RESET_DEFAULTS);
                    Console.WriteLine("\nFactory reset command sent.");
                    Thread.Sleep(500);
                    SendCommand(CMD_READ_CONFIG);
                    Thread.Sleep(500);
                    break;
                case 'R':
                    SendCommand(CMD_RESET_POSITIONS);
                    Console.WriteLine("\nReset positions command sent.");
                    Thread.Sleep(1000);
                    break;
                case 'Q':
                    return;
            }
        }
    }

    // ========================================================================
    // LIVE MONITOR
    // ========================================================================

    private static void RunMonitor(HidDevice device, CancellationToken ct)
    {
        Console.Clear();
        Console.WriteLine("Live Monitor (press any key to return)");
        Console.WriteLine("=======================================");

        while (!ct.IsCancellationRequested && device.IsConnected && !Console.KeyAvailable)
        {
            Console.SetCursorPosition(0, 2);

            lock (_lock)
            {
                Console.WriteLine("        Position         Range      Movement     Unbounded  Tier".PadRight(78));
                for (int i = 0; i < NUM_ENCODERS; i++)
                {
                    var enc = _deviceConfig.Encoders[i];
                    int tier = (_tierByte >> (i * 2)) & 0x03;
                    string tierStr = tier switch
                    {
                        1 => "*",
                        2 => "**",
                        3 => "***",
                        _ => ""
                    };
                    string range = $"[{enc.MinValue}-{enc.MaxValue}]";
                    string movement = _firmwareHasMovement ? _movementRaw[i].ToString() : "n/a";
                    string unbounded = _firmwareHasMovement ? _hostAccumulated[i].ToString() : "n/a";
                    Console.WriteLine(
                        $"Enc{i + 1}: {_encoderPositions[i],10}  {range,-12} {movement,12} {unbounded,13}  {tierStr,-3}"
                            .PadRight(78));
                }

                Console.WriteLine();
                Console.Write("Buttons: ");
                for (int i = 0; i < NUM_ENCODERS; i++)
                {
                    bool pressed = (_buttonStates & (1 << i)) != 0;
                    Console.Write(pressed ? "[X] " : "[ ] ");
                }
                Console.WriteLine("          ");

                Console.WriteLine();
                if (_firmwareHasMovement)
                {
                    Console.WriteLine("Turn a knob to its limit and keep turning:".PadRight(78));
                    Console.WriteLine("Position holds; Movement and Unbounded keep moving.".PadRight(78));
                }
                else
                {
                    Console.WriteLine("Firmware predates the movement accumulator (21-byte report).".PadRight(78));
                }
            }

            Thread.Sleep(50);
        }

        if (Console.KeyAvailable)
            Console.ReadKey(true); // consume the key
    }

    // ========================================================================
    // DECODER DIAGNOSTICS
    // ========================================================================

    private static void WriteLinePadded(string s) => Console.WriteLine(s.PadRight(78));

    private static void RunDiagnostics(HidDevice device, CancellationToken ct)
    {
        Console.Clear();
        bool? lastHadData = null;

        while (!ct.IsCancellationRequested && device.IsConnected)
        {
            bool everSeen;
            DateTime lastSeen;
            byte spd;
            byte globalFlags;
            var rawPins = new byte[NUM_ENCODERS];
            var edges = new uint[NUM_ENCODERS];
            var invalid = new uint[NUM_ENCODERS];
            var detents = new uint[NUM_ENCODERS];

            lock (_lock)
            {
                lastSeen = _diagLastSeenUtc;
                everSeen = _diagLastSeenUtc != DateTime.MinValue;
                spd = _diagStepsPerDetent;
                globalFlags = _deviceConfig.GlobalFlags;
                Array.Copy(_diagRawPins, rawPins, NUM_ENCODERS);
                Array.Copy(_diagEdgeCount, edges, NUM_ENCODERS);
                Array.Copy(_diagInvalidCount, invalid, NUM_ENCODERS);
                Array.Copy(_diagDetentCount, detents, NUM_ENCODERS);
            }

            // Only clear on a layout change; otherwise redraw in place to avoid flicker
            // while the user is counting detents.
            if (lastHadData != everSeen)
            {
                Console.Clear();
                lastHadData = everSeen;
            }
            Console.SetCursorPosition(0, 0);

            WriteLinePadded("Encoder Diagnostics");
            WriteLinePadded("===================");
            WriteLinePadded("");

            if (!everSeen)
            {
                WriteLinePadded("No diagnostic reports received (Input Report ID 0x04).");
                WriteLinePadded("");
                WriteLinePadded("  * This firmware build may predate report ID 0x04 - reflash the");
                WriteLinePadded("    .uf2 built from this branch.");
                WriteLinePadded("  * Or the keyboard-HID personality was flashed; rebuild with");
                WriteLinePadded("    cmake -DFIRMWARE_MODE=generic_hid ..");
                WriteLinePadded("");
                WriteLinePadded("Positions and config still work; only diagnostics are unavailable.");
                WriteLinePadded("");
                WriteLinePadded("");
                WriteLinePadded("");
            }
            else
            {
                double ageSec = (DateTime.UtcNow - lastSeen).TotalSeconds;
                string ageNote = ageSec > 2.0
                    ? $"STALE - last report {ageSec:F1}s ago (device hung or unplugged?)"
                    : $"updated {ageSec:F1}s ago";

                WriteLinePadded($"Firmware steps/detent: {spd}   (GlobalFlags bit 0 = {globalFlags & 0x01})   {ageNote}");
                WriteLinePadded("");
                WriteLinePadded("Enc    A  B  SW         Edges   Invalid   Detents   Edges/Detent");
                for (int i = 0; i < NUM_ENCODERS; i++)
                {
                    int a = (rawPins[i] >> 2) & 1;
                    int b = (rawPins[i] >> 1) & 1;
                    int sw = rawPins[i] & 1;
                    string ratio = detents[i] > 0
                        ? ((double)edges[i] / detents[i]).ToString("F2")
                        : "n/a";
                    WriteLinePadded($"  {i + 1}    {a}  {b}   {sw}   {edges[i],11}{invalid[i],10}{detents[i],10}{ratio,15}");
                }
                WriteLinePadded("");
                WriteLinePadded("Raw pins are sampled at 10 Hz: expect 7 (A=1 B=1 SW=1) at rest, even");
                WriteLinePadded("while spinning. Rotation shows up in the counters, not the pin bits.");
            }

            WriteLinePadded("");
            WriteLinePadded("[Z] Zero counters    [T] Toggle steps/detent (4<->2)");
            WriteLinePadded("[S] Save config to flash (persists the toggle)    [B] Back");

            // Poll for a keypress for ~400ms, then redraw.
            var deadline = DateTime.UtcNow.AddMilliseconds(400);
            while (DateTime.UtcNow < deadline)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    switch (char.ToUpper(key.KeyChar))
                    {
                        case 'Z':
                            SendCommand(CMD_RESET_DIAG);
                            Thread.Sleep(200);
                            break;
                        case 'T':
                            lock (_lock)
                            {
                                _deviceConfig.GlobalFlags ^= 0x01;
                                SendConfig(_deviceConfig);
                            }
                            Thread.Sleep(200);
                            SendCommand(CMD_READ_CONFIG);
                            Thread.Sleep(300);
                            // apply_config() does not reset the encoder's partial-step
                            // accumulator, so the first detent after a switch can land
                            // early or late by one. Zero the counters for a clean measurement.
                            SendCommand(CMD_RESET_DIAG);
                            Thread.Sleep(200);
                            break;
                        case 'S':
                            SendCommand(CMD_SAVE_CONFIG);
                            Thread.Sleep(500);
                            break;
                        case 'B':
                            return;
                    }
                    break;
                }
                Thread.Sleep(25);
            }
        }
    }

    // ========================================================================
    // CONFIGURE ENCODER
    // ========================================================================

    private static void RunConfigureEncoder(HidDevice device)
    {
        Console.Clear();
        Console.WriteLine("Configure Encoder");
        Console.WriteLine("=================");
        Console.Write("Select encoder [1-4]: ");

        var k = Console.ReadKey(true);
        int encIdx = k.KeyChar - '1';
        if (encIdx < 0 || encIdx >= NUM_ENCODERS)
        {
            Console.WriteLine("\nInvalid encoder number.");
            Thread.Sleep(1000);
            return;
        }
        Console.WriteLine(k.KeyChar);

        EncoderConfig working;
        lock (_lock)
        {
            working = _deviceConfig.Encoders[encIdx].Clone();
        }

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($"Encoder {encIdx + 1} current config:");
            Console.WriteLine($"  Min: {working.MinValue}  Max: {working.MaxValue}  Step: {working.StepSize}  " +
                            $"Wrap: {(working.Wrap ? "Yes" : "No")}  Reverse: {(working.Reverse ? "Yes" : "No")}");
            Console.Write("  ");
            for (int t = 0; t < NUM_TIERS; t++)
            {
                var tier = working.Tiers[t];
                if (tier.ThresholdMs > 0)
                    Console.Write($"Tier {t + 1}: <{tier.ThresholdMs}ms -> {tier.Multiplier}x    ");
                else
                    Console.Write($"Tier {t + 1}: disabled    ");
            }
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("[1] Min value          [5] Reverse");
            Console.WriteLine("[2] Max value          [6] Tier 1 (threshold, multiplier)");
            Console.WriteLine("[3] Step size          [7] Tier 2");
            Console.WriteLine("[4] Wrap on/off        [8] Tier 3");
            Console.WriteLine("[A] Apply (send to device)");
            Console.WriteLine("[P] Load preset");
            Console.WriteLine("[B] Back");
            Console.Write("\nChoice: ");

            var choice = Console.ReadKey(true);
            Console.WriteLine(choice.KeyChar);

            switch (char.ToUpper(choice.KeyChar))
            {
                case '1':
                    working.MinValue = ReadInt("Min value", working.MinValue);
                    break;
                case '2':
                    working.MaxValue = ReadInt("Max value", working.MaxValue);
                    break;
                case '3':
                    working.StepSize = ReadInt("Step size", working.StepSize);
                    break;
                case '4':
                    working.Wrap = !working.Wrap;
                    Console.WriteLine($"  Wrap: {(working.Wrap ? "Yes" : "No")}");
                    break;
                case '5':
                    working.Reverse = !working.Reverse;
                    Console.WriteLine($"  Reverse: {(working.Reverse ? "Yes" : "No")}");
                    break;
                case '6':
                    EditTier(working.Tiers[0], "Tier 1");
                    break;
                case '7':
                    EditTier(working.Tiers[1], "Tier 2");
                    break;
                case '8':
                    EditTier(working.Tiers[2], "Tier 3");
                    break;
                case 'A':
                    var validationError = ValidateEncoderConfig(working);
                    if (validationError != null)
                    {
                        Console.WriteLine($"  Validation error: {validationError}");
                        break;
                    }
                    lock (_lock)
                    {
                        _deviceConfig.Encoders[encIdx] = working;
                        SendConfig(_deviceConfig);
                    }
                    Console.WriteLine("  Config sent to device.");
                    // Readback to verify
                    Thread.Sleep(200);
                    SendCommand(CMD_READ_CONFIG);
                    Thread.Sleep(500);
                    break;
                case 'P':
                    var preset = SelectPreset();
                    if (preset != null)
                        working = preset.Clone();
                    break;
                case 'B':
                    return;
            }
        }
    }

    private static int ReadInt(string prompt, int current)
    {
        Console.Write($"  {prompt} [{current}]: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return current;
        if (int.TryParse(input, out int value)) return value;
        Console.WriteLine("  Invalid number, keeping current value.");
        return current;
    }

    private static string? ValidateEncoderConfig(EncoderConfig enc)
    {
        if (enc.MinValue >= enc.MaxValue)
            return $"min ({enc.MinValue}) must be less than max ({enc.MaxValue})";
        if (enc.StepSize <= 0)
            return $"step size ({enc.StepSize}) must be positive";

        // Check enabled tiers
        ushort prevThreshold = 0;
        ushort prevMultiplier = 0;
        bool hasPrev = false;
        for (int i = 0; i < NUM_TIERS; i++)
        {
            if (enc.Tiers[i].ThresholdMs > 0)
            {
                if (enc.Tiers[i].Multiplier == 0)
                    return $"tier {i + 1} has threshold but multiplier is 0";
                if (hasPrev)
                {
                    if (enc.Tiers[i].ThresholdMs >= prevThreshold)
                        return $"tier {i + 1} threshold must be less than previous enabled tier";
                    if (enc.Tiers[i].Multiplier <= prevMultiplier)
                        return $"tier {i + 1} multiplier must be greater than previous enabled tier";
                }
                prevThreshold = enc.Tiers[i].ThresholdMs;
                prevMultiplier = enc.Tiers[i].Multiplier;
                hasPrev = true;
            }
        }
        return null;
    }

    private static void EditTier(TierConfig tier, string name)
    {
        Console.Write($"  {name} threshold ms [{tier.ThresholdMs}] (0 to disable): ");
        var input = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(input) && ushort.TryParse(input, out ushort thresh))
            tier.ThresholdMs = thresh;

        if (tier.ThresholdMs > 0)
        {
            Console.Write($"  {name} multiplier [{tier.Multiplier}]: ");
            input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input) && ushort.TryParse(input, out ushort mult))
                tier.Multiplier = mult;
        }
    }

    private static EncoderConfig? SelectPreset()
    {
        Console.WriteLine("  Presets:");
        var presetNames = Presets.Keys.ToArray();
        for (int i = 0; i < presetNames.Length; i++)
            Console.WriteLine($"    [{i + 1}] {presetNames[i]}");
        Console.Write("  Select preset: ");

        var k = Console.ReadKey(true);
        Console.WriteLine(k.KeyChar);
        int idx = k.KeyChar - '1';
        if (idx >= 0 && idx < presetNames.Length)
        {
            Console.WriteLine($"  Loaded preset: {presetNames[idx]}");
            return Presets[presetNames[idx]];
        }
        Console.WriteLine("  Invalid selection.");
        return null;
    }

    // ========================================================================
    // KEYBOARD HID MODE IMPLEMENTATION
    // ========================================================================

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

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
            PostQuitMessage(0);
        };

        _hookCallback = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback,
            GetModuleHandle(curModule?.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
        {
            Console.WriteLine("Failed to install keyboard hook.");
            return;
        }

        Console.WriteLine("Keyboard hook installed. Waiting for events...");
        Console.WriteLine("-".PadRight(40, '-'));

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hookId);
        Console.WriteLine("Goodbye!");
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            string? action = (int)hookStruct.vkCode switch
            {
                VK_F1 => "Encoder 1: CW", VK_F2 => "Encoder 1: CCW",
                VK_F3 => "Encoder 2: CW", VK_F4 => "Encoder 2: CCW",
                VK_F5 => "Encoder 3: CW", VK_F6 => "Encoder 3: CCW",
                VK_F7 => "Encoder 4: CW", VK_F8 => "Encoder 4: CCW",
                VK_F9 => "Encoder 1: Button", VK_F10 => "Encoder 2: Button",
                VK_F11 => "Encoder 3: Button", VK_F12 => "Encoder 4: Button",
                _ => null
            };
            if (action != null)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {action}");
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }
}
