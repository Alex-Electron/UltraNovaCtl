using System;

namespace UltraNovaCtl.Core;

/// <summary>
/// Moves one parameter between the value a person sees, the value that travels over MIDI,
/// and the bytes inside a patch.
///
/// This is the layer the byte model deliberately does not have. <see cref="PatchModel"/>
/// addresses raw bytes by offset and is right to; everything that makes a byte mean
/// something - scaling, sign, bit packing, two-byte values, momentary parameters - lives
/// here, driven by a <see cref="ParameterDescriptor"/> rather than by special cases.
///
/// Nothing in this file talks to hardware and nothing allocates a lock, so all of it is
/// exactly checkable.
/// </summary>
public static class ParameterCodec
{
    /// <summary>Largest value a single seven-bit MIDI byte can carry.</summary>
    public const int MaxByte = 127;

    /// <summary>
    /// The display value as it travels: <c>floor(start + range * normalised + 0.5)</c>,
    /// with the normalised position taken across the parameter's own display domain. The
    /// rounding is a floor of the half-shifted value, not a round-half-even, which is why
    /// it is written out rather than left to a language's default.
    ///
    /// The result is not clamped to seven bits: a <see cref="ParameterStorage.Wide"/>
    /// parameter legitimately exceeds 127 and is split by the caller.
    /// </summary>
    public static int ToWire(ParameterDescriptor d, int display)
    {
        if (d == null) throw new ArgumentNullException(nameof(d));

        double normalised = Normalise(d, display);
        if (d.Inverted) normalised = 1.0 - normalised;
        return (int)Math.Floor(d.MidiStart + d.MidiRange * normalised + 0.5);
    }

    /// <summary>The inverse of <see cref="ToWire"/>, back into the display domain.</summary>
    public static int FromWire(ParameterDescriptor d, int wire)
    {
        if (d == null) throw new ArgumentNullException(nameof(d));
        if (d.MidiRange == 0) return d.Min;

        double normalised = (wire - d.MidiStart) / (double)d.MidiRange;
        if (d.Inverted) normalised = 1.0 - normalised;
        int span = d.Max - d.Min;
        int display = (int)Math.Floor(d.Min + span * normalised + 0.5);
        return Clamp(display, Math.Min(d.Min, d.Max), Math.Max(d.Min, d.Max));
    }

    /// <summary>
    /// The byte actually put on the wire. Only one parameter in 497 is signed, but getting
    /// it wrong would send Octave -4 as 252 and wrap to a random note offset, so the
    /// conversion is explicit: a negative value travels as seven-bit two's complement.
    /// </summary>
    public static byte ToMidiByte(ParameterDescriptor d, int display)
    {
        int wire = ToWire(d, display);
        if (wire < 0)
        {
            if (!d.Signed) throw new InvalidOperationException(
                $"parameter {d.Id} '{d.Name}' encoded to {wire}, which needs a signed descriptor");
            if (wire < -64) throw new InvalidOperationException(
                $"parameter {d.Id} '{d.Name}' encoded to {wire}, below the seven-bit floor of -64");
            return (byte)(wire + 128);
        }
        if (wire > MaxByte) throw new InvalidOperationException(
            $"parameter {d.Id} '{d.Name}' encoded to {wire}, which does not fit one byte - it is a wide parameter");
        return (byte)wire;
    }

    /// <summary>Read one wire byte back into the display domain, undoing the sign.</summary>
    public static int FromMidiByte(ParameterDescriptor d, byte value)
    {
        int wire = d != null && d.Signed && value > 63 ? value - 128 : value;
        return FromWire(d, wire);
    }

    /// <summary>
    /// The value to send when writing a packed flag through the instrument's packed-control
    /// address. The instrument does not take the whole byte; each flag and each of its
    /// states has its own number, which the descriptor's wire start encodes. Filter Freq
    /// Link is 42 and 43, Filter Res Link 44 and 45, on one shared address.
    /// </summary>
    public static int PackedControlValue(ParameterDescriptor d, int display)
    {
        if (d == null) throw new ArgumentNullException(nameof(d));
        if (d.Storage != ParameterStorage.PackedBits) throw new InvalidOperationException(
            $"parameter {d.Id} '{d.Name}' is not a packed field");
        return d.MidiStart + Clamp(display - d.Min, 0, d.BitRange);
    }

