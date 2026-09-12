using UltraNovaCtl.Core;

namespace UltraNovaCtl.CoreRegressionTests;

static partial class Program
{
    // ---- gaps an adversarial review proved with surviving mutations ---------
    //
    // Each of these was demonstrated missing the hard way: the mutation was applied to the
    // working code, the suite was run, and it stayed green. They are here so that stops
    // being true. The code they cover was already correct - what was missing was anything
    // that would notice if it stopped being.

    /// <summary>
    /// The handshake was asserted by counting writes, so replacing the transport enable
    /// with a second status request, or deleting the disable on teardown, changed nothing.
    /// The instrument is the only thing that would have noticed, and only in person.
    /// </summary>
    static void TheHandshakeSendsTheBytesItIsSupposedTo()
    {
        using var r = new EditorRig();
        True(r.Engine.AttachEditor(), "attached");
        Equal(3, r.Writes.Count, "three messages open the connection");

        True(r.Writes[0].SequenceEqual(PatchProtocol.TransportEnable),
             "first the transport enable, F0 01 00 01 F7");
        True(r.Writes[1].SequenceEqual(PatchProtocol.RequestStatus()),
             "then the status request that doubles as hello");
        True(r.Writes[2].SequenceEqual(PatchProtocol.RequestEditBuffer()),
             "then the edit-buffer request");

        // The two transport messages are not interchangeable, and neither is a status
        // request: mixing them up is exactly the mutation that used to survive.
        True(!PatchProtocol.TransportEnable.SequenceEqual(PatchProtocol.TransportDisable),
             "enable and disable differ");
        True(!r.Writes[0].SequenceEqual(r.Writes[1]), "and the enable is not a status request");

        int before = r.Writes.Count;
        r.Engine.DetachEditor();
        Equal(before + 1, r.Writes.Count, "detaching sends exactly one more message");
        True(r.Writes[^1].SequenceEqual(PatchProtocol.TransportDisable),
             "and it is the transport disable the plug-in sends");
    }

    /// <summary>
    /// SendMetadata is the only public way raw caller bytes reach the write pin, and its
    /// validation gate had nothing testing it: every check handed it a well-formed message.
    /// </summary>
    static void MetadataThatIsNotAMetadataMessageIsRefused()
    {
        using var r = new EditorRig();
        r.Ready();
        int before = r.Writes.Count;

        True(!r.Engine.SendMetadata(null!), "null is refused");
        True(!r.Engine.SendMetadata(System.Array.Empty<byte>()), "empty is refused");
        True(!r.Engine.SendMetadata(new byte[] { 0xF0, 0xF7 }), "a stub SysEx is refused");
        True(!r.Engine.SendMetadata(SyntheticDump()), "a patch dump is not a metadata message");
        True(!r.Engine.SendMetadata(StatusReply(1)), "nor is a status reply");

        var wrongLength = PatchMetadata.Message("Pad", 1, 2);
        var truncated = wrongLength[..(wrongLength.Length - 1)];
        True(!r.Engine.SendMetadata(truncated), "a truncated metadata message is refused");

        Equal(before, r.Writes.Count, "not one of those reached the pin");

        True(r.Engine.SendMetadata(PatchMetadata.Message("Pad", 1, 2)), "a real one is sent");
        Equal(before + 1, r.Writes.Count, "and only it");
    }

    /// <summary>
    /// The generation gate exists so a request scheduled before a reconnection does not
    /// arrive after it, addressed to a session that no longer exists. Nothing ever handed
    /// it a stale generation, so the gate could have been deleted unnoticed.
    /// </summary>
    static void ARequestFromAnOldConnectionIsDropped()
    {
        using var r = new EditorRig();
        r.Ready();
        long generation = r.Engine.EditorGeneration;
        int before = r.Writes.Count;

        True(r.Engine.RequestPatch(generation), "a request on the current generation goes out");
        Equal(before + 1, r.Writes.Count, "and reaches the pin");

        True(!r.Engine.RequestPatch(generation - 1), "one from an older generation is dropped");
        True(!r.Engine.RequestPatch(generation + 1), "and so is one from a generation that never existed");
        Equal(before + 1, r.Writes.Count, "neither reached the pin");

        // Reconnecting moves the generation on, which is what makes the gate work.
        r.Engine.DetachEditor();
        r.Ready();
        True(r.Engine.EditorGeneration != generation, "reconnecting starts a new generation");
        int after = r.Writes.Count;
        True(!r.Engine.RequestPatch(generation), "the old generation is now refused");
        Equal(after, r.Writes.Count, "nothing was written for it");
    }

