namespace UltraNovaCtl.Core;

public enum EditorConnectionState { Disconnected, Connecting, Synchronizing, Ready, NativeOwned, Faulted }

/// <summary>
/// Serializes the private writer and its ownership checks. A connection is ready only
/// after both status and edit buffer have arrived. No queued edits survive a generation.
/// IO callbacks never publish UI events or logs while this lock is held.
/// </summary>
internal sealed class EditorTransport
{
    readonly object _sync = new();
    readonly Func<bool> _open, _nativeOwns;
    readonly Func<byte[], bool> _write;
    readonly Action _close;
    readonly Func<long> _clock;
    bool _attached, _status, _patch;
    int _channel;
    long _generation, _started;
    EditorConnectionState _state;

    internal EditorTransport(Func<bool> open, Action close, Func<byte[], bool> write,
        Func<bool> nativeOwns, Func<long> clock = null)
    { _open = open; _close = close; _write = write; _nativeOwns = nativeOwns; _clock = clock ?? (() => Environment.TickCount64); }

    public bool Attached { get { lock (_sync) return _attached; } }
    public EditorConnectionState State { get { lock (_sync) return _state; } }
    public int Channel { get { lock (_sync) return _channel; } }
    public long Generation { get { lock (_sync) return _generation; } }

    bool Available()
    {
        bool busy;
        try { busy = _nativeOwns(); }
        catch { End(EditorConnectionState.Faulted); return false; }
        if (busy) { if (_state != EditorConnectionState.NativeOwned) End(EditorConnectionState.NativeOwned); return false; }
        if (_attached && _state == EditorConnectionState.Synchronizing && _clock() - _started >= 5000)
        { End(EditorConnectionState.Faulted); return false; }
        return true;
    }

    // Closing due to takeover deliberately sends no transport-disable: that would
    // change the new owner's connection. The private reader remains the engine's.
    void End(EditorConnectionState state)
    {
        bool close = _attached;
        _attached = _status = _patch = false;
        _channel = 0;
        _state = state;
        _generation++;
        if (close) { try { _close(); } catch { _state = EditorConnectionState.Faulted; } }
    }

    bool Write(byte[] message)
    {
        if (!Available() || !_attached) return false;
        try { if (_write(message)) return true; }
        catch { /* failure is surfaced as connection state, never success */ }
        End(EditorConnectionState.Faulted);
        return false;
    }

    public bool Attach()
    {
        lock (_sync)
        {
            if (!Available()) return false;
            if (_attached) return true;
            _channel = 0;
            _status = _patch = false;
            _state = EditorConnectionState.Connecting;
            _generation++;
            bool opened;
            try { opened = _open(); }
            catch { opened = false; }
            if (!opened) { _state = EditorConnectionState.Faulted; return false; }
            _attached = true;
            _started = _clock();
            _state = EditorConnectionState.Synchronizing;
            return Write(PatchProtocol.TransportEnable)
                && Write(PatchProtocol.RequestStatus())
                && Write(PatchProtocol.RequestEditBuffer());
        }
    }

    public void CheckOwnership() { lock (_sync) Available(); }

    public void Detach()
    {
        lock (_sync)
        {
            if (_attached && Available()) Write(PatchProtocol.TransportDisable);
            End(EditorConnectionState.Disconnected);
        }
    }

    public bool Request(byte[] message, long? generation = null)
    {
        lock (_sync) return (!generation.HasValue || generation == _generation) && Write(message);
    }

    public bool SendParameter(int channel, Func<int, byte[]> makeMessage)
    {
        lock (_sync)
        {
            if (!Available() || !_attached || _state != EditorConnectionState.Ready) return false;
            return Write(makeMessage(channel == 0 ? _channel : channel));
        }
    }

    public bool SendMetadata(byte[] message)
    {
        lock (_sync)
        {
            if (!Available() || !_attached || _state != EditorConnectionState.Ready) return false;
            return Write(message);
        }
    }

    public void ObserveStatus(int channel)
    {
        lock (_sync)
        {
            if (!_attached) { _channel = channel; return; }
            if (!Available()) return;
            _channel = channel;
            _status = true;
            if (_patch) _state = EditorConnectionState.Ready;
        }
    }

    public void ObserveEditBuffer()
    {
        lock (_sync)
        {
            if (!_attached || !Available()) return;
            _patch = true;
            if (_status) _state = EditorConnectionState.Ready;
        }
    }
}
