namespace UltraNovaCtl.Core;

/// <summary>
/// Standard names for MIDI controllers and notes, the way a monitor like MIDI-OX shows
/// them. Knowing that CC 74 is the filter cutoff saves looking it up every time.
/// </summary>
public static class MidiNames
{
    static readonly string[] Note = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>
    /// Every label is padded to the same width. A dropdown sizes itself to the longest
    /// visible entry, so ragged labels make it jump about while scrolling.
    /// </summary>
    public const int LabelWidth = 30;

    static string Pad(string s) =>
        s.Length >= LabelWidth ? s.Substring(0, LabelWidth) : s.PadRight(LabelWidth);

    /// <summary>"Note 042 (F#1)", matching the original Automap editor.</summary>
    public static string NoteLabel(int n)
    {
        n = Math.Clamp(n, 0, 127);
        return Pad($"Note {n:000} ({Note[n % 12]}{n / 12 - 2})");
    }

    /// <summary>"CC# 074 (Brightness)", or just the number when it has no standard use.</summary>
    public static string CcLabel(int n)
    {
        n = Math.Clamp(n, 0, 127);
        return Pad(Cc.TryGetValue(n, out string name) ? $"CC# {n:000} ({name})" : $"CC# {n:000}");
    }

    /// <summary>Compact form for the synth display: "N026-D0", eight characters at most.</summary>
    public static string NoteCompact(int n)
    {
        n = Math.Clamp(n, 0, 127);
        return $"N{n:000}-{Note[n % 12]}{n / 12 - 2}";
    }

    /// <summary>The same text without padding, for places where it is shown inline.</summary>
    public static string CcShort(int n) => CcLabel(n).TrimEnd();
    public static string NoteShort(int n) => NoteLabel(n).TrimEnd();

    /// <summary>Assigned controller names from the MIDI 1.0 specification.</summary>
    public static readonly Dictionary<int, string> Cc = new()
    {
        [0] = "Bank Select",
        [1] = "Modulation",
        [2] = "Breath",
        [4] = "Foot Controller",
        [5] = "Portamento Time",
        [6] = "Data Entry",
        [7] = "Volume",
        [8] = "Balance",
        [10] = "Pan",
        [11] = "Expression",
        [12] = "Effect Control 1",
        [13] = "Effect Control 2",
        [16] = "General Purpose 1",
        [17] = "General Purpose 2",
        [18] = "General Purpose 3",
        [19] = "General Purpose 4",
        [32] = "Bank Select LSB",
        [33] = "Modulation LSB",
        [38] = "Data Entry LSB",
        [39] = "Volume LSB",
        [42] = "Pan LSB",
        [43] = "Expression LSB",
        [64] = "Sustain Pedal",
        [65] = "Portamento",
        [66] = "Sostenuto",
        [67] = "Soft Pedal",
        [68] = "Legato Footswitch",
        [69] = "Hold 2",
        [70] = "Sound Variation",
        [71] = "Resonance",
        [72] = "Release Time",
        [73] = "Attack Time",
        [74] = "Brightness / Cutoff",
        [75] = "Decay Time",
        [76] = "Vibrato Rate",
        [77] = "Vibrato Depth",
        [78] = "Vibrato Delay",
        [80] = "General Purpose 5",
        [81] = "General Purpose 6",
        [82] = "General Purpose 7",
        [83] = "General Purpose 8",
        [84] = "Portamento Control",
        [91] = "Reverb Send",
        [92] = "Tremolo Depth",
        [93] = "Chorus Send",
        [94] = "Detune Depth",
        [95] = "Phaser Depth",
        [96] = "Data Increment",
        [97] = "Data Decrement",
        [98] = "NRPN LSB",
        [99] = "NRPN MSB",
        [100] = "RPN LSB",
        [101] = "RPN MSB",
        [120] = "All Sound Off",
        [121] = "Reset All Controllers",
        [122] = "Local Control",
        [123] = "All Notes Off",
        [124] = "Omni Off",
        [125] = "Omni On",
        [126] = "Mono Mode",
        [127] = "Poly Mode",
    };
}
