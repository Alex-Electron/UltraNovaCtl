using System;
using System.Collections.Generic;
using System.IO;

namespace UltraNovaCtl.Core;

/// <summary>
/// Patches on disk. The instrument's own format is the 526-byte dump exactly as it travels
/// over the wire, and a .syx file is one or more of those laid end to end - a single patch
/// is 526 bytes, a whole bank is 128 of them, 67 328 bytes. Nothing is added or wrapped, so
/// a file written here loads in Novation's tools and a file captured from the instrument
/// loads here.
///
/// Reading is strict about shape and lenient about neighbours: a file is split at SysEx
/// boundaries, every complete 526-byte patch is kept, and anything else in the stream - a
/// status reply, a stray message, trailing bytes - is counted and skipped rather than
/// failing the whole file. Writing is exact.
/// </summary>
public static class PatchFile
{
    /// <summary>Number of patches in one bank, and therefore in a bank file.</summary>
    public const int BankSize = 128;

    /// <summary>Outcome of reading a file: the patches found and what was passed over.</summary>
    public sealed class ReadResult
    {
        public List<byte[]> Patches { get; } = new();

        /// <summary>SysEx messages in the file that were not 526-byte patches.</summary>
        public int Skipped { get; internal set; }

        /// <summary>Bytes at the end that did not form a complete message.</summary>
        public int TrailingBytes { get; internal set; }

        public bool IsBank => Patches.Count == BankSize;
    }

    /// <summary>Split raw bytes into patches. Never throws on content; only on a null argument.</summary>
    public static ReadResult Parse(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        var result = new ReadResult();
        int i = 0;
        while (i < bytes.Length)
        {
            // Walk to the next SysEx start; anything before it is not ours.
            if (bytes[i] != 0xF0) { i++; continue; }
            int end = Array.IndexOf(bytes, (byte)0xF7, i + 1);
            if (end < 0) { result.TrailingBytes = bytes.Length - i; break; }

            int len = end - i + 1;
            if (len == PatchProtocol.DumpLength)
            {
                var sx = new byte[len];
                Array.Copy(bytes, i, sx, 0, len);
                if (PatchProtocol.IsDump(sx)) result.Patches.Add(sx);
                else result.Skipped++;
            }
            else result.Skipped++;
            i = end + 1;
        }
        return result;
    }

    /// <summary>Read a .syx file. Throws only for file-system reasons.</summary>
    public static ReadResult Read(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Bytes for one or more patches laid end to end, as the instrument would emit them.
    /// Every entry must be a complete dump; anything else is refused rather than written
    /// out as a file that other tools would choke on.
    /// </summary>
    public static byte[] Serialize(IEnumerable<byte[]> patches)
    {
        if (patches == null) throw new ArgumentNullException(nameof(patches));
        using var ms = new MemoryStream();
        int n = 0;
        foreach (var p in patches)
        {
            if (!PatchProtocol.IsDump(p))
                throw new ArgumentException($"entry {n} is not a 526-byte patch dump", nameof(patches));
            ms.Write(p, 0, p.Length);
            n++;
        }
        return ms.ToArray();
    }

    /// <summary>Write one patch to a .syx file.</summary>
    public static void Write(string path, byte[] patch) => Write(path, new[] { patch });

    /// <summary>
    /// Write patches to a .syx file. Written to a sibling temporary file first and moved
    /// into place, so an interrupted save never leaves a half-written patch where a good
    /// one used to be.
    /// </summary>
    public static void Write(string path, IEnumerable<byte[]> patches)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is empty", nameof(path));
        byte[] bytes = Serialize(patches);
        string tmp = path + ".writing";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
