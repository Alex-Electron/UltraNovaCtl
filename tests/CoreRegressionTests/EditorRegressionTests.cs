using UltraNovaCtl.Core;

namespace UltraNovaCtl.CoreRegressionTests;

static partial class Program
{
    sealed class EditorRig : IDisposable
    {
        public volatile bool Busy;
        public int Opened, Closed, FailAt;
        public long Clock;
        public readonly List<byte[]> Writes = new();
        public readonly EditorTransport Transport;
        public readonly AutomapEngine Engine;
        public EditorRig()
        {
            Transport = new EditorTransport(() => { Opened++; return true; }, () => Closed++,
                m => { lock (Writes) { Writes.Add((byte[])m.Clone()); return Writes.Count != FailAt; } }, () => Busy, () => Clock);
            Engine = new AutomapEngine(Transport);
            Engine.Config.OutputPort = "";
        }
        public void Ready(int channel = 7)
        {
            True(Engine.AttachEditor(), "attach started");
            var status = StatusReply(1); status[13] = (byte)(channel - 1);
            Engine.DispatchEditorSysEx(status);
            Engine.DispatchEditorSysEx(SyntheticDump());
            Equal(EditorConnectionState.Ready, Engine.EditorState, "fresh handshake ready");
        }
        public void Dispose() => Engine.Dispose();
    }

    static void EditorWaitsForStatusAndBuffer()
    {
        using var r = new EditorRig();
        True(r.Engine.AttachEditor(), "writer opened");
        Equal(3, r.Writes.Count, "enable, status, edit buffer request");
        True(!r.Engine.SendControlChange(0, 74, 64), "no guessed channel before status");
        True(!r.Engine.SendControlChange(2, 74, 64), "explicit channel does not bypass synchronization");
        var status = StatusReply(1); status[13] = 6;
        r.Engine.DispatchEditorSysEx(status);
        True(!r.Engine.SendNrpn(0, 1, 2, 3), "status alone is not readiness");
        var stored = SyntheticDump(); stored[11] = 3;
        r.Engine.DispatchEditorSysEx(stored);
        True(!r.Engine.SendControlChange(0, 74, 64), "stored slot is not the current buffer");
        r.Engine.DispatchEditorSysEx(SyntheticDump());
        True(r.Engine.SendControlChange(0, 74, 64), "ready channel accepted");
        True(r.Writes[^1].SequenceEqual(new byte[] { 0xB6, 74, 64 }), "uses channel seven from status");
    }

    static void EditorYieldsToNativeOwner()
    {
        Func<AutomapEngine, bool>[] sends = {
            e => e.RequestPatch(), e => e.RequestStoredPatch(1, 2),
            e => e.SendControlChange(0, 74, 64), e => e.SendNrpn(0, 1, 2, 3),
            e => e.SendMetadata(PatchMetadata.Message("name", 1, 2)),
        };
        foreach (var send in sends)
        {
            using var r = new EditorRig(); r.Ready();
            int before = r.Writes.Count;
            r.Busy = true;
            True(!send(r.Engine), "a new native owner blocks the operation");
            Equal(before, r.Writes.Count, "no writes, including transport disable, after takeover");
            Equal(1, r.Closed, "our writer was released");
            Equal(EditorConnectionState.NativeOwned, r.Engine.EditorState, "reason is exposed");
            r.Busy = false;
            True(!send(r.Engine), "no auto replay when owner disappears");
            r.Ready(3);
            True(send(r.Engine), "explicit reattach and fresh data permit new work");
        }
        using var busy = new EditorRig { Busy = true };
        True(!busy.Engine.AttachEditor(), "native owner before startup blocks opening");
        Equal(0, busy.Opened, "no pin was taken");

        int unknownWrites = 0;
        var unknown = new EditorTransport(() => true, () => { },
            _ => { unknownWrites++; return true; }, () => throw new InvalidOperationException("probe failed"));
        True(!unknown.Attach(), "an inconclusive owner probe cannot grant access");
        Equal(0, unknownWrites, "no handshake on probe failure");
        Equal(EditorConnectionState.Faulted, unknown.State, "probe exception is surfaced");
    }

    static void EditorHandshakeFailures()
    {
        for (int fail = 1; fail <= 3; fail++)
        {
            using var r = new EditorRig { FailAt = fail };
            True(!r.Engine.AttachEditor(), "failed handshake is not reported as attached");
            Equal(fail, r.Writes.Count, "remaining handshake commands are cancelled");
            Equal(1, r.Closed, "failed writer is closed");
            True(!r.Engine.EditorAttached, "attachment state cleared");
        }
        using var timeout = new EditorRig();
        timeout.Engine.AttachEditor(); timeout.Clock = 5001;
        timeout.Transport.CheckOwnership();
        Equal(EditorConnectionState.Faulted, timeout.Engine.EditorState, "silent device times out");
        True(!timeout.Engine.SendControlChange(0, 74, 64), "timeout cannot send edits");
        Equal(1, timeout.Closed, "timeout releases writer");
        timeout.Engine.Dispose(); timeout.Engine.Dispose(); // idempotent
    }

