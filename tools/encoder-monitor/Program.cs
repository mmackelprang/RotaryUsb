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
// The firmware only sends a USB report WHEN something changes, so at idle every
// value reads 0 and "reports" stays flat — that is normal, not a fault.
//
// DEVICE LINK probe: on startup the monitor sends a READ_CONFIG command and
// waits for the device's config-readback reply. That round-trip does not involve
// the encoders at all, so it isolates faults:
//   * LINK ok but positions never move  -> encoder/GPIO/wiring (device side)
//   * LINK never replies                -> firmware not running / USB read path

using System.Diagnostics;
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
    private const byte ReportIdConfig = 0x02;
    private const byte ReportIdCommand = 0x03;
    private const byte CmdResetPositions = 0x03;
    private const byte CmdReadConfig = 0x04;
    private const int FullConfigSize = 106;

    private const int LineWidth = 74;

    // Shared state: written by the reader thread, read by the UI thread.
    private static readonly object Lock = new();
    private static readonly int[] Positions = new int[NumEncoders];
    private static readonly int[] LastDelta = new int[NumEncoders];
    private static readonly long[] CwUpdates = new long[NumEncoders];
    private static readonly long[] CcwUpdates = new long[NumEncoders];
    private static readonly long[] BtnPresses = new long[NumEncoders];
    private static byte ButtonStates;
    private static byte TierByte;

    private static long PosReports;     // count of position reports (ID 0x01)
    private static long CfgReports;     // count of config readbacks (ID 0x02)
    private static long AnyReports;     // count of ALL input reports of any ID
    private static byte LastUnknownId;  // last report ID we didn't recognize (0 = none)

    // Device-link probe results (config readback).
    private static bool ConfigReceived;
    private static byte CfgVersion;
    private static int Enc1Min, Enc1Max, Enc1Step;

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

        // Probe the device link immediately (independent of the encoders).
        SendCommand(CmdReadConfig);

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
            if (data.Length < 1) continue;
            byte id = data[0];

            lock (Lock)
            {
                AnyReports++;

                if (id == ReportIdPositions && data.Length >= 22)
                {
                    PosReports++;
                    HandlePositionReport(data);
                }
                else if (id == ReportIdConfig && data.Length >= FullConfigSize + 1)
                {
                    CfgReports++;
                    // Config payload starts at data[1]: [version][globalflags] then
                    // 4 x 26-byte encoder blocks (min,max,step int32 first).
                    CfgVersion = data[1];
                    Enc1Min = BitConverter.ToInt32(data, 1 + 2 + 0);
                    Enc1Max = BitConverter.ToInt32(data, 1 + 2 + 4);
                    Enc1Step = BitConverter.ToInt32(data, 1 + 2 + 8);
                    ConfigReceived = true;
                }
                else
                {
                    LastUnknownId = id;
                }
            }
        }
    }

    // Caller holds Lock.
    private static void HandlePositionReport(byte[] data)
    {
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

    private static void UiLoop(HidDevice device, int vid, int pid, CancellationTokenSource cts)
    {
        Console.Clear();
        var linkRetry = Stopwatch.StartNew();

        while (!cts.IsCancellationRequested)
        {
            if (!device.IsConnected)
            {
                Console.SetCursorPosition(0, 0);
                Console.WriteLine("Device disconnected.".PadRight(LineWidth));
                return;
            }

            // Keep re-probing the link until the device replies (covers a dropped first packet).
            bool gotConfig;
            lock (Lock) { gotConfig = ConfigReceived; }
            if (!gotConfig && linkRetry.ElapsedMilliseconds > 1000)
            {
                SendCommand(CmdReadConfig);
                linkRetry.Restart();
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
                    case ConsoleKey.C:
                        SendCommand(CmdReadConfig);
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
        int[] pos; int[] dl; long[] cw; long[] ccw; long[] bp; byte btn, tier;
        long posRep, cfgRep, anyRep; bool cfgOk; byte cfgVer, unkId; int e1Min, e1Max, e1Step;
        lock (Lock)
        {
            pos = (int[])Positions.Clone();
            dl = (int[])LastDelta.Clone();
            cw = (long[])CwUpdates.Clone();
            ccw = (long[])CcwUpdates.Clone();
            bp = (long[])BtnPresses.Clone();
            btn = ButtonStates;
            tier = TierByte;
            posRep = PosReports; cfgRep = CfgReports; anyRep = AnyReports;
            cfgOk = ConfigReceived; cfgVer = CfgVersion; unkId = LastUnknownId;
            e1Min = Enc1Min; e1Max = Enc1Max; e1Step = Enc1Step;
        }

        var sb = new StringBuilder();
        void Row(string s) => sb.Append(s.Length >= LineWidth ? s[..LineWidth] : s.PadRight(LineWidth)).Append('\n');

        Row($"RotaryUsb Encoder Monitor   VID:0x{vid:X4} PID:0x{pid:X4}");
        Row(new string('=', LineWidth));
        // Device-link probe line — the key diagnostic.
        if (cfgOk)
            Row($"  DEVICE LINK: OK  (config v{cfgVer}, Enc1 range {e1Min}..{e1Max} step {e1Step})");
        else
            Row("  DEVICE LINK: *** NO REPLY YET *** (sent READ_CONFIG; retrying every 1s)");
        Row($"  reports rx -> positions:{posRep}   config:{cfgRep}   total:{anyRep}"
            + (unkId != 0 ? $"   (saw unknown id 0x{unkId:X2})" : ""));
        Row(new string('-', LineWidth));
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
        Row("  Idle = all zeros (device only reports on change) — that's normal.");
        Row("  [R] reset positions   [C] re-probe link   [Z] zero counters   [Q] quit");

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
