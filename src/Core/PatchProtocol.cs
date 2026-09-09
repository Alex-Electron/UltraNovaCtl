using System;

namespace UltraNovaCtl.Core;

/// <summary>
/// The UltraNova patch protocol, as established on the instrument and written up in
/// docs/PATCH-PROTOCOL.ru.md. Everything here is a pure function over bytes, so it can be
/// checked without hardware; the transport that carries these messages lives elsewhere.
///
/// Messages are SysEx with a fixed seven-byte header. Commands are fourteen bytes, patch
/// dumps are 526, and a status reply is fifteen. Positions in the parameter table count
/// from message byte 13, so a table offset and a message index differ by exactly that -
/// the single most useful fact in the whole protocol, and the one that took longest to
/// pin down.
/// </summary>
public static class PatchProtocol
{
    /// <summary>Length of a full patch dump, header and terminator included.</summary>
    public const int DumpLength = 526;

    /// <summary>Length of a command such as a status or dump request.</summary>
    public const int CommandLength = 14;

    /// <summary>Length of the reply to a status request.</summary>
    public const int StatusReplyLength = 15;

    /// <summary>
    /// Message byte holding table offset zero. A parameter at table offset N lives at
    /// message index N + 13; confirmed on the instrument for three parameters in three
    /// different sections, and again when a patch change moved exactly the name range.
    /// </summary>
    public const int OffsetBase = 13;

    /// <summary>First and last byte of the 512-byte payload the checksum covers.</summary>
    public const int PayloadFirst = 13;
    public const int PayloadLast = 524;

    /// <summary>The patch name occupies table offsets 2 to 17, sixteen ASCII characters.</summary>
    public const int NameOffset = 2;
    public const int NameLength = 16;

    // Command bytes, index 7 of a message.
    public const byte CmdDumpReply = 0x00;   // instrument -> host, a patch
    public const byte CmdMetadata = 0x21;    // host -> instrument, name/category/genre
    public const byte CmdStatusReply = 0x20; // instrument -> host, answer to 0x60
    public const byte CmdEditBuffer = 0x40;  // host -> instrument, send the edit buffer
    public const byte CmdStored = 0x41;      // host -> instrument, a stored slot
    public const byte CmdAck = 0x61;         // host -> instrument, dump accepted
    public const byte CmdStatus = 0x60;      // host -> instrument, who are you

    // Control bytes, index 8.
    public const byte CtrlNone = 0x00;
    public const byte CtrlHello = 0x21;      // with 0x60: an editor attaching
    public const byte CtrlStored = 0x21;     // with 0x41: fetch a stored patch
    public const byte CtrlChecksum = 0x23;   // with 0x41: fetch a slot's checksum

    /// <summary>Byte 12 of a status reply: 1 when Local Control is on.</summary>
    public const int StatusLocalIndex = 12;

    static readonly byte[] Preamble = { 0xF0, 0x00, 0x20, 0x29, 0x03, 0x01, 0x7F };

    /// <summary>
    /// Transport framing for the private port, taken from the native plug-in: it sends
    /// the first thing after opening its output and the second on teardown. Manufacturer
    /// byte 01 means the transport layer consumes these, not the synth.
    /// </summary>
    public static byte[] TransportEnable => new byte[] { 0xF0, 0x01, 0x00, 0x01, 0xF7 };
    public static byte[] TransportDisable => new byte[] { 0xF0, 0x01, 0x00, 0x00, 0xF7 };

    /// <summary>Build a fourteen-byte command.</summary>
    public static byte[] Command(byte command, byte control, int bank = 0, int program = 0)
    {
        if ((uint)bank > 127) throw new ArgumentOutOfRangeException(nameof(bank));
        if ((uint)program > 127) throw new ArgumentOutOfRangeException(nameof(program));
        var m = new byte[CommandLength];
        Preamble.CopyTo(m, 0);
        m[7] = command;
        m[8] = control;
        m[9] = 0;                       // version of the sending device; zero is accepted
        m[10] = 0;
        m[11] = (byte)bank;
        m[12] = (byte)program;
        m[13] = 0xF7;
        return m;
    }

    /// <summary>Ask for the current edit buffer.</summary>
    public static byte[] RequestEditBuffer() => Command(CmdEditBuffer, CtrlNone);

    /// <summary>Ask for a stored patch. Bank is one-based, program zero-based.</summary>
    public static byte[] RequestStored(int bank, int program)
        => Command(CmdStored, CtrlStored, bank, program);

    /// <summary>Ask a stored slot for its checksum.</summary>
    public static byte[] RequestChecksum(int bank, int program)
        => Command(CmdStored, CtrlChecksum, bank, program);

    /// <summary>Ask the instrument to identify itself. Also how an editor announces itself.</summary>
    public static byte[] RequestStatus() => Command(CmdStatus, CtrlHello);

    /// <summary>True when the message carries this instrument family's SysEx header.</summary>
    public static bool HasHeader(byte[] sx)
    {
        if (sx == null || sx.Length < Preamble.Length + 1) return false;
        for (int i = 0; i < Preamble.Length; i++)
            if (sx[i] != Preamble[i]) return false;
        return sx[sx.Length - 1] == 0xF7;
    }