    static void MetadataPreservesFlags()
    {
        var message = PatchMetadata.Message("Pad\tA", 14, 9, (byte)0x63);
        Equal(33, message.Length, "wire length");
        Equal((byte)'P', message[13], "name uses metadata offset, not dump offset");
        Equal((byte)14, message[29], "category position");
        Equal((byte)9, message[30], "genre position");
        True(PatchMetadata.TryRead(message, out MetadataEventArgs metadata), "message parsed");
        Equal("Pad A", metadata.Name, "name sanitized");
        True(metadata.Chord, "Chord decoded");
        var renamed = PatchMetadata.Message("New", metadata.Category, metadata.Genre, metadata.Flags);
        Equal((byte)0x63, renamed[31], "unknown upper flags survive rename");
        message[20] = 0xF0;
        True(!PatchMetadata.IsMessage(message), "interior MIDI status is not valid metadata");
        bool threw = false;
        try { PatchMetadata.Message("x", 1, 2, (byte)128); } catch (ArgumentOutOfRangeException) { threw = true; }
        True(threw, "out-of-range flags rejected");
    }

    static void MetadataFollowsWithoutEcho()
    {
        using var rig = new EditorRig(); rig.Ready();
        using var session = new PatchSession(rig.Engine) { EditFollowDelayMs = 20 };
        MetadataEventArgs? seen = null;
        rig.Engine.MetadataReceived += (_, e) => seen = e;
        int before = rig.Writes.Count;
        rig.Engine.DispatchEditorSysEx(PatchMetadata.Message("From panel", 1, 2, true));
        True(seen != null && seen.Name == "From panel" && seen.Chord, "metadata reaches consumers");
        True(SpinWait.SpinUntil(() => { lock (rig.Writes) return rig.Writes.Count > before; }, 1000), "metadata requests fresh dump");
        True(rig.Writes[^1].SequenceEqual(PatchProtocol.RequestEditBuffer()), "only a read request, never an echo");
    }

    static void SessionRejectsStaleWork()
    {
        var (engine, session, requests) = Session();
        using (engine) using (session)
        {
            session.Load(PatchModel.FromDump(NamedDump("Draft")));
            var stored = NamedDump("Bank scan"); stored[11] = 2;
            engine.DispatchEditorSysEx(stored);
            Equal("Draft", session.Current!.Name, "stored dump cannot replace current draft");
            session.EditFollowDelayMs = 30;
            engine.DispatchEditorChannelMessage(0xB1, 74, 60);
            session.FollowPanel = false;
            Thread.Sleep(80);
            Equal(0, requests.Count, "switching follow off cancels an already armed poll");
            session.FollowPanel = true;
            engine.DispatchEditorChannelMessage(0xB1, 74, 61);
            engine.DetachEditor();
            Thread.Sleep(80);
            Equal(0, requests.Count, "old connection's queued poll is cancelled");
        }
    }

    sealed class QueuedContext : SynchronizationContext
    {
        readonly Queue<(SendOrPostCallback callback, object? state)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
        public void Drain() { while (_queue.TryDequeue(out var item)) item.callback(item.state); }
    }

    static void SessionUsesSuppliedContext()
    {
        using var engine = new AutomapEngine(); engine.Config.OutputPort = "";
        var context = new QueuedContext();
        using var session = new PatchSession(engine, context);
        var dump = NamedDump("Arrived");
        engine.DispatchEditorSysEx(dump);
        dump[15] = (byte)'X';
        True(session.Current == null, "reader thread does not touch the draft");
        context.Drain();
        Equal("Arrived", session.Current!.Name, "queued arrival owns a copy of its bytes");
        engine.DispatchEditorSysEx(NamedDump("Late"));
        session.Dispose(); context.Drain();
        Equal("Arrived", session.Current!.Name, "queued UI work is ignored after disposal");
    }

    static void NrpnRejectsStaleData()
    {
        var reader = new NrpnReader();
        reader.Feed(0xB1, 99, 63, out _); reader.Feed(0xB1, 98, 1, out _);
        True(!reader.Feed(0xB1, 38, 5, out _), "slot without bank is not a patch selection");
        reader.Feed(0xB1, 6, 2, out _);
        reader.Feed(0xB1, 98, 2, out _);
        True(!reader.Feed(0xB1, 38, 5, out _), "old data cannot belong to a newly selected parameter");
        reader.Feed(0xB1, 100, 0, out _);
        True(!reader.Feed(0xB1, 6, 2, out _), "RPN data is not emitted as old NRPN");
    }
}
