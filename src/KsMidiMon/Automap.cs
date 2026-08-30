using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KsMidiMon;

/// <summary>
/// A minimal Automap server: answers the synth's handshake and draws its display.
///
/// The synth exposes three MIDI ports as KS pins. Port 1 and 2 are ordinary MIDI;
/// Port 3 carries the Automap protocol and advertises a vendor subformat, which is
/// how we tell it apart. Everything the synth reports in Automap mode - encoders,
/// touch, buttons - arrives on the Port 3 read pin and nowhere else.
/// </summary>
internal static class Automap
{
    public const int DisplayWidth = 72;
    public const int FieldWidth = 9;

    // Protocol constants, all established by capturing the real Automap server.
    public static byte[] ModeOn => new byte[] { 0xF0, 0x00, 0x01, 0xF7 };
    public static byte[] ModeOff => new byte[] { 0xF0, 0x00, 0x00, 0xF7 };

    public const byte RowLabels = 0x00;
    public const byte RowValues = 0x01;

    /// <summary>F0 02 &lt;row&gt; &lt;pos&gt; &lt;ascii...&gt; F7</summary>
    public static byte[] DisplayWrite(byte row, byte pos, string text)
    {
        var body = new List<byte> { 0xF0, 0x02, row, pos };
        foreach (char c in text) body.Add((byte)(c < 32 || c > 126 ? ' ' : c));
        body.Add(0xF7);
        return body.ToArray();
    }

