namespace UltraNovaCtl.Core;

/// <summary>
/// Transport commands, in the two forms software actually understands.
///
/// Real-time messages (Start, Stop, Continue) are single bytes on no channel; they are
/// what a sequencer follows when it is slaved to an external clock. MMC is a SysEx
/// family that a DAW obeys as remote control of its own transport, including record and
/// locating - things real-time messages cannot express.
///
/// Which one works depends entirely on the receiving software, so both are offered
/// rather than guessing.
/// </summary>
public static class Transport
{
    public sealed record Command(string Id, string Label, byte[] Bytes);

    // Real-time: one byte, no channel, no data.
    static byte[] Rt(byte b) => new[] { b };

    /// <summary>
    /// MMC: F0 7F &lt;device&gt; 06 &lt;command&gt; F7. Device 7F means "all devices", which is
    /// what a DAW listens for unless it has been told otherwise.
    /// </summary>
    static byte[] Mmc(byte command) => new byte[] { 0xF0, 0x7F, 0x7F, 0x06, command, 0xF7 };

    public static readonly Command[] All =
    {
        new("rt-start",    "Start (real-time)",     Rt(0xFA)),
        new("rt-continue", "Continue (real-time)",  Rt(0xFB)),
        new("rt-stop",     "Stop (real-time)",      Rt(0xFC)),

        new("mmc-play",    "Play (MMC)",            Mmc(0x02)),
        new("mmc-stop",    "Stop (MMC)",            Mmc(0x01)),
        new("mmc-pause",   "Pause (MMC)",           Mmc(0x09)),
        new("mmc-record",  "Record (MMC)",          Mmc(0x06)),
        new("mmc-recexit", "Record exit (MMC)",     Mmc(0x07)),
        new("mmc-ff",      "Fast forward (MMC)",    Mmc(0x04)),
        new("mmc-rew",     "Rewind (MMC)",          Mmc(0x05)),
        new("mmc-home",    "Return to zero (MMC)",  new byte[]
            { 0xF0, 0x7F, 0x7F, 0x06, 0x44, 0x06, 0x01, 0, 0, 0, 0, 0, 0xF7 }),
    };

    public static Command Find(string id)
    {
        foreach (var c in All) if (c.Id == id) return c;
        return null;
    }

    public static string LabelOf(string id) => Find(id)?.Label ?? "not set";
}
