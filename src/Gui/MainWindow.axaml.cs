using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UltraNovaCtl.Core;

namespace UltraNovaCtl.Gui;

public partial class MainWindow : Window
{
    /// <summary>VIEW on the panel opens this window, the way the original Automap did.</summary>
    public const int ViewButtonCode = 1;
    /// <summary>LEARN on the panel arms learn mode here, so the hand never leaves the synth.</summary>
    public const int LearnButtonCode = 0;
    const int LogLines = 1000;

    readonly AutomapEngine _engine = new();
    readonly MidiIn _midiIn = new();
    readonly EncoderTile[] _encoders = new EncoderTile[AutomapEngine.EncoderCount];
    readonly Dictionary<int, ButtonTile> _buttons = new();
    readonly Dictionary<int, AnalogTile> _analogs = new();

    // Events from the synth arrive far faster than the screen refreshes. They only record
    // state here; a timer paints what is current, so a fast spin costs one repaint.
    readonly int[] _val = new int[AutomapEngine.EncoderCount];
    readonly bool[] _touch = new bool[AutomapEngine.EncoderCount];
    readonly bool[] _encDirty = new bool[AutomapEngine.EncoderCount];
    readonly Dictionary<int, bool> _btnState = new();
    readonly Dictionary<int, int> _btnSent = new();
    readonly HashSet<int> _btnDirty = new();
    readonly Dictionary<int, int> _analogValue = new();
    readonly HashSet<int> _analogDirty = new();
    readonly Queue<string> _pending = new();
    readonly List<string> _lines = new();
    readonly object _lock = new();

    DispatcherTimer _tick, _retry;
    /// <summary>0 off, 1 assign once then switch off, 2 latched. Same as the original.</summary>
    volatile int _learnMode;

    bool LearnActive => _learnMode > 0;

    /// <summary>LEARN on the panel steps Off -> Latch -> Off, matching how it latches there.</summary>
    void CycleLearn()
    {
        _learn.SelectedIndex = _learn.SelectedIndex > 0 ? 0 : 2;
        Enqueue(_learn.SelectedIndex > 0
            ? "learn armed from the panel (Latch) - touch a control"
            : "learn off");
    }

    /// <summary>Consume a learn hit: single-shot mode disarms itself after one.</summary>
    void LearnConsumed()
    {
        if (_learnMode != 1) return;
        _learn.SelectedIndex = 0;
        _learnMode = 0;
        _learnBadge.IsVisible = false;
        Enqueue("learn: assigned, switching off (use Latch to keep going)");
    }
    bool _logDirty;
    object _selected;                      // EncoderTile or ButtonTile
    bool _loadingSelection;                // suppress write-back while filling the fields

    Button _connect, _save, _reset, _rescan, _export, _import, _reinit, _test;
    ComboBox _learn;
    Border _learnBadge;
    ComboBox _portBox, _inPortBox, _selSend;
    TextBox _selLabel, _selNumber, _selFrom, _selTo, _selPoints, _selKey;
    ComboBox _selPick, _selMode, _selChannel, _selTransport;
    ComboBox _touchSend, _touchChannel, _touchPick, _touchMode;
    TextBox _touchOff, _touchOn;
    TextBlock _touchNumberLabel, _fromLabel, _toLabel, _pointsLabel, _stepSizeText;
    TabItem _touchTab;
    TextBlock _selBinding, _selValue, _numberLabel;
    TextBlock _status, _selName;
    SelectableTextBlock _logText;
    Button _logCopy, _logSave, _logClear;
    CheckBox _logFollow, _echoLeds;
    ComboBox _kbdCh, _octave, _transpose, _after;
    TextBlock _mode;
    ScrollViewer _logScroll;
    StackPanel _bankTabs;
    Button _pagePrev, _pageNext, _pageAdd, _pageDel;
    Button _ledOn, _ledOff, _ledPrev, _ledNext, _ledAllOff, _ledName;
    ComboBox _ledCode;
    TextBox _ledNameBox;
    TextBlock _pageText;
    readonly List<Button> _bankButtons = new();
    WrapPanel _encoderRow;
    WrapPanel _buttonWrap, _analogWrap, _reservedWrap;