    /// <summary>Lay eight labels out into the 72-character line the synth expects.</summary>
    public static string Row(params string[] fields)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            string f = i < fields.Length ? fields[i] ?? "" : "";
            if (f.Length > FieldWidth) f = f.Substring(0, FieldWidth);
            int pad = FieldWidth - f.Length;
            int left = pad / 2;
            sb.Append(new string(' ', left)).Append(f).Append(new string(' ', pad - left));
        }
        return sb.ToString();
    }

    sealed class Port
    {
        public uint ReadPin, WritePin;
        public Guid SubFormat;
        public string Name = "";
    }

    /// <summary>Find the pin pair belonging to a named port, e.g. "Port 3".</summary>
    static Port FindPort(List<Pins.PinInfo> pins, string nameFragment)
    {
        var port = new Port { Name = nameFragment, ReadPin = uint.MaxValue, WritePin = uint.MaxValue };
        foreach (var p in pins)
        {
            if (!p.IsMusic || p.Name.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (p.Ranges.Count == 0) continue;
            if (p.DataFlow == Pins.DATAFLOW_IN && port.WritePin == uint.MaxValue)
            {
                port.WritePin = p.Id;
                port.SubFormat = p.Ranges[0].SubFormat;
            }
            else if (p.DataFlow == Pins.DATAFLOW_OUT && port.ReadPin == uint.MaxValue)
            {
                port.ReadPin = p.Id;
                port.SubFormat = p.Ranges[0].SubFormat;
            }
        }
        return port;
    }

    static IntPtr OpenPin(IntPtr filter, uint pinId, bool write, Guid sub, string what)
    {
        IntPtr pin = Ks.CreateMidiPin(filter, pinId, write, sub, out uint status);
        if (pin == IntPtr.Zero)
        {
            Console.WriteLine($"{what}: KsCreatePin(pin {pinId}) не удался, " +
                              $"NTSTATUS 0x{status:X8}, win32 {Marshal.GetLastWin32Error()}");
            return IntPtr.Zero;
        }
        foreach (uint state in new[] { Ks.KSSTATE_ACQUIRE, Ks.KSSTATE_PAUSE, Ks.KSSTATE_RUN })
            if (!Ks.SetPinState(pin, state, out int err))
                Console.WriteLine($"{what}: переход в состояние {state} не удался, ошибка {err}");
        Console.WriteLine($"{what}: пин {pinId} открыт и запущен");
        return pin;
    }

    public static int Run(string filterPath, string message, int seconds, string outPort = null)
    {
        if (!string.IsNullOrEmpty(outPort))
        {
            // Several destinations at once: the virtual port for the DAW, the synth's
            // own port to reach its physical DIN socket, whatever else is wanted.
            foreach (string name in outPort.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var mo = new MidiOut();
                if (mo.Open(name.Trim())) _outs.Add(mo);
            }
        }

        IntPtr filter = Ks.CreateFileW(filterPath, Ks.GENERIC_READ | Ks.GENERIC_WRITE,
            Ks.FILE_SHARE_READ | Ks.FILE_SHARE_WRITE, IntPtr.Zero,
            Ks.OPEN_EXISTING, 0, IntPtr.Zero);
        if (filter == IntPtr.Zero || filter == new IntPtr(-1))
        {
            Console.WriteLine("фильтр не открылся, ошибка " + Marshal.GetLastWin32Error());
            return 1;
        }

        try
        {
            var pins = Pins.Enumerate(filter, out string diag);
            Console.WriteLine(diag);
            var port = FindPort(pins, "Port 3");
            if (port.ReadPin == uint.MaxValue || port.WritePin == uint.MaxValue)
            {
                Console.WriteLine("не нашёл пару пинов Port 3 — Automap-порт недоступен");
                return 2;
            }
            Console.WriteLine($"Automap порт: чтение пин {port.ReadPin}, запись пин {port.WritePin}");
            Console.WriteLine($"формат: {port.SubFormat}");

            IntPtr wr = OpenPin(filter, port.WritePin, true, port.SubFormat, "запись");
            if (wr == IntPtr.Zero) return 3;
            IntPtr rd = OpenPin(filter, port.ReadPin, false, port.SubFormat, "чтение");
            if (rd == IntPtr.Zero) { Ks.CloseHandle(wr); return 4; }

            // Set before the reader starts: the synth often announces itself within
            // milliseconds, and the reader thread draws the display from this field.
            _message = message;
            _writePin = wr;

            var stop = new ManualResetEventSlim(false);
            var reader = new Thread(() => ReadLoop(rd, wr, stop)) { IsBackground = true };
            reader.Start();

            // Announce ourselves the way the real server does, twice.
            Console.WriteLine("\n>>> шлю F0 00 01 F7 (сервер здесь)");
            for (int i = 0; i < 2; i++)
            {
                if (!Ks.WriteMidi(wr, ModeOn, out int e))
                    Console.WriteLine($"    запись не удалась, ошибка {e}");
                Thread.Sleep(30);
            }

            Thread.Sleep(250);

            string labels = Row(SplitFields(message));
            Console.WriteLine($">>> пишу на дисплей: '{labels}'");
            if (!Ks.WriteMidi(wr, DisplayWrite(RowLabels, 0, labels), out int err1))
                Console.WriteLine($"    строка меток не ушла, ошибка {err1}");
            Thread.Sleep(60);
            if (!Ks.WriteMidi(wr, DisplayWrite(RowValues, 0, Row("0", "0", "0", "0", "0", "0", "0", "0")), out int err2))
                Console.WriteLine($"    строка значений не ушла, ошибка {err2}");

            Console.WriteLine($"\nслушаю {seconds} с — покрути ручки на синте\n");
            Thread.Sleep(seconds * 1000);
            stop.Set();
            Thread.Sleep(150);

            if (Calls > 0)
            {
                Console.WriteLine($"\nчтение: вызовов {Calls}, с данными {WithData}, пустых {Empty}");
                Console.WriteLine($"        среднее время вызова {TotalBlockedUs / Calls} мкс, " +
                                  $"худшее {WorstBlockedUs} мкс");
                Console.WriteLine(TotalBlockedUs / Calls > 200
                    ? "        вызов блокирующий — задержка минимальна, опрос не нужен"
                    : "        вызов возвращается сразу — нужен overlapped, иначе крутим вхолостую");
            }

            foreach (var o in _outs)
                Console.WriteLine($"отправлено в '{o.PortName}': {o.Sent} сообщений");

            Ks.CloseHandle(rd);
            Ks.CloseHandle(wr);
            return 0;
        }
        finally { foreach (var o in _outs) o.Dispose(); Ks.CloseHandle(filter); }
    }

    /// <summary>
    /// Light LEDs one at a time so a human can map code to lamp. The host lights a
    /// lamp with B0 &lt;code&gt; 01 and clears it with 00, using the same numbering the
    /// buttons report on channel 3 - codes 42..49 are the rings under the encoders.
    /// </summary>
    public static int Scan(string filterPath, int from, int to, int holdMs)
    {
        IntPtr filter = Ks.CreateFileW(filterPath, Ks.GENERIC_READ | Ks.GENERIC_WRITE,
            Ks.FILE_SHARE_READ | Ks.FILE_SHARE_WRITE, IntPtr.Zero,
            Ks.OPEN_EXISTING, 0, IntPtr.Zero);
        if (filter == IntPtr.Zero || filter == new IntPtr(-1))
        {
            Console.WriteLine("фильтр не открылся, ошибка " + Marshal.GetLastWin32Error());
            return 1;
        }
        try
        {
            var pins = Pins.Enumerate(filter, out _);
            var port = FindPort(pins, "Port 3");
            if (port.WritePin == uint.MaxValue) { Console.WriteLine("нет пина записи"); return 2; }

            IntPtr wr = OpenPin(filter, port.WritePin, true, port.SubFormat, "запись");
            if (wr == IntPtr.Zero) return 3;
            IntPtr rd = OpenPin(filter, port.ReadPin, false, port.SubFormat, "чтение");

            _message = "LED SCAN";
            _writePin = wr;
            var stop = new ManualResetEventSlim(false);
            if (rd != IntPtr.Zero)
                new Thread(() => ReadLoop(rd, wr, stop)) { IsBackground = true }.Start();

            Initialize(wr, _message);
            Thread.Sleep(500);

            Console.WriteLine($"\nсканирую светодиоды {from}..{to}, по {holdMs} мс каждый");
            Console.WriteLine("смотри на панель и запоминай, что загорается\n");

            for (int code = from; code <= to; code++)
            {
                Ks.WriteMidi(wr, new byte[] { 0xB0, (byte)code, 0x01 }, out _);
                Ks.WriteMidi(wr, DisplayWrite(RowValues, 0,
                    Row("LED", code.ToString(), "", "", "", "", "", "")), out _);
                Console.WriteLine($"  код {code,3}  горит");
                Thread.Sleep(holdMs);
                Ks.WriteMidi(wr, new byte[] { 0xB0, (byte)code, 0x00 }, out _);
                Thread.Sleep(120);
            }

            // Stay up as a server afterwards. Quitting here would leave the synth
            // without a server, and it would put "Automap is not running" on screen.
            Console.WriteLine("\nсканирование закончено, держу сервер — Ctrl+C чтобы выйти");
            _message = "SCAN DONE SERVER STILL UP";
            Initialize(wr, _message);
            while (!stop.IsSet) Thread.Sleep(500);

            if (rd != IntPtr.Zero) Ks.CloseHandle(rd);
            Ks.CloseHandle(wr);
            return 0;
        }
        finally { Ks.CloseHandle(filter); }
    }

    /// <summary>Chop a message into eight display fields, breaking on spaces.</summary>
    static string[] SplitFields(string message)
    {
        var fields = new string[8];
        var words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < 8; i++) fields[i] = i < words.Length ? words[i] : "";
        return fields;
    }

    // Read-path timing, so latency claims can be checked instead of guessed.
    public static long Calls, Empty, WithData, TotalBlockedUs, WorstBlockedUs;

    static void ReadLoop(IntPtr pin, IntPtr writePin, ManualResetEventSlim stop)
    {
        const int bufSize = 4096;
        IntPtr buffer = Marshal.AllocHGlobal(bufSize);
        int headerSize = Marshal.SizeOf<Ks.KSSTREAM_HEADER>();
        IntPtr header = Marshal.AllocHGlobal(headerSize);
        var pending = new List<byte>();
        var sw = new System.Diagnostics.Stopwatch();

        // The read thread must not be preempted by ordinary background work.
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }

        try
        {
            while (!stop.IsSet)
            {
                var hdr = new Ks.KSSTREAM_HEADER
                {
                    Size = (uint)headerSize,
                    FrameExtent = bufSize,
                    DataUsed = 0,
                    Data = buffer
                };
                Marshal.StructureToPtr(hdr, header, false);

                sw.Restart();
                bool ok = Ks.DeviceIoControl(pin, Ks.IOCTL_KS_READ_STREAM,
                    IntPtr.Zero, 0, header, (uint)headerSize, out _, IntPtr.Zero);
                sw.Stop();

                long us = sw.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
                Calls++;
                TotalBlockedUs += us;
                if (us > WorstBlockedUs) WorstBlockedUs = us;

                if (!ok)
                {
                    // Only back off if the call returned immediately; a blocking read
                    // already paces us, and sleeping on top of it would add latency.
                    Empty++;
                    if (us < 200) Thread.Sleep(1);
                    continue;
                }

                hdr = Marshal.PtrToStructure<Ks.KSSTREAM_HEADER>(header);
                int used = (int)hdr.DataUsed;
                if (used <= 0) { Empty++; if (us < 200) Thread.Sleep(1); continue; }
                WithData++;

                var raw = new byte[used];
                Marshal.Copy(buffer, raw, 0, used);

                // Strip the KSMUSICFORMAT block headers and keep the MIDI bytes.
                int off = 0;
                while (off + 8 <= raw.Length)
                {
                    uint count = BitConverter.ToUInt32(raw, off + 4);
                    off += 8;
                    if (count == 0 || off + count > raw.Length) break;
                    for (int i = 0; i < count; i++) pending.Add(raw[off + i]);
                    off += ((int)count + 3) & ~3;
                }
                Drain(pending, writePin);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(header);
        }
    }

    /// <summary>
    /// Decode whole messages out of the running byte stream. The device never uses
    /// running status, so every message carries its own status byte and is exactly
    /// three bytes long unless it is a SysEx.
    /// </summary>
    static void Drain(List<byte> buf, IntPtr writePin)
    {
        while (buf.Count > 0)
        {
            byte st = buf[0];
            if (st == 0xF0)
            {
                int end = buf.IndexOf(0xF7);
                if (end < 0) return;
                var sysex = buf.GetRange(0, end + 1).ToArray();
                buf.RemoveRange(0, end + 1);
                OnSysEx(sysex, writePin);
                continue;
            }
            if (st < 0x80) { buf.RemoveAt(0); continue; }
            if (buf.Count < 3) return;
            byte d1 = buf[1], d2 = buf[2];
            buf.RemoveRange(0, 3);
            OnChannelMessage(st, d1, d2);
        }
    }

    static string _message = "";
    static int _handshakes;
    static DateTime _lastInit = DateTime.MinValue;

    static void OnSysEx(byte[] sx, IntPtr writePin)
    {
        if (sx.Length == 4 && sx[1] == 0x00)
        {
            if (sx[2] == 0x01)
            {
                _handshakes++;
                // The synth repeats its announcement until it is satisfied; re-running
                // the whole init on every repeat would flood the wire, so rate limit it.
                if ((DateTime.Now - _lastInit).TotalMilliseconds > 700)
                {
                    _lastInit = DateTime.Now;
                    Console.WriteLine($"<<< синт в AUTOMAP (запрос {_handshakes}) — инициализирую");
                    Initialize(writePin, _message);
                }
            }
            else
            {
                Console.WriteLine("<<< синт вышел из AUTOMAP");
            }
            return;
        }
        Console.WriteLine("<<< SysEx " + BitConverter.ToString(sx).Replace("-", " "));
    }

    /// <summary>
    /// The sequence the real server performs on entering Automap mode: announce
    /// twice, poll the keyboard-state registers, then paint both display rows.
    /// </summary>
    public static void Initialize(IntPtr wr, string message)
    {
        for (int i = 0; i < 2; i++) { Ks.WriteMidi(wr, ModeOn, out _); Thread.Sleep(10); }

        // Channel 16 registers 0..6: the host writes zeros and the synth answers.
        foreach (byte reg in new byte[] { 0, 2, 1, 3, 4, 5, 6 })
        {
            Ks.WriteMidi(wr, new byte[] { 0xBF, reg, 0x00 }, out _);
            Thread.Sleep(4);
        }

        Thread.Sleep(30);
        string labels = Row(SplitFields(message));
        bool a = Ks.WriteMidi(wr, DisplayWrite(RowLabels, 0, labels), out int e1);
        Thread.Sleep(20);
        bool b = Ks.WriteMidi(wr, DisplayWrite(RowValues, 0,
            Row("0", "0", "0", "0", "0", "0", "0", "0")), out int e2);
        Console.WriteLine($">>> дисплей: метки {(a ? "ok" : "ошибка " + e1)}, " +
                          $"значения {(b ? "ok" : "ошибка " + e2)}");
        _writePin = wr;
        for (int i = 0; i < 8; i++) { DrawValue(i); Thread.Sleep(4); }
        Console.WriteLine($">>> написал: '{labels}'");
    }

    // The synth stores nothing: values, pages and labels all live here, exactly as
    // they did in the real Automap server.
    static readonly int[] _values = new int[10];
    static readonly bool[] _touched = new bool[10];
    static IntPtr _writePin = IntPtr.Zero;
    static readonly List<MidiOut> _outs = new();

    /// <summary>Encoder index to CC number. 0..7 are the display row, 8 the filter knob,
    /// 9 the patch dial; the defaults match what Automap ships with.</summary>
    public static readonly byte[] EncoderCc = { 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 };
    public const byte OutChannel = 0;   // status nibble, 0 = MIDI channel 1

    // Buttons go out as CC rather than notes: nothing lands in a recorded clip, and Live
    // maps them as switches. Own channel so they cannot collide with the encoders, and a
    // base offset so the numbers avoid the standardised low CCs (volume, modulation, ...).
    public const byte ButtonChannel = 1;   // MIDI channel 2
    public const byte ButtonCcBase = 20;   // button code 0..39 -> CC 20..59

    /// <summary>Redraw one encoder's value field: 8 characters, bracketed while touched.</summary>
    static void DrawValue(int enc)
    {
        if (_writePin == IntPtr.Zero || enc < 0 || enc > 7) return;
        string n = _values[enc].ToString();
        string cell;
        if (_touched[enc])
        {
            int pad = 6 - n.Length;
            if (pad < 0) { n = n.Substring(0, 6); pad = 0; }
            int l = pad / 2;
            cell = "[" + new string(' ', l) + n + new string(' ', pad - l) + "]";
        }
        else
        {
            int pad = 8 - n.Length;
            if (pad < 0) { n = n.Substring(0, 8); pad = 0; }
            int l = pad / 2;
            cell = new string(' ', l) + n + new string(' ', pad - l);
        }
        Ks.WriteMidi(_writePin, DisplayWrite(RowValues, (byte)(enc * FieldWidth), cell), out _);
    }

    static void OnChannelMessage(byte status, byte d1, byte d2)
    {
        int ch = (status & 0x0F) + 1;
        switch (ch)
        {
            case 1:
                int delta = d2 > 63 ? d2 - 128 : d2;
                if (d1 < _values.Length)
                {
                    _values[d1] = Math.Clamp(_values[d1] + delta, 0, 127);
                    DrawValue(d1);
                    // Straight out to the DAW, absolute value on the mapped CC.
                    foreach (var o in _outs)
                        o.Send((byte)(0xB0 | OutChannel), EncoderCc[d1], (byte)_values[d1]);
                    Console.WriteLine($"<<< энкодер {d1}  {delta:+#;-#;0}  = {_values[d1]}" +
                                      (_outs.Count > 0 ? $"  -> CC{EncoderCc[d1]}" : ""));
                }
                else Console.WriteLine($"<<< энкодер {d1}  {delta:+#;-#;0}");
                break;
            case 2:
                if (d1 < _touched.Length) { _touched[d1] = d2 != 0; DrawValue(d1); }
                Console.WriteLine($"<<< энкодер {d1}  {(d2 != 0 ? "касание" : "отпущен")}");
                break;
            case 3:
                byte bcc = (byte)(ButtonCcBase + d1);
                foreach (var o in _outs)
                    o.Send((byte)(0xB0 | ButtonChannel), bcc, (byte)(d2 != 0 ? 127 : 0));
                Console.WriteLine($"<<< кнопка {d1}  {(d2 != 0 ? "нажата" : "отпущена")}" +
                                  (_outs.Count > 0 ? $"  -> CC{bcc} ch{ButtonChannel + 1}" : ""));
                break;
            case 4:
                Console.WriteLine($"<<< аналог {d1} = {d2}");
                break;
            case 16:
                string what = d1 switch
                {
                    0 => "канал клавиатуры",
                    1 => "октава",
                    2 => "транспонирование",
                    3 => "aftertouch",
                    _ => "регистр " + d1
                };
                int shown = (d1 == 1 || d1 == 2) ? d2 - 64 : d2;
                Console.WriteLine($"<<< {what} = {shown}");
                break;
            default:
                Console.WriteLine($"<<< ch{ch} {d1} {d2}");
                break;
        }
    }
}
