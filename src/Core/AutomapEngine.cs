using System.Linq;
using System.Runtime.InteropServices;

namespace UltraNovaCtl.Core;

public sealed class EncoderEventArgs : EventArgs
{
    public int Index;
    public int Delta;
    public int Value;
}

public sealed class ButtonEventArgs : EventArgs
{
    public int Code;
    public bool Pressed;

    /// <summary>What was actually sent, or null when the control is silent. In toggle
    /// and step modes this is not simply 127/0, so it is worth showing.</summary>
    public int? Sent;
}

public sealed class TouchEventArgs : EventArgs
{
    public int Index;
    public bool Touched;
}

/// <summary>Channel 16 carries the synth's keyboard settings, the ones the original
/// editor showed in its header: 0 kbd channel, 1 octave, 2 transpose, 3 aftertouch.</summary>
/// <summary>A wheel or pedal moved; code is the number the synth uses on channel 4.</summary>
public sealed class AnalogEventArgs : EventArgs
{
    public int Code;
    public int Value;
}

public sealed class KeyboardStateEventArgs : EventArgs
{
    public int Register;
    public int Value;
}

/// <summary>
/// Live connection to the synth in Automap mode: answers the handshake, keeps the
/// display and values in step, and raises an event for everything the panel does.
///
/// One instance owns the KS pins, so only one may run at a time - the synth accepts a
/// single server, exactly as the original Automap did.
/// </summary>
public sealed class AutomapEngine : IDisposable
{
    public const int EncoderCount = 10;
    public const int FieldWidth = 9;
    public const int DisplayWidth = 72;

    static readonly byte[] ModeOn = { 0xF0, 0x00, 0x01, 0xF7 };
    static readonly byte[] ModeOff = { 0xF0, 0x00, 0x00, 0xF7 };

    public event EventHandler<EncoderEventArgs> EncoderMoved;
    public event EventHandler<TouchEventArgs> EncoderTouched;
    public event EventHandler<ButtonEventArgs> ButtonChanged;
    public event EventHandler<bool> ModeChanged;
    public event EventHandler<KeyboardStateEventArgs> KeyboardState;
    public event EventHandler<AnalogEventArgs> AnalogMoved;

    /// <summary>Anything arriving on the synth's ordinary MIDI port.</summary>
    public event EventHandler<MidiInEventArgs> PortMidi;
    public event EventHandler<string> Log;

    readonly int[] _values = new int[EncoderCount];
    readonly bool[] _touched = new bool[EncoderCount];
    readonly Dictionary<int, bool> _toggles = new();
    readonly Dictionary<int, int> _steps = new();
    readonly List<MidiOut> _outs = new();
    readonly object _writeLock = new();

    IntPtr _filter = IntPtr.Zero, _readPin = IntPtr.Zero, _writePin = IntPtr.Zero;
    IntPtr _midiPin = IntPtr.Zero;      // Port 1: notes, wheels, aftertouch
    Thread _reader, _painter, _midiReader;
    volatile bool _stop;

    // Display updates are slow (a USB write each), so they never happen on the read
    // thread. Pending redraws collapse per field: turning a knob fast repaints once
    // with the final value instead of queueing forty writes.
    readonly bool[] _dirty = new bool[EncoderCount];
    readonly AutoResetEvent _paintWake = new(false);
    volatile bool _repaintAll;

    public Config Config { get; set; } = Config.CreateDefault();
    public int BankIndex { get; private set; }
    public int PageIndex { get; private set; }
    public bool Connected { get; private set; }
    public bool AutomapActive { get; private set; }

    /// <summary>Raised when the panel itself changed bank or page, so the UI can follow.</summary>
    public event EventHandler SelectionChanged;

    public Bank CurrentBank =>
        Config.Banks.Count > 0
            ? Config.Banks[Math.Clamp(BankIndex, 0, Config.Banks.Count - 1)]
            : new Bank();

    public Page CurrentPage
    {
        get
        {
            var b = CurrentBank;
            return b.Pages.Count > 0
                ? b.Pages[Math.Clamp(PageIndex, 0, b.Pages.Count - 1)]
                : new Page();
        }
    }

    /// <summary>Zero every encoder and repaint, without touching the assignments.</summary>
    public void ResetValues()
    {
        Array.Clear(_values);
        _repaintAll = true;
        _paintWake.Set();
    }

    public int ValueOf(int encoder) =>
        encoder >= 0 && encoder < EncoderCount ? _values[encoder] : 0;