    public MainWindow()
    {
        InitializeComponent();

        _connect = this.FindControl<Button>("ConnectBtn");
        _save = this.FindControl<Button>("SaveBtn");
        _reset = this.FindControl<Button>("ResetBtn");
        _rescan = this.FindControl<Button>("RescanBtn");
        _reinit = this.FindControl<Button>("ReinitBtn");
        _test = this.FindControl<Button>("TestBtn");
        _export = this.FindControl<Button>("ExportBtn");
        _import = this.FindControl<Button>("ImportBtn");
        _learn = this.FindControl<ComboBox>("LearnBox");
        _learnBadge = this.FindControl<Border>("LearnBadge");
        _portBox = this.FindControl<ComboBox>("PortBox");
        _inPortBox = this.FindControl<ComboBox>("InPortBox");
        _status = this.FindControl<TextBlock>("StatusText");
        _logText = this.FindControl<SelectableTextBlock>("LogText");
        _logCopy = this.FindControl<Button>("LogCopyBtn");
        _logSave = this.FindControl<Button>("LogSaveBtn");
        _logClear = this.FindControl<Button>("LogClearBtn");
        _logFollow = this.FindControl<CheckBox>("LogFollowBox");
        _echoLeds = this.FindControl<CheckBox>("EchoLedsBox");
        _logScroll = this.FindControl<ScrollViewer>("LogScroll");
        _encoderRow = this.FindControl<WrapPanel>("EncoderRow");
        _buttonWrap = this.FindControl<WrapPanel>("ButtonWrap");
        _analogWrap = this.FindControl<WrapPanel>("AnalogWrap");
        _reservedWrap = this.FindControl<WrapPanel>("ReservedWrap");
        _selName = this.FindControl<TextBlock>("SelName");
        _selBinding = this.FindControl<TextBlock>("SelBinding");
        _selValue = this.FindControl<TextBlock>("SelValue");
        _selFrom = this.FindControl<TextBox>("SelFrom");
        _selTo = this.FindControl<TextBox>("SelTo");
        _selPick = this.FindControl<ComboBox>("SelPick");
        _selMode = this.FindControl<ComboBox>("SelMode");
        _numberLabel = this.FindControl<TextBlock>("NumberLabel");
        _fromLabel = this.FindControl<TextBlock>("FromLabel");
        _toLabel = this.FindControl<TextBlock>("ToLabel");
        _selPoints = this.FindControl<TextBox>("SelPoints");
        _selKey = this.FindControl<TextBox>("SelKey");
        _selTransport = this.FindControl<ComboBox>("SelTransport");
        _pointsLabel = this.FindControl<TextBlock>("PointsLabel");
        _stepSizeText = this.FindControl<TextBlock>("StepSizeText");
        _touchTab = this.FindControl<TabItem>("TouchTab");
        _touchSend = this.FindControl<ComboBox>("TouchSend");
        _touchChannel = this.FindControl<ComboBox>("TouchChannel");
        _touchPick = this.FindControl<ComboBox>("TouchPick");
        _touchNumberLabel = this.FindControl<TextBlock>("TouchNumberLabel");
        _touchOff = this.FindControl<TextBox>("TouchOff");
        _touchOn = this.FindControl<TextBox>("TouchOn");
        _touchMode = this.FindControl<ComboBox>("TouchMode");
        _selLabel = this.FindControl<TextBox>("SelLabel");
        _selSend = this.FindControl<ComboBox>("SelSend");
        _selNumber = this.FindControl<TextBox>("SelNumber");
        _selChannel = this.FindControl<ComboBox>("SelChannel");
        _kbdCh = this.FindControl<ComboBox>("KbdChBox");
        _octave = this.FindControl<ComboBox>("OctaveBox");
        _transpose = this.FindControl<ComboBox>("TransposeBox");
        _after = this.FindControl<ComboBox>("AfterBox");
        _mode = this.FindControl<TextBlock>("ModeText");
        _bankTabs = this.FindControl<StackPanel>("BankTabs");
        _pageText = this.FindControl<TextBlock>("PageText");
        _pagePrev = this.FindControl<Button>("PagePrevBtn");
        _pageNext = this.FindControl<Button>("PageNextBtn");
        _pageAdd = this.FindControl<Button>("PageAddBtn");
        _pageDel = this.FindControl<Button>("PageDelBtn");
        _ledCode = this.FindControl<ComboBox>("LedCodeBox");
        _ledOn = this.FindControl<Button>("LedOnBtn");
        _ledOff = this.FindControl<Button>("LedOffBtn");
        _ledPrev = this.FindControl<Button>("LedPrevBtn");
        _ledNext = this.FindControl<Button>("LedNextBtn");
        _ledAllOff = this.FindControl<Button>("LedAllOffBtn");
        _ledName = this.FindControl<Button>("LedNameBtn");
        _ledNameBox = this.FindControl<TextBox>("LedNameBox");

        // Same set the original offers - "CC, Note On/Off and Pitchbend" - with names
        // that cannot be mistaken for one another.
        _selSend.ItemsSource = SendKinds.Select(k => k.label).ToList();
        // filled per selection: knobs and buttons offer different modes
        BuildStateStrip();
        BuildInputList();
        _selChannel.ItemsSource = Enumerable.Range(1, 16).Select(i => i.ToString()).ToList();
        _selTransport.ItemsSource = Transport.All.Select(c => c.Label).ToList();
        _touchSend.ItemsSource = SendKinds.Select(k => k.label).ToList();
        _touchChannel.ItemsSource = Enumerable.Range(1, 16).Select(i => i.ToString()).ToList();
        _touchMode.ItemsSource = new[] { "Momentary", "Toggle" };
        // The list is filled by ApplySendKind, because what you pick depends on what
        // the control sends: controller numbers, note names, or nothing at all.

        _engine.Config = Config.Load();
        BuildPortList();
        BuildBankTabs();
        BuildTiles();
        UpdateSelectionUi();

        _connect.Click += (_, _) => ToggleConnection();
        _save.Click += (_, _) => SaveConfig();
        _reset.Click += (_, _) => ZeroValues();
        _rescan.Click += (_, _) => RescanMidi();
        _reinit.Click += (_, _) => Reinitialise();
        _test.Click += (_, _) => _engine.SendTest();
        _logCopy.Click += async (_, _) => await CopyLogAsync(true);
        _logSave.Click += async (_, _) => await SaveLogAsync();
        _logClear.Click += (_, _) => ClearLog();
        _echoLeds.IsChecked = _engine.Config.EchoButtonLeds;
        _echoLeds.IsCheckedChanged += (_, _) =>
            _engine.Config.EchoButtonLeds = _echoLeds.IsChecked == true;
        WireLogMenu();
        _export.Click += async (_, _) => await ExportAsync();
        _import.Click += async (_, _) => await ImportAsync();
        _portBox.SelectionChanged += (_, _) => OnPortChanged();
        _learn.ItemsSource = new[] { "Off", "On", "Latch" };
        _learn.SelectedIndex = 0;
        _learn.SelectionChanged += (_, _) =>
        {
            _learnMode = _learn.SelectedIndex;
            _learnBadge.IsVisible = _learnMode > 0;
        };
        _pagePrev.Click += (_, _) => { _engine.SetPage(_engine.PageIndex - 1); ReloadPage(); };
        _pageNext.Click += (_, _) => { _engine.SetPage(_engine.PageIndex + 1); ReloadPage(); };
        _pageAdd.Click += (_, _) => AddPage();
        _pageDel.Click += (_, _) => DeletePage();
        _ledOn.Click += (_, _) => LightLed(true);
        _ledOff.Click += (_, _) => LightLed(false);
        _ledPrev.Click += (_, _) => StepLed(-1);
        _ledNext.Click += (_, _) => StepLed(1);
        _ledAllOff.Click += (_, _) => AllLedsOff();
        _ledName.Click += (_, _) => NameLed();
        BuildLedBench();

        // The synth display is ASCII: anything else arrives as blanks, so keep it out
        // of the field rather than silently mangling it later.
        _selLabel.AddHandler(TextInputEvent, (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text) && e.Text.Any(c => c < 32 || c > 126))
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _selLabel.LostFocus += (_, _) => WriteBackSelection();
        _selNumber.LostFocus += (_, _) => WriteBackSelection();
        _selChannel.SelectionChanged += (_, _) => WriteBackSelection();
        _selSend.SelectionChanged += (_, _) =>
        {
            ApplySendKind();
            WriteBackSelection();
        };
        _selFrom.LostFocus += (_, _) => WriteBackSelection();
        _selTo.LostFocus += (_, _) => WriteBackSelection();
        _selMode.SelectionChanged += (_, _) => { WriteBackSelection(); ShowStepFields(); };
        _selPoints.LostFocus += (_, _) => WriteBackSelection();

        // Capture the combination as it is pressed, rather than making anyone type
        // "Ctrl+Shift+Z" by hand.
        _selTransport.SelectionChanged += (_, _) => WriteBackSelection();
        _selKey.KeyDown += (_, e) =>
        {
            e.Handled = true;
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

            var parts = new List<string>();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
            parts.Add(e.Key.ToString());

            _selKey.Text = string.Join("+", parts);
            WriteBackSelection();
        };
        _touchSend.SelectionChanged += (_, _) => { ApplyTouchKind(); WriteBackTouch(); };
        _touchChannel.SelectionChanged += (_, _) => WriteBackTouch();
        _touchPick.SelectionChanged += (_, _) => WriteBackTouch();
        _touchOff.LostFocus += (_, _) => WriteBackTouch();
        _touchOn.LostFocus += (_, _) => WriteBackTouch();
        _touchMode.SelectionChanged += (_, _) => WriteBackTouch();
        _selPick.SelectionChanged += (_, _) =>
        {
            if (_loadingSelection || _selPick.SelectedIndex < 0) return;
            _selNumber.Text = _selPick.SelectedIndex.ToString();
            WriteBackSelection();
        };

        _engine.Log += (_, s) => Enqueue(s);
        _engine.EncoderMoved += (_, e) => OnEncoder(e);
        _engine.EncoderTouched += (_, e) => OnTouch(e);
        _engine.ButtonChanged += (_, e) => OnButton(e);
        _engine.ModeChanged += (_, on) =>
        {
            Enqueue(on ? "synth entered AUTOMAP" : "synth left AUTOMAP");
            Post(() =>
            {
                _mode.Text = on ? "AUTOMAP" : "SYNTH";
                if (on) SyncViewLed();
            });
        };
        _engine.KeyboardState += (_, e) => Post(() => ShowKeyboardState(e.Register, e.Value));
        // Notes, wheels and aftertouch come straight off the synth's own MIDI port.
        _engine.PortMidi += (_, e) => ShowIncomingOnFader(e);
        _engine.AnalogMoved += (_, e) =>
        {
            lock (_lock) { _analogValue[e.Code] = e.Value; _analogDirty.Add(e.Code); }
            Enqueue($"analog: control {e.Code} = {e.Value}");
            if (LearnActive)
                Post(() => { if (_analogs.TryGetValue(e.Code, out var t)) { Select(t); LearnConsumed(); } });
        };
        // The panel's own USER/FX/INST/MIXER and page buttons change the selection too.
        _engine.SelectionChanged += (_, _) => Post(ReloadPage);

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _tick.Tick += (_, _) => Paint();
        _tick.Start();

        // Closing the window hides it: the server carries on, and the tray icon is how
        // it comes back. Quitting for real is on the tray menu.
        Closing += (s, e) =>
        {
            if (_reallyClosing) return;
            e.Cancel = true;
            HideToTray();
        };
        PropertyChanged += (_, ev) =>
        {
            if (ev.Property == WindowStateProperty && WindowState == WindowState.Minimized)
                HideToTray();
        };
        Program.Shutdown.Register(() =>
        {
            SaveWindowPlacement();
            SaveConfigQuietly();
            ShutdownEngine();
        });
        PropertyChanged += (_, ev) =>
        {
            if (ev.Property == WindowStateProperty || ev.Property == IsVisibleProperty)
                SyncViewLed();
        };

        // The synth should be the only thing that has to be switched on: connect at
        // startup and keep trying, so plugging it in later just works.
        RestoreWindowPlacement();
        _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _retry.Tick += (_, _) => TryAutoConnect();
        if (_engine.Config.AutoConnect)
        {
            Append("Starting. Switch the synth to AUTOMAP whenever you like.");
            TryAutoConnect();
            _retry.Start();
        }
        else Append("Ready. Press Connect, then AUTOMAP on the synth.");
        FlushLog();
    }

    /// <summary>
    /// Put the window back where it was, unless that place has gone: a monitor can be
    /// unplugged between sessions, and a window restored onto it would open off-screen
    /// with no way to drag it back.
    /// </summary>
    void RestoreWindowPlacement()
    {
        var c = _engine.Config;
        if (c.WindowWidth < 300 || c.WindowHeight < 200) return;

        Width = c.WindowWidth;
        Height = c.WindowHeight;

        bool visible = false;
        foreach (var screen in Screens.All)
        {
            var wa = screen.WorkingArea;
            // Enough of the title bar must land on a screen to be grabbable.
            if (c.WindowX + 120 >= wa.X && c.WindowX + 40 <= wa.X + wa.Width &&
                c.WindowY + 30 >= wa.Y && c.WindowY + 30 <= wa.Y + wa.Height)
            {
                visible = true;
                break;
            }
        }

        if (visible)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(c.WindowX, c.WindowY);
        }

