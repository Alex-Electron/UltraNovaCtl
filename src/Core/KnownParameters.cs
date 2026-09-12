using System.Collections.Generic;

namespace UltraNovaCtl.Core;

/// <summary>
/// A small, hand-checked set of parameter descriptions, enough for one editor screen.
///
/// This is deliberately temporary. The real set is 497 parameters and belongs to Novation's
/// installed description, which an extractor will read on the user's own machine - nothing
/// of theirs ships here. What is below is the handful needed to put a real screen on the
/// real transport and find out whether the whole stack holds together, and every number in
/// it was read off the instrument's own factory content or off our own verified documents
/// before it was written down.
///
/// When the extractor exists, this becomes a fallback for "Novation's software is not
/// installed" - which is why it is shaped exactly like what the extractor will produce
/// rather than like something convenient for one screen.
/// </summary>
public static class KnownParameters
{
    /// <summary>Table offset of the filter-link byte, shared by two packed flags.</summary>
    const int FilterLinkOffset = 75;

    static readonly string[] FilterTypes =
    {
        "LP6NoRes", "LP12", "LP18", "LP24",
        "BP6/6", "BP12/12", "BP6/12", "BP12/6", "BP6/18", "BP18/6",
        "HP6NoRes", "HP12", "HP18", "HP24",
    };

    /// <summary>An ordinary parameter holding one whole byte.</summary>
    static ParameterDescriptor Plain(int id, string name, string group, int cc, int offset,
        int min = 0, int max = 127, int midiStart = 0, int midiRange = 127,
        string[] values = null, string units = "") => new()
    {
        Id = id, Name = name, Short = group, Units = units,
        Min = min, Max = max, MidiStart = midiStart, MidiRange = midiRange,
        Cc = cc, Offset = offset, Storage = ParameterStorage.Byte,
        Values = values ?? System.Array.Empty<string>(),
    };

    /// <summary>One flag inside a byte shared with others, written by its own NRPN value.</summary>
    static ParameterDescriptor Flag(int id, string name, string group, int bit, int midiStart) => new()
    {
        Id = id, Name = name, Short = group,
        Min = 0, Max = 1, MidiStart = midiStart, MidiRange = 1,
        NrpnMsb = 0, NrpnLsb = 122,
        Storage = ParameterStorage.PackedBits,
        Offset = FilterLinkOffset, BitIndex = bit, BitRange = 1,
        Values = new[] { "Off", "On" },
    };

    /// <summary>
    /// The screen's parameters, in the order they are shown. Groups match the instrument's
    /// own pages so the screen can be read next to the panel.
    /// </summary>
    public static IReadOnlyList<ParameterDescriptor> FirstScreen { get; } = new[]
    {
        // --- Oscillator 1 ----------------------------------------------------
        Plain(3, "Waveform", "Oscillator 1", cc: 19, offset: 33, max: 71, midiRange: 71),
        Plain(0, "Semitone", "Oscillator 1", cc: 26, offset: 40, min: -64, max: 63),
        // The one worked example whose wire range is not the display range: -50..+50
        // travels as 14..114, which is why start and range are spelled out here.
        Plain(1, "Cents", "Oscillator 1", cc: 27, offset: 41, min: -50, max: 50,
              midiStart: 14, midiRange: 100),

        // --- Filter 1 --------------------------------------------------------
        Plain(54, "Type", "Filter 1", cc: 68, offset: 78, max: 13, midiRange: 13, values: FilterTypes),
        Plain(50, "Frequency", "Filter 1", cc: 74, offset: 79),
        Plain(51, "Resonance", "Filter 1", cc: 71, offset: 81),
        Plain(66, "Balance", "Filter 1", cc: 61, offset: 74, min: -64, max: 63),

        // Two flags in one byte, written over one shared address with a value per flag
        // and state: 42 and 43 for frequency, 44 and 45 for resonance.
        Flag(68, "Frequency link", "Filter 1", bit: 0, midiStart: 42),
        Flag(69, "Resonance link", "Filter 1", bit: 1, midiStart: 44),

        // --- Amplitude envelope ----------------------------------------------
        Plain(70, "Attack", "Amp envelope", cc: 73, offset: 103),
        Plain(71, "Decay", "Amp envelope", cc: 75, offset: 104),
        Plain(72, "Sustain", "Amp envelope", cc: 70, offset: 105),
        Plain(73, "Release", "Amp envelope", cc: 72, offset: 106),
    };
}
