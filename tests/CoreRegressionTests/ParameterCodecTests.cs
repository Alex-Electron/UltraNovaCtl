using UltraNovaCtl.Core;

namespace UltraNovaCtl.CoreRegressionTests;

static partial class Program
{
    // ---- parameter codec ---------------------------------------------------
    //
    // The descriptors below are the shapes the installed parameter description actually
    // uses, with the numbers that were checked against the instrument's own factory
    // content. They are test anchors, not a shipped registry - the real ones are built on
    // the user's machine by an extractor.
    //
    // Every worked example here is one already written up in
    // docs/OSC-FILTER-REQUIREMENTS.ru.md, so a change to the codec that breaks the
    // document's own arithmetic fails here first.

    /// <summary>Osc1 Cents: display -50..+50, wire 14..114. One of the three worked examples.</summary>
    static ParameterDescriptor Cents() => new()
    {
        Id = 1, Name = "Osc1 Cents Offset", Min = -50, Max = 50,
        MidiStart = 14, MidiRange = 100, Cc = 27, Offset = 41,
    };

    /// <summary>Pre-FX Level in dB: display -12..+18, wire 52..82. Note CC 58, not the 57 the PDF prints.</summary>
    static ParameterDescriptor PreFx() => new()
    {
        Id = 42, Name = "Pre-FX Level", Units = "dB", Min = -12, Max = 18,
        MidiStart = 52, MidiRange = 30, Cc = 58, Offset = 70,
    };

    /// <summary>Osc1 Semitone: display -64..+63 across the whole byte.</summary>
    static ParameterDescriptor Semitone() => new()
    {
        Id = 0, Name = "Osc1 Semitone Offset", Min = -64, Max = 63,
        MidiStart = 0, MidiRange = 127, Cc = 26, Offset = 40,
    };

    /// <summary>Octave: the one signed parameter in 497. Wire runs -4..+4.</summary>
    static ParameterDescriptor Octave() => new()
    {
        Id = 463, Name = "Octave", Min = -4, Max = 4,
        MidiStart = -4, MidiRange = 8, Cc = 13, Signed = true, Offset = 25,
    };

    /// <summary>Clock BPM: wider than a byte, so it lives in two, most significant first.</summary>
    static ParameterDescriptor ClockBpm() => new()
    {
        Id = 449, Name = "Clock BPM", Min = 40, Max = 250,
        MidiStart = 40, MidiRange = 210, NrpnMsb = 2, NrpnLsb = 63,
        Storage = ParameterStorage.Wide, Offset = 434,
    };

    /// <summary>Filter Freq Link: bit 0 of the byte at offset 75, written as 42 or 43.</summary>
    static ParameterDescriptor FreqLink() => new()
    {
        Id = 68, Name = "Filter Freq Link", Min = 0, Max = 1,
        MidiStart = 42, MidiRange = 1, NrpnMsb = 0, NrpnLsb = 122,
        Storage = ParameterStorage.PackedBits, Offset = 75, BitIndex = 0, BitRange = 1,
    };

    /// <summary>Filter Res Link: bit 1 of the same byte, written as 44 or 45.</summary>
    static ParameterDescriptor ResLink() => new()
    {
        Id = 69, Name = "Filter Res Link", Min = 0, Max = 1,
        MidiStart = 44, MidiRange = 1, NrpnMsb = 0, NrpnLsb = 122,
        Storage = ParameterStorage.PackedBits, Offset = 75, BitIndex = 1, BitRange = 1,
    };

    /// <summary>Lfo1 Fade In/Out: a two-bit field at bit 4 of a byte shared with four flags.</summary>
    static ParameterDescriptor LfoFade() => new()
    {
        Id = 186, Name = "Lfo1 Fade In/Out", Min = 0, Max = 3,
        MidiStart = 0, MidiRange = 3, NrpnMsb = 0, NrpnLsb = 123,
        Storage = ParameterStorage.PackedBits, Offset = 194, BitIndex = 4, BitRange = 3,
        Values = new[] { "Off", "FadeIn", "FadeOut", "GateIn" },
    };

