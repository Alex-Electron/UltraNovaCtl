using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using UltraNovaCtl.Core;

namespace UltraNovaCtl.Gui;

/// <summary>
/// The first editor screen: a thin slice through the whole stack, on purpose.
///
/// It shows a dozen parameters rather than all 497, because the point is not coverage but
/// finding out whether the carefully checked core survives contact with a real interface
/// and a real instrument. Everything below the surface is the production path: the engine's
/// single Port 1 reader, the private write pin, the patch session, the parameter codec.
/// Nothing here is a stand-in.
///
/// Three things this screen has to get right, and each is where a naive version breaks.
///
/// **No echo.** A value arriving from the instrument updates a control, and a control being
/// updated raises a change event. Sending that back produces a loop that fights the hand on
/// the panel. Every update from the instrument runs inside <see cref="_updating"/>, and the
/// handlers do nothing while it is set.
///
/// **One thread owns the controls.** Engine events arrive on the Port 1 reader thread. The
/// session is given this window's synchronisation context so model updates land here; the
/// few places that subscribe to the engine directly marshal themselves.
///
/// **Ownership is not ours by default.** While Novation's own editor holds the instrument,
/// the controls are disabled and say why, rather than sending into a pin somebody else owns.
/// </summary>
public partial class EditorWindow : Window
{
    readonly AutomapEngine _engine;
    readonly PatchSession _session;
    readonly List<Row> _rows = new();
    readonly DispatcherTimer _stateTimer;

    // This project resolves named controls by hand rather than by generated fields, the
    // way MainWindow does; keeping to that means one pattern in the codebase, not two.
    Button _attachBtn, _refreshBtn;
    StackPanel _params;
    TextBlock _patchName, _patchSlot, _dirtyText, _stateText, _logText;

    bool _updating;
    bool _closed;
    bool _automapWarned;

    /// <summary>One parameter: its description and the controls showing it.</summary>
    sealed class Row
    {
        public ParameterDescriptor Descriptor;
        public Slider Slider;
        public ComboBox Choice;
        public CheckBox Toggle;
        public TextBlock Value;
    }

    public EditorWindow() : this(new AutomapEngine()) { }

    public EditorWindow(AutomapEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        InitializeComponent();

        _attachBtn = this.FindControl<Button>("AttachBtn");
        _refreshBtn = this.FindControl<Button>("RefreshBtn");
        _params = this.FindControl<StackPanel>("Params");
        _patchName = this.FindControl<TextBlock>("PatchName");
        _patchSlot = this.FindControl<TextBlock>("PatchSlot");
        _dirtyText = this.FindControl<TextBlock>("DirtyText");
        _stateText = this.FindControl<TextBlock>("StateText");
        _logText = this.FindControl<TextBlock>("LogText");

        // The session marshals model updates here, so handlers touch controls on this thread.
        _session = new PatchSession(_engine, SynchronizationContext.Current);
        _session.PatchLoaded += OnPatchLoaded;
        _session.PatchHeld += OnPatchHeld;
        _session.InstrumentEdited += OnInstrumentEdited;
        _session.InstrumentSelected += OnInstrumentSelected;

        BuildRows();

        _attachBtn.Click += OnAttachClicked;
        _refreshBtn.Click += (_, _) => Say(_session.Refresh() ? "запрошен буфер" : "запрос не ушёл");

        // The connection state changes without anyone calling in - the native editor can
        // take the instrument while this window sits idle - so it is polled for display.
        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _stateTimer.Tick += (_, _) => PaintState();
        _stateTimer.Start();

        PaintState();
    }

    // ---- building the screen ----------------------------------------------

    void BuildRows()
    {
        string group = null;
        foreach (var d in KnownParameters.FirstScreen)
        {
            if (d.Short != group)
            {
                group = d.Short;
                _params.Children.Add(new TextBlock { Text = group, Classes = { "group" } });
            }
            _params.Children.Add(BuildRow(d));
        }
    }

