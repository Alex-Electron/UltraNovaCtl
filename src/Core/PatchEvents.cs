using System;

namespace UltraNovaCtl.Core;

/// <summary>Immutable metadata, including the flag bits that a rename must preserve.</summary>
public sealed class MetadataEventArgs : EventArgs
{
    public MetadataEventArgs(string name, int category, int genre, byte flags)
    { Name = name; Category = category; Genre = genre; Flags = flags; }
    public string Name { get; }
    public int Category { get; }
    public int Genre { get; }
    public byte Flags { get; }
    public bool Chord => (Flags & PatchMetadata.ChordFlag) != 0;
}

/// <summary>
/// A complete patch arrived from the instrument. <see cref="Data"/> is the raw 526-byte
/// message, kept whole because the checksum and every parameter offset are defined against
/// it; the named members are conveniences read from it.
/// </summary>
public sealed class PatchEventArgs : EventArgs
{
    public byte[] Data = Array.Empty<byte>();

    /// <summary>Bank the patch came from. Zero for the edit buffer.</summary>
    public int Bank;

    /// <summary>Slot the patch came from. Zero for the edit buffer.</summary>
    public int Program;

    /// <summary>The sixteen-character name, trailing blanks removed.</summary>
    public string Name = "";
}

/// <summary>The instrument's answer to a status request.</summary>
public sealed class StatusEventArgs : EventArgs
{
    /// <summary>Local Control, as the instrument reports it in byte 12.</summary>
    public bool LocalOn;

    public int Major;
    public int Minor;
    public int Build;

    /// <summary>The instrument's MIDI channel, one-based, as the reply states it.</summary>
    public int Channel;

    public string Version => $"{Major}.{Minor}.{Build:00}";
}

/// <summary>
/// A parameter changed somewhere other than here. The instrument sends these two ways and
/// this carries both: an ordinary controller, or an NRPN with its two-part number.
/// </summary>
public sealed class ParameterEventArgs : EventArgs
{
    public bool IsNrpn;

    /// <summary>Controller number, when <see cref="IsNrpn"/> is false.</summary>
    public int Controller;

    /// <summary>NRPN parameter halves, when <see cref="IsNrpn"/> is true.</summary>
    public int Msb;
    public int Lsb;

    /// <summary>
    /// The value, or its high seven bits when <see cref="HasLow"/> says a second data byte
    /// followed. Parameters wider than seven bits exist - Clock BPM runs to 250 - so a
    /// consumer that reads only this for one of those gets the value divided by 128.
    /// </summary>
    public int Value;

    /// <summary>The low seven bits, meaningful only when <see cref="HasLow"/>.</summary>
    public int ValueLow;

    /// <summary>True when the instrument sent a second data byte for this parameter.</summary>
    public bool HasLow;

    /// <summary>
    /// The whole value: fourteen bits when both halves arrived, seven when only one did.
    /// This is what a consumer should read unless it knows the parameter is seven-bit.
    /// </summary>
    public int Wide => HasLow ? (Value << 7) | ValueLow : Value;

    /// <summary>One-based MIDI channel. The instrument uses channel 2 for these.</summary>
    public int Channel;

    public override string ToString() => IsNrpn
        ? $"NRPN ({Msb},{Lsb}) = {(HasLow ? Wide + " (14-bit)" : Value.ToString())} on channel {Channel}"
        : $"CC {Controller} = {Value} on channel {Channel}";
}

/// <summary>
/// A patch was selected on the panel. Bank and slot arrive as the two data bytes of one
/// NRPN, not as a single wide value.
/// </summary>
public sealed class PatchSelectedEventArgs : EventArgs
{
    public int Bank;
    public int Program;

    public override string ToString() => $"bank {Bank} slot {Program}";
}