    void Say(string s) => Log?.Invoke(this, s);

    // ---- lifecycle ---------------------------------------------------------

    public bool Start(string filterMatch = "vid_1235")
    {
        string path = FindFilter(filterMatch);
        if (path == null) { Say($"no KS filter matching '{filterMatch}'"); return false; }

        _filter = Ks.CreateFileW(path, Ks.GENERIC_READ | Ks.GENERIC_WRITE,
            Ks.FILE_SHARE_READ | Ks.FILE_SHARE_WRITE, IntPtr.Zero, Ks.OPEN_EXISTING, 0, IntPtr.Zero);
        if (_filter == IntPtr.Zero || _filter == new IntPtr(-1))
        {
            Say($"could not open filter, error {Marshal.GetLastWin32Error()}");
            _filter = IntPtr.Zero;
            return false;
        }

        var pins = Pins.Enumerate(_filter, out string diag);
        Say(diag);

        uint readPinId = uint.MaxValue, writePinId = uint.MaxValue;
        Guid sub = Guid.Empty;
        foreach (var p in pins)
        {
            if (!p.IsMusic || p.Ranges.Count == 0) continue;
            if (p.Name.IndexOf("Port 3", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (p.DataFlow == Pins.DATAFLOW_IN && writePinId == uint.MaxValue)
            { writePinId = p.Id; sub = p.Ranges[0].SubFormat; }
            else if (p.DataFlow == Pins.DATAFLOW_OUT && readPinId == uint.MaxValue)
            { readPinId = p.Id; sub = p.Ranges[0].SubFormat; }
        }
        if (readPinId == uint.MaxValue || writePinId == uint.MaxValue)
        {
            Say("Automap port (Port 3) not found");
            Stop();
            return false;
        }
        Say($"Automap: read pin {readPinId}, write pin {writePinId}");

        _writePin = OpenPin(writePinId, true, sub);
        _readPin = OpenPin(readPinId, false, sub);
        if (_writePin == IntPtr.Zero || _readPin == IntPtr.Zero) { Stop(); return false; }

        // Port 1 carries the keyboard, wheels and aftertouch. Optional: if the driver
        // will not give us a second reader we simply carry on without it.
        foreach (var p1 in pins)
        {
            if (!p1.IsMusic || p1.Ranges.Count == 0) continue;
            if (p1.Name.IndexOf("Port 1", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (p1.DataFlow != Pins.DATAFLOW_OUT) continue;
            _midiPin = OpenPin(p1.Id, false, p1.Ranges[0].SubFormat);
            if (_midiPin != IntPtr.Zero)
            {
                Say($"MIDI port pin {p1.Id} open (notes, wheels, aftertouch)");
                break;
            }
        }
        if (_midiPin == IntPtr.Zero)
            Say("MIDI port pin unavailable - notes and wheels will not be shown");

        OpenOutputs();

        _stop = false;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "automap-read" };
        _reader.Start();
        _painter = new Thread(PaintLoop) { IsBackground = true, Name = "automap-paint" };
        _painter.Start();
        if (_midiPin != IntPtr.Zero)
        {
            _midiReader = new Thread(MidiPortLoop) { IsBackground = true, Name = "midi-port-read" };
            _midiReader.Start();
        }

        Connected = true;
        Announce();          // the synth answers with its current mode
        return true;
    }

    void OpenOutputs()
    {
        foreach (var o in _outs) o.Dispose();
        _outs.Clear();
        foreach (string name in (Config.OutputPort ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var mo = new MidiOut();
            if (mo.Open(name.Trim())) { _outs.Add(mo); Say($"MIDI out: {mo.PortName}"); }
            else Say("MIDI out: " + (mo.LastError.Length > 0
                ? mo.LastError
                : $"'{name.Trim()}' not found"));
        }
    }

    /// <summary>
    /// Send a known message so it can be seen whether our end of the port works. If a
    /// monitor shows this but the DAW does not, the break is inside the DAW and only it
    /// can re-establish the connection.
    /// </summary>
    public bool SendTest(int channel = 1, int cc = 21)
    {
        if (_outs.Count == 0) { Say("no MIDI output open"); return false; }
        byte st = (byte)(0xB0 | (Math.Clamp(channel, 1, 16) - 1));
        foreach (var o in _outs)
        {
            o.Send(st, (byte)cc, 127);
            Thread.Sleep(60);
            o.Send(st, (byte)cc, 0);
        }
        Say($"test sent: CC {cc} on channel {channel}, value 127 then 0");
        return true;
    }

    /// <summary>Re-open the MIDI outputs after the port setting changed.</summary>
    public void ReopenOutputs() => OpenOutputs();

    IntPtr OpenPin(uint id, bool write, Guid sub)
    {
        IntPtr pin = Ks.CreateMidiPin(_filter, id, write, sub, out uint status);
        if (pin == IntPtr.Zero)
        {
            Say($"pin {id} could not be created, NTSTATUS 0x{status:X8}");
            return IntPtr.Zero;
        }
        foreach (uint st in new[] { Ks.KSSTATE_ACQUIRE, Ks.KSSTATE_PAUSE, Ks.KSSTATE_RUN })
            Ks.SetPinState(pin, st, out _);
        return pin;
    }

    public void Stop()
    {
        // The synth decides its own mode: both captures show F0 00 00 F7 travelling only
        // from the device, never towards it, so there is no command that can switch it
        // back to SYNTH. The most we can usefully do is say so on its display.
        if (_writePin != IntPtr.Zero && AutomapActive)
        {
            try
            {
                DisplayWrite(0, 0, Centre("server stopped", DisplayWidth));
                Thread.Sleep(8);
                DisplayWrite(1, 0, Centre("press SYNTH on the panel", DisplayWidth));
                Thread.Sleep(20);
            }
            catch { /* going down anyway */ }
        }

        _stop = true;
        Connected = false;
        AutomapActive = false;
        _modeKnown = false;
        _paintWake.Set();
        _reader?.Join(400);
        _painter?.Join(400);
        _midiReader?.Join(400);
        _reader = null;
        _painter = null;
        _midiReader = null;
        foreach (var o in _outs) o.Dispose();
        _outs.Clear();
        if (_midiPin != IntPtr.Zero) { Ks.CloseHandle(_midiPin); _midiPin = IntPtr.Zero; }
        if (_readPin != IntPtr.Zero) { Ks.CloseHandle(_readPin); _readPin = IntPtr.Zero; }
        if (_writePin != IntPtr.Zero) { Ks.CloseHandle(_writePin); _writePin = IntPtr.Zero; }
        if (_filter != IntPtr.Zero) { Ks.CloseHandle(_filter); _filter = IntPtr.Zero; }
    }

    public void Dispose() => Stop();

    // ---- talking to the synth ----------------------------------------------

    void Write(byte[] data)
    {
        if (_writePin == IntPtr.Zero) return;
        lock (_writeLock) Ks.WriteMidi(_writePin, data, out _);
    }

    void Announce() { Write(ModeOn); Thread.Sleep(10); Write(ModeOn); }

    /// <summary>F0 02 &lt;row&gt; &lt;pos&gt; &lt;ascii&gt; F7</summary>
    public void DisplayWrite(byte row, byte pos, string text)
    {
        var b = new List<byte> { 0xF0, 0x02, row, pos };
        foreach (char c in text) b.Add((byte)(c < 32 || c > 126 ? ' ' : c));
        b.Add(0xF7);
        Write(b.ToArray());
    }

    /// <summary>
    /// Register 4 on channel 16 gates the wheels and pedals. With it at 0 the synth
    /// never reports them; setting it to 1 opens the stream on channel 4. Established
    /// from the captures: 455 wheel events, every one of them after a BF 04 01, and
    /// none at all in the session where that command never appeared.
    /// </summary>
    public void EnableAnalog(bool on) => Write(new byte[] { 0xBF, 0x04, (byte)(on ? 1 : 0) });

    /// <summary>
    /// Write one of the keyboard-state registers on channel 16. The synth answers with
    /// the value it settled on, so the display follows what the hardware actually did
    /// rather than what we asked for.
    ///   0 keyboard channel   1 octave (offset 64)   2 transpose (offset 64)   3 aftertouch
    /// </summary>
    public void SetKeyboardRegister(int register, int value)
    {
        Write(new byte[] { 0xBF, (byte)register, (byte)Math.Clamp(value, 0, 127) });
    }

    /// <summary>First ring LED code; the eight display encoders own 42..49.</summary>
    public const int RingLedBase = 42;

    /// <summary>
    /// Which encoder is being edited in the application, or -1. Its ring stays lit so
    /// the hand can find it on the panel without looking at the screen.
    /// </summary>
    public int HighlightedEncoder
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value) return;
            int was = _highlighted;
            _highlighted = value;
            if (was >= 0) UpdateRing(was);
            if (value >= 0) UpdateRing(value);
        }
    }
    int _highlighted = -1;

    /// <summary>
    /// A ring is lit while the knob is touched, and while it is the one selected in the
    /// window. The synth lights these itself in normal mode; in Automap mode the panel
    /// belongs to us, so nothing happens unless we do it.
    /// </summary>
    void UpdateRing(int encoder)
    {
        if (encoder < 0 || encoder > 7 || !AutomapActive) return;
        bool on = _touched[encoder] || encoder == _highlighted;
        SetLed(RingLedBase + encoder, on);
    }

    /// <summary>
    /// Flash a lamp a few times, to point at a control on the panel from the window.
    /// Runs off the calling thread so the interface does not wait for it, and puts the
    /// navigation lamps back afterwards in case one of them was borrowed.
    /// </summary>
    public void BlinkLed(int code, int times = 3, int onMs = 110, int offMs = 90)
    {
        if (!AutomapActive || code < 0) return;

        var t = new Thread(() =>
        {
            for (int i = 0; i < times; i++)
            {
                SetLed(code, true);
                Thread.Sleep(onMs);
                SetLed(code, false);
                Thread.Sleep(offMs);
            }
            LightMode();
            LightBanks();
            RefreshRings();
        })
        { IsBackground = true, Name = "led-blink" };
        t.Start();
    }

    /// <summary>Redraw every ring, after a mode change or a page repaint.</summary>
    public void RefreshRings()
    {
        for (int i = 0; i < 8; i++) UpdateRing(i);
    }

    /// <summary>Light or clear one panel LED. Codes match the button codes.</summary>
    public void SetLed(int code, bool on) => Write(new byte[] { 0xB0, (byte)code, (byte)(on ? 1 : 0) });

    static string Centre(string s, int width)
    {
        if (s == null) s = "";
        if (s.Length > width) s = s.Substring(0, width);
        int pad = width - s.Length, left = pad / 2;
        return new string(' ', left) + s + new string(' ', pad - left);
    }

    /// <summary>Paint the label row from the current page.</summary>
    public void DrawLabels()
    {
        var page = CurrentPage;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            var m = page.Encoders != null && i < page.Encoders.Length ? page.Encoders[i] : null;
            // A silent control shows a dash: a leftover name would promise something
            // the knob no longer does.
            string label = m == null ? "" : m.DisplayLabel;
            sb.Append(Centre(label, FieldWidth));
        }
        DisplayWrite(0, 0, sb.ToString());
    }

