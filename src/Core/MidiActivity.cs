namespace UltraNovaCtl.Core;

/// <summary>
/// Where MIDI last moved, by source, for the activity lamps in the window.
///
/// The instrument talks to us on two wires and we talk back on two more, and "is anything
/// flowing?" has a different answer for each. So rather than one in/out pair this keeps a
/// timestamp per source; a lamp is lit when its source moved within the last moment, which
/// the window can decide on its own clock without an event crossing threads for every
/// message. Counts are for the tooltip, so a glance can also say how much.
/// </summary>
public sealed class MidiActivity
{
    public enum Source
    {
        /// <summary>The control surface: encoders, buttons, touch - pins 16/18.</summary>
        PanelIn,
        /// <summary>What we write to the panel: lamps, display, handshake.</summary>
        PanelOut,
        /// <summary>The player's hands on the instrument's own port: notes, pressure, wheels, pedals.</summary>
        Keys,
        /// <summary>The instrument speaking for itself: SysEx, NRPN, program change, its state registers.</summary>
        Synth,
        /// <summary>Everything that left through our MIDI output.</summary>
        DawOut,
    }

    const int Sources = 5;
    readonly long[] _last = new long[Sources];
    readonly long[] _count = new long[Sources];

    public void Touch(Source s)
    {
        Volatile.Write(ref _last[(int)s], Environment.TickCount64);
        Interlocked.Increment(ref _count[(int)s]);
    }

    public long LastMs(Source s) => Volatile.Read(ref _last[(int)s]);
    public long Count(Source s) => Interlocked.Read(ref _count[(int)s]);

    /// <summary>True when the source moved within the last <paramref name="holdMs"/>.</summary>
    public bool Lit(Source s, int holdMs = 150)
    {
        long l = LastMs(s);
        return l != 0 && Environment.TickCount64 - l < holdMs;
    }

    /// <summary>
    /// Which lamp a message on the instrument's own port belongs to. The player's hands
    /// - notes, pressure, wheels, pedals - are Keys. Everything the instrument says about
    /// itself - SysEx, NRPN and other controllers, program changes - is Synth. Both arrive
    /// on the same wire, so the split is by what the message is, not where it came from.
    /// </summary>
    public static Source ClassifyPort(byte status, byte d1)
    {
        if (status == 0xF0) return Source.Synth;
        int kind = status & 0xF0;
        if (kind is 0x80 or 0x90 or 0xA0 or 0xD0 or 0xE0) return Source.Keys;
        if (kind == 0xB0 && d1 is 1 or 11 or 64) return Source.Keys;   // mod wheel, expression, sustain
        return Source.Synth;
    }
}
