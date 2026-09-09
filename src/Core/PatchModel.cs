using System;
using System.Collections.Generic;

namespace UltraNovaCtl.Core;

/// <summary>
/// An editable patch: the instrument's 526-byte dump, plus the memory of what it looked
/// like when it arrived.
///
/// Two things the plan asks for fall out of keeping both. Compare is the difference
/// between the working bytes and the original ones, so it needs no second copy of the
/// instrument's state and no round trip. A local draft outlives the connection, because
/// nothing here talks to hardware - the model can be built from a file, edited with the
/// synth unplugged, and sent later, or never.
///
/// Edits are addressed by parameter-table offset, not by message index, because that is
/// how every parameter in the table is described. The conversion is one place, here.
///
/// Not thread-safe: one editor, one model, one thread. The transport's events arrive on a
/// reader thread and must be marshalled before they touch this.
/// </summary>
public sealed class PatchModel
{
    readonly byte[] _original;
    readonly byte[] _working;
    readonly List<Edit> _undo = new();
    readonly List<Edit> _redo = new();

    /// <summary>Highest addressable parameter offset: message byte 524 is the last payload byte.</summary>
    public const int MaxOffset = PatchProtocol.PayloadLast - PatchProtocol.OffsetBase;

    readonly struct Edit
    {
        public Edit(int offset, byte from, byte to) { Offset = offset; From = from; To = to; }
        public int Offset { get; }
        public byte From { get; }
        public byte To { get; }
    }

    PatchModel(byte[] dump)
    {
        _original = (byte[])dump.Clone();
        _working = (byte[])dump.Clone();
    }

    /// <summary>
    /// Build a model from a complete dump. The array is copied, so the caller may reuse
    /// the buffer the transport handed it.
    /// </summary>
    public static PatchModel FromDump(byte[] dump)
    {
        if (!PatchProtocol.IsDump(dump))
            throw new ArgumentException("not a 526-byte patch dump", nameof(dump));
        return new PatchModel(dump);
    }

    /// <summary>Bank the patch came from, as it arrived. Zero for the edit buffer.</summary>
    public int Bank => _original[11];

    /// <summary>Slot the patch came from, as it arrived. Zero for the edit buffer.</summary>
    public int Program => _original[12];

    /// <summary>Read or write one parameter by its table offset.</summary>
    public byte this[int offset]
    {
        get
        {
            CheckOffset(offset);
            return _working[PatchProtocol.MessageIndex(offset)];
        }
        set => Set(offset, value);
    }

    /// <summary>The value this parameter had when the patch arrived.</summary>
    public byte OriginalAt(int offset)
    {
        CheckOffset(offset);
        return _original[PatchProtocol.MessageIndex(offset)];
    }

    /// <summary>
    /// Set a parameter. Consecutive writes to the same offset collapse into one undo step,
    /// so a knob swept across its range is one undo rather than ninety.
    /// </summary>
    public void Set(int offset, byte value)
    {
        CheckOffset(offset);
        int i = PatchProtocol.MessageIndex(offset);
        byte was = _working[i];
        if (was == value) return;

        _working[i] = value;
        _redo.Clear();

        if (_undo.Count > 0 && _undo[_undo.Count - 1].Offset == offset)
        {
            var top = _undo[_undo.Count - 1];
            if (top.From == value) _undo.RemoveAt(_undo.Count - 1);   // swept back to where it started
            else _undo[_undo.Count - 1] = new Edit(offset, top.From, value);
        }
        else _undo.Add(new Edit(offset, was, value));
    }

    /// <summary>
    /// The patch name. Writing pads with blanks or truncates to the sixteen characters the
    /// instrument stores, and replaces anything unprintable with a blank.
    /// </summary>
    public string Name
    {
        get
        {
            var chars = new char[PatchProtocol.NameLength];
            for (int i = 0; i < PatchProtocol.NameLength; i++)
            {
                byte b = _working[PatchProtocol.MessageIndex(PatchProtocol.NameOffset + i)];
                chars[i] = b >= 0x20 && b < 0x7F ? (char)b : ' ';
            }
            return new string(chars).TrimEnd();
        }
        set
        {
            string s = value ?? "";
            for (int i = 0; i < PatchProtocol.NameLength; i++)
            {
                char c = i < s.Length ? s[i] : ' ';
                if (c < 0x20 || c >= 0x7F) c = ' ';
                Set(PatchProtocol.NameOffset + i, (byte)c);
            }
        }
    }

    /// <summary>True when the working bytes differ from the ones that arrived.</summary>
    public bool IsDirty
    {
        get
        {
            for (int i = PatchProtocol.PayloadFirst; i <= PatchProtocol.PayloadLast; i++)
                if (_working[i] != _original[i]) return true;
            return false;
        }
    }

    /// <summary>
    /// Every parameter offset whose value differs from the original, in order. This is
    /// what a compare view shows and what an editor needs to send when pushing changes
    /// one parameter at a time.
    /// </summary>
    public IReadOnlyList<int> ChangedOffsets
    {
        get
        {
            var changed = new List<int>();
            for (int i = PatchProtocol.PayloadFirst; i <= PatchProtocol.PayloadLast; i++)
                if (_working[i] != _original[i]) changed.Add(i - PatchProtocol.OffsetBase);
            return changed;
        }
    }

    /// <summary>Throw away every edit and go back to the patch as it arrived.</summary>
    public void Revert()
    {
        Array.Copy(_original, _working, _original.Length);
        _undo.Clear();
        _redo.Clear();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Undo the last edit. Returns the offset that moved, or -1 if there was none.</summary>
    public int Undo()
    {
        if (_undo.Count == 0) return -1;
        var e = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
        _working[PatchProtocol.MessageIndex(e.Offset)] = e.From;
        _redo.Add(e);
        return e.Offset;
    }

    /// <summary>Redo the last undone edit. Returns the offset that moved, or -1.</summary>
    public int Redo()
    {
        if (_redo.Count == 0) return -1;
        var e = _redo[_redo.Count - 1];
        _redo.RemoveAt(_redo.Count - 1);
        _working[PatchProtocol.MessageIndex(e.Offset)] = e.To;
        _undo.Add(e);
        return e.Offset;
    }

    /// <summary>
    /// The working patch as a message the instrument would accept. Everything outside the
    /// payload is carried through untouched, so a dump that round-trips unedited is
    /// byte-for-byte what arrived.
    /// </summary>
    public byte[] ToDump() => (byte[])_working.Clone();

    /// <summary>The patch as it arrived, for saving the unedited original alongside a draft.</summary>
    public byte[] ToOriginalDump() => (byte[])_original.Clone();

    /// <summary>Checksum of the working bytes, for comparing against the instrument's own.</summary>
    public uint Checksum() => PatchProtocol.Checksum(_working);

    static void CheckOffset(int offset)
    {
        if ((uint)offset > MaxOffset)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"parameter offset must be 0..{MaxOffset}");
    }
}
