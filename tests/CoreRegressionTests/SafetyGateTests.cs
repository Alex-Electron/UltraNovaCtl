using UltraNovaCtl.Core;

namespace UltraNovaCtl.CoreRegressionTests;

static partial class Program
{
    // ---- the gates that decide whether we may drive the instrument ----------
    //
    // A review proved each of these was undefended by applying the mutation and watching
    // the suite stay green. They are the difference between coexisting with Novation's own
    // editor and writing into an edit buffer somebody else owns, so they get checks of
    // their own rather than being covered incidentally.

    /// <summary>
    /// An attempt to open a named lock has three outcomes, not two, and the third is the
    /// one that matters. A named object may be a mutex or an event, so an attempt of the
    /// wrong kind proves nothing; being refused permission proves it exists; anything else
    /// is unknown and must stay unknown rather than collapsing into "free".
    /// </summary>
    static void ProbingALockTellsHeldFromAbsentFromUnknown()
    {
        Equal(NativeLocks.Attempted.Held, NativeLocks.Attempt(() => true),
              "an object that opens is held");
        Equal(NativeLocks.Attempted.Absent, NativeLocks.Attempt(() => false),
              "one that is simply not there is absent");
        Equal(NativeLocks.Attempted.Absent,
              NativeLocks.Attempt(() => throw new WaitHandleCannotBeOpenedException()),
              "and so is one of the wrong kind - that attempt just has nothing to say");
        Equal(NativeLocks.Attempted.Held,
              NativeLocks.Attempt(() => throw new UnauthorizedAccessException()),
              "being refused permission means it exists");
        Equal(NativeLocks.Attempted.Unknown,
              NativeLocks.Attempt(() => throw new InvalidOperationException("something else")),
              "anything unplanned is unknown, not absent");
    }

    /// <summary>
    /// The whole difference between the two probes. The informational one may guess that
    /// an unexplained failure means nobody is there; the one gating our writes may not,
    /// because guessing wrong there puts two editors into one edit buffer at exactly the
    /// moment the probe itself is misbehaving.
    /// </summary>
    static void AnUnknownProbeIsOnlySafeForTheInformationalCaller()
    {
        True(NativeLocks.Decide(NativeLocks.Attempted.Held, failClosed: false), "held is held either way");
        True(NativeLocks.Decide(NativeLocks.Attempted.Held, failClosed: true), "for both callers");
        True(!NativeLocks.Decide(NativeLocks.Attempted.Absent, failClosed: false), "absent is absent either way");
        True(!NativeLocks.Decide(NativeLocks.Attempted.Absent, failClosed: true), "for both callers");

        True(!NativeLocks.Decide(NativeLocks.Attempted.Unknown, failClosed: false),
             "the informational caller treats an unknown as nobody there");
        True(NativeLocks.Decide(NativeLocks.Attempted.Unknown, failClosed: true),
             "the write gate treats the same unknown as somebody there");
    }

    /// <summary>
    /// The engine's own write gate must be wired to the editor's lock by name. Every editor
    /// check injects its own probe, so without this the name could change, or the wiring
    /// could be replaced by a constant, and nothing would notice.
    /// </summary>
    static void TheEngineGatesWritesOnTheEditorsOwnLock()
    {
        Equal("NovaControlPluginHardwareLock", NativeLocks.EditorHardware,
              "the name the native editor takes while it drives the hardware");

        // Nobody holds it here, so the strict probe says free and the informational one
        // agrees. What matters is that both answer at all - a throwing probe would be
        // reported as held by the strict one and as free by the other.
        True(!NativeLocks.IsHeldOrUnknown(NativeLocks.EditorHardware),
             "with no native editor running, the write gate is open");
        True(!NativeLocks.IsHeld(NativeLocks.EditorHardware), "and so is the informational probe");

        // A name that cannot exist must still be refused by the strict probe rather than
        // throwing out of it.
        True(NativeLocks.IsHeldOrUnknown(""), "an unusable name fails closed for the write gate");
        True(!NativeLocks.IsHeld(""), "and fails open for the informational one");
    }

    /// <summary>
    /// Disposal refuses new work from the moment it begins, not once it has finished.
    /// A caller attaching in between would open a pin on a filter about to close and leave
    /// it running - the state that makes the instrument look busy to Novation's software.
    ///
    /// What this check can prove is the contract after disposal. The window itself is not
    /// observable here: on an engine that never connected, Stop does nothing that raises an
    /// event, and an attach inside the window fails for the unrelated reason that there is
    /// no filter - so reverting the flag order survives this check. Closing that gap needs
    /// a connected instrument, and it is listed as such rather than faked.
    /// </summary>
    static void DisposalRefusesNewWorkAfterwards()
    {
        var engine = new AutomapEngine();
        engine.Config.OutputPort = "";

        engine.Dispose();

        bool refused = false;
        try { engine.AttachEditor(); } catch (ObjectDisposedException) { refused = true; }
        True(refused, "attaching after disposal is refused");

        bool startRefused = false;
        try { engine.Start(); } catch (ObjectDisposedException) { startRefused = true; }
        True(startRefused, "and so is starting");

        engine.Dispose();
        True(true, "disposing twice is harmless");

        // Detach must stay silent rather than throw - it is what teardown paths call.
        engine.DetachEditor();
        True(true, "detaching after disposal is a no-op, not an error");
    }
}