    /// <summary>Osc1 Solo: momentary, no place in a patch. Its offset of -1 is a marker.</summary>
    static ParameterDescriptor Solo() => new()
    {
        Id = 44, Name = "Osc1 Solo", Min = 0, Max = 1,
        MidiStart = 0, MidiRange = 127, NrpnMsb = 61, NrpnLsb = 0,
        Storage = ParameterStorage.Transient, Offset = -1,
    };

    static void TheCodecReproducesTheDocumentedExamples()
    {
        var cents = Cents();
        Equal(14, ParameterCodec.ToWire(cents, -50), "Osc1 cents at -50");
        Equal(64, ParameterCodec.ToWire(cents, 0), "Osc1 cents at 0");
        Equal(114, ParameterCodec.ToWire(cents, 50), "Osc1 cents at +50");

        var prefx = PreFx();
        Equal(52, ParameterCodec.ToWire(prefx, -12), "Pre-FX at -12 dB");
        Equal(64, ParameterCodec.ToWire(prefx, 0), "Pre-FX at 0 dB");
        Equal(82, ParameterCodec.ToWire(prefx, 18), "Pre-FX at +18 dB");

        var semi = Semitone();
        Equal(0, ParameterCodec.ToWire(semi, -64), "semitone at -64");
        Equal(64, ParameterCodec.ToWire(semi, 0), "semitone at 0");
        Equal(127, ParameterCodec.ToWire(semi, 63), "semitone at +63");
    }

    /// <summary>
    /// The rounding rule is floor of the half-shifted value, taken from the disassembled
    /// encoder, and it is NOT the same as .NET's default rounding: at exactly x.5 with x
    /// even, Math.Round goes down to even and this goes up.
    ///
    /// No parameter among the instrument's 497 ever lands on that boundary - checked across
    /// every display value of every descriptor - so today the two rules agree everywhere on
    /// this hardware. The check exists anyway because the extractor builds descriptors from
    /// a file that can change, and because a rule copied from a disassembly should be the
    /// rule, not something that merely happens to match.
    /// </summary>
    static void TheRoundingRuleIsFloorOfTheHalf()
    {
        // display 0..2 over a wire range of 1 puts the midpoint exactly on 0.5.
        var d = new ParameterDescriptor
        { Id = 998, Name = "half-step probe", Min = 0, Max = 2, MidiStart = 0, MidiRange = 1, Offset = 200 };
        Equal(0, ParameterCodec.ToWire(d, 0), "bottom");
        Equal(1, ParameterCodec.ToWire(d, 1), "the midpoint rounds up, where round-half-to-even would give 0");
        Equal(1, ParameterCodec.ToWire(d, 2), "top");

        // And again one step higher, where the integer part is even at 2.5.
        var e = new ParameterDescriptor
        { Id = 997, Name = "half-step probe 2", Min = 0, Max = 2, MidiStart = 2, MidiRange = 1, Offset = 201 };
        Equal(3, ParameterCodec.ToWire(e, 1), "2.5 rounds up to 3, not down to 2");
    }

    static void TheCodecRoundTripsAcrossItsRange()
    {
        foreach (var d in new[] { Cents(), PreFx(), Semitone() })
        {
            for (int display = d.Min; display <= d.Max; display++)
            {
                byte wire = ParameterCodec.ToMidiByte(d, display);
                int back = ParameterCodec.FromMidiByte(d, wire);
                if (back != display)
                {
                    True(false, $"{d.Name} at {display} came back as {back} through wire {wire}");
                    return;
                }
            }
        }
        True(true, "every displayed value survives the wire and returns unchanged");

        // Out-of-domain input is clamped rather than producing a byte outside the range.
        var c = Cents();
        Equal(14, ParameterCodec.ToWire(c, -9999), "a value below the domain clamps to its floor");
        Equal(114, ParameterCodec.ToWire(c, 9999), "and above it to its ceiling");
    }