    /// <summary>Ask for a field to be redrawn; the painter thread does the work.</summary>
    public void DrawValue(int enc) => Invalidate(enc);

    /// <summary>Actually write one value field. Only the painter thread calls this.</summary>
    void DrawValueNow(int enc)
    {
        if (enc < 0 || enc > 7) return;
        var page = CurrentPage;
        var map = page.Encoders != null && enc < page.Encoders.Length ? page.Encoders[enc] : null;
        string n = map != null && map.Silent ? "" : _values[enc].ToString();
        string cell = _touched[enc] ? "[" + Centre(n, FieldWidth - 3) + "]" : Centre(n, FieldWidth - 1);
        DisplayWrite(1, (byte)(enc * FieldWidth), cell);
    }

    public void DrawAllValues() { for (int i = 0; i < 8; i++) Invalidate(i); }

    /// <summary>Full refresh: labels, values, and the keyboard-state poll.</summary>
    public void Initialise()
    {
        Announce();
        foreach (byte reg in new byte[] { 0, 2, 1, 3, 5, 6 })
        { Write(new byte[] { 0xBF, reg, 0x00 }); Thread.Sleep(3); }

        // Always open the wheel and pedal stream. Gating it on "is anything assigned"
        // was a trap: nothing could be seen until it was assigned, and nobody assigns a
        // control they cannot watch move.
        EnableAnalog(true);
        Thread.Sleep(3);
        _repaintAll = true;
        _paintWake.Set();
        LightMode();
        LightBanks();
        RefreshRings();
    }

