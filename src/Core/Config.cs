using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace UltraNovaCtl.Core;

/// <summary>What a single physical control sends when it moves.</summary>
public sealed class Mapping
{
    /// <summary>"cc", "cc14", "nrpn", "rpn", "note", "pitchbend", "program", "key", "transport" or "none".</summary>
    public string Send { get; set; } = "cc";

    /// <summary>
    /// Key combination for send type "key", written as "Ctrl+Shift+Z". Goes to whatever
    /// window has focus, which is how a panel button reaches software with no MIDI learn.
    /// </summary>
    public string KeyGesture { get; set; } = "";

    /// <summary>Transport command id for send type "transport", e.g. "mmc-play".</summary>
    public string TransportCommand { get; set; } = "";

    /// <summary>MIDI channel, 1..16 as a human counts them.</summary>
    public int Channel { get; set; } = 1;

    /// <summary>CC number or note number.</summary>
    public int Number { get; set; }

    /// <summary>Label drawn on the synth display. Up to 8 characters fit a field.</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// Working range, as the original called From/To: the span the control actually
    /// covers. Setting From above To inverts the control, which is how the original
    /// handled reversed parameters.
    /// </summary>
    /// <summary>
    /// How many positions Step walks through. They are spread evenly between From and
    /// To, so five points across 0..127 give 0, 32, 64, 95, 127 - one value per setting
    /// on the other end rather than a bare increment.
    /// </summary>
    public int Points { get; set; } = 2;

    /// <summary>The distance between neighbouring Step positions, for display.</summary>
    [JsonIgnore]
    public double StepSize => Points > 1 ? (double)(To - From) / (Points - 1) : 0;

    /// <summary>Value at one Step position, counted from zero.</summary>
    public int StepValue(int index)
    {
        if (Points <= 1) return To;
        index = ((index % Points) + Points) % Points;
        return Math.Clamp(From + (int)Math.Round(index * StepSize), 0, 127);
    }

    public int From { get; set; }
    public int To { get; set; } = 127;

    /// <summary>
    /// How a movement becomes a value. The useful modes differ by control type, which
    /// is why the original shows a different list for knobs and for buttons.
    ///
    /// Continuous controls - encoders, wheels, pedals:
    ///   normal    absolute position inside From..To
    ///   inverted  the same, reversed
    ///   relative  increment/decrement: sends the movement itself, not a position.
    ///             Two's complement (1..63 up, 127..65 down) - the encoding the synth
    ///             already uses, so nothing is lost in translation.
    ///
    /// Switches - buttons, pedals, encoder touch:
    ///   momentary  To while held, From when let go
    ///   normal     the plain switch: 127 held, 0 released, ignoring From/To
    ///   toggle     alternates between To and From on each press
    ///   step       advances by one on each press and wraps, for stepping through a
    ///              list of settings on the other end
    /// </summary>
    public string Mode { get; set; } = "momentary";

    /// <summary>
    /// Soft takeover after a bank or page change: wait until the physical position
    /// matches the last sent value. Null means on, the default for wheels and pedals.
    /// Encoders ignore this (they are endless); sustain ignores it (a switch).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Pickup { get; set; }

    [JsonIgnore] public bool Silent => Send == "none";

    /// <summary>Whether analog pickup is active. Null in the file means on.</summary>
    [JsonIgnore] public bool PickupEnabled => Pickup != false;

    /// <summary>
    /// Last value this mapping sent on its page. -1 means this page has never
    /// spoken. Session-only: it is how pickup knows where to catch.
    /// </summary>
    [JsonIgnore] public int LastValue { get; set; } = -1;

    /// <summary>One line under a tile: what this mapping sends.</summary>
    [JsonIgnore]
    public string Caption => Send switch
    {
        "none" => "\u2014",
        "pitchbend" => "Bend \u00b7 ch " + Channel,
        "aftertouch" => "Aftertouch \u00b7 ch " + Channel,
        "note" => MidiNames.NoteShort(Number) + " \u00b7 ch " + Channel,
        "program" => "Program \u00b7 ch " + Channel,
        "nrpn" => $"NRPN {Number} \u00b7 ch {Channel}",
        "rpn" => $"RPN {Number} \u00b7 ch {Channel}",
        "cc14" => $"CC {Number:000}+{Number + 32:000} \u00b7 ch {Channel}",
        "key" => string.IsNullOrWhiteSpace(KeyGesture) ? "key" : KeyGesture,
        "transport" => Transport.LabelOf(TransportCommand),
        _ => $"CC {Number:000} \u00b7 ch {Channel}",
    };
    [JsonIgnore] public bool Inverted => Mode == "inverted" || From > To;
    [JsonIgnore] public bool Relative => Mode.StartsWith("relative");

