using System;
using System.Collections.Generic;

namespace UltraNovaCtl.Core;

/// <summary>
/// A patch's name, category and genre - the three things the instrument shows on its own
/// display and the librarian sorts its list by.
///
/// Category and genre are single bytes indexing fixed tables. The instrument's own software
/// loads those tables from Statics.nun rather than hard-coding them, so the numeric value
/// and the label are decoupled; the labels below are that file's, and the ranges were
/// checked against 1024 real patches from the four factory banks and the four read off this
/// instrument - category 0..14 and genre 0..9, with every value in range used.
///
/// The awkward part is that metadata sits at different places depending on which message
/// carries it. In a 526-byte dump the name is at table offsets 2..17 with category at 18
/// and genre at 19. In the dedicated 33-byte metadata message the name starts at byte 13
/// with category at 29 and genre at 30. Both layouts live here so nothing else has to
/// remember which is which.
/// </summary>
public static class PatchMetadata
{
    /// <summary>Table offset of the category byte inside a patch dump.</summary>
    public const int CategoryOffset = 18;

    /// <summary>Table offset of the genre byte inside a patch dump.</summary>
    public const int GenreOffset = 19;

    /// <summary>Length of the dedicated metadata message.</summary>
    public const int MetadataLength = 33;

    // Byte positions inside that message.
    const int MetaName = 13;
    const int MetaCategory = 29;
    const int MetaGenre = 30;
    const int MetaFlags = 31;

    /// <summary>
    /// Bit 0 of the metadata message's flag byte is the patch's Chord setting. The native
    /// editor keeps Chord in sync through this bit as well as through its own parameter
    /// address, which is why a metadata push must carry the current value rather than zero.
    /// </summary>
    public const int ChordFlag = 0x01;

    static readonly string[] CategoryNames =
    {
        "None", "Arp", "Bass", "Bell", "Classic", "Drum", "Keyboard", "Lead",
        "Movement", "Pad", "Poly", "SFX", "String", "ExtInput", "Vocoder",
    };

    static readonly string[] GenreNames =
    {
        "None", "Classic", "D&B/Brks", "House", "Industrl", "Jazz",
        "R&B/HHop", "Rock/Pop", "Techno", "Dubstep",
    };

    /// <summary>The fifteen category labels, in value order.</summary>
    public static IReadOnlyList<string> Categories { get; } = Array.AsReadOnly(CategoryNames);

    /// <summary>The ten genre labels, in value order.</summary>
    public static IReadOnlyList<string> Genres { get; } = Array.AsReadOnly(GenreNames);

    /// <summary>Label for a category byte, or a plain number when it is outside the table.</summary>
    public static string CategoryName(int value)
        => (uint)value < CategoryNames.Length ? CategoryNames[value] : value.ToString();

    /// <summary>Label for a genre byte, or a plain number when it is outside the table.</summary>
    public static string GenreName(int value)
        => (uint)value < GenreNames.Length ? GenreNames[value] : value.ToString();

    /// <summary>
    /// Build the 33-byte message that pushes name, category and genre to the instrument.
    /// This is the only way the native editor writes metadata, and the librarian uses it
    /// to refresh the front panel the moment a name changes on the patch the instrument
    /// currently holds.
    /// </summary>
    public static byte[] Message(string name, int category, int genre, bool chord = false)
        => Message(name, category, genre, (byte)(chord ? ChordFlag : 0));

    /// <summary>Preserve all received flags when changing only the text or classification.</summary>
    public static byte[] Message(string name, int category, int genre, byte flags)
    {
        if ((uint)category > 127) throw new ArgumentOutOfRangeException(nameof(category));
        if ((uint)genre > 127) throw new ArgumentOutOfRangeException(nameof(genre));
        if (flags > 127) throw new ArgumentOutOfRangeException(nameof(flags));

        var m = new byte[MetadataLength];
        byte[] head = { 0xF0, 0x00, 0x20, 0x29, 0x03, 0x01, 0x7F };
        head.CopyTo(m, 0);
        m[7] = PatchProtocol.CmdMetadata;
        m[8] = PatchProtocol.CtrlNone;

        string s = name ?? "";
        for (int i = 0; i < PatchProtocol.NameLength; i++)
        {
            char c = i < s.Length ? s[i] : ' ';
            if (c < 0x20 || c >= 0x7F) c = ' ';
            m[MetaName + i] = (byte)c;
        }
        m[MetaCategory] = (byte)category;
        m[MetaGenre] = (byte)genre;
        m[MetaFlags] = flags;
        m[MetadataLength - 1] = 0xF7;
        return m;
    }

    /// <summary>True for a well-formed 33-byte metadata message.</summary>
    public static bool IsMessage(byte[] sx)
    {
        if (sx == null || sx.Length != MetadataLength || !PatchProtocol.HasHeader(sx)
            || sx[7] != PatchProtocol.CmdMetadata) return false;
        for (int i = 1; i < sx.Length - 1; i++) if (sx[i] > 127) return false;
        return true;
    }

    public static bool TryRead(byte[] sx, out MetadataEventArgs metadata)
    {
        metadata = null;
        if (!TryRead(sx, out string name, out int category, out int genre, out _)) return false;
        metadata = new MetadataEventArgs(name, category, genre, sx[MetaFlags]);
        return true;
    }

    /// <summary>Read back what a metadata message carries.</summary>
    public static bool TryRead(byte[] sx, out string name, out int category, out int genre, out bool chord)
    {
        name = ""; category = 0; genre = 0; chord = false;
        if (!IsMessage(sx)) return false;

        var chars = new char[PatchProtocol.NameLength];
        for (int i = 0; i < PatchProtocol.NameLength; i++)
        {
            byte b = sx[MetaName + i];
            chars[i] = b >= 0x20 && b < 0x7F ? (char)b : ' ';
        }
        name = new string(chars).TrimEnd();
        category = sx[MetaCategory];
        genre = sx[MetaGenre];
        chord = (sx[MetaFlags] & ChordFlag) != 0;
        return true;
    }
}