    static void TheOneSignedParameterTravelsAsTwosComplement()
    {
        var o = Octave();
        Equal(-4, ParameterCodec.ToWire(o, -4), "the wire value itself goes negative");
        Equal(0, ParameterCodec.ToWire(o, 0), "centre");
        Equal(4, ParameterCodec.ToWire(o, 4), "top");

        Equal((byte)124, ParameterCodec.ToMidiByte(o, -4), "-4 travels as 124");
        Equal((byte)127, ParameterCodec.ToMidiByte(o, -1), "-1 travels as 127");
        Equal((byte)0, ParameterCodec.ToMidiByte(o, 0), "0 travels as 0");
        Equal((byte)4, ParameterCodec.ToMidiByte(o, 4), "+4 travels as 4");

        for (int display = o.Min; display <= o.Max; display++)
            Equal(display, ParameterCodec.FromMidiByte(o, ParameterCodec.ToMidiByte(o, display)),
                  $"octave {display} survives the round trip");

        // A descriptor that is not marked signed must refuse rather than send a wrapped byte.
        var unsigned = new ParameterDescriptor
        { Id = 999, Name = "unmarked", Min = -4, Max = 4, MidiStart = -4, MidiRange = 8 };
        bool threw = false;
        try { ParameterCodec.ToMidiByte(unsigned, -4); } catch (InvalidOperationException) { threw = true; }
        True(threw, "a negative value from an unsigned descriptor is refused, not wrapped");
    }

    static void AWideParameterUsesTwoBytesMostSignificantFirst()
    {
        var bpm = ClockBpm();

        // 120 BPM is what 310 of the 512 factory patches carry, stored as 0 then 120.
        var (high, low) = ParameterCodec.ToWidePair(bpm, 120);
        Equal((byte)0, high, "120 BPM has nothing in the high byte");
        Equal((byte)120, low, "and 120 in the low one");

        var (h250, l250) = ParameterCodec.ToWidePair(bpm, 250);
        Equal((byte)1, h250, "250 BPM needs the high byte");
        Equal((byte)122, l250, "with 122 left in the low one");
        Equal(250, ParameterCodec.FromWidePair(bpm, 1, 122), "and reassembles");

        // The other byte order would decode this pair as 15 482, far outside 40..250.
        // That is exactly how the order was settled, so it is worth a check of its own.
        True(ParameterCodec.FromWidePair(bpm, 1, 122) != ((122 << 7) | 1),
             "the low byte is not the most significant one");

        for (int display = bpm.Min; display <= bpm.Max; display++)
        {
            var (hi, lo) = ParameterCodec.ToWidePair(bpm, display);
            if (ParameterCodec.FromWidePair(bpm, hi, lo) != display)
            { True(false, $"{display} BPM did not survive the split"); return; }
        }
        True(true, "every tempo survives the split and reassembly");

        // A wide value must not be squeezed into one byte by accident.
        bool threw = false;
        try { ParameterCodec.ToMidiByte(bpm, 250); } catch (InvalidOperationException) { threw = true; }
        True(threw, "a wide value is refused as a single byte rather than truncated");
    }

    static void APackedFieldLeavesItsNeighboursAlone()
    {
        var patch = PatchModel.FromDump(SyntheticDump());
        var freq = FreqLink();
        var res = ResLink();

        // Put rubbish in the shared byte, including bits nothing here claims.
        patch[freq.Offset] = 0b1011100;

        True(ParameterCodec.TryWrite(freq, patch, 1), "the freq link writes");
        Equal(0b1011101, patch[freq.Offset], "its bit goes on and every other bit stays");

        True(ParameterCodec.TryWrite(res, patch, 1), "the res link writes");
        Equal(0b1011111, patch[freq.Offset], "its own bit goes on, freq link and the unknown bits survive");

        True(ParameterCodec.TryWrite(freq, patch, 0), "the freq link clears");
        Equal(0b1011110, patch[freq.Offset], "only its bit cleared");

        True(ParameterCodec.TryRead(res, patch, out int resValue), "the res link reads back");
        Equal(1, resValue, "still on");
        True(ParameterCodec.TryRead(freq, patch, out int freqValue), "and the freq link");
        Equal(0, freqValue, "off");

        // A two-bit field occupies two bits and nothing more.
        var fade = LfoFade();
        var p2 = PatchModel.FromDump(SyntheticDump());
        p2[fade.Offset] = 0b0001111;
        Equal(0b0110000, fade.BitMask, "a range of 3 needs two bits at index 4");
        True(ParameterCodec.TryWrite(fade, p2, 3), "the widest value writes");
        Equal(0b0111111, p2[fade.Offset], "both its bits set, the four flags below untouched");
        True(ParameterCodec.TryRead(fade, p2, out int fadeValue), "reads back");
        Equal(3, fadeValue, "as the value written");
        Equal("GateIn", ParameterCodec.Format(fade, 3), "and formats by its label");
    }