    /// <summary>
    /// What to show when no label was typed: the assignment itself. Kept to eight
    /// characters because that is one field on the synth display.
    /// </summary>
    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Label)) return Label;
            return Send switch
            {
                "none" => "-",
                "key" => KeyGesture.Length > 8 ? KeyGesture.Substring(0, 8) : KeyGesture,
                "transport" => TransportShort(),
                "note" => MidiNames.NoteCompact(Number),
                "pitchbend" => "Bend",
                "aftertouch" => "Aftouch",
                "program" => "Program",
                "nrpn" => Compact("NR", Number),
                "rpn" => Compact("RP", Number),
                "cc14" => $"C14#{Number:00}",
                _ => $"CC#{Number:000}",
            };

            static string Compact(string p, int n)
            {
                string s = p + n;
                return s.Length <= 8 ? s : s[..8];
            }
        }
    }

    /// <summary>Eight characters for the synth display, e.g. "Play" or "Rec".</summary>
    string TransportShort() => TransportCommand switch
    {
        "rt-start" => "Start", "rt-continue" => "Cont", "rt-stop" => "Stop",
        "mmc-play" => "Play", "mmc-stop" => "Stop", "mmc-pause" => "Pause",
        "mmc-record" => "Rec", "mmc-recexit" => "RecOut",
        "mmc-ff" => "FFwd", "mmc-rew" => "Rew", "mmc-home" => "Zero",
        _ => "Transp",
    };

    /// <summary>Map a raw 0..127 reading into the configured working range.</summary>
    public int Scale(int raw)
    {
        raw = Math.Clamp(raw, 0, 127);
        int lo = Math.Clamp(Math.Min(From, To), 0, 127);
        int hi = Math.Clamp(Math.Max(From, To), 0, 127);
        int v = lo + raw * (hi - lo) / 127;
        return Inverted ? hi - (v - lo) : v;
    }

    public Mapping Clone() => new()
    {
        Send = Send, Channel = Channel, Number = Number,
        Label = Label, From = From, To = To, Mode = Mode, Points = Points,
        KeyGesture = KeyGesture, TransportCommand = TransportCommand,
        Pickup = Pickup,
    };
}

/// <summary>
/// One page of assignments: the ten encoders and every panel button, as they behave
/// while this page is showing. Eight knobs cover a fifty-parameter plug-in by paging.
/// </summary>
public sealed class Page
{
    public string Name { get; set; } = "Page";

    /// <summary>Ten encoders: 0..7 under the display, 8 the filter knob, 9 the patch dial.</summary>
    public Mapping[] Encoders { get; set; } = Array.Empty<Mapping>();

    /// <summary>Keyed by button code as the synth reports it on channel 3.</summary>
    public Dictionary<string, Mapping> Buttons { get; set; } = new();

    /// <summary>What a touch on each encoder sends, keyed by encoder index.</summary>
    public Dictionary<string, Mapping> Touch { get; set; } = new();

    /// <summary>
    /// Continuous controls the synth reports on channel 4: wheels and pedals. Keyed by
    /// the number it sends there, which is why the mod wheel lives under "1".
    /// </summary>
    public Dictionary<string, Mapping> Analog { get; set; } = new();

    /// <summary>Encoder positions last seen on this page. Not saved with the mappings.</summary>
    [JsonIgnore] public int[] LiveEncoders { get; set; }
}

/// <summary>
/// A bank selected by one of the panel's own mode buttons, mirroring how the original
/// Automap used USER / FX / INST / MIXER to choose what the knobs were aimed at.
/// </summary>
public sealed class Bank
{
    public string Name { get; set; } = "BANK";

    /// <summary>Panel button code that selects this bank; -1 means selectable only in the app.</summary>
    public int SelectButton { get; set; } = -1;

    public List<Page> Pages { get; set; } = new();
}

