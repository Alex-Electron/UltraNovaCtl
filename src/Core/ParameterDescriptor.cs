using System;

namespace UltraNovaCtl.Core;

/// <summary>
/// How a parameter is stored inside a 526-byte patch.
/// </summary>
public enum ParameterStorage
{
    /// <summary>One whole byte of its own.</summary>
    Byte,

    /// <summary>A field of one or more bits sharing a byte with other parameters.</summary>
    PackedBits,

    /// <summary>A value wider than seven bits, held in two bytes, most significant first.</summary>
    Wide,

    /// <summary>Not in the patch at all - a momentary state such as Solo.</summary>
    Transient,
}

/// <summary>
/// Everything needed to move one parameter between what a person sees, what travels over
/// MIDI, and what sits in a patch.
///
/// These are built from the parameter description installed with Novation's own software,
/// on the user's machine, by an extractor - not shipped with this application. Nothing here
/// hard-codes a parameter; the shapes below are the vocabulary that description uses, and
/// each was confirmed against the instrument's own factory content:
///
/// * the display domain and the wire domain are different, related by
///   <c>floor(MidiStart + MidiRange * normalised + 0.5)</c>. Reproduces the three worked
///   examples in docs/OSC-FILTER-REQUIREMENTS.ru.md exactly.
/// * one parameter in 497 is <see cref="Signed"/> (Octave, display -4..+4) and one is
///   <see cref="Inverted"/>.
/// * 64 parameters across 25 bytes are packed bit fields, where the store position is a
///   bit index rather than a byte offset. The factory banks bear this out: the byte holding
///   the two filter links only ever contains 0, 1 or 3.
/// * 9 parameters are wider than seven bits and occupy two bytes. Order established from
///   512 factory patches: most significant seven bits first, which puts every one of them
///   inside its declared range while the other order puts none.
/// * 6 parameters have no patch offset at all. They are momentary and must never be
///   written into a dump - and their offset of -1 must never be used as an index.
/// </summary>
public sealed class ParameterDescriptor
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Short { get; init; } = "";
    public string Units { get; init; } = "";

    /// <summary>Lowest and highest value a person sees. Equal bounds mean a single value.</summary>
    public int Min { get; init; }
    public int Max { get; init; }

    /// <summary>Wire scaling: the value at <see cref="Min"/>, and the span added across the range.</summary>
    public int MidiStart { get; init; }
    public int MidiRange { get; init; }

    public int? Cc { get; init; }
    public int? NrpnMsb { get; init; }
    public int? NrpnLsb { get; init; }

    /// <summary>Negative wire values travel as seven-bit two's complement.</summary>
    public bool Signed { get; init; }

    /// <summary>The wire value runs opposite to the displayed one.</summary>
    public bool Inverted { get; init; }

    public ParameterStorage Storage { get; init; } = ParameterStorage.Byte;

    /// <summary>
    /// Table offset of the first byte, or -1 when <see cref="Storage"/> is
    /// <see cref="ParameterStorage.Transient"/>.
    /// </summary>
    public int Offset { get; init; } = -1;

    /// <summary>Bit index within the shared byte, for a packed field.</summary>
    public int BitIndex { get; init; }

    /// <summary>Highest value the packed field holds, which fixes how many bits it needs.</summary>
    public int BitRange { get; init; } = 1;

    /// <summary>Value labels for an enumeration, in value order. Empty when it is not one.</summary>
    public string[] Values { get; init; } = Array.Empty<string>();

    /// <summary>True when the parameter is written into a patch at all.</summary>
    public bool InPatch => Storage != ParameterStorage.Transient && Offset >= 0;

    /// <summary>How many bits a packed field occupies.</summary>
    public int BitWidth
    {
        get
        {
            int width = 1, span = BitRange;
            while (span > 1) { span >>= 1; width++; }
            return width;
        }
    }

    /// <summary>The mask a packed field occupies within its byte, already shifted into place.</summary>
    public int BitMask => ((1 << BitWidth) - 1) << BitIndex;

    public override string ToString() => $"{Id} {Name}";
}