    /// <summary>
    /// The 250 ms ownership watch is the only thing that ticks the connection state while
    /// nobody is calling anything. Disarming it passed the whole suite, which meant an idle
    /// takeover - the native editor starting while we sit still - would never be noticed.
    ///
    /// This waits on the real timer rather than driving CheckOwnership by hand, because
    /// driving it by hand is exactly what hides the missing arming.
    /// </summary>
    static void TheOwnershipWatchNoticesATakeoverWhileIdle()
    {
        using var r = new EditorRig();
        r.Ready();
        Equal(EditorConnectionState.Ready, r.Engine.EditorState, "connected and synchronised");

        r.Busy = true;                       // the native editor takes the hardware
        True(SpinWait.SpinUntil(() => r.Engine.EditorState == EditorConnectionState.NativeOwned, 3000),
             "the watch noticed on its own, with nothing else calling in");
        Equal(1, r.Closed, "and released our writer");
        True(!r.Engine.EditorAttached, "we are no longer attached");
    }

    /// <summary>
    /// An RPN selection cancels the current NRPN, and both controllers that carry it must
    /// do so. Controller 101 was never fed anywhere, so deleting its case label survived.
    /// </summary>
    static void EitherHalfOfAnRpnCancelsTheNrpn()
    {
        foreach (byte rpnController in new byte[] { 100, 101 })
        {
            var reader = new NrpnReader();
            reader.Feed(0xB1, 99, 2, out _);
            reader.Feed(0xB1, 98, 63, out _);
            True(reader.Feed(0xB1, 6, 5, out _), $"a parameter is selected before controller {rpnController}");

            reader.Feed(0xB1, rpnController, 0, out _);
            True(!reader.Feed(0xB1, 6, 9, out _),
                 $"controller {rpnController} cancelled the NRPN, so a data byte belongs to nothing");
        }

        // And the engine drops both of them rather than reporting them as parameter edits.
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";
        int edits = 0;
        engine.ParameterChanged += (_, _) => edits++;
        engine.DispatchEditorChannelMessage(0xB1, 100, 0);
        engine.DispatchEditorChannelMessage(0xB1, 101, 0);
        Equal(0, edits, "neither RPN controller is a parameter edit");
    }

    /// <summary>
    /// Loading a draft from a file has to cancel a poll already armed by the panel.
    /// Otherwise the edit buffer arrives a moment later and quietly replaces the file the
    /// user just opened. The review document claimed this behaviour; nothing checked it.
    /// </summary>
    static void OpeningAFileCancelsAPollThePanelArmed()
    {
        var (engine, session, requests) = Session();
        using (engine) using (session)
        {
            session.EditFollowDelayMs = 40;

            // The panel moves something, which arms a poll.
            engine.DispatchEditorChannelMessage(0xB1, 74, 60);

            // Before it fires, the user opens a file.
            session.Load(PatchModel.FromDump(NamedDump("From disk")));

            Thread.Sleep(200);
            lock (requests) Equal(0, requests.Count, "the armed poll was cancelled by the load");
            Equal("From disk", session.Current!.Name, "and the opened file is still the draft");

            // Following still works afterwards - the cancel must not disable it.
            engine.DispatchEditorChannelMessage(0xB1, 74, 61);
            Thread.Sleep(200);
            lock (requests) Equal(1, requests.Count, "a later panel move still arms a poll");
        }
    }
}
