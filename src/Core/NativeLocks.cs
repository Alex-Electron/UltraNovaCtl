namespace UltraNovaCtl.Core;

/// <summary>
/// Novation's own software announces itself with named locks. Reading them is how we can
/// tell that the native editor - or the original Automap server - is driving the
/// instrument, without touching the device and without taking anything away from them.
///
/// Every probe here OPENS an existing object and never creates one. Creating it would be
/// worse than useless: the next native instance would find the name taken and refuse to
/// run, which is exactly the kind of damage coexistence is supposed to avoid.
/// </summary>
public static class NativeLocks
{
    /// <summary>
    /// Held for as long as one UltraNova Editor instance is driving the hardware. The
    /// plug-in constructor does <c>CreateMutexA(NULL, TRUE, "NovaControlPluginHardwareLock")</c>
    /// and a second instance that finds it already there gives up, shows "Already Open"
    /// and starts no timer. Released in the destructor, so it disappears when the plug-in
    /// is removed from the track.
    /// </summary>
    public const string EditorHardware = "NovaControlPluginHardwareLock";

    /// <summary>
    /// The original Automap server. Ours has to stand aside while it runs: both want the
    /// same Port 3 pins, and two servers on one panel is not a thing the instrument
    /// supports.
    /// </summary>
    public const string AutomapServer = "AutomapServerRunning";

    public static readonly (string name, string meaning)[] Known =
    {
        (EditorHardware, "native UltraNova Editor is driving the instrument"),
        (AutomapServer, "the original Automap server is running"),
    };

    /// <summary>
    /// True when something already holds this name. False when it does not, and also
    /// whenever we cannot tell - a probe that fails must not be reported as a native
    /// application being present, because the answer gates our own behaviour.
    /// </summary>
    public static bool IsHeld(string name) => Probe(name, failClosed: false);

    /// <summary>For editor writes, an inconclusive probe must never grant access.</summary>
    internal static bool IsHeldOrUnknown(string name) => Probe(name, failClosed: true);

    static bool Probe(string name, bool failClosed)
    {
        if (string.IsNullOrWhiteSpace(name)) return failClosed;
        if (!OperatingSystem.IsWindows()) return failClosed;

        // The plug-in creates the name unqualified, which puts it in the session
        // namespace; a service would put the same name under Global. Both are checked, in
        // that order, because the editor is the case that matters in practice.
        foreach (string candidate in new[] { name, @"Local\" + name, @"Global\" + name })
            if (Exists(candidate, failClosed)) return true;
        return false;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool Exists(string name, bool failClosed)
    {
        // A named object can be a mutex or an event depending on who made it, and the
        // open fails with a different exception for each mismatch - so both are tried and
        // missing/wrong-type results fall through. Unexpected errors only grant access
        // to legacy informational callers, never to the editor's strict write gate.
        try
        {
            if (Mutex.TryOpenExisting(name, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch (WaitHandleCannotBeOpenedException) { /* no such name, or not a mutex */ }
        catch (UnauthorizedAccessException) { return true; }   // it exists, we may not open it
        catch { if (failClosed) return true; }

        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                handle.Dispose();
                return true;
            }
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { return true; }
        catch { if (failClosed) return true; }

        return false;
    }
}