        if (c.WindowMaximised) WindowState = WindowState.Maximized;
    }

    /// <summary>Record the placement; size and position mean nothing while maximised.</summary>
    void SaveWindowPlacement()
    {
        var c = _engine.Config;
        c.WindowMaximised = WindowState == WindowState.Maximized;
        if (WindowState != WindowState.Normal) return;

        c.WindowX = Position.X;
        c.WindowY = Position.Y;
        c.WindowWidth = (int)Width;
        c.WindowHeight = (int)Height;
    }

    /// <summary>Write the configuration without announcing it; used on the way out.</summary>
    void SaveConfigQuietly()
    {
        try { _engine.Config.Save(); } catch { /* nothing useful to do while closing */ }
    }

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    static void Post(Action a) => Dispatcher.UIThread.Post(a);

    /// <summary>Stored value and the human label shown in the dropdown.</summary>
    static readonly (string value, string label)[] SendKinds =
    {
        ("cc",        "Control Change"),
        ("note",      "Note On/Off"),
        ("pitchbend", "Pitch Bend"),
        ("key",       "Keystroke"),
        ("transport", "Transport"),
        ("none",      "Disabled"),
    };

    /// <summary>Mode list as the original offers it, plus toggle for buttons.</summary>
    /// <summary>Modes for continuous controls: encoders, wheels, pedals.</summary>
    static readonly (string value, string label)[] ContinuousModes =
    {
        ("normal",           "Normal"),
        ("inverted",         "Inverted"),
        ("relative",         "Relative (Two's Comp)"),
        ("relative-signed",  "Relative (Signed Bit)"),
        ("relative-signed2", "Relative (Signed Bit 2)"),
        ("relative-offset",  "Relative (Bin Offset)"),
    };

    /// <summary>Modes for switches: buttons, and encoder touch.</summary>
    static readonly (string value, string label)[] SwitchModes =
    {
        ("momentary", "Momentary"),
        ("normal",    "Normal"),
        ("toggle",    "Toggle"),
        ("step",      "Step"),
    };

    (string value, string label)[] ModeKinds =>
        _selected is ButtonTile ? SwitchModes : ContinuousModes;

    int ModeIndex(string value)
    {
        var kinds = ModeKinds;
        for (int i = 0; i < kinds.Length; i++) if (kinds[i].value == value) return i;
        return 0;
    }

    public static string NoteName(int n) => MidiNames.NoteLabel(n);

    /// <summary>Reshape the number field and its list for the selected message type.</summary>
    void ApplySendKind()
    {
        int si = _selSend.SelectedIndex;
        string kind = si >= 0 && si < SendKinds.Length ? SendKinds[si].value : "cc";

        bool disabled = kind == "none";
        bool isKey = kind == "key";
        bool isTransport = kind == "transport";
        bool usesNumber = kind is "cc" or "note";

        _selKey.IsVisible = isKey;
        _selTransport.IsVisible = isTransport;

        // A disabled control has no name to show, here or on the synth.
        _selLabel.IsEnabled = !disabled;
        if (disabled) _selLabel.Text = "-";
        else if (_selLabel.Text == "-") _selLabel.Text = "";
        _numberLabel.Text = kind switch
        {
            "note" => "Note", "key" => "Keys", "transport" => "Command",
            "pitchbend" => "", _ => "CC",
        };
        _numberLabel.IsVisible = usesNumber || isKey || isTransport;
        _selPick.IsVisible = usesNumber;
        _selChannel.IsEnabled = !disabled && !isKey && !isTransport;

        bool loading = _loadingSelection;
        _loadingSelection = true;
        _selPick.ItemsSource = kind switch
        {
            "note" => Enumerable.Range(0, 128).Select(NoteName).ToList(),
            "cc" => Enumerable.Range(0, 128).Select(MidiNames.CcLabel).ToList(),
            _ => new List<string>(),
        };
        if (usesNumber && int.TryParse(_selNumber.Text, out int cur) && cur is >= 0 and < 128)
            _selPick.SelectedIndex = cur;
        else
            _selPick.SelectedIndex = -1;
        _loadingSelection = loading;
    }

    static int SendIndex(string value)
    {
        for (int i = 0; i < SendKinds.Length; i++) if (SendKinds[i].value == value) return i;
        return 0;
    }

    // ---- building ----------------------------------------------------------

    /// <summary>
    /// The keyboard-state strip is editable: these registers travel both ways on
    /// channel 16, so the app can set the synth's channel, octave and transpose, not
    /// just report them.
    /// </summary>
    void BuildStateStrip()
    {
        _kbdCh.ItemsSource = Enumerable.Range(1, 16).Select(i => i.ToString()).ToList();
        _octave.ItemsSource = Enumerable.Range(-5, 10).Select(i => i.ToString("+#;-#;0")).ToList();
        _transpose.ItemsSource = Enumerable.Range(-12, 25).Select(i => i.ToString("+#;-#;0")).ToList();
        // "No change" leaves whatever the synth already has, as the original offered.
        _after.ItemsSource = new[] { "No change", "Off", "On" };

        _kbdCh.SelectionChanged += (_, _) => PushState(0, _kbdCh.SelectedIndex + 1);
        _octave.SelectionChanged += (_, _) => PushState(1, 64 + (_octave.SelectedIndex - 5));
        _transpose.SelectionChanged += (_, _) => PushState(2, 64 + (_transpose.SelectedIndex - 12));
        _after.SelectionChanged += (_, _) =>
        {
            if (_after.SelectedIndex <= 0) return;      // "No change" sends nothing
            PushState(3, _after.SelectedIndex == 2 ? 1 : 0);
        };
    }

    bool _fillingState;

    void PushState(int register, int value)
    {
        if (_fillingState || !_engine.Connected) return;
        _engine.SetKeyboardRegister(register, value);
        Enqueue($"synth: register {register} set to {value}");
    }

    /// <summary>
    /// Learn listens here. Pointing it at the interface a foreign synth is plugged into
    /// is what lets us copy the controller number that synth expects; pointing it at a
    /// virtual port does the same for a plug-in that sends its own CCs.
    /// </summary>
    void BuildInputList()
    {
        var names = MidiIn.PortNames();
        names.Insert(0, "(none)");
        _inPortBox.ItemsSource = names;
        _inPortBox.SelectedIndex = 0;
        _inPortBox.SelectionChanged += (_, _) => OpenInput();
        _midiIn.Received += (_, e) => OnMidiIn(e);
    }

    /// <summary>
    /// Rebuild both device lists, keeping the current choices if they are still there.
    /// Windows does not tell a running app about a MIDI interface appearing, so this is
    /// the honest way to notice one.
    /// </summary>
    /// <summary>
    /// Reopen everything without restarting the program: the MIDI outputs, the learn
    /// input, and the synth's display and lamps. Ports can be left stale by a process
    /// that ended badly, or by unplugging the device, and nothing recovers on its own.
    /// </summary>
    void Reinitialise()
    {
        Enqueue("reinitialising");

        _engine.ReopenOutputs();
        if (_inPortBox.SelectedIndex > 0) OpenInput();

        if (_engine.Connected)
        {
            _engine.Refresh();
            _engine.LightBanks();
            Enqueue("ports reopened, synth redrawn. If the DAW still hears nothing, "
                    + "press Test - and if a monitor sees that but the DAW does not, "
                    + "toggle the port off and on in the DAW's MIDI preferences.");
        }
        else
        {
            _reportedMissing = false;
            TryAutoConnect();
        }
    }

    void RescanMidi()
    {
        string wantOut = _portBox.SelectedItem as string;
        string wantIn = _inPortBox.SelectedItem as string;

        var outs = MidiOut.PortNames();
        _portBox.ItemsSource = outs;
        int oi = wantOut == null ? -1 : outs.FindIndex(n => n == wantOut);
        _portBox.SelectedIndex = oi >= 0 ? oi : (outs.Count > 0 ? 0 : -1);

        var ins = MidiIn.PortNames();
        ins.Insert(0, "(none)");
        bool wasOpen = _midiIn.IsOpen;
        _midiIn.Close();
        _inPortBox.ItemsSource = ins;
        int ii = wantIn == null ? 0 : ins.FindIndex(n => n == wantIn);
        _inPortBox.SelectedIndex = ii >= 0 ? ii : 0;
        if (wasOpen && ii > 0) OpenInput();

        Enqueue($"rescanned: {outs.Count} outputs, {ins.Count - 1} inputs");
    }

    void OpenInput()
    {
        _midiIn.Close();
        if (_inPortBox.SelectedIndex <= 0) { Enqueue("learn input closed"); return; }
        string name = _inPortBox.SelectedItem as string ?? "";
        if (_midiIn.Open(name, out string err)) Enqueue($"learn input: {_midiIn.PortName}");
        else
        {
            Enqueue("learn input failed: " + err);
            _inPortBox.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Something arrived on the learn input. With learn armed and a control selected,
    /// its number and channel are copied onto that control - which is exactly how the
    /// original learned a foreign synth's parameters.
    /// </summary>
    void OnMidiIn(MidiInEventArgs e)
    {
        Enqueue("in: " + e.Describe());
        ShowIncomingOnFader(e);
        if (!LearnActive || _selected == null) return;
        if (!e.IsCc && !e.IsNote && !e.IsPitchBend) return;

        Post(() =>
        {
            if (_selected == null) return;
            Mapping m = _selected switch
            {
                EncoderTile en => en.Mapping,
                AnalogTile an => an.Mapping,
                ButtonTile bt => bt.Mapping,
                _ => null,
            };
            if (m == null) return;

            m.Send = e.IsCc ? "cc" : e.IsNote ? "note" : "pitchbend";
            m.Channel = e.Channel;
            if (!e.IsPitchBend) m.Number = e.Data1;

            _loadingSelection = true;
            _selSend.SelectedIndex = SendIndex(m.Send);
            _selChannel.SelectedIndex = m.Channel - 1;
            _selNumber.Text = m.Number.ToString();
            _loadingSelection = false;
            ApplySendKind();
            ShowBinding(m);

            switch (_selected)
            {
                case EncoderTile en: en.RefreshCaption(); break;
                case AnalogTile an: an.RefreshCaption(); break;
                case ButtonTile bt: bt.RefreshCaption(); break;
            }

            Enqueue($"learned: {e.Describe()}");
            if (_engine.Connected) _engine.DrawLabels();
            LearnConsumed();
        });
    }

    /// <summary>
    /// The synth sends its wheels and aftertouch down the ordinary MIDI port, not the
    /// Automap channel, so when that port is the learn input we can still show them
    /// moving. Purely for display - what gets sent onward is decided by the mapping.
    /// </summary>
    void ShowIncomingOnFader(MidiInEventArgs e)
    {
        int code = -1;
        int value = e.Data2;

        if (e.IsPitchBend) { code = 2; value = e.Value >> 7; }          // 14-bit to 0..127
        else if (e.Kind == 0xD0) { code = 5; value = e.Data1; }         // channel aftertouch
        else if (e.IsCc)
            code = e.Data1 switch { 1 => 1, 11 => 3, 64 => 4, _ => -1 };

        if (code < 0) return;
        lock (_lock) { _analogValue[code] = Math.Clamp(value, 0, 127); _analogDirty.Add(code); }
    }

    void BuildPortList()
    {
        var names = MidiOut.PortNames();
        _portBox.ItemsSource = names;
        string want = _engine.Config.OutputPort ?? "";
        int idx = names.FindIndex(n => n.Contains(want, StringComparison.OrdinalIgnoreCase));
        _portBox.SelectedIndex = idx >= 0 ? idx : (names.Count > 0 ? 0 : -1);
    }

    void BuildTiles()
    {
        var page = _engine.CurrentPage;

        _encoderRow.Children.Clear();
        for (int i = 0; i < AutomapEngine.EncoderCount; i++)
        {
            var m = page.Encoders != null && i < page.Encoders.Length ? page.Encoders[i] : new Mapping();
            string title = i < 8 ? (i + 1).ToString() : (i == 8 ? "FILT" : "PATCH");
            var tile = new EncoderTile(i, title, m);
            tile.Root.PointerPressed += (_, _) => Select(tile);
            _encoders[i] = tile;
            _encoderRow.Children.Add(tile.Root);
            if (i == 7)
                _encoderRow.Children.Add(new Border
                {
                    Width = 1, Margin = new Thickness(6, 6, 6, 6),
                    Background = new SolidColorBrush(Color.Parse("#24242E")),
                });
        }

        _analogWrap.Children.Clear();
        _analogs.Clear();
        foreach (var (code, name, _) in Config.AnalogControls)
        {
            if (page.Analog == null || !page.Analog.TryGetValue(code.ToString(), out var am))
                am = new Mapping { Send = "none", Channel = 1, Number = 1, Label = name };
            var atile = new AnalogTile(code, name, am);
            atile.Root.PointerPressed += (_, _) => Select(atile);
            _analogs[code] = atile;
            _analogWrap.Children.Add(atile.Root);
        }

        _buttonWrap.Children.Clear();
        foreach (var kv in Config.KnownButtons.OrderBy(k => k.Key))
        {
            if (Config.IsReserved(kv.Key)) continue;      // shown separately, not mappable
            if (page.Buttons == null || !page.Buttons.TryGetValue(kv.Key.ToString(), out var m))
                m = new Mapping { Send = "none", Channel = 2, Number = 20 + kv.Key };
            var tile = new ButtonTile(kv.Key, kv.Value, m);
            tile.Root.PointerPressed += (_, _) => Select(tile);
            _buttons[kv.Key] = tile;
            _buttonWrap.Children.Add(tile.Root);
        }

        BuildReservedTiles();
    }

    // ---- banks and pages ---------------------------------------------------

    void BuildBankTabs()
    {
        _bankTabs.Children.Clear();
        _bankButtons.Clear();
        for (int i = 0; i < _engine.Config.Banks.Count; i++)
        {
            var bank = _engine.Config.Banks[i];
            int index = i;
            var btn = new Button
            {
                Content = bank.Name,
                MinWidth = 74,
                FontSize = 12,
                Padding = new Thickness(10, 5),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
            };
            ToolTip.SetTip(btn, bank.SelectButton >= 0
                ? $"also selected by {Config.KnownButtons[bank.SelectButton]} on the synth"
                : "app only");
            btn.Click += (_, _) => { _engine.SetBank(index); ReloadPage(); };
            _bankButtons.Add(btn);
            _bankTabs.Children.Add(btn);
        }
        UpdateSelectionUi();
    }

    static readonly IBrush TabIdle = new SolidColorBrush(Color.Parse("#171B22"));
    static readonly IBrush TabActive = new SolidColorBrush(Color.Parse("#2A2416"));
    static readonly IBrush TabIdleEdge = new SolidColorBrush(Color.Parse("#252530"));
    static readonly IBrush TabActiveEdge = new SolidColorBrush(Color.Parse("#F5A524"));
    static readonly IBrush TabIdleText = new SolidColorBrush(Color.Parse("#9A9AAA"));
    static readonly IBrush TabActiveText = new SolidColorBrush(Color.Parse("#F5C87A"));

    void UpdateSelectionUi()
    {
        for (int i = 0; i < _bankButtons.Count; i++)
        {
            bool on = i == _engine.BankIndex;
            _bankButtons[i].Background = on ? TabActive : TabIdle;
            _bankButtons[i].BorderBrush = on ? TabActiveEdge : TabIdleEdge;
            _bankButtons[i].Foreground = on ? TabActiveText : TabIdleText;
        }
        int pages = _engine.CurrentBank.Pages.Count;
        _pageText.Text = $"{_engine.PageIndex + 1}/{Math.Max(1, pages)}";
        _pagePrev.IsEnabled = _engine.PageIndex > 0;
        _pageNext.IsEnabled = _engine.PageIndex < pages - 1;
        _pageDel.IsEnabled = pages > 1;
    }

    /// <summary>Rebuild the tiles for whatever bank and page are current.</summary>
    void ReloadPage()
    {
        _selected = null;
        _engine.HighlightedEncoder = -1;
        BuildTiles();
        UpdateSelectionUi();
        ClearSelectionFields();
    }

    void AddPage()
    {
        var bank = _engine.CurrentBank;
        bank.Pages.Add(Config.NewPage(bank.Name, bank.Pages.Count + 1,
                                      21 + 20 * _engine.BankIndex));
        _engine.SetPage(bank.Pages.Count - 1);
        ReloadPage();
        Enqueue($"added page {bank.Pages.Count} to {bank.Name}");
    }

    /// <summary>One-line summary of what the selected control sends.</summary>
    /// <summary>Points only matter in Step mode, so they only appear there.</summary>
    void ShowStepFields()
    {
        var kinds = ModeKinds;
        int mi = _selMode.SelectedIndex;
        bool isStep = mi >= 0 && mi < kinds.Length && kinds[mi].value == "step";

        _pointsLabel.IsVisible = isStep;
        _selPoints.IsVisible = isStep;
        _stepSizeText.IsVisible = isStep;

        if (!isStep) return;
        Mapping m = _selected switch
        {
            EncoderTile e => e.Mapping,
            AnalogTile a => a.Mapping,
            ButtonTile b => b.Mapping,
            _ => null,
        };
        if (m == null) return;
        _stepSizeText.Text = m.Points > 1
            ? $"\u00d7 {m.StepSize:0.0}   ({string.Join(", ", Enumerable.Range(0, Math.Min(m.Points, 6)).Select(m.StepValue))}{(m.Points > 6 ? ", ..." : "")})"
            : "";
    }

    void ShowBinding(Mapping m)
    {
        string what = m.Send switch
        {
            "none" => "disabled",
            "key" => "types " + (m.KeyGesture.Length > 0 ? m.KeyGesture : "nothing yet"),
            "transport" => Transport.LabelOf(m.TransportCommand),
            "note" => MidiNames.NoteShort(m.Number),
            "pitchbend" => "Pitch Bend",
            _ => MidiNames.CcShort(m.Number),
        };
        string range = m.From == 0 && m.To == 127 ? "" : $" · {m.From}–{m.To}";
        string mode = m.Mode switch
        {
            "inverted" => " · inverted",
            "relative" => " · rel 2comp",
            "relative-signed" => " · rel signed",
            "relative-signed2" => " · rel signed2",
            "relative-offset" => " · rel offset",
            "toggle" => " · toggle",
            _ => "",
        };
        _selBinding.Text = m.Silent ? what : $"{what} · ch {m.Channel}{range}{mode}";
    }

    /// <summary>
    /// Lighting a lamp by hand is the only way to learn which code belongs to which
    /// button: the synth never tells us, so a person has to look at the panel and say.
    /// Names recorded here go into the button list.
    /// </summary>
    void BuildLedBench()
    {
        _ledCode.ItemsSource = Enumerable.Range(0, 128)
            .Select(i =>
            {
                string n = Config.LedName(i);
                return n.Length > 0 ? $"{i:000}  {n}" : $"{i:000}";
            })
            .ToList();
        _ledCode.SelectedIndex = 0;
        _ledCode.SelectionChanged += (_, _) =>
        {
            int code = _ledCode.SelectedIndex;
            _ledNameBox.Text = Config.LedName(code);
        };
    }

    void LightLed(bool on)
    {
        if (!_engine.Connected) { Enqueue("not connected"); return; }
        int code = Math.Max(0, _ledCode.SelectedIndex);
        _engine.SetLed(code, on);
        Enqueue($"LED {code:000} {(on ? "on" : "off")}");
    }

    /// <summary>Step to the next code and light it, so a whole range can be walked.</summary>
    void StepLed(int delta)
    {
        int code = Math.Max(0, _ledCode.SelectedIndex);
        if (_engine.Connected) _engine.SetLed(code, false);
        _ledCode.SelectedIndex = Math.Clamp(code + delta, 0, 127);
        LightLed(true);
    }

    void AllLedsOff()
    {
        if (!_engine.Connected) { Enqueue("not connected"); return; }
        for (int i = 0; i < 128; i++) _engine.SetLed(i, false);
        Enqueue("all LEDs cleared - press a bank button to bring navigation lamps back");
    }

    void NameLed()
    {
        int code = Math.Max(0, _ledCode.SelectedIndex);
        string name = (_ledNameBox.Text ?? "").Trim();
        if (name.Length == 0) { Enqueue("type what lit up first"); return; }

        Config.KnownLeds[code] = name;
        _engine.Config.SetControlName(code, name);
        BuildLedBench();
        _ledCode.SelectedIndex = code;
        ReloadPage();
        Enqueue($"LED {code:000} named \"{name}\"");
    }

    void DeletePage()
    {
        var bank = _engine.CurrentBank;
        if (bank.Pages.Count <= 1) { Enqueue("a bank keeps at least one page"); return; }
        int idx = _engine.PageIndex;
        string name = bank.Pages[idx].Name;
        bank.Pages.RemoveAt(idx);
        _engine.SetPage(Math.Min(idx, bank.Pages.Count - 1));
        ReloadPage();
        Enqueue($"removed page \"{name}\" from {bank.Name}");
    }

    void ClearSelectionFields()
    {
        _loadingSelection = true;
        _selName.Text = "nothing selected";
        _selLabel.Text = "";
        _selNumber.Text = "";
        _selChannel.SelectedIndex = -1;
        _selSend.SelectedIndex = -1;
        _selFrom.Text = "";
        _selTo.Text = "";
        _selPick.SelectedIndex = -1;
        _selMode.SelectedIndex = -1;
        _selBinding.Text = "click a knob or button";
        _selValue.Text = "";
        _loadingSelection = false;
    }

    /// <summary>
    /// The buttons the application keeps for itself, shown so it is clear they are
    /// taken and by what - rather than simply missing from the list.
    /// </summary>
    void BuildReservedTiles()
    {
        _reservedWrap.Children.Clear();
        foreach (var kv in Config.ReservedButtons.OrderBy(k => k.Key))
        {
            string name = Config.KnownButtons.TryGetValue(kv.Key, out var n) ? n : $"code {kv.Key}";

            var title = new TextBlock
            {
                Text = name, FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#9AA4B4")),
            };
            var what = new TextBlock
            {
                Text = kv.Value, FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#5E6675")),
                TextWrapping = TextWrapping.Wrap,
            };
            var stack = new StackPanel { Spacing = 1, Width = 132 };
            stack.Children.Add(title);
            stack.Children.Add(what);

            _reservedWrap.Children.Add(new Border
            {
                Child = stack,
                Padding = new Thickness(8, 5),
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.Parse("#101318")),
                BorderBrush = new SolidColorBrush(Color.Parse("#1E242C")),
            });
        }
    }

    // ---- selection ---------------------------------------------------------

    void Select(object tile)
    {
        WriteBackSelection();

        if (_selected is EncoderTile old) old.Selected = false;
        else if (_selected is ButtonTile oldB) oldB.Selected = false;
        else if (_selected is AnalogTile oldA) oldA.Selected = false;

        _selected = tile;
        _loadingSelection = true;

        // Light the ring of whatever encoder is being edited; clear it for anything else.
        _engine.HighlightedEncoder = tile is EncoderTile sel ? sel.Index : -1;

        // Picking a button here blinks it there, so the hand finds it without hunting.
        if (tile is ButtonTile picked && Config.HasOwnLed(picked.Code))
            _engine.BlinkLed(picked.Code);

        Mapping m;
        if (tile is EncoderTile e)
        {
            e.Selected = true;
            m = e.Mapping;
            _selName.Text = e.Index < 8 ? $"Encoder {e.Index + 1}"
                          : e.Index == 8 ? "Filter knob" : "Patch dial";
        }
        else if (tile is AnalogTile a)
        {
            a.Selected = true;
            m = a.Mapping;
            _selName.Text = a.Name;
        }
        else
        {
            var b = (ButtonTile)tile;
            b.Selected = true;
            m = b.Mapping;
            _selName.Text = $"{b.Name}  ·  code {b.Code}";
        }

        _selLabel.Text = m.Label;
        _selSend.SelectedIndex = SendIndex(m.Send);
        _selNumber.Text = m.Number.ToString();
        _selKey.Text = m.KeyGesture;
        _selTransport.SelectedIndex = Array.FindIndex(Transport.All, c => c.Id == m.TransportCommand);
        _selChannel.SelectedIndex = Math.Clamp(m.Channel, 1, 16) - 1;
        bool isSwitch = _selected is ButtonTile;
        _fromLabel.Text = isSwitch ? "Release" : "From";
        _toLabel.Text = isSwitch ? "Press" : "To";
        _selMode.ItemsSource = ModeKinds.Select(k => k.label).ToList();
        _selFrom.Text = m.From.ToString();
        _selTo.Text = m.To.ToString();
        _selMode.SelectedIndex = ModeIndex(m.Mode);
        _selPoints.Text = m.Points.ToString();
        _loadingSelection = false;
        ApplySendKind();
        LoadTouch();
        ShowStepFields();
        ShowBinding(m);
    }

    /// <summary>The touch mapping of the selected encoder, or null for anything else.</summary>
    Mapping SelectedTouch()
    {
        if (_selected is not EncoderTile e) return null;
        var page = _engine.CurrentPage;
        if (page.Touch == null) return null;
        string key = e.Index.ToString();
        if (!page.Touch.TryGetValue(key, out var m))
            page.Touch[key] = m = new Mapping { Send = "none", Channel = 3, Number = 21 + e.Index };
        return m;
    }

    void ApplyTouchKind()
    {
        int si = _touchSend.SelectedIndex;
        string kind = si >= 0 && si < SendKinds.Length ? SendKinds[si].value : "cc";
        bool usesNumber = kind is "cc" or "note";

        _touchNumberLabel.Text = kind switch { "note" => "Note", "pitchbend" => "", _ => "CC" };
        _touchNumberLabel.IsVisible = usesNumber;
        _touchPick.IsVisible = usesNumber;
        _touchChannel.IsEnabled = kind != "none";

        bool loading = _loadingSelection;
        _loadingSelection = true;
        int keep = _touchPick.SelectedIndex;
        _touchPick.ItemsSource = kind switch
        {
            "note" => Enumerable.Range(0, 128).Select(MidiNames.NoteLabel).ToList(),
            "cc" => Enumerable.Range(0, 128).Select(MidiNames.CcLabel).ToList(),
            _ => new List<string>(),
        };
        if (usesNumber) _touchPick.SelectedIndex = Math.Clamp(keep, 0, 127);
        _loadingSelection = loading;
    }

    void LoadTouch()
    {
        var m = SelectedTouch();
        _touchTab.IsEnabled = m != null;
        if (m == null) return;

        _loadingSelection = true;
        _touchSend.SelectedIndex = SendIndex(m.Send);
        _touchChannel.SelectedIndex = Math.Clamp(m.Channel, 1, 16) - 1;
        _loadingSelection = false;

        ApplyTouchKind();
        _loadingSelection = true;
        _touchPick.SelectedIndex = Math.Clamp(m.Number, 0, 127);
        // From is what a released finger sends, To is what a touching one sends.
        _touchOff.Text = m.From.ToString();
        _touchOn.Text = m.To.ToString();
        _touchMode.SelectedIndex = m.Mode == "toggle" ? 1 : 0;
        _loadingSelection = false;
    }

    void WriteBackTouch()
    {
        if (_loadingSelection) return;
        var m = SelectedTouch();
        if (m == null) return;

        int si = _touchSend.SelectedIndex;
        m.Send = si >= 0 && si < SendKinds.Length ? SendKinds[si].value : "none";
        if (_touchChannel.SelectedIndex >= 0) m.Channel = _touchChannel.SelectedIndex + 1;
        if (_touchPick.SelectedIndex >= 0) m.Number = _touchPick.SelectedIndex;
        if (int.TryParse(_touchOff.Text, out int off)) m.From = Math.Clamp(off, 0, 127);
        if (int.TryParse(_touchOn.Text, out int on)) m.To = Math.Clamp(on, 0, 127);
        m.Mode = _touchMode.SelectedIndex == 1 ? "toggle" : "normal";
    }

    void WriteBackSelection()
    {
        if (_loadingSelection || _selected == null) return;

        Mapping m = _selected switch
        {
            EncoderTile e => e.Mapping,
            AnalogTile a => a.Mapping,
            _ => ((ButtonTile)_selected).Mapping,
        };
        m.Label = new string((_selLabel.Text ?? "").Where(c => c >= 32 && c <= 126).ToArray());
        int si = _selSend.SelectedIndex;
        m.Send = si >= 0 && si < SendKinds.Length ? SendKinds[si].value : "cc";
        if (int.TryParse(_selNumber.Text, out int n)) m.Number = Math.Clamp(n, 0, 127);
        m.KeyGesture = Keystroke.Normalise(_selKey.Text ?? "");
        if (_selTransport.SelectedIndex >= 0)
            m.TransportCommand = Transport.All[_selTransport.SelectedIndex].Id;
        if (_selChannel.SelectedIndex >= 0) m.Channel = _selChannel.SelectedIndex + 1;
        if (int.TryParse(_selFrom.Text, out int f)) m.From = Math.Clamp(f, 0, 127);
        if (int.TryParse(_selTo.Text, out int t)) m.To = Math.Clamp(t, 0, 127);
        var kinds = ModeKinds;
        int mi = _selMode.SelectedIndex;
        m.Mode = mi >= 0 && mi < kinds.Length ? kinds[mi].value
               : (_selected is ButtonTile ? "momentary" : "normal");
        if (int.TryParse(_selPoints.Text, out int pts)) m.Points = Math.Clamp(pts, 2, 128);
        ShowStepFields();
        ShowBinding(m);

        switch (_selected)
        {
            case EncoderTile enc: enc.RefreshCaption(); break;
            case AnalogTile ana: ana.RefreshCaption(); break;
            case ButtonTile btn: btn.RefreshCaption(); break;
        }

        if (_engine.Connected) _engine.DrawLabels();
    }

    // ---- events from the synth ---------------------------------------------

    void OnEncoder(EncoderEventArgs e)
    {
        if ((uint)e.Index >= _val.Length) return;
        lock (_lock) { _val[e.Index] = e.Value; _encDirty[e.Index] = true; }
        if (LearnActive) Post(() => { if (_encoders[e.Index] != null) Select(_encoders[e.Index]); });
    }

    void OnTouch(TouchEventArgs e)
    {
        if ((uint)e.Index >= _touch.Length) return;
        lock (_lock) { _touch[e.Index] = e.Touched; _encDirty[e.Index] = true; }
        if (LearnActive && e.Touched)
            Post(() => { if (_encoders[e.Index] != null) { Select(_encoders[e.Index]); LearnConsumed(); } });
    }

    void OnButton(ButtonEventArgs e)
    {
        if (e.Pressed && e.Code == ViewButtonCode) { Post(ToggleWindow); return; }
        if (e.Pressed && e.Code == LearnButtonCode) { Post(CycleLearn); return; }
        lock (_lock)
        {
            _btnState[e.Code] = e.Pressed;
            if (e.Sent.HasValue) _btnSent[e.Code] = e.Sent.Value;
            _btnDirty.Add(e.Code);
        }
        if (LearnActive && e.Pressed)
            Post(() => { if (_buttons.TryGetValue(e.Code, out var t)) { Select(t); LearnConsumed(); } });
    }

    void ShowKeyboardState(int reg, int value)
    {
        // Filling from the device must not bounce straight back as a command.
        _fillingState = true;
        switch (reg)
        {
            case 0: _kbdCh.SelectedIndex = Math.Clamp(value, 1, 16) - 1; break;
            case 1: _octave.SelectedIndex = Math.Clamp(value - 64 + 5, 0, 9); break;
            case 2: _transpose.SelectedIndex = Math.Clamp(value - 64 + 12, 0, 24); break;
            case 3: _after.SelectedIndex = value != 0 ? 2 : 1; break;
        }
        _fillingState = false;
    }

    /// <summary>The only place the UI is touched: once per frame, from current state.</summary>
    void Paint()
    {
        int[] vals = null; bool[] touched = null; bool[] dirty = null;
        List<int> btns = null, analogs = null; List<string> lines = null;

        lock (_lock)
        {
            if (Array.IndexOf(_encDirty, true) >= 0)
            {
                vals = (int[])_val.Clone();
                touched = (bool[])_touch.Clone();
                dirty = (bool[])_encDirty.Clone();
                Array.Clear(_encDirty);
            }
            if (_btnDirty.Count > 0) { btns = new List<int>(_btnDirty); _btnDirty.Clear(); }
            if (_analogDirty.Count > 0) { analogs = new List<int>(_analogDirty); _analogDirty.Clear(); }
            if (_pending.Count > 0) { lines = new List<string>(_pending); _pending.Clear(); }
        }

        if (dirty != null)
            for (int i = 0; i < dirty.Length; i++)
            {
                if (!dirty[i] || _encoders[i] == null) continue;
                _encoders[i].Set(vals[i], touched[i]);
                if (_selected is EncoderTile sel && sel.Index == i)
                    _selValue.Text = vals[i].ToString();
            }

        if (btns != null)
            foreach (int code in btns)
                if (_buttons.TryGetValue(code, out var t))
                {
                    t.SetPressed(_btnState.TryGetValue(code, out var d) && d);
                    if (_btnSent.TryGetValue(code, out int v)) t.SetValue(v);
                }

        if (analogs != null)
            foreach (int code in analogs)
                if (_analogs.TryGetValue(code, out var t))
                    t.Set(_analogValue.TryGetValue(code, out int v) ? v : 0);

        if (lines != null) { foreach (string l in lines) Append(l); }
        FlushLog();
    }

    /// <summary>Silent connection attempt; noisy only when the state actually changes.</summary>
    void TryAutoConnect()
    {
        if (_engine.Connected || _connecting) return;
        _connecting = true;
        try
        {
            if (_engine.Start())
            {
                _connect.Content = "Disconnect";
                _status.Text = "connected to UltraNova";
                Enqueue("connected automatically");
            }
            else if (!_reportedMissing)
            {
                _reportedMissing = true;
                _status.Text = "waiting for the synth";
                Enqueue("synth not found - waiting. Check it is on and the original Automap is closed.");
            }
        }
        finally { _connecting = false; }
    }

    bool _connecting, _reportedMissing;

    // ---- actions -----------------------------------------------------------

    void ToggleConnection()
    {
        if (_engine.Connected)
        {
            _retry?.Stop();          // an explicit disconnect means stay disconnected
            _engine.Stop();
            _connect.Content = "Connect";
            _status.Text = "not connected";
            _mode.Text = "-";
            Enqueue("disconnected");
            return;
        }

        WriteBackSelection();
        if (_portBox.SelectedItem is string p) _engine.Config.OutputPort = p;
        _reportedMissing = false;
        if (_engine.Config.AutoConnect) _retry?.Start();

        if (_engine.Start())
        {
            _connect.Content = "Disconnect";
            _status.Text = "connected to UltraNova";
            Enqueue("connected. Press AUTOMAP on the synth.");
        }
        else
        {
            _status.Text = "connection failed";
            Enqueue("could not connect - check the synth is on and the original Automap is not running.");
        }
    }

    void OnPortChanged()
    {
        if (_portBox.SelectedItem is not string name) return;
        _engine.Config.OutputPort = name;
        if (_engine.Connected) { _engine.ReopenOutputs(); Enqueue($"output switched to \"{name}\""); }
    }

    void ZeroValues()
    {
        lock (_lock)
        {
            for (int i = 0; i < _val.Length; i++) { _val[i] = 0; _encDirty[i] = true; }
        }
        _engine.ResetValues();
        Enqueue("all encoder values reset to zero");
    }

    static readonly FilePickerFileType MapFile = new("UltraNovaCtl mapping")
    {
        Patterns = new[] { "*.json" },
    };

    static readonly FilePickerFileType AutomapFile = new("Novation Automap mapping")
    {
        Patterns = new[] { "*.automap" },
    };

    /// <summary>Write the current mappings somewhere the user chooses.</summary>
    async Task ExportAsync()
    {
        WriteBackSelection();
        if (_portBox.SelectedItem is string p) _engine.Config.OutputPort = p;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export mappings",
            SuggestedFileName = "ultranovactl-mappings.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { MapFile },
        });
        if (file is null) return;

        try
        {
            _engine.Config.Save(file.Path.LocalPath);
            Enqueue($"exported to {file.Path.LocalPath}");
        }
        catch (Exception e) { Enqueue("export failed: " + e.Message); }
    }

    /// <summary>
    /// Replace the mappings with a file. The output port from the file is ignored: a
    /// mapping shared between machines should not silently repoint MIDI somewhere the
    /// other machine does not have.
    /// </summary>
    async Task ImportAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import mappings",
            AllowMultiple = false,
            FileTypeFilter = new[] { MapFile, AutomapFile },
        });
        if (files is null || files.Count == 0) return;

        string path = files[0].Path.LocalPath;
        try
        {
            Config loaded;
            if (AutomapImport.LooksLikeAutomap(path))
            {
                loaded = AutomapImport.Load(path, out string what);
                Enqueue($"read Novation mapping: {what}");
            }
            else loaded = Config.Load(path);
            string keepPort = _engine.Config.OutputPort;
            loaded.OutputPort = keepPort;
            _engine.Config = loaded;

            _selected = null;
            BuildBankTabs();
            BuildTiles();
            UpdateSelectionUi();
            ClearSelectionFields();
            if (_engine.Connected) _engine.Refresh();

            int pages = loaded.Banks.Sum(b => b.Pages.Count);
            Enqueue($"imported {loaded.Banks.Count} banks, {pages} pages from {path}");
        }
        catch (Exception e) { Enqueue("import failed: " + e.Message); }
    }

    void SaveConfig()
    {
        WriteBackSelection();
        if (_portBox.SelectedItem is string p) _engine.Config.OutputPort = p;
        try
        {
            _engine.Config.Save();
            Enqueue($"configuration saved: {Config.DefaultPath}");
            if (_engine.Connected) { _engine.ReopenOutputs(); _engine.DrawLabels(); }
        }
        catch (Exception e) { Enqueue("save failed: " + e.Message); }
    }

    /// <summary>Bring the window up whether it is hidden, minimised or just behind.</summary>
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        SyncViewLed();
    }

    bool _reallyClosing;

    public void HideToTray()
    {
        SaveWindowPlacement();
        // Restore the state first: a hidden minimised window comes back minimised.
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Hide();
        SyncViewLed();
    }

    /// <summary>Called from the tray menu.</summary>
    public void ToggleFromTray() => ToggleWindow();

    public void ReinitialiseFromTray() => Reinitialise();

    /// <summary>Stop everything for good; only the tray Quit does this.</summary>
    public void ShutdownEngine()
    {
        _reallyClosing = true;
        _tick?.Stop();
        _retry?.Stop();
        _midiIn.Dispose();
        _engine.Stop();
    }

    /// <summary>VIEW on the panel works as a toggle, and its lamp shows the window state.</summary>
    void ToggleWindow()
    {
        if (IsVisible && WindowState != WindowState.Minimized) HideToTray();
        else ShowFromTray();
    }

    /// <summary>Keep the VIEW lamp in step with whether the editor is on screen.</summary>
    void SyncViewLed()
    {
        if (_engine.Connected)
            _engine.SetLed(ViewButtonCode, IsVisible && WindowState != WindowState.Minimized);
    }

    void Enqueue(string line)
    {
        lock (_lock) { if (_pending.Count < 500) _pending.Enqueue(line); }
    }

    void Append(string line)
    {
        _lines.Add(line);
        if (_lines.Count > LogLines) _lines.RemoveRange(0, _lines.Count - LogLines);
        _logDirty = true;
    }

    /// <summary>Rebuild the log text at most once a frame, newest at the bottom.</summary>
    void FlushLog()
    {
        if (!_logDirty) return;

        // Rewriting the text drops any selection, so hold off while something is
        // selected - otherwise copying a line is a race against the next event.
        if (_logText.SelectionStart != _logText.SelectionEnd) return;

        _logDirty = false;
        _logText.Text = string.Join('\n', _lines);
        if (_logFollow.IsChecked == true) _logScroll.ScrollToEnd();
    }

    void ClearLog()
    {
        _lines.Clear();
        _logDirty = true;
        _logText.Text = "";
    }

    /// <summary>Context menu and Ctrl+C over the log.</summary>
    void WireLogMenu()
    {
        void Hook(string name, Action a)
        {
            var item = this.FindControl<MenuItem>(name);
            if (item != null) item.Click += (_, _) => a();
        }

        Hook("LogMenuCopySel", async () => await CopyLogAsync(true));
        Hook("LogMenuCopyAll", async () => await CopyLogAsync(false));   // deliberate: whole log
        Hook("LogMenuSave", async () => await SaveLogAsync());
        Hook("LogMenuClear", ClearLog);

        // Ctrl+C copies the selection, or the whole log when nothing is selected.
        _logText.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            e.Handled = true;
            await CopyLogAsync(true);
        };
    }

    /// <summary>Copy the selection if there is one, otherwise everything.</summary>
    async Task CopyLogAsync(bool preferSelection)
    {
        var clip = GetTopLevel(this)?.Clipboard;
        if (clip == null) { Enqueue("no clipboard available"); return; }

        string sel = preferSelection ? _logText.SelectedText : null;
        bool haveSelection = !string.IsNullOrEmpty(sel);
        string text = haveSelection ? sel : string.Join(Environment.NewLine, _lines);

        // Avalonia 12 replaced SetTextAsync and DataObject with this pair.
        var payload = new DataTransfer();
        payload.Add(DataTransferItem.CreateText(text));
        await clip.SetDataAsync(payload);

        Enqueue(haveSelection ? "copied the selection" : $"copied {_lines.Count} lines");
    }

    async Task SaveLogAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save log",
            SuggestedFileName = "ultranovactl-log.txt",
            DefaultExtension = "txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } },
            },
        });
        if (file is null) return;

        try
        {
            await File.WriteAllLinesAsync(file.Path.LocalPath, _lines);
            Enqueue($"log written to {file.Path.LocalPath}");
        }
        catch (Exception e) { Enqueue("could not write the log: " + e.Message); }
    }
}