    /// <summary>
    /// Hold the AUTOMAP lamp on for as long as we are the server, and keep SYNTH dark.
    ///
    /// The instrument lights AUTOMAP itself when the button is pressed and then lets it go
    /// again. Neither mode lamp is ever written in the captures, so on the stock setup it
    /// presumably just went dark too - but a dark lamp next to a plainly active mode is
    /// wrong, so we assert it, and re-assert wherever the panel is repainted.
    /// </summary>
    public void LightMode()
    {
        if (!AutomapActive) return;
        SetLed(Config.LedAutomap, true);
        SetLed(Config.LedSynth, false);
    }

    public void SetPage(int index)
    {
        var bank = CurrentBank;
        if (bank.Pages.Count == 0) return;
        // Stop at the ends rather than wrapping: the lamps promise a direction, and a
        // lamp that is dark should mean the button does nothing.
        int clamped = Math.Clamp(index, 0, bank.Pages.Count - 1);
        if (clamped == PageIndex && index != PageIndex) return;
        PageIndex = clamped;
        Say($"{bank.Name}: page {PageIndex + 1}/{bank.Pages.Count} — {CurrentPage.Name}");
        LightBanks();
        Refresh();
    }

    public void SetBank(int index)
    {
        if (Config.Banks.Count == 0) return;
        BankIndex = ((index % Config.Banks.Count) + Config.Banks.Count) % Config.Banks.Count;
        PageIndex = 0;
        Say($"bank {CurrentBank.Name}");
        LightBanks();
        Refresh();
    }

