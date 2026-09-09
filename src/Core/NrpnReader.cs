using System;

namespace UltraNovaCtl.Core;

/// <summary>
/// One NRPN as the instrument sends it: the selected parameter, and whichever data bytes
/// have arrived for it.
/// </summary>
public readonly struct Nrpn
{
    public Nrpn(int channel, int msb, int lsb, byte data, byte dataLsb, bool hasLsb)
    { Channel = channel; Msb = msb; Lsb = lsb; Data = data; DataLsb = dataLsb; HasLsb = hasLsb; }

    /// <summary>One-based MIDI channel the NRPN arrived on.</summary>
    public int Channel { get; }

    /// <summary>Parameter number, as the two halves the instrument actually sends.</summary>
    public int Msb { get; }
    public int Lsb { get; }

    /// <summary>Data Entry MSB, controller 6.</summary>
    public byte Data { get; }

    /// <summary>Data Entry LSB, controller 38, meaningful only when <see cref="HasLsb"/>.</summary>
    public byte DataLsb { get; }
    public bool HasLsb { get; }

    /// <summary>
    /// The two data bytes read as one fourteen-bit value. Correct only for parameters that
    /// are actually fourteen bits wide - patch selection is not one of them, it puts the
    /// bank in the MSB and the slot in the LSB, so read those separately.
    /// </summary>
    public int Wide => (Data << 7) | DataLsb;

    public override string ToString()
        => $"NRPN ({Msb},{Lsb}) ch {Channel} = {Data}" + (HasLsb ? $" / {DataLsb}" : "");
}

/// <summary>
/// Reassembles NRPN messages from the controller changes that carry them: 99 selects the
/// parameter's high half, 98 the low half, 6 sends the data and 38 an optional second
/// byte. The instrument uses these on channel 2 to report both which patch was selected
/// and which parameters were edited on the panel.
///
/// Each Data Entry byte produces an event rather than waiting for a pair, because the
/// instrument sends controller 6 alone for most parameters and follows with 38 only for
/// some. A caller that needs both gets a second event with <see cref="Nrpn.HasLsb"/> set,
/// carrying the same parameter, so it can simply overwrite what it stored.
///
/// Parameter selection is remembered across messages the way running status is: the
/// instrument may send several Data Entry values against one selected parameter.
/// </summary>
public sealed class NrpnReader
{
    // Per channel, because nothing guarantees the instrument keeps to one.
    readonly int[] _msb = new int[16];
    readonly int[] _lsb = new int[16];
    readonly byte[] _data = new byte[16];

    public NrpnReader()
    {
        for (int i = 0; i < 16; i++) { _msb[i] = -1; _lsb[i] = -1; }
    }

    /// <summary>
    /// Feed one channel message. Returns true and fills <paramref name="nrpn"/> when a
    /// Data Entry byte completed something worth reporting.
    /// </summary>
    public bool Feed(byte status, byte controller, byte value, out Nrpn nrpn)
    {
        nrpn = default;
        if ((status & 0xF0) != 0xB0) return false;
        int ch = status & 0x0F;

        switch (controller)
        {
            case 99:                                  // NRPN parameter MSB
                _msb[ch] = value;
                return false;
            case 98:                                  // NRPN parameter LSB
                _lsb[ch] = value;
                return false;
            case 6:                                   // Data Entry MSB
                if (_msb[ch] < 0 || _lsb[ch] < 0) return false;
                _data[ch] = value;
                nrpn = new Nrpn(ch + 1, _msb[ch], _lsb[ch], value, 0, false);
                return true;
            case 38:                                  // Data Entry LSB
                if (_msb[ch] < 0 || _lsb[ch] < 0) return false;
                nrpn = new Nrpn(ch + 1, _msb[ch], _lsb[ch], _data[ch], value, true);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Forget the selected parameter on every channel.</summary>
    public void Reset()
    {
        for (int i = 0; i < 16; i++) { _msb[i] = -1; _lsb[i] = -1; _data[i] = 0; }
    }

    /// <summary>
    /// The NRPN the instrument uses to announce which patch is selected. Its Data Entry
    /// MSB is the bank and the LSB is the slot, so it is not a fourteen-bit value.
    /// </summary>
    public const int PatchSelectMsb = 63;
    public const int PatchSelectLsb = 1;

    /// <summary>
    /// True when this NRPN addresses the patch-selection parameter, whether or not the
    /// second data byte has arrived yet. Selection is announced as two Data Entry bytes
    /// against one parameter, so the first of them is still part of the announcement and
    /// must not be mistaken for an edit of some parameter numbered (63,1).
    /// </summary>
    public static bool IsPatchSelectParameter(in Nrpn n)
        => n.Msb == PatchSelectMsb && n.Lsb == PatchSelectLsb;

    /// <summary>True when this NRPN is a complete patch-selection announcement.</summary>
    public static bool IsPatchSelect(in Nrpn n) => IsPatchSelectParameter(n) && n.HasLsb;
}