/// <summary>One encoder: dial, editable caption underneath, live value.</summary>
public sealed class EncoderTile
{
    public Border Root { get; }
    public int Index { get; }
    public Mapping Mapping { get; }

    readonly KnobVisual _knob;
    readonly TextBlock _caption, _value;
    static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#14141A"));
    static readonly IBrush Picked = new SolidColorBrush(Color.Parse("#1B2430"));
    static readonly IBrush IdleEdge = new SolidColorBrush(Color.Parse("#22222C"));
    static readonly IBrush PickedEdge = new SolidColorBrush(Color.Parse("#4C7FA8"));

    bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Root.Background = value ? Picked : Idle;
            Root.BorderBrush = value ? PickedEdge : IdleEdge;
        }
    }

    public EncoderTile(int index, string title, Mapping m)
    {
        Index = index;
        Mapping = m;

        _knob = new KnobVisual { Width = 60, Height = 60 };
        _value = new TextBlock
        {
            Text = "0", FontSize = 14, FontFamily = new FontFamily("Consolas,monospace"),
            Foreground = new SolidColorBrush(Color.Parse("#D8D8E4")),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _caption = new TextBlock
        {
            FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#8A8A9A")),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 82,
        };

        var head = new TextBlock
        {
            Text = title, FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#5E5E6E")),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var stack = new StackPanel { Spacing = 2, Width = 84 };
        stack.Children.Add(head);
        stack.Children.Add(_knob);
        stack.Children.Add(_value);
        stack.Children.Add(_caption);

        Root = new Border
        {
            Child = stack, Padding = new Thickness(2, 5), Margin = new Thickness(2),
            CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            Background = Idle, BorderBrush = IdleEdge, Cursor = new Cursor(StandardCursorType.Hand),
        };
        RefreshCaption();
    }

    public void Set(int value, bool touched)
    {
        _knob.Value = value;
        _knob.Touched = touched;
        _value.Text = value.ToString();
    }

    public void RefreshCaption() => _caption.Text = Mapping.Send switch
    {
        "none" => "—",
        "pitchbend" => "Bend · ch " + Mapping.Channel,
        "note" => $"{MidiNames.NoteShort(Mapping.Number)} · ch {Mapping.Channel}",
        _ => $"{Mapping.DisplayLabel} · {Mapping.Number:000}",
    };
}

/// <summary>
/// A wheel or pedal drawn as a horizontal fader. The bar travels smoothly rather than
/// jumping, because a jump reads as a glitch while a 90ms slide reads as movement; the
/// border lights briefly on each message so activity is visible even when the value
/// barely changes. Pitch bend is bipolar - it grows from the centre in both directions,
/// since its rest position is the middle of the range, not the left edge.
/// </summary>
public sealed class AnalogTile
{
    const double TrackWidth = 110;

    public Border Root { get; }
    public int Code { get; }
    public string Name { get; }
    public Mapping Mapping { get; }

    readonly TextBlock _caption, _value;
    readonly Border _fill, _centre;
    readonly DispatcherTimer _glow;

    static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#14141A"));
    static readonly IBrush Picked = new SolidColorBrush(Color.Parse("#1B2430"));
    static readonly IBrush IdleEdge = new SolidColorBrush(Color.Parse("#22222C"));
    static readonly IBrush PickedEdge = new SolidColorBrush(Color.Parse("#4C7FA8"));
    static readonly IBrush ActiveEdge = new SolidColorBrush(Color.Parse("#F5A524"));
    static readonly IBrush FillIdle = new SolidColorBrush(Color.Parse("#5AA9E6"));
    static readonly IBrush FillActive = new SolidColorBrush(Color.Parse("#F5A524"));

    bool _selected, _hot;

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Restyle(); }
    }

    /// <summary>Pitch bend rests in the middle, so its bar grows from the centre.</summary>
    bool Bipolar => Code == 2;

    public AnalogTile(int code, string name, Mapping m)
    {
        Code = code; Name = name; Mapping = m;

        var title = new TextBlock
        {
            Text = name, FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#C8C8D4")),
        };
        _value = new TextBlock
        {
            Text = "-", FontSize = 11, FontFamily = new FontFamily("Consolas,monospace"),
            Foreground = new SolidColorBrush(Color.Parse("#8A8A9A")),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var head = new Grid();
        head.Children.Add(title);
        head.Children.Add(_value);

        _fill = new Border
        {
            Background = FillIdle,
            Height = 5,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(2),
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Layoutable.WidthProperty,
                    Duration = TimeSpan.FromMilliseconds(90),
                },
                new ThicknessTransition
                {
                    Property = Layoutable.MarginProperty,
                    Duration = TimeSpan.FromMilliseconds(90),
                },
            },
        };

        // Faint centre tick, so the middle is findable at a glance on bipolar controls.
        _centre = new Border
        {
            Width = 1, Height = 5,
            Background = new SolidColorBrush(Color.Parse("#3A3A46")),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = Bipolar,
        };

        var track = new Grid { Height = 5, Width = TrackWidth };
        track.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1C1C24")),
            CornerRadius = new CornerRadius(2),
        });
        track.Children.Add(_centre);
        track.Children.Add(_fill);

        _caption = new TextBlock
        {
            FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#6E6E80")),
        };

        var stack = new StackPanel { Spacing = 4, Width = TrackWidth + 16 };
        stack.Children.Add(head);
        stack.Children.Add(track);
        stack.Children.Add(_caption);

        Root = new Border
        {
            Child = stack, Padding = new Thickness(8, 6), Margin = new Thickness(3),
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Background = Idle, BorderBrush = IdleEdge,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        _glow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _glow.Tick += (_, _) =>
        {
            _glow.Stop();
            _hot = false;
            _fill.Background = FillIdle;
            Restyle();
        };

        RefreshCaption();
    }

    public void Set(int value)
    {
        value = Math.Clamp(value, 0, 127);
        _value.Text = value.ToString();

        if (Bipolar)
        {
            // Distance from the centre, drawn to whichever side the control moved.
            double half = TrackWidth / 2;
            double offset = (value - 64) / 63.0;
            double w = Math.Abs(offset) * half;
            _fill.HorizontalAlignment = HorizontalAlignment.Left;
            _fill.Margin = new Thickness(offset >= 0 ? half : half - w, 0, 0, 0);
            _fill.Width = w;
        }
        else
        {
            _fill.Margin = new Thickness(0);
            _fill.Width = TrackWidth * value / 127.0;
        }

        _hot = true;
        _fill.Background = FillActive;
        Restyle();
        _glow.Stop();
        _glow.Start();
    }

    void Restyle()
    {
        Root.Background = _selected ? Picked : Idle;
        Root.BorderBrush = _hot ? ActiveEdge : _selected ? PickedEdge : IdleEdge;
    }

    public void RefreshCaption() => _caption.Text = Mapping.Send switch
    {
        "none" => "\u2014",
        "pitchbend" => "Bend \u00b7 ch " + Mapping.Channel,
        "note" => MidiNames.NoteShort(Mapping.Number) + " \u00b7 ch " + Mapping.Channel,
        _ => $"CC {Mapping.Number:000} \u00b7 ch {Mapping.Channel}",
    };
}