    /// <summary>Repaint the synth display and tell the UI the selection moved.</summary>
    public void Refresh()
    {
        _repaintAll = true;
        _paintWake.Set();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Show the navigation state on the panel: which bank is active, and whether there
    /// are pages to step to. The guide describes the page buttons as illuminating "to
    /// indicate that additional pages are available", so we do exactly that.
    /// </summary>
    public void LightBanks()
    {
        if (!Config.LightBankButtons || !AutomapActive) return;

        foreach (var b in Config.Banks)
            if (b.SelectButton >= 0)
                SetLed(b.SelectButton, ReferenceEquals(b, CurrentBank));

        // Lit only where there is somewhere to go, as the guide describes for these
        // buttons: they "illuminate to indicate that additional pages are available".
        SetLed(Config.BtnPagePrev, PageIndex > 0);
        SetLed(Config.BtnPageNext, PageIndex < CurrentBank.Pages.Count - 1);
    }

    /// <summary>Queue a field for redraw without blocking the caller.</summary>
    void Invalidate(int enc)
    {
        if (!AutomapActive) return;    // synth is in SYNTH mode; its display is its own
        if (enc < 0 || enc > 7) return;
        _dirty[enc] = true;
        _paintWake.Set();
    }

    void PaintLoop()
    {
        while (!_stop)
        {
            _paintWake.WaitOne(50);
            if (_stop) return;

            if (!AutomapActive) continue;

            if (_repaintAll)
            {
                _repaintAll = false;
                DrawLabels();
                for (int i = 0; i < 8; i++) { _dirty[i] = false; DrawValueNow(i); }
                continue;
            }

            for (int i = 0; i < 8 && !_stop; i++)
            {
                if (!_dirty[i]) continue;
                _dirty[i] = false;      // cleared first: a value arriving mid-write
                DrawValueNow(i);        // just marks it dirty again
            }
        }
    }

    // ---- reading -----------------------------------------------------------

    void ReadLoop()
    {
        const int bufSize = 4096;
        IntPtr buffer = Marshal.AllocHGlobal(bufSize);
        int headerSize = Marshal.SizeOf<Ks.KSSTREAM_HEADER>();
        IntPtr header = Marshal.AllocHGlobal(headerSize);
        var pending = new List<byte>();
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }

        try
        {
            while (!_stop)
            {
                var hdr = new Ks.KSSTREAM_HEADER
                { Size = (uint)headerSize, FrameExtent = bufSize, DataUsed = 0, Data = buffer };
                Marshal.StructureToPtr(hdr, header, false);

                if (!Ks.DeviceIoControl(_readPin, Ks.IOCTL_KS_READ_STREAM,
                        IntPtr.Zero, 0, header, (uint)headerSize, out _, IntPtr.Zero))
                { Thread.Sleep(1); continue; }

                hdr = Marshal.PtrToStructure<Ks.KSSTREAM_HEADER>(header);
                int used = (int)hdr.DataUsed;
                if (used <= 0) continue;

                var raw = new byte[used];
                Marshal.Copy(buffer, raw, 0, used);

                int off = 0;
                while (off + 8 <= raw.Length)
                {
                    uint count = BitConverter.ToUInt32(raw, off + 4);
                    off += 8;
                    if (count == 0 || off + count > raw.Length) break;
                    for (int i = 0; i < count; i++) pending.Add(raw[off + i]);
                    off += ((int)count + 3) & ~3;
                }
                Drain(pending);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(header);
        }
    }

    /// <summary>
    /// Same shape as the Automap reader, but for the class MIDI port. That stream does
    /// use running status and variable-length messages, so it is parsed properly rather
    /// than on a fixed three-byte grid.
    /// </summary>
    void MidiPortLoop()
    {
        const int bufSize = 4096;
        IntPtr buffer = Marshal.AllocHGlobal(bufSize);
        int headerSize = Marshal.SizeOf<Ks.KSSTREAM_HEADER>();
        IntPtr header = Marshal.AllocHGlobal(headerSize);
        var pending = new List<byte>();
        byte status = 0;

        try
        {
            while (!_stop)
            {
                var hdr = new Ks.KSSTREAM_HEADER
                { Size = (uint)headerSize, FrameExtent = bufSize, DataUsed = 0, Data = buffer };
                Marshal.StructureToPtr(hdr, header, false);

                if (!Ks.DeviceIoControl(_midiPin, Ks.IOCTL_KS_READ_STREAM,
                        IntPtr.Zero, 0, header, (uint)headerSize, out _, IntPtr.Zero))
                { Thread.Sleep(1); continue; }

                hdr = Marshal.PtrToStructure<Ks.KSSTREAM_HEADER>(header);
                int used = (int)hdr.DataUsed;
                if (used <= 0) continue;

                var raw = new byte[used];
                Marshal.Copy(buffer, raw, 0, used);

                int off = 0;
                while (off + 8 <= raw.Length)
                {
                    uint count = BitConverter.ToUInt32(raw, off + 4);
                    off += 8;
                    if (count == 0 || off + count > raw.Length) break;
                    for (int i = 0; i < count; i++) pending.Add(raw[off + i]);
                    off += ((int)count + 3) & ~3;
                }

                while (pending.Count > 0)
                {
                    byte b = pending[0];
                    if (b >= 0xF8) { pending.RemoveAt(0); continue; }   // real-time
                    if (b >= 0xF0) { pending.RemoveAt(0); status = 0; continue; }
                    if (b >= 0x80) { status = b; pending.RemoveAt(0); continue; }
                    if (status == 0) { pending.RemoveAt(0); continue; }

                    int need = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                    if (pending.Count < need) break;
                    byte d1 = pending[0];
                    byte d2 = need > 1 ? pending[1] : (byte)0;
                    pending.RemoveRange(0, need);
                    PortMidi?.Invoke(this, new MidiInEventArgs
                    { Status = status, Data1 = d1, Data2 = d2 });
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(header);
        }
    }

    void Drain(List<byte> buf)
    {
        while (buf.Count > 0)
        {
            byte st = buf[0];
            if (st == 0xF0)
            {
                int end = buf.IndexOf(0xF7);
                if (end < 0) return;
                var sx = buf.GetRange(0, end + 1).ToArray();
                buf.RemoveRange(0, end + 1);
                if (sx.Length == 4 && sx[1] == 0x00) OnMode(sx[2] == 0x01);
                continue;
            }
            if (st < 0x80) { buf.RemoveAt(0); continue; }
            if (buf.Count < 3) return;
            byte d1 = buf[1], d2 = buf[2];
            buf.RemoveRange(0, 3);
            OnMessage(st, d1, d2);
        }
    }

    bool _modeKnown;

    void OnMode(bool on)
    {
        // The synth repeats its mode announcement as a keepalive. Treating every repeat
        // as a fresh entry floods the log and re-initialises the display for nothing.
        bool changed = !_modeKnown || on != AutomapActive;
        _modeKnown = true;
        AutomapActive = on;
        if (!changed) return;

        ModeChanged?.Invoke(this, on);

        if (on) { Initialise(); return; }

        // Leaving Automap: stop painting at once and acknowledge the way the original
        // server did - a burst of the one-way context ticks on channel 16. Writing to
        // the display after this point keeps the synth from settling into SYNTH mode.
        _repaintAll = false;
        Array.Clear(_dirty);
        foreach (byte reg in new byte[] { 4, 5, 6 })
        { Write(new byte[] { 0xBF, reg, 0x00 }); Thread.Sleep(2); }
    }

    void OnMessage(byte status, byte d1, byte d2)
    {
        switch ((status & 0x0F) + 1)
        {
            case 1: OnEncoder(d1, d2 > 63 ? d2 - 128 : d2); break;
            case 2: OnTouch(d1, d2 != 0); break;
            case 3: OnButton(d1, d2 != 0); break;
            case 4: OnAnalog(d1, d2); break;
            case 16:
                KeyboardState?.Invoke(this, new KeyboardStateEventArgs
                { Register = d1, Value = d2 });
                break;
        }
    }

    void OnEncoder(int index, int delta)
    {
        if (index < 0 || index >= EncoderCount) return;
        var page = CurrentPage;
        var m = page.Encoders != null && index < page.Encoders.Length ? page.Encoders[index] : null;
        // The counter always runs 0..127; the working range is applied when sending,
        // so narrowing a range never strands the display at an unreachable number.
        _values[index] = Math.Clamp(_values[index] + delta, 0, 127);
        DrawValue(index);

        if (m != null && !m.Silent)
        {
            // Relative passes the movement through untouched: the synth already encodes
            // it two's complement, which is the same encoding DAWs expect.
            if (m.Relative) SendRelative(m, delta);
            else SendMapped(m, m.Scale(_values[index]));
        }
        EncoderMoved?.Invoke(this, new EncoderEventArgs
        { Index = index, Delta = delta, Value = _values[index] });
    }

    void OnTouch(int index, bool touched)
    {
        if (index < 0 || index >= EncoderCount) return;
        _touched[index] = touched;
        DrawValue(index);
        UpdateRing(index);

        var page = CurrentPage;
        if (page.Touch != null && page.Touch.TryGetValue(index.ToString(), out var m) && !m.Silent)
        {
            SendSwitch(m, 1000 + index, touched);
        }

        EncoderTouched?.Invoke(this, new TouchEventArgs { Index = index, Touched = touched });
    }

    void OnButton(int code, bool pressed)
    {

        if (pressed)
        {
            // The panel's own mode buttons pick the bank, exactly as they did in Automap.
            int bank = Config.Banks.FindIndex(b => b.SelectButton == code);
            if (bank >= 0) { SetBank(bank); return; }

            if (code == Config.BtnPageNext) { SetPage(PageIndex + 1); return; }
            if (code == Config.BtnPagePrev) { SetPage(PageIndex - 1); return; }
        }
        else if (Config.Banks.Exists(b => b.SelectButton == code)
                 || code == Config.BtnPageNext || code == Config.BtnPagePrev)
        {
            return;    // release of a navigation button carries no meaning
        }

        var page = CurrentPage;
        int? sent = null;
        if (page.Buttons != null && page.Buttons.TryGetValue(code.ToString(), out var m) && !m.Silent)
            sent = SendSwitch(m, code, pressed);

        // Now that toggle and step have moved, the lamp can show where they landed.
        if (Config.EchoButtonLeds && AutomapActive
            && Config.HasOwnLed(code) && !Config.IsReserved(code))
            LampForMode(code, pressed);

        ButtonChanged?.Invoke(this, new ButtonEventArgs
        { Code = code, Pressed = pressed, Sent = sent });
    }

    /// <summary>
    /// Send a movement rather than a position. Four encodings exist and they disagree
    /// with each other, so the one to use is whatever the receiving end expects - hence
    /// offering all of them rather than picking a favourite.
    ///
    ///   two's complement   +1..+63 as 1..63,     -1..-63 as 127..65
    ///   signed bit         +1..+63 as 1..63,     -1..-63 as 65..127
    ///   signed bit 2       +1..+63 as 65..127,   -1..-63 as 1..63
    ///   binary offset      64 is nought, above it up, below it down
    /// </summary>
    void SendRelative(Mapping m, int delta)
    {
        if (delta == 0) return;
        if (m.Inverted) delta = -delta;
        delta = Math.Clamp(delta, -63, 63);

        int v = m.Mode switch
        {
            "relative-signed"  => delta > 0 ? delta : 64 - delta,
            "relative-signed2" => delta > 0 ? 64 + delta : -delta,
            "relative-offset"  => 64 + delta,
            _                  => delta > 0 ? delta : 128 + delta,   // two's complement
        };

        byte ch = (byte)(Math.Clamp(m.Channel, 1, 16) - 1);
        foreach (var o in _outs)
            o.Send((byte)(0xB0 | ch), (byte)m.Number, (byte)Math.Clamp(v, 0, 127));
    }

    /// <summary>Wheels and pedals: absolute position, straight through the mapping.</summary>
    void OnAnalog(int code, int value)
    {
        var page = CurrentPage;
        if (page.Analog != null && page.Analog.TryGetValue(code.ToString(), out var m) && !m.Silent)
            SendMapped(m, m.Scale(value));
        AnalogMoved?.Invoke(this, new AnalogEventArgs { Code = code, Value = value });
    }

    /// <summary>
    /// A button or pedal, in whichever mode it is set to. From is what a released
    /// control sends, To what a pressed one sends - the original calls these Release
    /// and Press, which is clearer than a "range" for something with two states.
    /// </summary>
    /// <summary>
    /// What the lamp should show, given the mode the button is in:
    ///   momentary and normal  lit while held - the lamp is the finger
    ///   toggle                lit while the switch stands on its upper value
    ///   step                  lit while anywhere past the first position
    ///   key and transport     a brief flash, because the action is an instant
    /// Anything else would tell a lie: a toggle that goes dark on release looks off
    /// when it is on.
    /// </summary>
    void LampForMode(int code, bool pressed)
    {
        var page = CurrentPage;
        if (page.Buttons == null || !page.Buttons.TryGetValue(code.ToString(), out var m))
        {
            SetLed(code, pressed);
            return;
        }

        switch (m.Send == "key" || m.Send == "transport" ? "flash" : m.Mode)
        {
            case "flash":
                if (pressed) BlinkLed(code, 1, 90, 0);
                break;

            case "toggle":
                if (pressed && _toggles.TryGetValue(code, out bool on)) SetLed(code, on);
                break;

            case "step":
                if (pressed && _steps.TryGetValue(code, out int at)) SetLed(code, at > 0);
                break;

            default:
                SetLed(code, pressed);
                break;
        }
    }

    /// <summary>Returns the value sent, or null when nothing was sent.</summary>
    int? SendSwitch(Mapping m, int id, bool pressed)
    {
        // A keystroke happens once, on the press. Repeating it on release would type
        // everything twice.
        if (m.Send == "transport")
        {
            // Fires once, on the press: a transport command is an event, not a state.
            if (!pressed) return null;
            var cmd = Transport.Find(m.TransportCommand);
            if (cmd == null) { Say("no transport command chosen"); return null; }
            foreach (var o in _outs) o.SendRaw(cmd.Bytes);
            Say("transport: " + cmd.Label);
            return 1;
        }

        if (m.Send == "key")
        {
            if (!pressed) return null;
            bool ok = Keystroke.Send(m.KeyGesture);
            Say(ok ? $"key: {Keystroke.Normalise(m.KeyGesture)}"
                   : $"key '{m.KeyGesture}' could not be sent");
            return ok ? 1 : null;
        }

        int value;
        switch (m.Mode)
        {
            case "toggle":
                // Latch on the way down only; the release is ignored.
                if (!pressed) return null;
                _toggles.TryGetValue(id, out bool on);
                _toggles[id] = !on;
                value = !on ? m.To : m.From;
                break;

            case "step":
                // Advance one position and wrap. Positions are spread across the range,
                // so each press lands on a setting rather than nudging by one.
                if (!pressed) return null;
                _steps.TryGetValue(id, out int at);
                at = (at + 1) % Math.Max(1, m.Points);
                _steps[id] = at;
                value = m.StepValue(at);
                break;

            case "normal":
                // The plain switch: full range, ignoring the Release/Press fields. Use
                // Momentary when those values matter.
                value = pressed ? 127 : 0;
                break;

            default:   // momentary
                value = pressed ? m.To : m.From;
                break;
        }

        SendMapped(m, value);
        return value;
    }

    void SendMapped(Mapping m, int value)
    {
        byte ch = (byte)(Math.Clamp(m.Channel, 1, 16) - 1);
        value = Math.Clamp(value, 0, 127);

        switch (m.Send)
        {
            case "key":
            case "transport":
                // Continuous controls cannot sensibly type or drive transport: a knob
                // would fire on every click of travel. Only switches use these.
                break;

            case "note":
                foreach (var o in _outs)
                    o.Send((byte)((value > 0 ? 0x90 : 0x80) | ch), (byte)m.Number, (byte)value);
                break;

            case "pitchbend":
                // Pitch bend is 14-bit; spread our 0..127 across the full range so the
                // whole span of the wheel is reachable from one knob.
                int wide = value * 16383 / 127;
                foreach (var o in _outs)
                    o.Send((byte)(0xE0 | ch), (byte)(wide & 0x7F), (byte)((wide >> 7) & 0x7F));
                break;

            default:
                foreach (var o in _outs) o.Send((byte)(0xB0 | ch), (byte)m.Number, (byte)value);
                break;
        }
    }

    // ---- device discovery --------------------------------------------------

    public static string FindFilter(string match)
    {
        foreach (string p in Filters.EnumerateAudio())
            if (p.Contains(match, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }
}
