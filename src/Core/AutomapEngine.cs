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
    /// <summary>True while pickup is waiting for the wheel to match the last sent value.</summary>
    public bool Pickup;
    /// <summary>The page value to catch, valid while Pickup is true.</summary>
    public int Catch;
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

    /// <summary>
    /// Panel sections for lamp probes. Lighting every lamp at once sags the rail;
    /// these groups are small enough to try one bank at a time.
    /// </summary>
    static readonly (string name, int[] codes)[] LampGroups =
    {
        ("row",    new[] { 0, 1, 2, 3, 4, 5, 6, 7 }),
        ("mode",   new[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 }),
        ("edit",   new[] { 19, 20, 21, 22, 23, 24, 25, 26, 27 }),
        ("arp",    new[] { 28, 29, 30, 31, 32, 33, 34, 35 }),
        ("select", new[] { 36, 37, 38, 39, 40, 41 }),
        ("rings",  new[] { 42, 43, 44, 45, 46, 47, 48, 49 }),
    };

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

    MidiClockMeter _clock;
    MidiClockMeter Clock => _clock ??= new MidiClockMeter(Say, "midi: ");

    readonly int[] _values = new int[EncoderCount];
    readonly bool[] _touched = new bool[EncoderCount];
    readonly Dictionary<int, bool> _toggles = new();
    readonly Dictionary<int, int> _steps = new();
    readonly Dictionary<int, bool> _analogDown = new();
    readonly Dictionary<int, int> _analogRaw = new();
    readonly Dictionary<int, AnalogPickup> _analogPick = new();
    readonly Dictionary<int, long> _analogStreamMs = new();
    readonly object _analogLock = new();

    struct AnalogPickup
    {
        public bool Armed;
        public int CatchScaled;
        public int SnapshotRaw;
        /// <summary>Scaled position when pickup was armed. int.MinValue = not yet seen.</summary>
        public int OriginScaled;
    }
    readonly List<MidiOut> _outs = new();
    readonly object _writeLock = new();

    IntPtr _filter = IntPtr.Zero, _readPin = IntPtr.Zero, _writePin = IntPtr.Zero;
    IntPtr _midiPin = IntPtr.Zero;      // Port 1: notes, wheels, aftertouch
    Thread _reader, _painter, _midiReader;
    volatile bool _stop;
    volatile bool _demo;
    volatile bool _demoHold;
    volatile bool _demoQueued;
    volatile int _demoConcurrent = PanelLamps.MaxAtOnce;
    string _demoRunId = "hello";
    int _demoRunMs = PanelLamps.HelloMs;

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
    public bool DemoRunning => _demo;

    /// <summary>
    /// Ceiling on how many lamps Demo lights together. Hardware starts pumping
    /// the analogue output at 14. Not saved. Clamped to <see cref="PanelLamps.MaxAtOnce"/>.
    /// </summary>
    public int DemoConcurrent
    {
        get => _demoConcurrent;
        set => _demoConcurrent = Math.Clamp(value, 1, PanelLamps.MaxAtOnce);
    }

    bool _learnArmed;

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

    /// <summary>Zero every encoder on this page and repaint, without touching the assignments.</summary>
    public void ResetValues()
    {
        Array.Clear(_values);
        StashPageEncoders();
        _repaintAll = true;
        _paintWake.Set();
    }

    public int ValueOf(int encoder) =>
        encoder >= 0 && encoder < EncoderCount ? _values[encoder] : 0;

    void Say(string s) => Log?.Invoke(this, s);

    /// <summary>
    /// Display paint (F0 02 …) and the Automap handshake are already accounted for
    /// elsewhere; everything else belongs in the log.
    /// </summary>
    void NoteSysEx(string prefix, byte[] sx)
    {
        if (sx == null || sx.Length == 0) return;
        if (sx.Length >= 2 && sx[1] == 0x02) return;
        if (sx.Length == 4 && sx[1] == 0x00) return;
        Say(prefix + ": " + MidiNames.SysEx(sx));
    }

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

        _demo = false;
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
        if (_demoHold || !AutomapActive || code < 0) return;

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

    /// <summary>
    /// Play the selected button-show. Stops on a second press, on leaving Automap,
    /// or when the process goes down.
    /// </summary>
    public void ToggleDemo()
    {
        if (_demo) { _demo = false; _demoQueued = false; return; }
        StartDemo("film", PanelLamps.ShowMs);
    }

    /// <summary>
    /// Debug tools just came on: play the 1 s hello if Automap is already live,
    /// otherwise wait and run it after the 0.7 s entry wave.
    /// </summary>
    public void QueueDemo()
    {
        _demoQueued = true;
        if (_demo) return;
        if (Connected && AutomapActive)
            StartDemo("hello", PanelLamps.HelloMs);
    }

    public void CancelQueuedDemo() => _demoQueued = false;

    void StartDemo(string id, int ms)
    {
        if (_demo) return;
        if (!Connected || !AutomapActive)
        {
            _demoQueued = true;
            Say("demo needs the synth in AUTOMAP");
            return;
        }
        if (id == "hello") _demoQueued = false;
        _demoRunId = string.IsNullOrWhiteSpace(id) ? "film" : id;
        _demoRunMs = Math.Max(200, ms);
        _demo = true;
        _demoHold = true;
        new Thread(RunDemo) { IsBackground = true, Name = "panel-demo" }.Start();
    }

    void RunDemo()
    {
        _demoHold = true;
        var lit = new bool[50];
        var want = new bool[50];
        var score = new float[50];
        const int frameMs = 28;
        string show = _demoRunId ?? "hello";
        bool hello = show == "hello";
        int frames = Math.Max(1, _demoRunMs / frameMs);
        const string name = "Alex.Electron";

        void Want(int code, bool on)
        {
            if ((uint)code >= lit.Length) return;
            if (lit[code] == on) return;
            lit[code] = on;
            SetLed(code, on);
        }

        Say("demo: " + show + " (" + (_demoRunMs / 1000.0).ToString("0.#") + " s)");
        try
        {
            int frame = 0;
            while (_demo && !_stop && AutomapActive && frame < frames)
            {
                float t = frames <= 1 ? 1 : frame / (float)(frames - 1);
                float sec = frame * frameMs / 1000f;
                int cap = Math.Clamp(_demoConcurrent, 1, PanelLamps.MaxAtOnce);
                PanelLamps.Score(show, score, t, sec);
                PanelLamps.Pick(score, cap, want);
                for (int c = 0; c < lit.Length; c++)
                    Want(c, c < want.Length && want[c]);

                // Two full rows, not patches: tearing looked like a slow refresh.
                // Every 4th lamp frame so the 20 s show has a real LCD film,
                // not a crawl of one character a second.
                if (frame % 4 == 0)
                    PaintDemoLcd(hello ? "debug" : show, name, t, sec);
                frame++;
                Thread.Sleep(frameMs);
            }
        }
        finally
        {
            _demo = false;
            _demoHold = false;
            if (AutomapActive && !_stop)
            {
                for (int i = 0; i < lit.Length; i++)
                    if (lit[i]) SetLed(i, false);
                LightMode();
                LightBanks();
                RefreshRings();
                DrawLabels();
                DrawAllValues();
            }
            Say("demo ended");
            if (show == "enter" && _demoQueued && AutomapActive && !_stop)
                StartDemo("hello", PanelLamps.HelloMs);
        }
    }

    /// <summary>
    /// Isolated USB/lamp probes for the "does the all-flash make a sound" question.
    /// The GUI watches probe.txt next to the repo or the exe.
    /// </summary>
    public void Probe(string spec)
    {
        if (!Connected || !AutomapActive)
        {
            Say("probe: need AUTOMAP");
            return;
        }
        if (_demo)
        {
            Say("probe: stop the demo first");
            return;
        }
        new Thread(() => RunProbe(spec)) { IsBackground = true, Name = "lamp-probe" }.Start();
    }

    public void ProbeBlocking(string spec) => RunProbe(spec);

    void RunProbe(string spec)
    {
        spec = (spec ?? "").Trim();
        if (spec.Length == 0) return;
        _demoHold = true;
        bool touchDisplay = false;
        Say("probe: " + spec);
        try
        {
            string[] parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string kind = parts[0].ToLowerInvariant();
            switch (kind)
            {
                case "burst":
                    WaveLamps(Enumerable.Range(0, 50).Where(c => Config.KnownLeds.ContainsKey(c)), 4, 6, 35);
                    break;
                case "slow":
                    WaveLamps(Enumerable.Range(0, 50).Where(c => Config.KnownLeds.ContainsKey(c)), 4, 4, 70);
                    break;
                case "all":
                    BurstLamps(Enumerable.Range(0, 50).Where(c => Config.KnownLeds.ContainsKey(c)), 4, 0);
                    break;
                case "groups":
                    ProbeGroups(null);
                    break;
                case "group":
                    if (parts.Length > 1) ProbeGroups(parts[1]);
                    else Say("probe: group needs a name (row|mode|edit|arp|select|rings)");
                    break;
                case "display":
                    touchDisplay = true;
                    for (int i = 0; i < 4; i++)
                    {
                        DisplayWrite(0, 0, new string('#', DisplayWidth));
                        DisplayWrite(1, 0, new string('#', DisplayWidth));
                        Thread.Sleep(80);
                        DisplayWrite(0, 0, new string(' ', DisplayWidth));
                        DisplayWrite(1, 0, new string(' ', DisplayWidth));
                        Thread.Sleep(220);
                    }
                    break;
                case "rings":
                    BurstLamps(Enumerable.Range(RingLedBase, 8), 4, 0);
                    break;
                case "audio":
                    FlashLamp(8, 4);
                    break;
                case "vocoder":
                    FlashLamp(27, 4);
                    break;
                case "arp":
                    FlashLamp(28, 4);
                    break;
                case "filter":
                    FlashLamp(7, 4);
                    FlashLamp(23, 4);
                    break;
                case "cc":
                    foreach (int c in new[] { 1, 7, 11 }) FlashLamp(c, 4);
                    break;
                case "code":
                    if (parts.Length > 1 && int.TryParse(parts[1], out int code))
                        FlashLamp(code, 4);
                    else
                        Say("probe: code needs a number");
                    break;
                default:
                    Say("probe: unknown (burst|slow|all|groups|group NAME|display|rings|audio|vocoder|arp|filter|cc|code N)");
                    break;
            }
        }
        finally
        {
            _demoHold = false;
            if (AutomapActive && !_stop)
            {
                LightMode();
                LightBanks();
                RefreshRings();
                // Lamp-only probes must not touch the LCD: the previous display
                // probe already showed SysEx paint is silent, and a redraw here
                // mixed the two on the same trial.
                if (touchDisplay)
                {
                    DrawLabels();
                    DrawAllValues();
                }
            }
            Say("probe done — heard a sound?");
        }
    }

    void ProbeGroups(string only)
    {
        bool any = false;
        foreach (var g in LampGroups)
        {
            if (only != null && !g.name.Equals(only, StringComparison.OrdinalIgnoreCase))
                continue;
            any = true;
            Say("probe group: " + g.name + " (" + g.codes.Length + " lamps)");
            BurstLamps(g.codes, 2, 0);
            Thread.Sleep(500);
        }
        if (!any) Say("probe: no group named " + only);
    }

    void BurstLamps(IEnumerable<int> codes, int times, int gapMs)
    {
        int[] list = codes.ToArray();
        for (int t = 0; t < times; t++)
        {
            foreach (int c in list) { SetLed(c, true); if (gapMs > 0) Thread.Sleep(gapMs); }
            Thread.Sleep(80);
            foreach (int c in list) { SetLed(c, false); if (gapMs > 0) Thread.Sleep(gapMs); }
            Thread.Sleep(220);
        }
    }

    /// <summary>
    /// Walk a short bar along the lamps so the panel never has more than
    /// <paramref name="width"/> LEDs on. All-on sags the rail (LCD dims, analog whip).
    /// </summary>
    void WaveLamps(IEnumerable<int> codes, int times, int width, int stepMs)
    {
        int[] list = codes.ToArray();
        int n = list.Length;
        if (n == 0) return;
        if (width < 1) width = 1;
        var on = new bool[n];
        void Want(int i, bool v)
        {
            if (on[i] == v) return;
            on[i] = v;
            SetLed(list[i], v);
        }

        for (int t = 0; t < times; t++)
        {
            for (int pos = 0; pos <= n + width; pos++)
            {
                for (int i = 0; i < n; i++)
                    Want(i, i >= pos - width && i < pos);
                Thread.Sleep(stepMs);
            }
            Thread.Sleep(150);
        }
        for (int i = 0; i < n; i++) Want(i, false);
    }

    void FlashLamp(int code, int times)
    {
        for (int t = 0; t < times; t++)
        {
            SetLed(code, true);
            Thread.Sleep(80);
            SetLed(code, false);
            Thread.Sleep(220);
        }
    }

    /// <summary>
    /// A 20 s LCD film in four acts, always two complete 72-char rows so the
    /// display never paints in torn strips. Nickname stays centred.
    /// </summary>
    void PaintDemoLcd(string show, string name, float t, float sec)
    {
        if (show is "enter" or "hello" or "debug")
        {
            DisplayWrite(0, 0, Centre(name, DisplayWidth));
            DisplayWrite(1, 0, Centre(show == "enter" ? "automap" : show, DisplayWidth));
            return;
        }

        const string pal = " .:-=+*#";
        var r0 = new char[DisplayWidth];
        var r1 = new char[DisplayWidth];
        int act = t < 0.18f ? 0 : t < 0.42f ? 1 : t < 0.78f ? 2 : 3;

        if (act == 0)
        {
            int beam = (int)(sec * 48 % (DisplayWidth + 10)) - 5;
            for (int x = 0; x < DisplayWidth; x++)
            {
                int d = Math.Abs(x - beam);
                r0[x] = d < 2 ? '#' : d < 5 ? '=' : d < 9 ? '-' : '.';
                r1[x] = d < 1 ? '#' : d < 4 ? '=' : '.';
            }
        }
        else if (act == 1)
        {
            for (int x = 0; x < DisplayWidth; x++)
            {
                double v0 = 0.5 + 0.5 * Math.Sin(x * 0.31 + sec * 6.2);
                double v1 = 0.5 + 0.5 * Math.Sin(x * 0.27 - sec * 5.1 + 1.7);
                r0[x] = pal[(int)(v0 * (pal.Length - 1))];
                r1[x] = pal[(int)(v1 * (pal.Length - 1))];
            }
        }
        else if (act == 2)
        {
            int tick = (int)(sec * 11);
            for (int x = 0; x < DisplayWidth; x++)
            {
                uint n = unchecked((uint)((x + 3) * 2654435761u + (uint)tick * 97u));
                n ^= n >> 16;
                r0[x] = (n & 15) == 0 ? '*' : (n & 7) == 0 ? '+' : '.';
                n ^= (uint)(x * 17 + tick);
                r1[x] = (n & 15) == 0 ? '*' : (n & 11) == 0 ? ':' : ' ';
            }
        }
        else
        {
            const string banner = "  * UltraNovaCtl *  -=+*#  ";
            int off = (int)(sec * 14) % banner.Length;
            int fill = (int)(t * DisplayWidth);
            for (int x = 0; x < DisplayWidth; x++)
            {
                r0[x] = banner[(off + x) % banner.Length];
                r1[x] = x < fill ? '#' : (x == fill ? '>' : ' ');
            }
        }

        string title = show is "debug" or "hello" or "enter" ? show : PanelLamps.ActName(t);
        Stamp(r0, " " + name + " ");
        Stamp(r1, " " + title + " ");
        DisplayWrite(0, 0, new string(r0));
        DisplayWrite(1, 0, new string(r1));
    }

    static void Stamp(char[] row, string text)
    {
        if (text == null || row.Length == 0) return;
        int at = (row.Length - text.Length) / 2;
        for (int i = 0; i < text.Length; i++)
        {
            int x = at + i;
            if ((uint)x < (uint)row.Length) row[x] = text[i];
        }
    }

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

        // Immediately, not at the end: answering the handshake is what makes the synth
        // hand the panel over and blank it, and the register writes below take some
        // 25 ms. Lighting the lamp only after them is long enough to see it blink.
        LightMode();

        foreach (byte reg in new byte[] { 0, 2, 1, 3, 5, 6 })
        { Write(new byte[] { 0xBF, reg, 0x00 }); Thread.Sleep(3); }

        // Always open the wheel and pedal stream. Gating it on "is anything assigned"
        // was a trap: nothing could be seen until it was assigned, and nobody assigns a
        // control they cannot watch move.
        ArmAnalogPickup();
        EnableAnalog(true);
        Thread.Sleep(3);
        _repaintAll = true;
        _paintWake.Set();
        LightMode();
        LightBanks();
        RefreshRings();

        SettlePanel();
    }

    /// <summary>
    /// Hold the panel through the hand-over.
    ///
    /// The synth blanks its lamps when it gives control to a server, but not at a moment we
    /// are told about: it lands after the reply, after the register writes, and by the look
    /// of it after the first display write too. One late re-assert leaves the lamp dark for
    /// the couple of hundred milliseconds in between, which reads as a blink.
    ///
    /// Since the instrument never says when it has finished, the honest answer is to keep
    /// asserting for a moment rather than to guess. Two bytes every 40 ms for half a second
    /// is nothing on the wire, and it shortens any gap below what the eye reports as a
    /// flash. The heavier bank and ring refresh runs only at the end.
    /// </summary>
    void SettlePanel()
    {
        var t = new Thread(() =>
        {
            for (int i = 0; i < 12; i++)
            {
                if (_stop || !AutomapActive || _demoHold) return;
                LightMode();
                Thread.Sleep(40);
            }
            if (_stop || !AutomapActive || _demoHold) return;
            LightMode();
            LightBanks();
            RefreshRings();
        })
        { IsBackground = true, Name = "panel-settle" };
        t.Start();
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
        lock (_analogLock)
        {
            StashPageEncoders();
            PageIndex = clamped;
            RestorePageEncoders();
            ArmAnalogPickupUnlocked();
        }
        Say($"{bank.Name}: page {PageIndex + 1}/{bank.Pages.Count} — {CurrentPage.Name}");
        LightBanks();
        Refresh();
    }

    public void SetBank(int index)
    {
        if (Config.Banks.Count == 0) return;
        lock (_analogLock)
        {
            StashPageEncoders();
            BankIndex = ((index % Config.Banks.Count) + Config.Banks.Count) % Config.Banks.Count;
            PageIndex = 0;
            RestorePageEncoders();
            ArmAnalogPickupUnlocked();
        }
        Say($"bank {CurrentBank.Name}");
        LightBanks();
        Refresh();
    }

    void StashPageEncoders()
    {
        CurrentPage.LiveEncoders = (int[])_values.Clone();
    }

    void RestorePageEncoders()
    {
        var stored = CurrentPage.LiveEncoders;
        if (stored != null && stored.Length >= EncoderCount)
            Array.Copy(stored, _values, EncoderCount);
        else
            Array.Clear(_values);
    }

    /// <summary>
    /// After a page or bank change, wheels and pedals wait until the physical
    /// position matches the value last sent on this page. First visit has no
    /// stored value, so they send immediately. Sustain is a switch and is left
    /// alone. Silent rows and pickup-off rows are not armed.
    /// </summary>
    public void ArmAnalogPickup(int? only = null)
    {
        lock (_analogLock) ArmAnalogPickupUnlocked(only);
    }

    void ArmAnalogPickupUnlocked(int? only = null)
    {
        foreach (var (code, _, _) in Config.AnalogControls)
        {
            if (only is int want && want != code) continue;
            if (Config.IsAnalogSwitch(code)) continue;
            Mapping m = null;
            CurrentPage.Analog?.TryGetValue(code.ToString(), out m);
            if (m == null || m.Silent || m.Relative || !m.PickupEnabled || m.LastValue < 0)
            {
                _analogPick.Remove(code);
                continue;
            }
            int raw = _analogRaw.GetValueOrDefault(code, -1);
            int origin = raw < 0 ? int.MinValue : m.Scale(raw);
            if (origin != int.MinValue && Math.Abs(origin - m.LastValue) <= 2)
            {
                _analogPick.Remove(code);
                continue;
            }
            _analogPick[code] = new AnalogPickup
            {
                Armed = true,
                CatchScaled = m.LastValue,
                SnapshotRaw = raw,
                OriginScaled = origin,
            };
        }
    }

    /// <summary>True while this analog control is holding for pickup on the current page.</summary>
    public bool AnalogPickupArmed(int code)
    {
        lock (_analogLock)
            return _analogPick.TryGetValue(code, out var p) && p.Armed;
    }

    /// <summary>Last physical reading, or -1 if this control has not spoken yet.</summary>
    public int AnalogPhysical(int code)
    {
        lock (_analogLock)
            return _analogRaw.GetValueOrDefault(code, -1);
    }

    /// <summary>The page value pickup is catching, if armed.</summary>
    public bool AnalogPickupState(int code, out int catchAt)
    {
        lock (_analogLock)
        {
            if (_analogPick.TryGetValue(code, out var p) && p.Armed)
            {
                catchAt = p.CatchScaled;
                return true;
            }
            catchAt = -1;
            return false;
        }
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
        if (_demoHold || !AutomapActive) return;

        if (Config.LightBankButtons)
        {
            foreach (var b in Config.Banks)
                if (b.SelectButton >= 0)
                    SetLed(b.SelectButton, ReferenceEquals(b, CurrentBank));

            // Lit only where there is somewhere to go, as the guide describes for these
            // buttons: they "illuminate to indicate that additional pages are available".
            SetLed(Config.BtnPagePrev, PageIndex > 0);
            SetLed(Config.BtnPageNext, PageIndex < CurrentBank.Pages.Count - 1);
        }
        LightLearn();
    }

    /// <summary>
    /// LEARN's lamp follows the app's learn mode, not a finger on the button. In
    /// Automap the synth no longer drives that lamp itself.
    /// </summary>
    public void SetLearnArmed(bool on)
    {
        _learnArmed = on;
        LightLearn();
    }

    void LightLearn()
    {
        if (AutomapActive) SetLed(Config.BtnLearn, _learnArmed);
    }

    /// <summary>Queue a field for redraw without blocking the caller.</summary>
    void Invalidate(int enc)
    {
        if (_demoHold || !AutomapActive) return;    // synth is in SYNTH mode; its display is its own
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

            if (!AutomapActive || _demoHold) continue;

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
                    if (b >= 0xF8)
                    {
                        pending.RemoveAt(0);
                        Clock.Realtime(b);
                        continue;
                    }
                    if (b == 0xF0)
                    {
                        int end = pending.IndexOf(0xF7);
                        if (end < 0)
                        {
                            if (pending.Count > 65536) pending.Clear();
                            break;
                        }
                        var sx = pending.GetRange(0, end + 1).ToArray();
                        pending.RemoveRange(0, end + 1);
                        status = 0;
                        NoteSysEx("midi", sx);
                        continue;
                    }
                    if (b >= 0xF0) { pending.RemoveAt(0); status = 0; continue; }
                    if (b >= 0x80) { status = b; pending.RemoveAt(0); continue; }
                    if (status == 0) { pending.RemoveAt(0); continue; }

                    int need = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                    if (pending.Count < need) break;
                    byte d1 = pending[0];
                    byte d2 = need > 1 ? pending[1] : (byte)0;
                    pending.RemoveRange(0, need);
                    // Mod wheel / pitch / aftertouch ride this port as well as (or
                    // instead of) the Automap analog stream. Same pickup path as B3.
                    OnPortMidiAnalog(status, d1, d2);
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
                else NoteSysEx("automap", sx);
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
        // The announcement arrives as a burst - three within about 17 ms at each entry,
        // measured in both captures, with minutes of silence in between. So a repeat is
        // not a fresh entry and must not re-run the whole initialisation or log again,
        // but it is still worth answering: the synth blanks the panel every time it hands
        // control over, and each announcement in the burst is another chance to lose the
        // mode lamp to that. Re-asserting it costs two bytes and no noise.
        bool changed = !_modeKnown || on != AutomapActive;
        _modeKnown = true;
        AutomapActive = on;
        if (!changed)
        {
            if (on && !_demoHold) LightMode();
            return;
        }

        ModeChanged?.Invoke(this, on);

        if (on)
        {
            Initialise();
            if (!_demo)
                StartDemo("enter", PanelLamps.EnterMs);
            return;
        }

        _demo = false;
        // Keep _demoQueued: leaving Automap for a moment should still play
        // the show when the synth comes back, if debug asked for it.

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
            case 4:
                lock (_analogLock) _analogStreamMs[d1] = Environment.TickCount64;
                OnAnalog(d1, d2);
                break;
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
            if (m.Relative && m.Send is "cc") SendRelative(m, delta);
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
            // Navigation still happens; a mapping on the debug bench is extra MIDI on top.
            int bank = Config.Banks.FindIndex(b => b.SelectButton == code);
            if (bank >= 0) SetBank(bank);
            else if (code == Config.BtnPageNext) SetPage(PageIndex + 1);
            else if (code == Config.BtnPagePrev) SetPage(PageIndex - 1);
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

    /// <summary>
    /// Wheels and aftertouch on the synth's own MIDI port. Ignored when the Automap
    /// analog stream already reported the same control a moment ago, so a wheel that
    /// appears on both paths is not sent twice.
    /// </summary>
    void OnPortMidiAnalog(byte status, byte d1, byte d2)
    {
        int kind = status & 0xF0;
        int code = -1, value = d2;
        if (kind == 0xE0) { code = 2; value = ((d2 << 7) | d1) >> 7; }
        else if (kind == 0xD0) { code = 5; value = d1; }
        else if (kind == 0xB0)
            code = d1 switch { 1 => 1, 11 => 3, 64 => 4, _ => -1 };
        if (code < 0) return;
        lock (_analogLock)
        {
            if (Environment.TickCount64 - _analogStreamMs.GetValueOrDefault(code) < 80)
                return;
        }
        OnAnalog(code, value);
    }

    /// <summary>
    /// Wheels and the expression pedal: absolute position. Sustain is a footswitch,
    /// so it uses the same press/release path as a panel button — including toggle
    /// and keystroke — and only fires when the contact actually changes.
    /// </summary>
    void OnAnalog(int code, int value)
    {
        bool pickup = false;
        int catchAt = -1;
        lock (_analogLock)
        {
            _analogRaw[code] = value;

            var page = CurrentPage;
            Mapping m = null;
            page.Analog?.TryGetValue(code.ToString(), out m);

            if (Config.IsAnalogSwitch(code))
            {
                bool pressed = value >= 64;
                if (!_analogDown.TryGetValue(code, out bool was) || was != pressed)
                {
                    _analogDown[code] = pressed;
                    if (m != null && !m.Silent)
                        SendSwitch(m, 2000 + code, pressed);
                }
            }
            else if (m != null && !m.Silent)
            {
                int scaled = m.Scale(value);
                if (m.Relative || !m.PickupEnabled)
                {
                    SendMapped(m, scaled);
                    m.LastValue = scaled;
                }
                else if (!HoldOrCatchAnalog(code, m, value, scaled, out pickup))
                {
                    SendMapped(m, scaled);
                    m.LastValue = scaled;
                }
                if (pickup && _analogPick.TryGetValue(code, out var pk))
                    catchAt = pk.CatchScaled;
            }
        }

        AnalogMoved?.Invoke(this, new AnalogEventArgs
        { Code = code, Value = value, Pickup = pickup, Catch = catchAt });
    }

    /// <summary>
    /// Soft takeover. Returns true when this reading was consumed (held or caught).
    /// False means pickup is off and the caller should send. A missing pickup
    /// record with no stored page value sends; with a stored value it holds.
    /// </summary>
    bool HoldOrCatchAnalog(int code, Mapping m, int value, int scaled, out bool pickup)
    {
        pickup = false;
        if (!_analogPick.TryGetValue(code, out var pk))
        {
            if (m.LastValue < 0) return false;
            pk = new AnalogPickup
            {
                Armed = true,
                CatchScaled = m.LastValue,
                SnapshotRaw = value,
                OriginScaled = scaled,
            };
            _analogPick[code] = pk;
            pickup = true;
            return true;
        }
        if (!pk.Armed) return false;

        if (pk.OriginScaled == int.MinValue || pk.SnapshotRaw < 0)
        {
            pk.SnapshotRaw = value;
            pk.OriginScaled = scaled;
            _analogPick[code] = pk;
            pickup = true;
            return true;
        }

        bool crossed;
        if (pk.OriginScaled < pk.CatchScaled)
            crossed = scaled >= pk.CatchScaled;
        else if (pk.OriginScaled > pk.CatchScaled)
            crossed = scaled <= pk.CatchScaled;
        else
            crossed = Math.Abs(value - pk.SnapshotRaw) >= 3;

        if (!crossed)
        {
            pickup = true;
            return true;
        }

        pk.Armed = false;
        _analogPick[code] = pk;
        SendMapped(m, scaled);
        m.LastValue = scaled;
        return true;
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
            Say("transport: " + cmd.Label + " · " + MidiNames.SysEx(cmd.Bytes));
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

            case "aftertouch":
                foreach (var o in _outs)
                    o.Send((byte)(0xD0 | ch), (byte)value, 0);
                break;

            case "program":
                foreach (var o in _outs)
                    o.Send((byte)(0xC0 | ch), (byte)value, 0);
                break;

            case "nrpn":
                SendRegistered(ch, 99, 98, m.Number, value);
                break;

            case "rpn":
                SendRegistered(ch, 101, 100, m.Number, value);
                break;

            case "cc14":
                {
                    int cc = Math.Clamp(m.Number, 0, 31);
                    int w = value * 16383 / 127;
                    foreach (var o in _outs)
                    {
                        o.Send((byte)(0xB0 | ch), (byte)cc, (byte)((w >> 7) & 0x7F));
                        o.Send((byte)(0xB0 | ch), (byte)(cc + 32), (byte)(w & 0x7F));
                    }
                }
                break;

            default:
                foreach (var o in _outs) o.Send((byte)(0xB0 | ch), (byte)m.Number, (byte)value);
                break;
        }
    }

    /// <summary>
    /// NRPN/RPN: select the 14-bit parameter, then data entry as 14-bit too, so LSB
    /// is not left behind at zero.
    /// </summary>
    void SendRegistered(byte ch, byte paramMsbCc, byte paramLsbCc, int param, int value7)
    {
        param = Math.Clamp(param, 0, 16383);
        int data = value7 * 16383 / 127;
        byte pMsb = (byte)((param >> 7) & 0x7F);
        byte pLsb = (byte)(param & 0x7F);
        byte dMsb = (byte)((data >> 7) & 0x7F);
        byte dLsb = (byte)(data & 0x7F);
        foreach (var o in _outs)
        {
            o.Send((byte)(0xB0 | ch), paramMsbCc, pMsb);
            o.Send((byte)(0xB0 | ch), paramLsbCc, pLsb);
            o.Send((byte)(0xB0 | ch), 6, dMsb);
            o.Send((byte)(0xB0 | ch), 38, dLsb);
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
