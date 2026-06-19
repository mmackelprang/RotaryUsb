// SPDX-FileCopyrightText: 2026 RotaryUsb Project
// SPDX-License-Identifier: Apache-2.0
//
// Encoder Monitor — a minimal, no-menu diagnostic for the RotaryUsb device.
// It streams live position + button state for all 4 encoders so you can verify
// wiring and firmware by hand:
//   * turn a knob  -> its Value should change (clockwise = increase)
//   * press a shaft -> its Button cell should light up
//   * spin fast     -> the accel tier (x2/x3) should appear
//
// It does NOT change or save any device config. The default firmware config is
// range 0-100 / step 1, so Values clamp at the 0 and 100 bounds (that's normal,
// not a fault). Press R to re-zero the positions between tests.

using System.Text;
using HidLibrary;

namespace EncoderMonitor;

internal static class Program
{
    // Generic-HID identifiers (same set the windows-example matches).
    private static readonly int[] KnownVids = { 0x239A, 0xCAFE };
    private static readonly int[] KnownPids = { 0x80F4, 0x4005 };
    private const short VendorUsagePage = unchecked((short)0xFF00);

    private const int NumEncoders = 4;

    // Report IDs / commands (from the firmware's HID protocol).
    private const byte ReportIdPositions = 0x01;
    private const byte ReportIdCommand = 0x03;
    private const byte CmdResetPositions = 0x03;

    private const int LineWidth = 70;

    // Shared state: written by the reader thread, read by the UI thread.
    private static readonly object Lock = new();
    private static readonly int[] Positions = new int[NumEncoders];
    private static readonly int[] LastDelta = new int[NumEncoders];
    private static readonly long[] CwUpdates = new long[NumEncoders];
    private static readonly long[] CcwUpdates = new long[NumEncoders];
    private static readonly long[] BtnPresses = new long[NumEncoders];
    private static byte ButtonStates;
    private static byte TierByte;
    private static long ReportCount;

    // Reader-thread tracking for edge/delta detection.
    private static readonly int[] PrevPositions = new int[NumEncoders];
    private static byte PrevButtons;
    private static bool Resync = true;              // skip the first delta after (re)sync
    private static volatile bool ResetTrackingRequested;

    private static HidDevice? _device;

    private static int Main()
    {
        Console.WriteLine("RotaryUsb Encoder Monitor");
        Console.WriteLine("=========================");
        Console.WriteLine("Searching for the device (VID 0xCAFE / PID 0x4005, usage page 0xFF00)...");

        var device = FindDevice();
        if (device == null)
        {
            Console.WriteLine();
            Console.WriteLine("No RotaryUsb generic-HID device found.");
            Console.WriteLine("  * Is it plugged in? (it should show in Device Manager as a HID-compliant");
            Console.WriteLine("    vendor-defined device, VID_CAFE/PID_4005)");
            Console.WriteLine("  * Is another app holding it open (e.g. the menu example)? Close it first.");
            return 1;
        }

        _device = device;
        device.OpenDevice(DeviceMode.Overlapped, DeviceMode.Overlapped, ShareMode.ShareRead | ShareMode.ShareWrite);
        if (!device.IsConnected)
        {
            Console.WriteLine("ERROR: found the device but could not open it (is another app using it?).");
            return 1;
        }

        int vid = device.Attributes.VendorId;
        int pid = device.Attributes.ProductId;

        var cts = new CancellationTokenSource();
        var reader = new Thread(() => ReaderLoop(device, cts.Token)) { IsBackground = true };
        reader.Start();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        bool hadCursor = true;
        try { hadCursor = Console.CursorVisible; Console.CursorVisible = false; } catch { /* redirected */ }

        try
        {
            UiLoop(device, vid, pid, cts);
        }
        finally
        {
            cts.Cancel();
            reader.Join(1000);
            try { device.CloseDevice(); } catch { /* ignore */ }
            try { Console.CursorVisible = hadCursor; } catch { /* ignore */ }
            Console.WriteLine();
            Console.WriteLine("Monitor closed.");
        }

        return 0;
    }

    private static HidDevice? FindDevice()
    {
        foreach (var d in HidDevices.Enumerate())
        {
            try
            {
                if (d.Capabilities.UsagePage != VendorUsagePage) continue;
                var a = d.Attributes;
                if (KnownVids.Contains(a.VendorId) && KnownPids.Contains(a.ProductId))
                    return d;
            }
            catch
            {
                // Some HID devices can't be queried without elevated access — skip them.
            }
        }
        return null;
    }