    static void APackedFieldIsWrittenByItsOwnControlValue()
    {
        // The instrument does not take the whole byte over MIDI. Each flag and each state
        // has its own number on one shared address - this is how the pair is told apart.
        Equal(42, ParameterCodec.PackedControlValue(FreqLink(), 0), "freq link off is 42");
        Equal(43, ParameterCodec.PackedControlValue(FreqLink(), 1), "freq link on is 43");
        Equal(44, ParameterCodec.PackedControlValue(ResLink(), 0), "res link off is 44");
        Equal(45, ParameterCodec.PackedControlValue(ResLink(), 1), "res link on is 45");
        Equal(122, ResLink().NrpnLsb, "both on the same address");

        bool threw = false;
        try { ParameterCodec.PackedControlValue(Cents(), 0); } catch (InvalidOperationException) { threw = true; }
        True(threw, "asking an ordinary parameter for a packed control value is refused");
    }

    static void AMomentaryParameterNeverTouchesAPatch()
    {
        var solo = Solo();
        True(!solo.InPatch, "solo is not part of a patch");

        var patch = PatchModel.FromDump(SyntheticDump());
        var before = patch.ToDump();

        True(!ParameterCodec.TryWrite(solo, patch, 1), "writing it is refused");
        True(!ParameterCodec.TryRead(solo, patch, out _), "and so is reading it");
        True(!patch.IsDirty, "and the patch did not change");

        var after = patch.ToDump();
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i]) { True(false, $"byte {i} moved while handling a momentary parameter"); return; }
        True(true, "not one byte moved - an offset of -1 was never used as a position");

        // It still travels over MIDI; it simply has no home in the patch.
        Equal(61, solo.NrpnMsb, "it has an address of its own");
        Equal(127, ParameterCodec.ToWire(solo, 1), "and a full-scale on value");
    }

    static void AnInvertedParameterRunsBackwards()
    {
        var d = new ParameterDescriptor
        {
            Id = 480, Name = "VocalTune Speed", Min = 0, Max = 127,
            MidiStart = 0, MidiRange = 127, Inverted = true, Offset = 300,
        };
        Equal(127, ParameterCodec.ToWire(d, 0), "the bottom of the display is the top of the wire");
        Equal(0, ParameterCodec.ToWire(d, 127), "and the top is the bottom");
        for (int v = 0; v <= 127; v += 17)
            Equal(v, ParameterCodec.FromMidiByte(d, ParameterCodec.ToMidiByte(d, v)), $"inverted {v} round-trips");
    }

    static void FormattingPrefersLabelsThenUnits()
    {
        var fade = LfoFade();
        Equal("Off", ParameterCodec.Format(fade, 0), "an enumeration shows its label");
        Equal("FadeOut", ParameterCodec.Format(fade, 2), "at the right index");

        Equal("-12 dB", ParameterCodec.Format(PreFx(), -12), "a number carries its unit");
        Equal("0", ParameterCodec.Format(Semitone(), 0), "and shows bare when it has none");

        // A value outside the label list must not throw or show a neighbour's label.
        Equal("9", ParameterCodec.Format(fade, 9), "an out-of-range value falls back to the number");
    }
}