    /// <summary>True for a complete 526-byte patch dump.</summary>
    public static bool IsDump(byte[] sx)
        => sx != null && sx.Length == DumpLength && HasHeader(sx) && sx[7] == CmdDumpReply;

    /// <summary>True for the fifteen-byte answer to a status request.</summary>
    public static bool IsStatusReply(byte[] sx)
        => sx != null && sx.Length == StatusReplyLength && HasHeader(sx) && sx[7] == CmdStatusReply;

    /// <summary>Local Control state carried by a status reply.</summary>
    public static bool LocalOn(byte[] statusReply)
    {
        if (!IsStatusReply(statusReply)) throw new ArgumentException("not a status reply");
        return statusReply[StatusLocalIndex] == 1;
    }

    /// <summary>
    /// Firmware version from a message's version pair: the high byte packs major in its
    /// upper five bits and minor in its lower three, the low byte is the build. A reply
    /// carrying 0x10 0x00 is version 2.0.00.
    /// </summary>
    public static (int Major, int Minor, int Build) Firmware(byte high, byte low)
        => (high >> 3, high & 7, low);

    /// <summary>Version of the device that sent the message, at bytes 9 and 10.</summary>
    public static (int Major, int Minor, int Build) SenderFirmware(byte[] sx)
    {
        if (!HasHeader(sx) || sx.Length < 11) throw new ArgumentException("not a protocol message");
        return Firmware(sx[9], sx[10]);
    }

    /// <summary>Message index of a parameter-table offset.</summary>
    public static int MessageIndex(int tableOffset) => tableOffset + OffsetBase;

    /// <summary>Read one parameter out of a dump by its table offset.</summary>
    public static bool TryValue(byte[] dump, int tableOffset, out byte value)
    {
        value = 0;
        if (!IsDump(dump) || tableOffset < 0) return false;
        int i = MessageIndex(tableOffset);
        if (i > PayloadLast) return false;
        value = dump[i];
        return true;
    }

    /// <summary>
    /// The patch name, trailing blanks removed. Stored as sixteen ASCII characters at
    /// table offsets 2 to 17.
    /// </summary>
    public static string NameOf(byte[] dump)
    {
        if (!IsDump(dump)) throw new ArgumentException("not a patch dump");
        var chars = new char[NameLength];
        for (int i = 0; i < NameLength; i++)
        {
            byte b = dump[MessageIndex(NameOffset + i)];
            chars[i] = b >= 0x20 && b < 0x7F ? (char)b : ' ';
        }
        return new string(chars).TrimEnd();
    }

    /// <summary>Bank and program a dump came from. Both are zero for the edit buffer.</summary>
    public static (int Bank, int Program) SlotOf(byte[] dump)
    {
        if (!IsDump(dump)) throw new ArgumentException("not a patch dump");
        return (dump[11], dump[12]);
    }

    /// <summary>
    /// Checksum over the 512-byte payload: 128 big-endian 32-bit words summed with
    /// end-around carry. Verified against the instrument's own answers for 508 of 512
    /// factory and user slots.
    ///
    /// The four that differed were each short by exactly 0x80, which in a big-endian word
    /// is bit 7 of the byte where (offset - 13) % 4 == 3 - in the metadata area that is
    /// byte 32, the genre. Setting that bit reproduces all four and breaks none of the
    /// other 508, so the instrument keeps a genre bit that it does not transmit. Pass
    /// <paramref name="setGenreHighBit"/> to reproduce the instrument's value for those;
    /// leave it alone to checksum the bytes exactly as they arrived.
    /// </summary>
    public static uint Checksum(byte[] dump, bool setGenreHighBit = false)
    {
        if (dump == null || dump.Length < PayloadLast + 1)
            throw new ArgumentException("message too short to checksum");

        long sum = 0;
        for (int i = PayloadFirst; i + 3 <= PayloadLast; i += 4)
        {
            long word = ((long)dump[i] << 24) | ((long)dump[i + 1] << 16)
                      | ((long)dump[i + 2] << 8) | dump[i + 3];
            if (setGenreHighBit && i + 3 == 32) word |= 0x80;
            sum += word;
            sum += sum >> 32;
            sum &= 0xFFFFFFFF;
        }
        return (uint)sum;
    }

    /// <summary>
    /// Decode a checksum reply: five seven-bit groups, most significant first, in bytes
    /// 13 to 17 of a nineteen-byte message.
    /// </summary>
    public static bool TryReadChecksumReply(byte[] sx, out uint checksum)
    {
        checksum = 0;
        if (!HasHeader(sx) || sx.Length < 18) return false;
        long v = 0;
        for (int i = 17; i >= 13; i--)
        {
            if (sx[i] > 127) return false;
            v = (v << 7) | sx[i];
        }
        checksum = (uint)(v & 0xFFFFFFFF);
        return true;
    }
}