    private static void ReaderLoop(HidDevice device, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && device.IsConnected)
        {
            var report = device.Read(100);
            if (report.Status != HidDeviceData.ReadStatus.Success) continue;

            var data = report.Data;
            // HidLibrary prepends the report ID, so the 21-byte payload starts at data[1].
            if (data.Length < 22 || data[0] != ReportIdPositions) continue;

            lock (Lock)
            {
                ReportCount++;
                if (ResetTrackingRequested)
                {
                    Resync = true;
                    ResetTrackingRequested = false;
                }

                for (int i = 0; i < NumEncoders; i++)
                {
                    int val = BitConverter.ToInt32(data, 1 + i * 4);
                    if (!Resync)
                    {
                        int delta = val - PrevPositions[i];
                        if (delta != 0)
                        {
                            LastDelta[i] = delta;
                            if (delta > 0) CwUpdates[i]++;
                            else CcwUpdates[i]++;
                        }
                    }
                    PrevPositions[i] = val;
                    Positions[i] = val;
                }

                byte buttons = data[17];   // payload offset 16
                if (!Resync)
                {
                    for (int i = 0; i < NumEncoders; i++)
                    {
                        bool was = (PrevButtons & (1 << i)) != 0;
                        bool now = (buttons & (1 << i)) != 0;
                        if (now && !was) BtnPresses[i]++;   // count rising edges (press)
                    }
                }
                PrevButtons = buttons;
                ButtonStates = buttons;
                TierByte = data[18];        // payload offset 17

                Resync = false;
            }
        }
    }

    private static void UiLoop(HidDevice device, int vid, int pid, CancellationTokenSource cts)
    {
        Console.Clear();
        while (!cts.IsCancellationRequested)
        {
            if (!device.IsConnected)
            {
                Console.SetCursorPosition(0, 0);
                Console.WriteLine("Device disconnected.".PadRight(LineWidth));
                return;
            }

            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true).Key;
                switch (key)
                {
                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                        cts.Cancel();
                        return;
                    case ConsoleKey.R:
                        SendCommand(CmdResetPositions);
                        lock (Lock)
                        {
                            ResetTrackingRequested = true;
                            Array.Clear(CwUpdates);
                            Array.Clear(CcwUpdates);
                            Array.Clear(BtnPresses);
                            Array.Clear(LastDelta);
                        }
                        break;
                    case ConsoleKey.Z:
                        lock (Lock)
                        {
                            Array.Clear(CwUpdates);
                            Array.Clear(CcwUpdates);
                            Array.Clear(BtnPresses);
                            Array.Clear(LastDelta);
                        }
                        break;
                }
            }

            Render(vid, pid);
            Thread.Sleep(50);
        }
    }

    private static void Render(int vid, int pid)
    {
        int[] pos; int[] dl; long[] cw; long[] ccw; long[] bp; byte btn, tier; long rc;
        lock (Lock)
        {
            pos = (int[])Positions.Clone();
            dl = (int[])LastDelta.Clone();
            cw = (long[])CwUpdates.Clone();
            ccw = (long[])CcwUpdates.Clone();
            bp = (long[])BtnPresses.Clone();
            btn = ButtonStates;
            tier = TierByte;
            rc = ReportCount;
        }

        var sb = new StringBuilder();
        void Row(string s) => sb.Append(s.Length >= LineWidth ? s[..LineWidth] : s.PadRight(LineWidth)).Append('\n');

        Row($"RotaryUsb Encoder Monitor   VID:0x{vid:X4} PID:0x{pid:X4}   reports:{rc}");
        Row(new string('=', LineWidth));
        Row("  Enc        Value     LastD   Dir    CW    CCW    Button    Presses");
        Row(new string('-', LineWidth));
        for (int i = 0; i < NumEncoders; i++)
        {
            bool pressed = (btn & (1 << i)) != 0;
            int tierVal = (tier >> (i * 2)) & 0x03;
            string deltaStr = dl[i] > 0 ? $"+{dl[i]}" : dl[i].ToString();
            string dir = dl[i] > 0 ? "CW " : dl[i] < 0 ? "CCW" : " . ";
            string btnCell = pressed ? "[#] DN" : "[ ] up";
            string accel = tierVal > 0 ? $"  accel x{tierVal}" : "";
            Row($"  {i + 1,-3}  {pos[i],10}  {deltaStr,6}   {dir}  {cw[i],5}  {ccw[i],5}   {btnCell}  {bp[i],6}{accel}");
        }
        Row(new string('-', LineWidth));
        Row($"  raw: buttons=0x{btn:X2}  tier=0x{tier:X2}");
        Row("");
        Row("  Default config is range 0-100 step 1 -> Values clamp at 0/100 (normal).");
        Row("  [R] reset positions   [Z] zero counters   [Q/Esc] quit");

        try
        {
            Console.SetCursorPosition(0, 0);
            Console.Write(sb.ToString());
        }
        catch
        {
            // Window too small / resized mid-write — ignore this frame.
        }
    }

    private static void SendCommand(byte command)
    {
        if (_device == null) return;
        var data = new byte[] { ReportIdCommand, command, 0x00 };
        try { _device.Write(data); } catch { /* device may have gone away */ }
    }
}
