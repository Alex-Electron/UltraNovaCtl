using System;
using System.Threading;

namespace UltraNovaCtl.Core;

/// <summary>
/// Keeps an editable draft in step with the instrument.
///
/// The engine delivers what the private port carries: whole patches, parameter edits made
/// on the panel, and patch selections. This turns those into one <see cref="Current"/>
/// draft and applies the rules the plan sets down for it.
///
/// A patch that arrives becomes the draft only if the draft is clean. A dirty draft is the
/// user's work, and nothing the instrument sends is allowed to throw it away unasked; the
/// arrival is held and reported instead, and the caller decides. Changes reported by the
/// instrument are never treated as edits to send back - they are the instrument speaking,
/// and echoing them would build the loop this project has spent its life avoiding.
///
/// Following the panel works the way the native plug-in does it: a patch selection or an
/// edit on the panel schedules one request for the edit buffer a moment later, and the
/// reply becomes the draft. Polling is the source of truth; the live edit stream only
/// says when to poll. When a parameter table is available the edits can be applied
/// directly and the poll skipped, but nothing here depends on that.
///
/// Events are raised on whatever thread delivered the message - the engine's reader or a
/// timer - so an interface marshals them before touching a view.
/// </summary>
public sealed class PatchSession : IDisposable
{
    readonly AutomapEngine _engine;
    readonly object _sync = new();
    readonly Timer _follow;
    Func<bool> _request;
    PatchModel _current;
    byte[] _pending;
    bool _disposed;

    /// <summary>
    /// How long to wait after a selection or an edit before asking for the buffer. The
    /// native plug-in waits about a second after a Program Change; a shorter wait after a
    /// knob movement keeps the draft close behind the hand without a request per tick.
    /// </summary>
    public int FollowDelayMs { get; set; } = 1000;
    public int EditFollowDelayMs { get; set; } = 400;

    /// <summary>Ask for the edit buffer when the panel changes patch or edits a parameter.</summary>
    public bool FollowPanel { get; set; } = true;

    /// <summary>The draft being edited, or null before the first patch has arrived.</summary>
    public PatchModel Current { get { lock (_sync) return _current; } }

    /// <summary>True when a patch arrived that could not become the draft because the draft is dirty.</summary>
    public bool HasPending { get { lock (_sync) return _pending != null; } }

    /// <summary>A patch arrived and became the draft.</summary>
    public event EventHandler<PatchModel> PatchLoaded;

    /// <summary>
    /// A patch arrived while the draft had unsaved edits, so it was held rather than applied.
    /// The caller decides: <see cref="DiscardDraftAndLoadPending"/>, <see cref="KeepDraft"/>,
    /// or save first and then discard.
    /// </summary>
    public event EventHandler<PatchEventArgs> PatchHeld;

    /// <summary>The panel edited a parameter. Informational; the draft follows by polling.</summary>
    public event EventHandler<ParameterEventArgs> InstrumentEdited;

    /// <summary>The panel selected a patch. Informational; the draft follows by polling.</summary>
    public event EventHandler<PatchSelectedEventArgs> InstrumentSelected;

    public PatchSession(AutomapEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _request = engine.RequestPatch;
        _follow = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
        _engine.PatchReceived += OnPatchReceived;
        _engine.PatchSelected += OnPatchSelected;
        _engine.ParameterChanged += OnParameterChanged;
    }

    /// <summary>How the session asks for the buffer. Replaceable so the checks can count requests.</summary>
    internal Func<bool> Requester
    {
        get => _request;
        set => _request = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Start a draft from a file or elsewhere, replacing whatever was current.</summary>
    public void Load(PatchModel model)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        lock (_sync) { _current = model; _pending = null; }
        PatchLoaded?.Invoke(this, model);
    }

    /// <summary>Throw away the dirty draft and take the patch that was held.</summary>
    public bool DiscardDraftAndLoadPending()
    {
        PatchModel loaded;
        lock (_sync)
        {
            if (_pending == null) return false;
            loaded = PatchModel.FromDump(_pending);
            _current = loaded;
            _pending = null;
        }
        PatchLoaded?.Invoke(this, loaded);
        return true;
    }

    /// <summary>Keep the dirty draft and forget the patch that was held.</summary>
    public void KeepDraft()
    {
        lock (_sync) _pending = null;
    }

    /// <summary>Ask the instrument for its edit buffer now.</summary>
    public bool Refresh() => _request();

    void OnPatchReceived(object sender, PatchEventArgs e)
    {
        PatchModel loaded = null;
        bool held = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (_current != null && _current.IsDirty)
            {
                _pending = e.Data;
                held = true;
            }
            else
            {
                loaded = PatchModel.FromDump(e.Data);
                _current = loaded;
                _pending = null;
            }
        }
        if (held) PatchHeld?.Invoke(this, e);
        else PatchLoaded?.Invoke(this, loaded);
    }

    void OnPatchSelected(object sender, PatchSelectedEventArgs e)
    {
        InstrumentSelected?.Invoke(this, e);
        Schedule(FollowDelayMs);
    }

    void OnParameterChanged(object sender, ParameterEventArgs e)
    {
        InstrumentEdited?.Invoke(this, e);
        Schedule(EditFollowDelayMs);
    }

    /// <summary>
    /// Arm one poll for a moment from now. Arming again before it fires restarts the wait,
    /// so a run of selections or a swept knob produces one request after the last of them.
    /// </summary>
    void Schedule(int delayMs)
    {
        if (!FollowPanel) return;
        lock (_sync)
        {
            if (_disposed) return;
            _follow.Change(Math.Max(1, delayMs), Timeout.Infinite);
        }
    }

    void Poll()
    {
        lock (_sync) if (_disposed) return;
        _request();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _follow.Change(Timeout.Infinite, Timeout.Infinite);
        }
        _engine.PatchReceived -= OnPatchReceived;
        _engine.PatchSelected -= OnPatchSelected;
        _engine.ParameterChanged -= OnParameterChanged;
        _follow.Dispose();
    }
}
