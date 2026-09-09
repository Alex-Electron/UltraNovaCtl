using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace KsMidiMon;

/// <summary>
/// Probe for the instrument's private editor port.
///
/// The filter publishes Port 1 twice - a standard-MIDI pair the system claims as the
/// WinMM device, and a private pair behind Novation's own subformat - but Port 2 and
/// Port 3 exist only privately, with no public counterpart at all. Port 3 is Automap.
/// Port 2 is the one the native editor uses, which is how it edits parameters while the
/// DAW keeps the WinMM port and while GLOBAL MIDI Out is off: the gate is on the public
/// port, and Windows cannot even see this one.
///
/// Opening the pin is not enough. Decompiling the native plug-in showed it sends a
/// transport-level enable, F0 01 00 01 F7, as the first thing after its output
/// connection opens, and a matching disable, F0 01 00 00 F7, on teardown. Manufacturer
/// byte 01 means the HIDI transport consumes these, not the synth. Then it says hello
/// with a 14-byte 0x60 and asks for the edit buffer with 0x40. This probe reproduces
/// that sequence and prints whatever comes back.
///
/// Nothing here writes to the instrument's memory or sound: the enable and disable are
/// transport framing, and 0x60 and 0x40 are the status and dump requests already used
/// by the existing read-only probes.
/// </summary>
internal static class EditorPort
{
    static readonly byte[] TransportEnable = { 0xF0, 0x01, 0x00, 0x01, 0xF7 };
    static readonly byte[] TransportDisable = { 0xF0, 0x01, 0x00, 0x00, 0xF7 };

    static int _total, _dumps;

    /// <summary>A 14-byte command in the UltraNova patch protocol.</summary>
    static byte[] Command(byte cmd, byte ctrl, byte bank = 0, byte prog = 0) => new byte[]
    {
        0xF0, 0x00, 0x20, 0x29, 0x03, 0x01, 0x7F, cmd, ctrl, 0x00, 0x00, bank, prog, 0xF7
    };