public sealed class Config
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Version of the persisted shape. Files written before 1.2.1 have no field; the
    /// property default keeps those files on the first, backward-compatible schema.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// True once the one-off analog routing migration has been applied to this file.
    /// A file written before it existed reads as false, gets the migration once, and
    /// then keeps whatever the user chose - including silence.
    /// </summary>
    public bool AnalogFactorySendsRepaired { get; set; }

    /// <summary>Substring of the MIDI output port name, e.g. "loopMIDI".</summary>
    public string OutputPort { get; set; } = "loopMIDI";

    /// <summary>Connect on startup and keep retrying, so the synth alone decides when to work.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Light the panel button of the bank that is currently selected.</summary>
    public bool LightBankButtons { get; set; } = true;

    /// <summary>
    /// Light a button's own lamp while it is held. Off by default for anyone who wants
    /// the panel to stay as the synth left it.
    /// </summary>
    public bool EchoButtonLeds { get; set; } = true;

    /// <summary>
    /// Pass the instrument's keyboard - notes and polyphonic pressure - to the MIDI
    /// output, so a DAW fed from here can play it. Turn it off when the DAW already
    /// listens to the instrument's own port, or every note arrives twice.
    /// </summary>
    public bool ForwardKeyboardNotes { get; set; } = true;

    /// <summary>
    /// Stop forwarding while Novation's own UltraNova Editor is driving the instrument.
    ///
    /// That editor takes Local off and expects the DAW to sit in the middle, listening to
    /// the instrument's own port and sending back to it. Forwarding into the same DAW on
    /// top of that gives it every note twice, and the return path then plays both.
    /// Detected from the editor's own lock, without taking it - see
    /// <see cref="NativeLocks.EditorHardware"/>.
    /// </summary>
    public bool PauseForwardingForNativeEditor { get; set; } = true;

    /// <summary>
    /// Panel LED walker, All off, naming codes by eye — the tools used to map the
    /// hardware. Off by default so a mapping session is not a lamp test. Not saved:
    /// tray "Debug tools" or <c>--debug</c> for this session only.
    /// </summary>
    [JsonIgnore]
    public bool ShowDebugTools { get; set; }

    /// <summary>
    /// Lamps and buttons share numbering only up to this code. Above it the lamps run
    /// on into indicators that have no button - so a press there cannot be echoed.
    /// </summary>
    public const int SharedNumberingLimit = 34;

    public static bool HasOwnLed(int buttonCode) =>
        buttonCode >= 0 && buttonCode <= SharedNumberingLimit;

    /// <summary>
    /// Where the window was last seen. Zero width means "never saved", in which case the
    /// window opens at its designed size.
    /// </summary>
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximised { get; set; }

    public List<Bank> Banks { get; set; } = new();

    /// <summary>
    /// Names discovered by lighting lamps and looking at the panel, keyed by code. The
    /// synth never reports what a button is called, so this is knowledge a person adds
    /// and it must survive a restart.
    /// </summary>
    public Dictionary<string, string> ControlNames { get; set; } = new();

    // Panel buttons that drive navigation rather than sending MIDI.
    public const int BtnUser = 2, BtnFx = 3, BtnInst = 4, BtnMixer = 5;
    public const int BtnPagePrev = 17, BtnPageNext = 18;
    public const int BtnLearn = 0, BtnView = 1;

    /// <summary>
    /// The two mode lamps. They have no buttons that report a press - SYNTH and AUTOMAP
    /// change the instrument's mode and that change is all the host ever hears - but the
    /// lamps themselves are ours to drive.
    /// </summary>
    public const int LedSynth = 12, LedAutomap = 14;

    /// <summary>
    /// Buttons the application uses for itself. They keep those jobs; the debug bench
    /// can assign MIDI on top. They stay out of the everyday button row.
    /// </summary>
    public static readonly Dictionary<int, string> ReservedButtons = new()
    {
        [BtnLearn] = "arms learn mode",
        [BtnView] = "shows and hides this window",
        [BtnUser] = "selects the USER bank",
        [BtnFx] = "selects the FX bank",
        [BtnInst] = "selects the INST bank",
        [BtnMixer] = "selects the MIXER bank",
        [BtnPagePrev] = "previous page",
        [BtnPageNext] = "next page",
    };

    public static bool IsReserved(int code) => ReservedButtons.ContainsKey(code);

    /// <summary>
    /// LEARN and VIEW still do their jobs (learn mode, the window). They start silent;
    /// the debug bench can assign MIDI to them, and then they send as well.
    /// </summary>
    public static bool IsAppButton(int code) => code is BtnLearn or BtnView;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    const string FileName = "ultranovactl.json";

    /// <summary>
    /// Where the settings live, worked out once and remembered.
    ///
    /// Beside the executable when that works: it keeps a copy on a USB stick portable, and it
    /// is where every installation so far already has its file. But an install under Program
    /// Files is read-only for a standard user, and silently losing somebody's whole mapping is
    /// not an acceptable way to find that out, so there the file moves to the roaming profile.
    /// </summary>
    public static string DefaultPath => _path ??= ResolvePath();
    static string _path;

    /// <summary>
    /// Set when startup recovered a backup or had to quarantine an unreadable file.
    /// The GUI shows it after constructing its log.
    /// </summary>
    public static string LastLoadWarning { get; private set; } = "";

    static string ResolvePath()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, FileName);

        // A file already sitting there wins outright - never strand existing settings.
        if (File.Exists(beside)) return beside;
        if (IsWritable(AppContext.BaseDirectory)) return beside;

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UltraNovaCtl");
        try { Directory.CreateDirectory(dir); } catch { return beside; }
        return Path.Combine(dir, FileName);
    }

    /// <summary>Asks the file system instead of guessing from the shape of the path.</summary>
    static bool IsWritable(string dir)
    {
        string probe = Path.Combine(dir, ".write-probe-" + Environment.ProcessId);
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Load the working configuration. A damaged primary file is moved aside and its
    /// last known-good backup is preferred over factory defaults.
    /// </summary>
    public static Config Load(string path = null)
    {
        path ??= DefaultPath;
        LastLoadWarning = "";
        if (!File.Exists(path))
        {
            string backup = BackupPath(path);
            if (!File.Exists(backup)) return CreateDefault();
            try
            {
                var recovered = LoadStrict(backup);
                string persistence = TryRestorePrimary(recovered, path);
                LastLoadWarning = $"working settings file was missing; recovered {backup}{persistence}";
                Console.WriteLine(LastLoadWarning);
                return recovered;
            }
            catch (Exception backupError)
            {
                LastLoadWarning = $"working settings file was missing and its backup was unreadable "
                    + $"({backupError.Message}); using defaults";
                Console.WriteLine(LastLoadWarning);
                return CreateDefault();
            }
        }

        try
        {
            return LoadStrict(path);
        }
        catch (Exception e)
        {
            // A file we refuse to load always gets moved aside, never left at `path`.
            // Leaving it there looks kinder but is not: the session runs on defaults or
            // on the backup, and the first autosave rotates those rejected bytes into
            // `.bak` while a second one drops them entirely - so the edit that was meant
            // to be preserved is the thing that disappears, with no copy anywhere.
            //
            // What the two cases do differ in is the name, because the name is the only
            // thing the user has to go on. Bytes that are not JSON are damaged; a file
            // that parses but fails validation is a hand edit with a mistake in it, and
            // is worth renaming back and fixing rather than deleting.
            bool unparseable = e is JsonException || e.InnerException is JsonException;
            string suffix = unparseable ? "corrupt" : "rejected";
            string setAside = SetAside(path, suffix);
            string kept = setAside.Length > 0
                ? $"; {(unparseable ? "damaged" : "rejected")} file kept as {setAside}"
                    + (unparseable ? "" : " - fix it and rename it back")
                : $"; {path} could not be moved aside";
            string backup = BackupPath(path);
            if (File.Exists(backup))
            {
                try
                {
                    var recovered = LoadStrict(backup);
                    string persistence = TryRestorePrimary(recovered, path);
                    LastLoadWarning = $"settings were unreadable ({e.Message}); recovered {backup}"
                        + persistence + kept;
                    Console.WriteLine(LastLoadWarning);
                    return recovered;
                }
                catch (Exception backupError)
                {
                    LastLoadWarning = $"settings and backup were unreadable "
                        + $"({e.Message}; backup: {backupError.Message}); using defaults" + kept;
                    Console.WriteLine(LastLoadWarning);
                    return CreateDefault();
                }
            }

            LastLoadWarning = $"settings were unreadable ({e.Message}); using defaults" + kept;
            Console.WriteLine(LastLoadWarning);
            return CreateDefault();
        }
    }

    /// <summary>
    /// Load an explicitly selected file. Unlike startup loading, this never substitutes
    /// defaults: malformed imports must fail visibly and leave the current map untouched.
    /// </summary>
    public static Config LoadStrict(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("a configuration path is required", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("configuration file was not found", path);

        Config c;
        try
        {
            c = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("configuration contains JSON null");
        }
        catch (JsonException e)
        {
            string where = e.LineNumber.HasValue
                ? $" at line {e.LineNumber.Value + 1}, byte {e.BytePositionInLine.GetValueOrDefault() + 1}"
                : "";
            throw new InvalidDataException("configuration is not valid JSON" + where, e);
        }

        return PrepareLoaded(c);
    }

    static Config PrepareLoaded(Config c)
    {
        if (c.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException(
                $"configuration schema {c.SchemaVersion} is newer than supported schema {CurrentSchemaVersion}");
        if (c.SchemaVersion < 0)
            throw new InvalidDataException($"invalid configuration schema {c.SchemaVersion}");
        if (c.Banks == null || c.Banks.Count == 0)
            throw new InvalidDataException("configuration has no banks");

        c.OutputPort ??= "";
        c.ControlNames ??= new Dictionary<string, string>();

        for (int bankIndex = 0; bankIndex < c.Banks.Count; bankIndex++)
        {
            var bank = c.Banks[bankIndex]
                ?? throw new InvalidDataException($"bank {bankIndex + 1} is null");
            bank.Name ??= $"BANK {bankIndex + 1}";
            bank.Pages ??= new List<Page>();
            if (bank.Pages.Count == 0)
                bank.Pages.Add(NewPage(bank.Name, 1, FactoryFirstCc(bankIndex)));

            for (int pageIndex = 0; pageIndex < bank.Pages.Count; pageIndex++)
            {
                var page = bank.Pages[pageIndex]
                    ?? throw new InvalidDataException($"bank {bankIndex + 1}, page {pageIndex + 1} is null");
                NormalisePage(page, bankIndex, pageIndex);
            }
        }

        // Migrations from files written by 1.0-1.2 run before validation because they
        // intentionally repair values those versions emitted.
        c.SilenceFactoryAppButtons();
        c.RepairTouchModes();
        c.RepairAnalogModes();
        // Once per file, not on every load. The three repairs above correct values that
        // 1.0-1.2 actually emitted; this one is a routing policy, and re-applying it at
        // every start undid a deliberately silenced mod wheel - which is how someone
        // running loopMIDI alongside the instrument's own port got every movement twice.
        if (!c.AnalogFactorySendsRepaired)
        {
            c.RepairAnalogFactorySends();
            c.AnalogFactorySendsRepaired = true;
        }
        c.ValidateMappings();
        c.SchemaVersion = CurrentSchemaVersion;
        c.ApplyNames();
        return c;
    }

    static void NormalisePage(Page page, int bankIndex, int pageIndex)
    {
        page.Name ??= $"Page {pageIndex + 1}";
        page.Encoders ??= Array.Empty<Mapping>();
        if (page.Encoders.Length > 10)
            throw new InvalidDataException(
                $"bank {bankIndex + 1}, page {pageIndex + 1} has more than 10 encoders");
        if (page.Encoders.Length < 10)
        {
            int oldLength = page.Encoders.Length;
            var encoders = page.Encoders;
            Array.Resize(ref encoders, 10);
            page.Encoders = encoders;
            for (int i = oldLength; i < encoders.Length; i++)
                encoders[i] = new Mapping { Send = "none", Mode = "normal" };
        }
        for (int i = 0; i < page.Encoders.Length; i++)
            if (page.Encoders[i] == null)
                throw new InvalidDataException(
                    $"bank {bankIndex + 1}, page {pageIndex + 1}, encoder {i + 1} is null");

        page.Buttons ??= new Dictionary<string, Mapping>();
        page.Touch ??= new Dictionary<string, Mapping>();
        page.Analog ??= new Dictionary<string, Mapping>();
        ValidateDictionary(page.Buttons, bankIndex, pageIndex, "button");
        ValidateDictionary(page.Touch, bankIndex, pageIndex, "touch");
        ValidateDictionary(page.Analog, bankIndex, pageIndex, "analog");
    }

    static void ValidateDictionary(
        Dictionary<string, Mapping> mappings, int bankIndex, int pageIndex, string kind)
    {
        foreach (var kv in mappings)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                throw new InvalidDataException(
                    $"bank {bankIndex + 1}, page {pageIndex + 1} has an empty {kind} id");
            if (kv.Value == null)
                throw new InvalidDataException(
                    $"bank {bankIndex + 1}, page {pageIndex + 1}, {kind} {kv.Key} is null");
        }
    }

    static readonly HashSet<string> SendKinds = new(StringComparer.Ordinal)
    {
        "cc", "cc14", "nrpn", "rpn", "note", "pitchbend", "aftertouch",
        "program", "key", "transport", "none",
    };

    static readonly HashSet<string> ModeKinds = new(StringComparer.Ordinal)
    {
        "normal", "inverted", "relative", "relative-signed", "relative-signed2",
        "relative-offset", "momentary", "toggle", "step",
    };

    void ValidateMappings()
    {
        for (int bankIndex = 0; bankIndex < Banks.Count; bankIndex++)
        for (int pageIndex = 0; pageIndex < Banks[bankIndex].Pages.Count; pageIndex++)
        {
            var page = Banks[bankIndex].Pages[pageIndex];
            for (int i = 0; i < page.Encoders.Length; i++)
                ValidateMapping(page.Encoders[i], bankIndex, pageIndex, $"encoder {i + 1}");
            foreach (var kv in page.Buttons)
                ValidateMapping(kv.Value, bankIndex, pageIndex, $"button {kv.Key}");
            foreach (var kv in page.Touch)
                ValidateMapping(kv.Value, bankIndex, pageIndex, $"touch {kv.Key}");
            foreach (var kv in page.Analog)
                ValidateMapping(kv.Value, bankIndex, pageIndex, $"analog {kv.Key}");
        }
    }

    static void ValidateMapping(Mapping m, int bankIndex, int pageIndex, string location)
    {
        string prefix = $"bank {bankIndex + 1}, page {pageIndex + 1}, {location}";
        if (m.Send == null || !SendKinds.Contains(m.Send))
            throw new InvalidDataException($"{prefix} has unknown send type '{m.Send ?? "null"}'");
        if (m.Mode == null || !ModeKinds.Contains(m.Mode))
            throw new InvalidDataException($"{prefix} has unknown mode '{m.Mode ?? "null"}'");
        if (m.Channel is < 1 or > 16)
            throw new InvalidDataException($"{prefix} has MIDI channel {m.Channel}; expected 1..16");
        int maxNumber = m.Send switch { "cc14" => 31, "nrpn" or "rpn" => 16383, _ => 127 };
        if (m.Number is < 0 || m.Number > maxNumber)
            throw new InvalidDataException(
                $"{prefix} has number {m.Number}; expected 0..{maxNumber} for {m.Send}");
        if (m.From is < 0 or > 127 || m.To is < 0 or > 127)
            throw new InvalidDataException($"{prefix} has a range outside 0..127");
        if (m.Points is < 1 or > 128)
            throw new InvalidDataException($"{prefix} has {m.Points} step points; expected 1..128");

        m.Label ??= "";
        m.KeyGesture ??= "";
        m.TransportCommand ??= "";
    }

    static string BackupPath(string path) => path + ".bak";

    static string TryRestorePrimary(Config recovered, string path)
    {
        // Never replace a primary that could not be quarantined: doing so would rotate
        // its corrupt bytes over the good backup. The in-memory recovered map is still used.
        if (File.Exists(path)) return "; working file could not be recreated";
        try
        {
            recovered.Save(path);
            return "; working file recreated";
        }
        catch (Exception e)
        {
            return $"; working file could not be recreated ({e.Message})";
        }
    }

    /// <summary>
    /// Move a file we will not load out of the way, under a name that says why, and
    /// return where it went ("" if it could not be moved at all).
    ///
    /// The stamp is to the second, so two failed loads inside the same second used to
    /// collide - `File.Move` threw onto an existing name and the whole thing was
    /// swallowed, losing the file the call exists to preserve. A suffix settles it.
    /// </summary>
    static string SetAside(string path, string reason)
    {
        if (!File.Exists(path)) return "";
        string stem = $"{path}.{reason}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string candidate = attempt == 0 ? stem : $"{stem}-{attempt}";
            if (File.Exists(candidate)) continue;
            try
            {
                File.Move(path, candidate);
                return candidate;
            }
            catch (IOException) { /* lost the race or name taken; try the next one */ }
            catch { return ""; }
        }
        return "";
    }

    /// <summary>Merge saved names into the shared table so the whole app sees them.</summary>
    public void ApplyNames()
    {
        foreach (var kv in ControlNames)
            if (int.TryParse(kv.Key, out int code) && !string.IsNullOrWhiteSpace(kv.Value))
                KnownButtons[code] = kv.Value;
    }

    /// <summary>Record a name for a code, both in the table and for saving.</summary>
    public void SetControlName(int code, string name)
    {
        KnownButtons[code] = name;
        ControlNames[code.ToString()] = name;
    }

    /// <summary>
    /// Persist through a fully flushed temporary file, then atomically replace the old
    /// file while retaining one last known-good backup.
    /// </summary>
    public void Save(string path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath);
        string dir = Path.GetDirectoryName(path)
            ?? throw new IOException($"cannot determine the directory for {path}");
        Directory.CreateDirectory(dir);

        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json));
        try
        {
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                string backup = BackupPath(path);
                try
                {
                    File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(path, backup, overwrite: true);
                    File.Move(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Encoders on channel 1 as CC 21..30, buttons on their own channel so they cannot
    /// collide, offset past the standardised low controller numbers. Each bank starts
    /// on a different block of CCs so switching banks never sends the same number.
    /// </summary>
    public static Config CreateDefault()
    {
        // A fresh map is already routed the way the migration would route it, so mark
        // it done rather than letting the first load rewrite what the user just set.
        var cfg = new Config { AnalogFactorySendsRepaired = true };
        (string name, int btn, int cc)[] banks =
        {
            ("USER",  BtnUser,  21),
            ("FX",    BtnFx,    41),
            ("INST",  BtnInst,  61),
            ("MIXER", BtnMixer, 81),
        };
        foreach (var (name, btn, cc) in banks)
            cfg.Banks.Add(new Bank
            {
                Name = name,
                SelectButton = btn,
                Pages = { NewPage(name, 1, cc) },
            });
        return cfg;
    }

    /// <summary>
    /// Continuous controls on channel 4, by the number the synth sends. Only the mod
    /// wheel is confirmed on the wire; the others are placeholders until seen, since
    /// they report only once the analog stream is open.
    /// </summary>
    public static readonly (int code, string name, int defaultCc)[] AnalogControls =
    {
        (1, "Mod wheel", 1),
        (2, "Pitch bend", 0),
        (3, "Expression", 11),
        (4, "Sustain", 64),
        (5, "Aftertouch", 74),
    };

    public static string AnalogName(int code)
    {
        foreach (var a in AnalogControls) if (a.code == code) return a.name;
        return $"Analog {code}";
    }

    /// <summary>
    /// Sustain is a footswitch on a two-pole jack, same kind of control as a panel
    /// button. Expression and the wheels are pots and stay continuous.
    /// </summary>
    public static bool IsAnalogSwitch(int code) => code == 4;

    /// <summary>
    /// Every performance control is routed to the application's stable virtual output.
    /// The DAW deliberately does not open the old UltraNova WinMM input, because that
    /// driver handle is invalidated whenever an Automap host genuinely restarts.
    /// </summary>
    public static string AnalogSendKind(int code) => code switch
    {
        1 => "cc",
        2 => "pitchbend",
        5 => "aftertouch",
        _ => "cc",
    };

    public static Page NewPage(string bankName, int index, int firstCc = 21)
    {
        string[] labels = { "OSC1", "OSC2", "CUTOFF", "RES", "ATTACK", "DECAY", "SUSTAIN", "RELEASE" };
        var page = new Page { Name = $"{bankName} {index}" };

        var enc = new Mapping[10];
        for (int i = 0; i < 10; i++)
            enc[i] = new Mapping
            {
                Send = "cc",
                Channel = 1,
                Number = Math.Min(127, firstCc + i),
                Label = i < labels.Length ? labels[i] : (i == 8 ? "FILTER" : "PATCH"),
            };
        page.Encoders = enc;

        foreach (int code in KnownButtons.Keys)
        {
            bool silent = IsReserved(code);
            page.Buttons[code.ToString()] = new Mapping
            {
                Send = silent ? "none" : "cc",
                Channel = 2,
                Number = Math.Min(127, 20 + code),
                Label = KnownButtons[code],
            };
        }

        // Touch stays silent until someone asks for it: a knob that fires a message the
        // moment a finger lands would be a surprise, not a feature.
        for (int i = 0; i < 10; i++)
            page.Touch[i.ToString()] = new Mapping
            {
                Send = "none", Channel = 3, Number = 21 + i, From = 0, To = 127,
            };

        // All five performance controls share the virtual route with the keyboard and
        // mapped panel controls; the DAW needs only one stable MIDI input.
        foreach (var (code, name, cc) in AnalogControls)
            page.Analog[code.ToString()] = new Mapping
            {
                Send = AnalogSendKind(code), Channel = 1, Number = cc, Label = name,
                Mode = IsAnalogSwitch(code) ? "momentary" : "normal",
            };

        return page;
    }

    public static int FactoryFirstCc(int bankIndex) => 21 + Math.Max(0, bankIndex) * 20;

    /// <summary>Strip every assignment on a page. Labels and numbers stay.</summary>
    public static void SilencePage(Page page)
    {
        if (page == null) return;
        if (page.Encoders != null)
            foreach (var m in page.Encoders) m.Send = "none";
        if (page.Buttons != null)
            foreach (var m in page.Buttons.Values) m.Send = "none";
        if (page.Touch != null)
            foreach (var m in page.Touch.Values) m.Send = "none";
        if (page.Analog != null)
            foreach (var m in page.Analog.Values) m.Send = "none";
    }

    /// <summary>Every page of this bank, the way Automap's Clear emptied a map.</summary>
    public void ClearBank(int bankIndex)
    {
        if (bankIndex < 0 || bankIndex >= Banks.Count) return;
        foreach (var p in Banks[bankIndex].Pages) SilencePage(p);
    }

    /// <summary>Put this page back to the factory assignments for its bank.</summary>
    public void RevertPage(int bankIndex, int pageIndex)
    {
        if (bankIndex < 0 || bankIndex >= Banks.Count) return;
        var bank = Banks[bankIndex];
        if (pageIndex < 0 || pageIndex >= bank.Pages.Count) return;
        bank.Pages[pageIndex] = NewPage(bank.Name, pageIndex + 1, FactoryFirstCc(bankIndex));
    }

    /// <summary>
    /// v1.0.0 shipped LEARN and VIEW as CC 20 and 21 on channel 2 — the factory
    /// numbers — while the window treated them as reserved and hid the assignment.
    /// A mapping that is still exactly that factory row is silenced; anything the
    /// user set in the debug bench is left alone.
    /// </summary>
    public void SilenceFactoryAppButtons()
    {
        foreach (var bank in Banks)
        foreach (var page in bank.Pages)
        {
            if (page.Buttons == null) continue;
            foreach (int code in new[] { BtnLearn, BtnView })
            {
                if (!page.Buttons.TryGetValue(code.ToString(), out var m)) continue;
                if (m.Send != "cc" || m.Channel != 2 || m.Number != 20 + code) continue;
                m.Send = "none";
            }
        }
    }

    /// <summary>
    /// The Touch tab labels its first mode Momentary, but used to write "normal",
    /// which ignores Released/Touched. Rewrite that to momentary. Toggle is left
    /// alone; a Touch mapping cannot have been set to Normal on purpose, the list
    /// never offered it.
    /// </summary>
    public void RepairTouchModes()
    {
        foreach (var bank in Banks)
        foreach (var page in bank.Pages)
        {
            if (page.Touch == null) continue;
            foreach (var m in page.Touch.Values)
                if (m.Mode == "normal") m.Mode = "momentary";
        }
    }

    /// <summary>
    /// Analog rows used to inherit Mapping.Mode's default of momentary, which is a
    /// switch mode. Sustain keeps that; wheels and expression become normal unless
    /// the user already picked inverted or a relative encoding.
    /// </summary>
    public void RepairAnalogModes()
    {
        foreach (var bank in Banks)
        foreach (var page in bank.Pages)
        {
            if (page.Analog == null) continue;
            foreach (var kv in page.Analog)
            {
                if (!int.TryParse(kv.Key, out int code)) continue;
                if (IsAnalogSwitch(code))
                {
                    if (kv.Value.Mode?.StartsWith("relative", StringComparison.Ordinal) == true
                        || kv.Value.Mode == "inverted")
                        kv.Value.Mode = "momentary";
                }
                else if (kv.Value.Mode is "momentary" or "toggle" or "step")
                    kv.Value.Mode = "normal";
            }
        }
    }

    /// <summary>
    /// Factory analog rows that are still silent are enabled on the stable virtual route.
    /// A non-default assignment remains untouched.
    /// </summary>
    public void RepairAnalogFactorySends()
    {
        foreach (var bank in Banks)
        foreach (var page in bank.Pages)
        {
            if (page.Analog == null) continue;
            foreach (var (code, _, cc) in AnalogControls)
            {
                if (!page.Analog.TryGetValue(code.ToString(), out var m)) continue;
                if (m.Channel != 1 || m.Number != cc) continue;
                if (m.Send == "none")
                    m.Send = AnalogSendKind(code);
            }
        }
    }

    /// <summary>
    /// Every panel button by the code it sends on channel 3. All forty were named on
    /// the hardware by pressing them one at a time and reading the code back.
    ///
    /// Codes 0-34 match the lamp numbering in KnownLeds. From 35 the two diverge: the
    /// lamps continue into indicators that have no button under them (the extra vocoder
    /// lamp, SELECT 1-6, the encoder rings), while the buttons carry on counting. So
    /// code 35 is VALUE+ when pressed but the vocoder indicator when lit.
    /// </summary>
    public static readonly Dictionary<int, string> KnownButtons = new()
    {
        // Automap row, left to right
        [0] = "LEARN", [1] = "VIEW", [2] = "USER", [3] = "FX", [4] = "INST",
        [5] = "MIXER", [6] = "LOCK", [7] = "FILTER",

        // mode, patch and global
        [8] = "AUDIO", [9] = "OCTAVE-", [10] = "GLOBAL", [11] = "OCTAVE+",
        [13] = "PATCH", [15] = "COMPARE", [16] = "WRITE",
        [17] = "PAGE<", [18] = "PAGE>",

        // SYNTH EDIT, in panel order
        [19] = "OSCILLATOR", [20] = "ENVELOPE", [21] = "MIXER SYN", [22] = "LFO",
        [23] = "FILTER SYN", [24] = "MODULATION", [25] = "VOICE", [26] = "EFFECTS",
        [27] = "VOCODER",

        // arpeggiator, chord, animate
        [28] = "ARP ON", [29] = "ARP SET", [30] = "ARP LATCH",
        [31] = "CHORD ON", [32] = "CHORD EDIT",
        [33] = "TWEAK", [34] = "TOUCH",

        // value and block selection, and the push on the patch dial
        [35] = "VALUE+", [36] = "VALUE-",
        [37] = "SELECT UP", [38] = "SELECT DN",
        [39] = "PATCH KNOB PUSH",
    };

    /// <summary>
    /// Lamps by the code that lights them when written on channel 1. Established on the
    /// hardware by lighting each code in turn and reading the panel.
    ///
    /// Note this is NOT the same numbering as the buttons: code 37 lights SELECT 2 but
    /// is sent by VALUE+. Above 49 nothing new lights - the codes repeat earlier lamps.
    /// </summary>
    public static readonly Dictionary<int, string> KnownLeds = new()
    {
        [0] = "LEARN", [1] = "VIEW", [2] = "USER", [3] = "FX", [4] = "INST",
        [5] = "MIXER", [6] = "LOCK", [7] = "FILTER",
        [8] = "AUDIO", [9] = "OCTAVE-", [10] = "GLOBAL", [11] = "OCTAVE+",
        [12] = "SYNTH", [13] = "PATCH", [14] = "AUTOMAP",
        [15] = "COMPARE", [16] = "WRITE", [17] = "PAGE<", [18] = "PAGE>",

        // SYNTH EDIT section
        [19] = "OSCILLATOR", [20] = "ENVELOPE", [21] = "MIXER", [22] = "LFO",
        [23] = "FILTER", [24] = "MODULATION", [25] = "VOICE", [26] = "EFFECTS",
        [27] = "VOCODER",

        // arpeggiator, chord, animate
        [28] = "ARP ON", [29] = "ARP SETTINGS", [30] = "ARP LATCH",
        [31] = "CHORD ON", [32] = "CHORD EDIT",
        [33] = "ANIMATE TWEAK", [34] = "ANIMATE TOUCH",

        // indicators with no button of their own
        [35] = "VOCODER (extra lamp)",
        [36] = "SELECT 1", [37] = "SELECT 2", [38] = "SELECT 3",
        [39] = "SELECT 4", [40] = "SELECT 5", [41] = "SELECT 6",
        [42] = "RING 1", [43] = "RING 2", [44] = "RING 3", [45] = "RING 4",
        [46] = "RING 5", [47] = "RING 6", [48] = "RING 7", [49] = "RING 8",
    };

    public static string LedName(int code) =>
        KnownLeds.TryGetValue(code, out var n) ? n : "";

    /// <summary>Encoder index to display name, as printed on the panel.</summary>
    public static string EncoderName(int i) =>
        i < 8 ? $"Encoder {i + 1}" : i == 8 ? "Filter knob" : "Patch dial";
}