    Control BuildRow(ParameterDescriptor d)
    {
        var row = new Row { Descriptor = d };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*,70"),
            Margin = new Avalonia.Thickness(0, 3, 0, 3),
        };

        var label = new TextBlock { Text = d.Name, Classes = { "label" } };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        if (d.Storage == ParameterStorage.PackedBits && d.Max - d.Min == 1)
        {
            row.Toggle = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            row.Toggle.IsCheckedChanged += (_, _) => Send(row, row.Toggle.IsChecked == true ? 1 : 0);
            Grid.SetColumn(row.Toggle, 1);
            grid.Children.Add(row.Toggle);
        }
        else if (d.Values.Length > 0)
        {
            row.Choice = new ComboBox { ItemsSource = d.Values, MinWidth = 160 };
            row.Choice.SelectionChanged += (_, _) =>
            {
                if (row.Choice.SelectedIndex >= 0) Send(row, d.Min + row.Choice.SelectedIndex);
            };
            Grid.SetColumn(row.Choice, 1);
            grid.Children.Add(row.Choice);
        }
        else
        {
            row.Slider = new Slider
            {
                Minimum = d.Min, Maximum = d.Max, SmallChange = 1, LargeChange = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Slider.ValueProperty) Send(row, (int)Math.Round(row.Slider.Value));
            };
            Grid.SetColumn(row.Slider, 1);
            grid.Children.Add(row.Slider);
        }

        row.Value = new TextBlock
        {
            Classes = { "value" },
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = "-",
        };
        Grid.SetColumn(row.Value, 2);
        grid.Children.Add(row.Value);

        _rows.Add(row);
        return grid;
    }

    // ---- sending ------------------------------------------------------------

    /// <summary>
    /// A human moved a control. Update the draft, then put it on the wire. Doing nothing
    /// while <see cref="_updating"/> is set is what stops an incoming value from being sent
    /// straight back out.
    /// </summary>
    void Send(Row row, int display)
    {
        if (_updating || _closed) return;

        var d = row.Descriptor;
        var patch = _session.Current;
        if (patch != null) ParameterCodec.TryWrite(d, patch, display);

        bool sent = d.Storage == ParameterStorage.PackedBits
            ? _engine.SendNrpn(0, d.NrpnMsb ?? 0, d.NrpnLsb ?? 0, ParameterCodec.PackedControlValue(d, display))
            : d.Cc.HasValue && ParameterCodec.ToWire(d, display) <= ParameterCodec.MaxByte
                ? _engine.SendControlChange(0, d.Cc.Value, ParameterCodec.ToMidiByte(d, display))
                : false;

        row.Value.Text = ParameterCodec.Format(d, display);
        row.Value.Foreground = new SolidColorBrush(sent ? Color.Parse("#E6E9EF") : Color.Parse("#C97A7A"));
        if (!sent) Say($"{d.Name}: не ушло ({_engine.EditorState})");
        PaintDirty();
    }

    // ---- receiving ----------------------------------------------------------

    void OnPatchLoaded(object sender, PatchModel patch)
    {
        _patchName.Text = patch.Name.Length > 0 ? patch.Name : "без имени";
        _patchSlot.Text = patch.Bank == 0 && patch.Program == 0
            ? "буфер редактирования"
            : $"банк {patch.Bank}, слот {patch.Program}";
        ShowAll(patch);
        Say("патч прочитан");
    }

    void OnPatchHeld(object sender, PatchEventArgs e)
    {
        // The instrument sent a patch while there are unsaved edits. Nothing is overwritten;
        // the choice belongs to whoever is looking at the screen.
        Say($"прибор прислал «{e.Name}», но черновик правлен — «Перечитать» возьмёт его");
        PaintDirty();
    }

    void OnInstrumentEdited(object sender, ParameterEventArgs e)
    {
        // Show the panel's own move at once rather than waiting for the poll behind it.
        foreach (var row in _rows)
        {
            var d = row.Descriptor;
            bool mine = e.IsNrpn
                ? d.NrpnMsb == e.Msb && d.NrpnLsb == e.Lsb
                : d.Cc == e.Controller && d.Storage != ParameterStorage.PackedBits;
            if (!mine) continue;
            if (e.IsNrpn && d.Storage == ParameterStorage.PackedBits) continue; // shared address
            Show(row, ParameterCodec.FromMidiByte(d, (byte)Math.Clamp(e.Value, 0, 127)));
            return;
        }
    }

    void OnInstrumentSelected(object sender, PatchSelectedEventArgs e)
        => Say($"на панели выбран банк {e.Bank}, слот {e.Program}");

    void ShowAll(PatchModel patch)
    {
        foreach (var row in _rows)
            if (ParameterCodec.TryRead(row.Descriptor, patch, out int display))
                Show(row, display);
    }

    /// <summary>Put a value into its control without that looking like a human doing it.</summary>
    void Show(Row row, int display)
    {
        bool was = _updating;
        _updating = true;
        try
        {
            var d = row.Descriptor;
            if (row.Toggle != null) row.Toggle.IsChecked = display > d.Min;
            else if (row.Choice != null) row.Choice.SelectedIndex = Math.Clamp(display - d.Min, 0, d.Values.Length - 1);
            else if (row.Slider != null) row.Slider.Value = display;
            row.Value.Text = ParameterCodec.Format(d, display);
            row.Value.Foreground = new SolidColorBrush(Color.Parse("#E6E9EF"));
        }
        finally { _updating = was; }
    }

    // ---- connection ---------------------------------------------------------

    void OnAttachClicked(object sender, RoutedEventArgs e)
    {
        if (_engine.EditorAttached) { _engine.DetachEditor(); Say("отключён"); }
        else if (!_engine.Connected) Say("нет связи с прибором — подключись в главном окне");
        else Say(_engine.AttachEditor() ? "подключаюсь…" : $"подключиться не удалось ({_engine.EditorState})");
        PaintState();
    }

    void PaintState()
    {
        if (_closed) return;
        var state = _engine.EditorState;
        _stateText.Text = state switch
        {
            EditorConnectionState.Ready => $"готов, канал {_engine.InstrumentChannel}",
            EditorConnectionState.Synchronizing => "синхронизация…",
            EditorConnectionState.Connecting => "подключение…",
            EditorConnectionState.NativeOwned => "прибором управляет Novation Editor",
            EditorConnectionState.Faulted => "сбой соединения",
            _ => "не подключён",
        };

        // In AUTOMAP the panel is driving other instruments: the synth does not sound and
        // its knobs edit assignments rather than the patch. An editor is useless there, and
        // says so rather than looking broken.
        if (_engine.AutomapActive && !_automapWarned)
        {
            _automapWarned = true;
            Say("прибор в режиме AUTOMAP: панель правит назначения, а не звук. Нажми SYNTH на приборе");
        }
        else if (!_engine.AutomapActive) _automapWarned = false;
        _attachBtn.Content = _engine.EditorAttached ? "Отключить" : "Подключить";

        // Controls are live only when the instrument is actually ours to drive.
        bool live = state == EditorConnectionState.Ready;
        foreach (var row in _rows)
        {
            if (row.Slider != null) row.Slider.IsEnabled = live;
            if (row.Choice != null) row.Choice.IsEnabled = live;
            if (row.Toggle != null) row.Toggle.IsEnabled = live;
        }
        _refreshBtn.IsEnabled = live;
        PaintDirty();
    }

    void PaintDirty()
    {
        var patch = _session.Current;
        _dirtyText.Text = patch == null ? "" : patch.IsDirty ? $"правок: {patch.ChangedOffsets.Count}" : "без правок";
        if (_session.HasPending) _dirtyText.Text += " · прибор ждёт";
    }

    void Say(string message) => _logText.Text = message;

    void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _stateTimer.Stop();
        _session.Dispose();
        // The engine is the main window's and outlives this screen, so it is not disposed
        // here - only the editor channel is given back.
        _engine.DetachEditor();
        base.OnClosed(e);
    }
}