    public static int Probe(string filterPath, string portName, int seconds)
    {
        IntPtr filter = Ks.CreateFileW(filterPath, Ks.GENERIC_READ | Ks.GENERIC_WRITE,
            Ks.FILE_SHARE_READ | Ks.FILE_SHARE_WRITE, IntPtr.Zero,
            Ks.OPEN_EXISTING, 0, IntPtr.Zero);
        if (filter == IntPtr.Zero || filter == new IntPtr(-1))
        {
            Console.WriteLine("фильтр не открылся, ошибка " + Marshal.GetLastWin32Error());
            return 1;
        }

        IntPtr wr = IntPtr.Zero, rd = IntPtr.Zero;
        try
        {
            var pins = Pins.Enumerate(filter, out string diag);
            Console.WriteLine(diag);

            uint readPin = uint.MaxValue, writePin = uint.MaxValue;
            Guid sub = Guid.Empty;

            // "read:write" names an explicit pin pair. Port 1 is published twice, so a
            // name match alone cannot say whether the public pair or the private one is
            // wanted, and the private pair is the interesting one.
            int colon = portName.IndexOf(':');
            if (colon > 0 && uint.TryParse(portName.Substring(0, colon), out uint rp)
                          && uint.TryParse(portName.Substring(colon + 1), out uint wp))
            {
                foreach (var p in pins)
                {
                    if (p.Ranges.Count == 0) continue;
                    if (p.Id == rp) { readPin = rp; sub = p.Ranges[0].SubFormat; }
                    if (p.Id == wp) { writePin = wp; sub = p.Ranges[0].SubFormat; }
                }
            }
            else
            foreach (var p in pins)
            {
                if (!p.IsMusic || p.Ranges.Count == 0) continue;
                if (p.Name.IndexOf(portName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (p.DataFlow == Pins.DATAFLOW_IN && writePin == uint.MaxValue)
                { writePin = p.Id; sub = p.Ranges[0].SubFormat; }
                else if (p.DataFlow == Pins.DATAFLOW_OUT && readPin == uint.MaxValue)
                { readPin = p.Id; sub = p.Ranges[0].SubFormat; }
            }
            if (readPin == uint.MaxValue || writePin == uint.MaxValue)
            {
                Console.WriteLine("не нашёл пару пинов для порта " + portName);
                return 2;
            }
            Console.WriteLine($"{portName}: чтение пин {readPin}, запись пин {writePin}");
            Console.WriteLine($"формат: {sub}");

            wr = Open(filter, writePin, true, sub, "запись");
            if (wr == IntPtr.Zero) return 3;
            rd = Open(filter, readPin, false, sub, "чтение");
            if (rd == IntPtr.Zero) return 4;

            var stop = new ManualResetEventSlim(false);
            var reader = new Thread(() => Read(rd, stop)) { IsBackground = true };
            reader.Start();
            Thread.Sleep(120);

            Send(wr, TransportEnable, "F0 01 00 01 F7 - транспорт включить");
            Thread.Sleep(80);
            Send(wr, Command(0x60, 0x21), "0x60 ctrl 0x21 - редактор представился");
            Thread.Sleep(150);
            Send(wr, Command(0x40, 0x00), "0x40 - дай текущий буфер редактирования");

            Console.WriteLine($"\nслушаю {seconds} с - покрути ручку и смени патч\n");
            Thread.Sleep(seconds * 1000);
            stop.Set();
            Thread.Sleep(200);

            Console.WriteLine($"\nИТОГО: сообщений {_total}, из них 526-байтных дампов {_dumps}");
            Send(wr, TransportDisable, "F0 01 00 00 F7 - транспорт выключить");
            Thread.Sleep(60);
            return 0;
        }
        finally
        {
            if (rd != IntPtr.Zero) Close(rd);
            if (wr != IntPtr.Zero) Close(wr);
            Ks.CloseHandle(filter);
        }
    }

    static IntPtr Open(IntPtr filter, uint pinId, bool write, Guid sub, string what)
    {
        IntPtr pin = Ks.CreateMidiPin(filter, pinId, write, sub, out uint status);
        if (pin == IntPtr.Zero)
        {
            Console.WriteLine($"{what}: KsCreatePin(пин {pinId}) не удался, NTSTATUS 0x{status:X8}");
            return IntPtr.Zero;
        }
        foreach (uint state in new[] { Ks.KSSTATE_ACQUIRE, Ks.KSSTATE_PAUSE, Ks.KSSTATE_RUN })
            if (!Ks.SetPinState(pin, state, out int e))
                Console.WriteLine($"{what}: состояние {state} не встало, ошибка {e}");
        Console.WriteLine($"{what}: пин {pinId} открыт и запущен");
        return pin;
    }

    static void Close(IntPtr pin)
    {
        foreach (uint state in new[] { Ks.KSSTATE_PAUSE, Ks.KSSTATE_ACQUIRE, Ks.KSSTATE_STOP })
            Ks.SetPinState(pin, state, out _);
        Ks.CloseHandle(pin);
    }

    static void Send(IntPtr pin, byte[] midi, string label)
    {
        bool ok = Ks.WriteMidi(pin, midi, out int err);
        Console.WriteLine(">>> " + label + (ok ? "" : "  - НЕ УШЛО, ошибка " + err));
    }

    static void Read(IntPtr pin, ManualResetEventSlim stop)
    {
        const int bufSize = 4096;
        IntPtr buffer = Marshal.AllocHGlobal(bufSize);
        int headerSize = Marshal.SizeOf<Ks.KSSTREAM_HEADER>();
        IntPtr header = Marshal.AllocHGlobal(headerSize);
        var pending = new List<byte>();
        try
        {
            while (!stop.IsSet)
            {
                var hdr = new Ks.KSSTREAM_HEADER
                { Size = (uint)headerSize, FrameExtent = bufSize, DataUsed = 0, Data = buffer };
                Marshal.StructureToPtr(hdr, header, false);

                bool ok = Ks.DeviceIoControl(pin, Ks.IOCTL_KS_READ_STREAM,
                    IntPtr.Zero, 0, header, (uint)headerSize, out _, IntPtr.Zero);
                if (!ok) { Thread.Sleep(1); continue; }

                hdr = Marshal.PtrToStructure<Ks.KSSTREAM_HEADER>(header);
                int used = (int)hdr.DataUsed;
                if (used <= 0) { Thread.Sleep(1); continue; }

                var raw = new byte[used];
                Marshal.Copy(buffer, raw, 0, used);

                // Strip KSMUSICFORMAT block headers, keep the MIDI bytes.
                int off = 0;
                while (off + 8 <= raw.Length)
                {
                    int count = BitConverter.ToInt32(raw, off + 4);
                    off += 8;
                    if (count <= 0 || off + count > raw.Length) break;
                    for (int i = 0; i < count; i++) pending.Add(raw[off + i]);
                    off += (count + 3) & ~3;
                }

                Drain(pending);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(header); }
    }

    static void Drain(List<byte> pending)
    {
        while (pending.Count > 0)
        {
            if (pending[0] == 0xF0)
            {
                int end = pending.IndexOf(0xF7);
                if (end < 0) return;                       // incomplete, wait for more
                var sx = pending.GetRange(0, end + 1).ToArray();
                pending.RemoveRange(0, end + 1);
                _total++;
                if (sx.Length == 526) _dumps++;
                Console.WriteLine($"RX SysEx {sx.Length} байт: {Hex(sx, 16)}" +
                                  (sx.Length == 526 ? "  <-- ПАТЧ-ДАМП" : ""));
            }
            else if (pending[0] >= 0x80)
            {
                int len = 1;
                byte st = (byte)(pending[0] & 0xF0);
                if (st == 0xC0 || st == 0xD0) len = 2;
                else if (st >= 0x80 && st <= 0xEF) len = 3;
                if (pending.Count < len) return;
                var msg = pending.GetRange(0, len).ToArray();
                pending.RemoveRange(0, len);
                _total++;
                Console.WriteLine("RX " + Channel(msg));
            }
            else
            {
                // Stray data byte with no status in front of it: report and drop one.
                Console.WriteLine($"RX одинокий байт данных {pending[0]:X2}");
                pending.RemoveAt(0);
                _total++;
            }
        }
    }

    static string Channel(byte[] m)
    {
        int ch = (m[0] & 0x0F) + 1;
        switch (m[0] & 0xF0)
        {
            case 0x80: return $"Note Off {m[1]} · ch {ch} · {m[2]}";
            case 0x90: return $"Note On  {m[1]} · ch {ch} · {m[2]}";
            case 0xA0: return $"Poly Pressure {m[1]} · ch {ch} · {m[2]}";
            case 0xB0: return $"CC# {m[1]:000} · ch {ch} · {m[2]}";
            case 0xC0: return $"Program {m[1]} · ch {ch}";
            case 0xD0: return $"Channel Pressure · ch {ch} · {m[1]}";
            case 0xE0: return $"Pitch Bend · ch {ch} · {(m[2] << 7) | m[1]}";
            default: return Hex(m, 3);
        }
    }

    static string Hex(byte[] b, int max)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < b.Length && i < max; i++) sb.Append(b[i].ToString("X2")).Append(' ');
        if (b.Length > max) sb.Append("…");
        return sb.ToString().TrimEnd();
    }
}