/// <summary>One panel button: name, code, and what it sends.</summary>
public sealed class ButtonTile
{
    public Border Root { get; }
    public int Code { get; }
    public string Name { get; }
    public Mapping Mapping { get; }

    readonly TextBlock _caption, _value;
    static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#14141A"));
    static readonly IBrush Down = new SolidColorBrush(Color.Parse("#3A2E14"));
    static readonly IBrush Picked = new SolidColorBrush(Color.Parse("#1B2430"));
    static readonly IBrush IdleEdge = new SolidColorBrush(Color.Parse("#22222C"));
    static readonly IBrush DownEdge = new SolidColorBrush(Color.Parse("#E8A33D"));
    static readonly IBrush PickedEdge = new SolidColorBrush(Color.Parse("#4C7FA8"));

    bool _selected, _pressed;

    public bool Selected { get => _selected; set { _selected = value; Restyle(); } }

    public ButtonTile(int code, string name, Mapping m)
    {
        Code = code; Name = name; Mapping = m;

        var title = new TextBlock
        {
            Text = name, FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#C8C8D4")),
        };
        // What the button last sent. In toggle and step modes this is the only way to
        // see where it currently stands.
        _value = new TextBlock
        {
            Text = "", FontSize = 11, FontFamily = new FontFamily("Consolas,monospace"),
            Foreground = new SolidColorBrush(Color.Parse("#8A8A9A")),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var head = new Grid();
        head.Children.Add(title);
        head.Children.Add(_value);

        _caption = new TextBlock
        {
            FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#6E6E80")),
        };

        var stack = new StackPanel { Spacing = 1, Width = 92 };
        stack.Children.Add(head);
        stack.Children.Add(_caption);

        Root = new Border
        {
            Child = stack, Padding = new Thickness(7, 5), Margin = new Thickness(3),
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Background = Idle, BorderBrush = IdleEdge, Cursor = new Cursor(StandardCursorType.Hand),
        };
        RefreshCaption();
    }

    public void SetPressed(bool down) { _pressed = down; Restyle(); }

    public void SetValue(int v) => _value.Text = v.ToString();

    void Restyle()
    {
        Root.Background = _pressed ? Down : _selected ? Picked : Idle;
        Root.BorderBrush = _pressed ? DownEdge : _selected ? PickedEdge : IdleEdge;
    }

    public void RefreshCaption() => _caption.Text = Mapping.Send switch
    {
        "none" => "—",
        "pitchbend" => "Bend · ch " + Mapping.Channel,
        "note" => $"{MidiNames.NoteShort(Mapping.Number)} · ch {Mapping.Channel}",
        _ => $"CC {Mapping.Number:000} · ch {Mapping.Channel}",
    };
}