    /// <summary>Split a wide value into the two bytes a patch holds, most significant first.</summary>
    public static (byte High, byte Low) ToWidePair(ParameterDescriptor d, int display)
    {
        int wire = ToWire(d, display);
        if (wire < 0 || wire > (MaxByte << 7 | MaxByte))
            throw new InvalidOperationException($"parameter {d.Id} '{d.Name}' encoded to {wire}, outside two seven-bit bytes");
        return ((byte)((wire >> 7) & 0x7F), (byte)(wire & 0x7F));
    }

    /// <summary>Reassemble a wide value from its two bytes.</summary>
    public static int FromWidePair(ParameterDescriptor d, byte high, byte low)
        => FromWire(d, ((high & 0x7F) << 7) | (low & 0x7F));

    // ---- patch access ------------------------------------------------------

    /// <summary>
    /// Read a parameter out of a patch. Returns false for a momentary parameter, which has
    /// no place in a patch to read - its offset of -1 is a marker, not a position.
    /// </summary>
    public static bool TryRead(ParameterDescriptor d, PatchModel patch, out int display)
    {
        display = 0;
        if (d == null) throw new ArgumentNullException(nameof(d));
        if (patch == null) throw new ArgumentNullException(nameof(patch));
        if (!d.InPatch) return false;

        switch (d.Storage)
        {
            case ParameterStorage.PackedBits:
                int field = (patch[d.Offset] & d.BitMask) >> d.BitIndex;
                display = Clamp(d.Min + field, d.Min, d.Min + d.BitRange);
                return true;

            case ParameterStorage.Wide:
                display = FromWidePair(d, patch[d.Offset], patch[d.Offset + 1]);
                return true;

            default:
                display = FromMidiByte(d, patch[d.Offset]);
                return true;
        }
    }

    /// <summary>
    /// Write a parameter into a patch. Returns false for a momentary parameter rather than
    /// corrupting whatever byte a -1 offset would land on.
    ///
    /// A packed field replaces only its own bits. The rest of the byte - other parameters,
    /// and any bit whose meaning is not known - is carried through untouched, which is the
    /// whole reason this does not simply assign a byte.
    /// </summary>
    public static bool TryWrite(ParameterDescriptor d, PatchModel patch, int display)
    {
        if (d == null) throw new ArgumentNullException(nameof(d));
        if (patch == null) throw new ArgumentNullException(nameof(patch));
        if (!d.InPatch) return false;

        switch (d.Storage)
        {
            case ParameterStorage.PackedBits:
            {
                int field = Clamp(display - d.Min, 0, d.BitRange);
                int merged = (patch[d.Offset] & ~d.BitMask) | ((field << d.BitIndex) & d.BitMask);
                patch[d.Offset] = (byte)(merged & 0x7F);
                return true;
            }

            case ParameterStorage.Wide:
            {
                var (high, low) = ToWidePair(d, display);
                patch[d.Offset] = high;
                patch[d.Offset + 1] = low;
                return true;
            }

            default:
                patch[d.Offset] = ToMidiByte(d, display);
                return true;
        }
    }

    /// <summary>
    /// What to show for a value: the enumeration's label when there is one, otherwise the
    /// number with its unit.
    /// </summary>
    public static string Format(ParameterDescriptor d, int display)
    {
        if (d == null) throw new ArgumentNullException(nameof(d));
        int index = display - d.Min;
        if (d.Values.Length > 0 && (uint)index < d.Values.Length) return d.Values[index];
        return string.IsNullOrEmpty(d.Units) ? display.ToString() : display + " " + d.Units;
    }

    static double Normalise(ParameterDescriptor d, int display)
    {
        int lo = Math.Min(d.Min, d.Max), hi = Math.Max(d.Min, d.Max);
        if (hi == lo) return 0.0;
        return (Clamp(display, lo, hi) - lo) / (double)(hi - lo);
    }

    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
