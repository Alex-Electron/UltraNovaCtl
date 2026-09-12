using UltraNovaCtl.Core;

namespace UltraNovaCtl.CoreRegressionTests;

static partial class Program
{
    // ---- the hand-written descriptor set the first screen uses --------------
    //
    // A typo here would not crash anything. It would quietly send the wrong controller, or
    // write into a neighbouring parameter's byte, and the only symptom would be a sound
    // that is not what the screen says. So the set is checked as data.

    static void EveryKnownParameterIsCoherent()
    {
        var seen = new HashSet<int>();
        foreach (var d in KnownParameters.FirstScreen)
        {
            string who = $"{d.Short} / {d.Name} (id {d.Id})";

            True(seen.Add(d.Id), $"{who}: appears once");
            True(d.Name.Length > 0 && d.Short.Length > 0, $"{who}: is named and grouped");
            True(d.Max >= d.Min, $"{who}: range runs the right way");
            True(d.InPatch, $"{who}: lives somewhere in the patch");
            True(d.Offset >= 0 && d.Offset <= PatchModel.MaxOffset, $"{who}: offset is inside the payload");
            True(d.Cc.HasValue || d.NrpnMsb.HasValue, $"{who}: has an address to send on");

            // Every displayed value must encode to something the wire can carry.
            for (int v = d.Min; v <= d.Max; v++)
            {
                int wire = ParameterCodec.ToWire(d, v);
                if (wire < 0 || wire > ParameterCodec.MaxByte)
                { True(false, $"{who}: display {v} encodes to {wire}, outside one byte"); return; }
            }

            // An enumeration must label every value it can take, and no more.
            if (d.Values.Length > 0)
                Equal(d.Max - d.Min + 1, d.Values.Length, $"{who}: one label per value");
        }
        True(KnownParameters.FirstScreen.Count >= 10, "the screen has enough on it to be worth opening");
    }

    /// <summary>
    /// The two filter links share one byte and one address. If their bits or their control
    /// values collided, setting one would silently move the other - the exact failure the
    /// packed-bit path exists to prevent.
    /// </summary>
    static void TheSharedFilterLinkBitsDoNotCollide()
    {
        var packed = new List<ParameterDescriptor>();
        foreach (var d in KnownParameters.FirstScreen)
            if (d.Storage == ParameterStorage.PackedBits) packed.Add(d);
        Equal(2, packed.Count, "two packed flags on this screen");

        Equal(packed[0].Offset, packed[1].Offset, "they share one byte");
        True((packed[0].BitMask & packed[1].BitMask) == 0, "and do not share a bit");
        Equal(packed[0].NrpnLsb, packed[1].NrpnLsb, "they share one address");

        // Four distinct values across the pair: off and on for each.
        var values = new HashSet<int>();
        foreach (var d in packed)
            for (int v = d.Min; v <= d.Max; v++)
                True(values.Add(ParameterCodec.PackedControlValue(d, v)),
                     $"{d.Name} at {v} has a control value of its own");
        Equal(4, values.Count, "four distinct control values across the two flags");

        // And writing one really does leave the other alone, through the real path.
        var patch = PatchModel.FromDump(SyntheticDump());
        ParameterCodec.TryWrite(packed[0], patch, 1);
        ParameterCodec.TryWrite(packed[1], patch, 1);
        ParameterCodec.TryWrite(packed[0], patch, 0);
        True(ParameterCodec.TryRead(packed[1], patch, out int other), "the other flag reads back");
        Equal(1, other, "and is still on");
    }

    /// <summary>
    /// Two parameters sharing a byte is fine when they are packed flags and a defect
    /// otherwise: a whole-byte parameter written over its neighbour destroys it.
    /// </summary>
    static void NoTwoWholeByteParametersShareAByte()
    {
        var used = new Dictionary<int, string>();
        foreach (var d in KnownParameters.FirstScreen)
        {
            if (d.Storage != ParameterStorage.Byte) continue;
            True(!used.ContainsKey(d.Offset),
                 $"{d.Name} has offset {d.Offset} to itself" +
                 (used.TryGetValue(d.Offset, out string? other) ? $", not shared with {other}" : ""));
            used[d.Offset] = d.Name;
        }
        True(used.Count > 0, "there are whole-byte parameters to check");
    }

    /// <summary>
    /// The worked example from the requirements document, through the descriptor the screen
    /// actually uses rather than a copy of it in a check.
    /// </summary>
    static void TheScreensCentsParameterMatchesTheDocument()
    {
        ParameterDescriptor? cents = null;
        foreach (var d in KnownParameters.FirstScreen)
            if (d.Name == "Cents") cents = d;
        True(cents != null, "the screen has the cents parameter");

        Equal(14, ParameterCodec.ToWire(cents!, -50), "-50 cents travels as 14");
        Equal(64, ParameterCodec.ToWire(cents!, 0), "centre travels as 64");
        Equal(114, ParameterCodec.ToWire(cents!, 50), "+50 cents travels as 114");
        Equal(27, cents!.Cc, "on controller 27");
        Equal(41, cents!.Offset, "and offset 41 in the patch");
    }
}
