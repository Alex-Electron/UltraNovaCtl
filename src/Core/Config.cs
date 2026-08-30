using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraNovaCtl.Core;

/// <summary>What a single physical control sends when it moves.</summary>
public sealed class Mapping
{
    /// <summary>"cc", "note", "pitchbend", "key" or "none".</summary>
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

    [JsonIgnore] public bool Silent => Send == "none";
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
                _ => $"CC#{Number:000}",
            };
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
    /// Buttons the application uses for itself. They drive navigation and cannot be
    /// mapped: offering them would let a user silently break the way the panel works.
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

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "ultranovactl.json");

    public static Config Load(string path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return CreateDefault();
        try
        {
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json);
            if (c == null || c.Banks.Count == 0) return CreateDefault();
            foreach (var b in c.Banks) if (b.Pages.Count == 0) b.Pages.Add(NewPage(b.Name, 1));
            c.ApplyNames();
            return c;
        }
        catch (Exception e)
        {
            Console.WriteLine($"config unreadable ({e.Message}), using defaults");
            return CreateDefault();
        }
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

    public void Save(string path = null)
        => File.WriteAllText(path ?? DefaultPath, JsonSerializer.Serialize(this, Json));

    /// <summary>
    /// Encoders on channel 1 as CC 21..30, buttons on their own channel so they cannot
    /// collide, offset past the standardised low controller numbers. Each bank starts
    /// on a different block of CCs so switching banks never sends the same number.
    /// </summary>
    public static Config CreateDefault()
    {
        var cfg = new Config();
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
            bool navigation = code is BtnUser or BtnFx or BtnInst or BtnMixer
                              or BtnPagePrev or BtnPageNext;
            page.Buttons[code.ToString()] = new Mapping
            {
                Send = navigation ? "none" : "cc",     // navigation buttons stay silent
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

        // Wheels and pedals. Only the mod wheel's number is confirmed on hardware; the
        // others fill in the first time they are moved with Learn on.
        foreach (var (code, name, cc) in AnalogControls)
            page.Analog[code.ToString()] = new Mapping
            {
                Send = "none", Channel = 1, Number = cc, Label = name,
            };

        return page;
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
        [39] = "DIAL PUSH",
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
