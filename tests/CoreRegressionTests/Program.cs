using UltraNovaCtl.Core;

namespace UltraNovaCtl.CoreRegressionTests;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--monitor") return Monitor(args);
        if (args.Length == 2 && args[0] == "--validate") return ValidateFile(args[1]);
        if (args.Length >= 2 && args[0] == "--dump") return DumpPatch(args);
        if (args.Length >= 3 && args[0] == "--scan") return ScanBank(args);
        if (args.Length >= 1 && args[0] == "--echo")
            return EchoProbe(args.Length > 1 && args[1] == "local-off");
        if (args.Length >= 1 && args[0] == "--locks") return LockProbe();
        if (args.Length >= 3 && args[0] == "--watch") return WatchEdits(args);
        if (args.Length >= 1 && args[0] == "--editor") return EditorSession(args);

        (string name, Action run)[] tests =
        {
            ("mapping scale clamps and inverts", MappingScale),
            ("config round-trip keeps a backup", ConfigRoundTripAndBackup),
            ("strict import rejects malformed JSON", StrictImportRejectsMalformedJson),
            ("strict import rejects future schemas", StrictImportRejectsFutureSchema),
            ("startup restores the last good backup", StartupRestoresBackup),
            ("shipped pre-schema config remains compatible", ShippedConfigRemainsCompatible),
            ("short encoder arrays are repaired safely", ShortEncoderArrayIsRepaired),
            ("unknown send types are rejected", UnknownSendTypeIsRejected),
            ("a mapping is a reference key, not a value key", MappingIsAReferenceKey),
            ("each page owns its own mapping objects", PagesOwnTheirMappings),
            ("step positions spread across the range", StepPositionsSpread),
            ("latching state is reported only for toggle and step", LatchingStateIsModeSpecific),
            ("releasing a note clears its latch", ReleasingANoteClearsTheLatch),
            ("latches do not leak between pages", LatchesDoNotLeakBetweenPages),
            ("step advances through its positions and wraps", StepAdvancesAndWraps),
            ("nothing is reported delivered without an output", NothingIsDeliveredWithoutAnOutput),
            ("the analog path does not send under the state lock", TheAnalogPathDoesNotSendUnderTheStateLock),
            ("a held momentary switch is released on a page change", AHeldMomentarySwitchIsReleasedOnAPageChange),
            ("the instrument-port relay respects the setting and the native editor", KeyboardForwardingRespectsSettingAndNativeEditor),
            ("the lock probe reads without creating", TheLockProbeReadsWithoutCreating),
            ("a held switch keeps the route it was pressed on", AHeldSwitchKeepsTheRouteItWasPressedOn),
            ("factory-routed wheels are held back with the relay off", FactoryRoutedWheelsAreHeldBackWithTheRelayOff),
            ("latched lamps stay within the rail budget", LatchedLampsStayWithinTheRailBudget),
            ("activity lamps split hands from instrument", ActivityLampsSplitHandsFromInstrument),
            ("a touch pulse is a report, not a hand", ATouchPulseIsAReportNotAHand),
            ("patch commands have the right shape", PatchCommandsHaveTheRightShape),
            ("a dump is recognised by shape and header", ADumpIsRecognisedByShapeAndHeader),
            ("the checksum matches the verified algorithm", TheChecksumMatchesTheVerifiedAlgorithm),
            ("the patch name lives at table offset two", TheNameLivesAtTableOffsetTwo),
            ("table offsets count from byte thirteen", TableOffsetsCountFromByteThirteen),
            ("firmware version unpacks from the packed byte", FirmwareVersionUnpacksFromThePackedByte),
            ("a status reply carries Local Control", AStatusReplyCarriesLocalControl),
            ("the checksum reply is five seven-bit groups", TheChecksumReplyIsFiveSevenBitGroups),
            ("NRPN bytes reassemble into one parameter", NrpnBytesReassembleIntoOneParameter),
            ("patch selection is bank then slot", PatchSelectionIsBankThenSlotNotAWideValue),
            ("NRPN ignores what is not its own", NrpnIgnoresWhatIsNotItsOwn),
            ("a dump raises PatchReceived", ADumpRaisesPatchReceived),
            ("a status reply raises StatusReceived", AStatusReplyRaisesStatusReceived),
            ("SysEx that is neither is ignored", SysExThatIsNeitherIsIgnored),
            ("panel patch selection is reported as such", PanelPatchSelectionIsReportedAsSuch),
            ("an edited parameter is reported", AnEditedParameterIsReported),
            ("attaching without a connection fails quietly", AttachingWithoutAConnectionFailsQuietly),
            ("releasing notes without a connection is harmless", ReleaseWithoutConnectionIsHarmless),
            ("reopening outputs reports failure instead of throwing", ReopenOutputsReportsFailure),
            ("an unopened output is not usable", UnopenedOutputIsNotUsable),
            ("analog routing migration runs once, not every load", AnalogMigrationRunsOnce),
            ("a rejected config is preserved and named by reason", RejectedConfigIsPreservedAndNamedByReason),
            ("two refusals in one second both survive", TwoRefusalsInOneSecondBothSurvive),
            ("a refusal recovers the working file from backup", RefusalRecoversTheWorkingFileFromBackup),
        };

        int failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception e)
            {
                failed++;
                Console.Error.WriteLine("FAIL " + name + ": " + e.Message);
            }
        }

        Console.WriteLine($"{tests.Length - failed}/{tests.Length} regression checks passed");
        return failed == 0 ? 0 : 1;
    }

    static int ValidateFile(string path)
    {
        try
        {
            var config = Config.LoadStrict(path);
            int pages = config.Banks.Sum(bank => bank.Pages.Count);
            Console.WriteLine($"VALID schema={config.SchemaVersion} banks={config.Banks.Count} pages={pages}"
                + $" output={config.OutputPort}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("INVALID: " + e.Message);
            return 1;
        }
    }

    static int Monitor(string[] args)
    {
        int seconds = args.Length >= 3 && int.TryParse(args[2], out int parsed)
            ? Math.Clamp(parsed, 1, 300)
            : 30;
        using var input = new MidiIn();
        int received = 0;
        input.Received += (_, e) =>
        {
            Interlocked.Increment(ref received);
            string bytes = e.IsSysEx ? " | " + Convert.ToHexString(e.SysEx) : "";
            Console.WriteLine($"RX {e.Describe()}{bytes}");
        };
        if (!input.Open(args[1], out string error))
        {
            Console.Error.WriteLine("MONITOR FAILED: " + error);
            Console.Error.WriteLine("INPUTS: " + string.Join(", ", MidiIn.PortNames()));
            return 2;
        }

        Console.WriteLine($"LISTENING {input.PortName} for {seconds} seconds");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"MONITOR DONE: {received} message(s)");
        return received > 0 ? 0 : 3;
    }

    // ---- hardware capture ---------------------------------------------------
    //
    // Read-only: status, patch dump and checksum requests, nothing that writes. The
    // point is the checksum - the algorithm was read out of Interop.dll but there was no
    // ground truth for it anywhere in the project, and the device will state its own.
    //
    //   --dump <outDir> [bank] [prog]     bank 1..4 = A..D, prog 0..127
    //
    // Requests are the 14-byte form from research-patch-editor/agent-findings/
    // librarian-protocol.md section 2.1.

    static readonly byte[] SysExHeader = { 0xF0, 0x00, 0x20, 0x29, 0x03, 0x01, 0x7F };

    static byte[] Request(byte command, byte flags, byte bank = 0, byte prog = 0) =>
        new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x03, 0x01, 0x7F, command, flags, 0, 0, bank, prog, 0xF7 };

    static int DumpPatch(string[] args)
    {
        string outDir = args[1];
        int bank = args.Length > 2 && int.TryParse(args[2], out int b) ? b : 0;
        int prog = args.Length > 3 && int.TryParse(args[3], out int p) ? p : 0;
        Directory.CreateDirectory(outDir);

        var received = new List<byte[]>();
        using var input = new MidiIn();
        input.Received += (_, e) => { if (e.IsSysEx) lock (received) received.Add(e.SysEx); };
        if (!input.Open("UltraNova", out string inError))
        {
            Console.Error.WriteLine("input failed: " + inError);
            Console.Error.WriteLine("INPUTS: " + string.Join(", ", MidiIn.PortNames()));
            return 2;
        }
        using var output = new MidiOut();
        if (!output.Open("UltraNova"))
        {
            Console.Error.WriteLine("output failed: " + output.LastError);
            Console.Error.WriteLine("OUTPUTS: " + string.Join(", ", MidiOut.PortNames()));
            return 2;
        }
        Console.WriteLine($"in '{input.PortName}' / out '{output.PortName}'");

        byte[][] Ask(string what, byte[] request, int expectLength)
        {
            lock (received) received.Clear();
            Console.WriteLine($"-> {what}: {Convert.ToHexString(request)}");
            if (!output.SendRaw(request))
            {
                Console.Error.WriteLine("   send failed: " + output.LastError);
                return Array.Empty<byte[]>();
            }
            // The synth answers in milliseconds; the wait is for the SysEx to be
            // reassembled by the driver, and long enough that a slow reply is not
            // reported as silence.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 1500)
            {
                lock (received)
                    if (received.Any(m => m.Length == expectLength)) break;
                Thread.Sleep(10);
            }
            byte[][] answers;
            lock (received) answers = received.ToArray();
            foreach (var m in answers)
                Console.WriteLine($"<- {m.Length} bytes"
                    + (m.Length <= 24 ? " " + Convert.ToHexString(m) : $" cmd={m[7]:X2} ctrl={m[8]:X2}"));
            if (answers.Length == 0) Console.WriteLine("<- (nothing)");
            return answers;
        }

        Ask("device status", Request(0x60, 0x21), 14);

        var edit = Ask("edit-buffer patch", Request(0x40, 0x21), 526)
            .FirstOrDefault(m => m.Length == 526);
        if (edit != null)
        {
            string path = System.IO.Path.Combine(outDir, "edit-buffer.syx");
            File.WriteAllBytes(path, edit);
            Console.WriteLine($"   saved {path}");
            Describe(edit);
        }

        if (bank >= 1 && bank <= 4)
        {
            var stored = Ask($"stored patch bank {bank} prog {prog}",
                Request(0x41, 0x21, (byte)bank, (byte)prog), 526).FirstOrDefault(m => m.Length == 526);
            if (stored != null)
            {
                string path = System.IO.Path.Combine(outDir, $"bank{bank}-prog{prog:D3}.syx");
                File.WriteAllBytes(path, stored);
                Console.WriteLine($"   saved {path}");
                Describe(stored);
                if (edit != null)
                    Console.WriteLine("   payload matches the edit buffer: "
                        + stored.Skip(15).Take(510).SequenceEqual(edit.Skip(15).Take(510)));
            }

            var sum = Ask($"checksum bank {bank} prog {prog}",
                Request(0x41, 0x23, (byte)bank, (byte)prog), 19).FirstOrDefault(m => m.Length == 19);
            if (sum != null && stored != null)
            {
                // Five 7-bit groups, big-endian.
                long reported = 0;
                for (int i = 13; i <= 17; i++) reported = (reported << 7) | (long)(sum[i] & 0x7F);
                long computed = PatchChecksum(stored);
                Console.WriteLine($"   device reports 0x{reported:X8}");
                Console.WriteLine($"   computed      0x{computed:X8}");
                Console.WriteLine(reported == computed
                    ? "   CHECKSUM ALGORITHM CONFIRMED ON HARDWARE"
                    : "   MISMATCH - the documented algorithm is wrong or incompletely specified");
            }
        }
        return 0;
    }

    /// <summary>
    /// Check the parameter table against the instrument. Polls the edit buffer and, each
    /// time bytes change, says which offsets moved and what the table claims lives there -
    /// alongside whatever the instrument's own port sent in the meantime, so one turn of a
    /// knob checks both the byte offset and the MIDI address. Read-only.
    ///
    ///   --watch <outDir> <seconds> [layout-params.json]
    /// </summary>
    static int WatchEdits(string[] args)
    {
        string outDir = args[1];
        int seconds = args.Length > 2 && int.TryParse(args[2], out int sec) ? Math.Clamp(sec, 5, 900) : 120;
        string? tablePath = args.Length > 3 ? args[3] : null;
        Directory.CreateDirectory(outDir);
        using var log = new StreamWriter(System.IO.Path.Combine(outDir, "watch.log"), append: false) { AutoFlush = true };
        void Say(string t) { Console.WriteLine(t); log.WriteLine(t); }

        // What the table says lives at each offset. Table offsets count from the start
        // of the payload - message byte 13 - not from byte 0: the table is empty at 0..20,
        // which is exactly the name at message bytes 15..30 seen from byte 13, and its
        // last entry (510, Tweak 8 Select) lands on byte 523 with the payload ending at
        // 524. Counted from byte 0, Polyphony Mode at 21 would sit inside the name.
        var byOffset = new Dictionary<int, List<string>>();
        var byCc = new Dictionary<string, List<string>>();
        if (tablePath != null && File.Exists(tablePath))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(tablePath));
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                string name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                string off = "", cc = "";
                if (p.TryGetProperty("program", out var pr) && pr.ValueKind == System.Text.Json.JsonValueKind.Object
                    && pr.TryGetProperty("offset", out var o)) off = o.GetString() ?? "";
                if (p.TryGetProperty("midi", out var mi) && mi.ValueKind == System.Text.Json.JsonValueKind.Object
                    && mi.TryGetProperty("cc", out var c)) cc = c.GetString() ?? "";
                string desc = $"{name}  [table: offset {off}, midi {cc}]";
                if (int.TryParse(off, out int oi) && oi >= 0)
                {
                    if (!byOffset.TryGetValue(oi, out var l)) byOffset[oi] = l = new List<string>();
                    l.Add(desc);
                }
                if (cc.Length > 0)
                {
                    if (!byCc.TryGetValue(cc, out var l2)) byCc[cc] = l2 = new List<string>();
                    l2.Add(desc);
                }
            }
            Say($"table: {byOffset.Count} distinct offsets, {byCc.Count} distinct MIDI addresses");
        }

        var sysex = new List<byte[]>();
        var traffic = new List<string>();
        using var input = new MidiIn();
        input.Received += (_, e) =>
        {
            if (e.IsSysEx) { lock (sysex) sysex.Add(e.SysEx); return; }
            // The instrument streams channel aftertouch on its own; it would drown the log.
            if (e.Kind == 0xD0) return;
            lock (traffic) traffic.Add(e.Describe());
        };
        if (!input.Open("UltraNova", out string inError)) { Console.Error.WriteLine("input failed: " + inError); return 2; }
        using var output = new MidiOut();
        if (!output.Open("UltraNova")) { Console.Error.WriteLine("output failed: " + output.LastError); return 2; }
        Say($"in '{input.PortName}' / out '{output.PortName}'; watching for {seconds} s");

        byte[]? Fetch()
        {
            lock (sysex) sysex.Clear();
            if (!output.SendRaw(Request(0x40, 0x21))) return null;
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 900)
            {
                lock (sysex)
                {
                    var hit = sysex.FirstOrDefault(m => m.Length == 526 && m[7] == 0x00);
                    if (hit != null) return hit;
                }
                Thread.Sleep(8);
            }
            return null;
        }

        var baseline = Fetch();
        if (baseline == null) { Say("no edit buffer came back - is the instrument in SYNTH mode and the port free?"); return 3; }
        File.WriteAllBytes(System.IO.Path.Combine(outDir, "watch-000.syx"), baseline);
        Say($"baseline: '{System.Text.Encoding.ASCII.GetString(baseline, 15, 16).TrimEnd()}'  checksum 0x{PatchChecksum(baseline):X8}");
        Say("turn one knob at a time; each change is reported as it lands");
        Say("");

        int snapshot = 0, misses = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(1200);
            lock (traffic) traffic.Clear();
            var cur = Fetch();
            if (cur == null) { misses++; continue; }
            var changed = new List<int>();
            for (int i = 15; i < 525; i++) if (cur[i] != baseline[i]) changed.Add(i);
            if (changed.Count == 0) continue;

            snapshot++;
            File.WriteAllBytes(System.IO.Path.Combine(outDir, $"watch-{snapshot:D3}.syx"), cur);
            string[] seen;
            lock (traffic) seen = traffic.ToArray();
            Say($"--- change #{snapshot} at +{clock.Elapsed.TotalSeconds:F0} s: {changed.Count} byte(s) ---");
            foreach (int i in changed)
            {
                string claim = i < 15 ? "(header)"
                    : i <= 30 ? "(patch name)"
                    : i == 31 ? "(category)"
                    : i == 32 ? "(genre)"
                    : byOffset.TryGetValue(i - 13, out var l) ? string.Join(" | ", l)
                    : "(table has nothing here)";
                Say($"  byte {i,3} (table offset {i - 13,3}): {baseline[i],3} -> {cur[i],3}   {claim}");
            }
            if (seen.Length > 0)
            {
                Say("  port said: " + string.Join("; ", seen.Distinct().Take(6)));
            }
            else Say("  port said: (nothing besides aftertouch)");
            Say("");
            baseline = cur;
        }
        Say($"done: {snapshot} change(s), {misses} poll(s) unanswered");
        return 0;
    }

    /// <summary>
    /// Report which of Novation's own named locks are held right now. Read-only: each is
    /// opened, never created, so probing cannot make another process believe the hardware
    /// is taken.
    ///
    ///   --locks
    /// </summary>

    /// <summary>
    /// End-to-end check of the editor transport through the real engine: start it, attach
    /// on the private Port 1 pair, and print what the instrument sends back. This is the
    /// only way to exercise the reader hook, since the regression checks drive the sorting
    /// code directly and never touch a pin.
    ///
    /// Read-only towards the instrument: the attach sends transport framing plus a status
    /// and a dump request, and detach sends the matching transport disable. Nothing is
    /// written to the edit buffer or to memory.
    /// </summary>
    static int EditorSession(string[] args)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 30;

        using var engine = new AutomapEngine();
        engine.Log += (_, m) => Console.WriteLine("  " + m);

        int dumps = 0, parameters = 0, selections = 0, statuses = 0;
        engine.StatusReceived += (_, e) =>
        {
            statuses++;
            Console.WriteLine($"СТАТУС: прошивка {e.Version}, Local {(e.LocalOn ? "On" : "Off")}");
        };
        engine.PatchReceived += (_, e) =>
        {
            dumps++;
            string where = e.Bank == 0 && e.Program == 0
                ? "буфер редактирования" : $"банк {e.Bank} слот {e.Program}";
            Console.WriteLine($"ПАТЧ: '{e.Name}' из {where}, "
                + $"контрольная сумма 0x{PatchProtocol.Checksum(e.Data):X8}");
        };
        engine.PatchSelected += (_, e) =>
        {
            selections++;
            Console.WriteLine($"ВЫБРАН ПАТЧ: банк {e.Bank} слот {e.Program}");
        };
        engine.ParameterChanged += (_, e) =>
        {
            parameters++;
            if (parameters <= 40) Console.WriteLine("ПРАВКА: " + e);
        };

        if (!engine.Start())
        {
            Console.WriteLine("движок не стартовал - прибор подключён? наше приложение закрыто?");
            return 1;
        }

        if (!engine.AttachEditor())
        {
            Console.WriteLine("редактор не подключился");
            engine.Stop();
            return 2;
        }

        Console.WriteLine($"\nслушаю {seconds} с - покрути ручку и смени патч\n");
        Thread.Sleep(seconds * 1000);

        engine.DetachEditor();
        engine.Stop();

        Console.WriteLine($"\nИТОГО: статусов {statuses}, патчей {dumps}, "
            + $"смен патча {selections}, правок {parameters}");
        return statuses > 0 && dumps > 0 ? 0 : 3;
    }

    static int LockProbe()
    {
        foreach (var (name, meaning) in NativeLocks.Known)
        {
            bool held = NativeLocks.IsHeld(name);
            Console.WriteLine($"{(held ? "HELD    " : "free    ")} {name,-32} {meaning}");
        }
        return 0;
    }

    /// <summary>
    /// Does the instrument put MIDI it receives back onto its own output? That is the
    /// question behind a feedback loop: if it does, then forwarding the keyboard to a DAW
    /// which routes anything back to the instrument closes a circle, and no amount of
    /// careful DAW setup makes the forwarding safe on its own.
    ///
    /// Sends a controller (silent) and one short quiet note on the public WinMM pair and
    /// reports what comes back within a second.
    ///
    ///   --echo
    /// </summary>
    static int EchoProbe(bool withLocalOff)
    {
        var seen = new List<(long ms, string what)>();
        var sysex = new List<byte[]>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var input = new MidiIn();
        input.Received += (_, e) =>
        {
            if (e.IsSysEx) { lock (sysex) sysex.Add(e.SysEx); return; }
            lock (seen) seen.Add((clock.ElapsedMilliseconds, e.Describe()));
        };
        if (!input.Open("UltraNova", out string inError))
        {
            Console.Error.WriteLine("input failed: " + inError);
            return 2;
        }
        using var output = new MidiOut();
        if (!output.Open("UltraNova"))
        {
            Console.Error.WriteLine("output failed: " + output.LastError);
            return 2;
        }
        Console.WriteLine($"in '{input.PortName}' / out '{output.PortName}'");

        // Read the instrument's status and return the reply, or null.
        byte[]? Status(string label)
        {
            lock (sysex) sysex.Clear();
            output.SendRaw(Request(0x60, 0x21));
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 900)
            {
                lock (sysex)
                {
                    var hit = sysex.FirstOrDefault(m => m.Length is 14 or 15 && m[7] == 0x20);
                    if (hit != null)
                    {
                        Console.WriteLine($"   status {label}: {Convert.ToHexString(hit)}"
                            + $"  (byte 12 = 0x{hit[12]:X2})");
                        return hit;
                    }
                }
                Thread.Sleep(10);
            }
            Console.WriteLine($"   status {label}: no reply");
            return null;
        }

        // CC 122 is the standard channel-mode Local Control, and the instrument's own CC
        // list documents it. Sent on both channel 1 and 2 because the status reply's
        // channel field is 0x01 and it is not settled whether that counts from zero;
        // a channel-mode message on a channel the instrument ignores costs nothing.
        void SetLocal(bool on)
        {
            // Not the standard 0/127. Novation's own editor uses vendor values on the
            // standard Local Control controller: 33 turns Local off, 99 turns it back on
            // (UltraNova Editor 64.dll, rva 0x1AF0 and 0x1A70). Sending 0 and 127 moved
            // nothing on this instrument.
            byte v = (byte)(on ? 99 : 33);
            output.Send(0xB0, 122, v);
            output.Send(0xB1, 122, v);
            Thread.Sleep(400);
        }

        byte[]? before = null;
        if (withLocalOff)
        {
            Console.WriteLine("-- bracketing the probe with Local Off (CC 122 = 33) --");
            before = Status("before");
            SetLocal(false);
            Status("with Local Off");
        }

        (long ms, string what)[] Probe(string label, Action send)
        {
            lock (seen) seen.Clear();
            long t0 = clock.ElapsedMilliseconds;
            Console.WriteLine($"-> {label}");
            send();
            Thread.Sleep(900);
            (long, string)[] got;
            lock (seen) got = seen.ToArray();
            foreach (var (ms, what) in got) Console.WriteLine($"   <- +{ms - t0,4} ms  {what}");
            if (got.Length == 0) Console.WriteLine("   <- (nothing came back)");
            return got;
        }

        // Silent: a controller the instrument will not sound.
        var cc = Probe("CC 64 = 127 then 0 on channel 1", () =>
        {
            output.Send(0xB0, 64, 127);
            Thread.Sleep(120);
            output.Send(0xB0, 64, 0);
        });

        // Audible but brief and quiet, so the instrument's own reaction is obvious too.
        var note = Probe("Note C3 velocity 40, released after 120 ms", () =>
        {
            output.Send(0x90, 60, 40);
            Thread.Sleep(120);
            output.Send(0x80, 60, 0);
        });

        bool ccEcho = cc.Any(g => g.what.Contains("64", StringComparison.Ordinal));
        bool noteEcho = note.Any(g => g.what.Contains("60", StringComparison.Ordinal)
            || g.what.Contains("C3", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine();
        Console.WriteLine($"controller echoed back: {ccEcho}");
        Console.WriteLine($"note echoed back      : {noteEcho}");
        Console.WriteLine(ccEcho || noteEcho
            ? "THE INSTRUMENT PASSES RECEIVED MIDI TO ITS OWN OUTPUT - forwarding needs an echo guard"
            : "no thru path: a loop cannot be closed through the instrument itself");

        if (withLocalOff)
        {
            Console.WriteLine();
            // Put it back the way it was found, not blindly On: the owner may have had
            // Local off on purpose. Unknown before-state defaults to On - an audible
            // keyboard is the safer mistake.
            bool wasOn = before == null || (before[12] & 1) == 1;
            Console.WriteLine($"-- restoring Local {(wasOn ? "On (CC 122 = 99)" : "Off (CC 122 = 33)")} --");
            SetLocal(wasOn);
            var after = Status("after");
            if (before != null && after != null)
                Console.WriteLine(before[12] == after[12]
                    ? $"   byte 12 unchanged at 0x{after[12]:X2} - it is not the Local flag,"
                        + " or CC 122 was ignored"
                    : $"   byte 12 moved 0x{before[12]:X2} -> 0x{after[12]:X2} across the bracket");
        }
        return 0;
    }

    /// <summary>
    /// Walk every slot of one bank, asking each for its patch and its checksum. Read-only.
    /// Two things come out of it that one slot cannot give: 128 independent confirmations
    /// of the checksum algorithm, and - by matching the edit buffer against the slot
    /// payloads - what the number on the front panel actually means as a program index.
    ///
    ///   --scan <outDir> <bank>          bank 1..4 = A..D
    /// </summary>
    static int ScanBank(string[] args)
    {
        string outDir = args[1];
        if (!int.TryParse(args[2], out int bank) || bank < 1 || bank > 4)
        {
            Console.Error.WriteLine("bank must be 1..4 (A..D)");
            return 2;
        }
        Directory.CreateDirectory(outDir);

        var received = new List<byte[]>();
        using var input = new MidiIn();
        input.Received += (_, e) => { if (e.IsSysEx) lock (received) received.Add(e.SysEx); };
        if (!input.Open("UltraNova", out string inError))
        {
            Console.Error.WriteLine("input failed: " + inError);
            return 2;
        }
        using var output = new MidiOut();
        if (!output.Open("UltraNova"))
        {
            Console.Error.WriteLine("output failed: " + output.LastError);
            return 2;
        }

        byte[]? Ask(byte[] request, int expectLength, int waitMs)
        {
            lock (received) received.Clear();
            if (!output.SendRaw(request)) return null;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < waitMs)
            {
                lock (received)
                {
                    var hit = received.FirstOrDefault(m => m.Length == expectLength);
                    if (hit != null) return hit;
                }
                Thread.Sleep(4);
            }
            return null;
        }

        var edit = Ask(Request(0x40, 0x21), 526, 1500);
        string editName = edit != null
            ? System.Text.Encoding.ASCII.GetString(edit, 15, 16).TrimEnd()
            : "(edit buffer unavailable)";
        Console.WriteLine($"edit buffer: '{editName}'");

        char letter = (char)('A' + bank - 1);
        string all = System.IO.Path.Combine(outDir, $"Bank-{letter}-hardware.syx");
        using var bulk = new FileStream(all, FileMode.Create, FileAccess.Write);

        int confirmed = 0, mismatched = 0, missing = 0, matchesEdit = -1;
        for (int prog = 0; prog < 128; prog++)
        {
            var patch = Ask(Request(0x41, 0x21, (byte)bank, (byte)prog), 526, 600);
            if (patch == null) { missing++; Console.WriteLine($"{letter}{prog:D3}  (no reply)"); continue; }
            bulk.Write(patch);

            string name = System.Text.Encoding.ASCII.GetString(patch, 15, 16).TrimEnd();
            long computed = PatchChecksum(patch);

            var sum = Ask(Request(0x41, 0x23, (byte)bank, (byte)prog), 19, 600);
            string verdict;
            if (sum == null) { verdict = "no checksum reply"; missing++; }
            else
            {
                long reported = 0;
                for (int i = 13; i <= 17; i++) reported = (reported << 7) | (long)(sum[i] & 0x7F);
                if (reported == computed) { verdict = "ok"; confirmed++; }
                else { verdict = $"MISMATCH device 0x{reported:X8}"; mismatched++; }
            }

            bool same = edit != null && patch.Skip(15).Take(510).SequenceEqual(edit.Skip(15).Take(510));
            if (same && matchesEdit < 0) matchesEdit = prog;
            Console.WriteLine($"{letter}{prog:D3}  0x{computed:X8}  {verdict,-24}  {name}"
                + (same ? "   <== matches the edit buffer" : ""));
        }

        Console.WriteLine($"\nwrote {all}");
        Console.WriteLine($"checksums confirmed {confirmed}, mismatched {mismatched}, no reply {missing}");
        Console.WriteLine(matchesEdit >= 0
            ? $"the selected patch is program index {matchesEdit} in bank {letter}"
            : "no slot matched the edit buffer - the selection may have been edited since it was recalled");
        return mismatched == 0 ? 0 : 1;
    }

    /// <summary>
    /// The algorithm read out of Interop.dll: the 512 payload bytes at offsets 13..524
    /// as 128 big-endian 32-bit words, summed with end-around carry.
    /// </summary>
    static long PatchChecksum(byte[] patch)
    {
        long sum = 0;
        for (int i = 13; i + 3 <= 524; i += 4)
        {
            long word = ((long)patch[i] << 24) | ((long)patch[i + 1] << 16)
                | ((long)patch[i + 2] << 8) | patch[i + 3];
            sum += word;
            sum += sum >> 32;
            sum &= 0xFFFFFFFF;
        }
        return sum;
    }

    static void Describe(byte[] patch)
    {
        if (patch.Length != 526) return;
        string name = System.Text.Encoding.ASCII.GetString(patch, 15, 16).TrimEnd();
        Console.WriteLine($"   name '{name}' cmd={patch[7]:X2} ctrl={patch[8]:X2}"
            + $" ver={patch[9]:X2}.{patch[10]:X2} bank={patch[11]} prog={patch[12]}"
            + $" cat={patch[31] & 0x7F} genre={patch[32] & 0x7F}"
            + $" checksum=0x{PatchChecksum(patch):X8}");
    }

    static void MappingScale()
    {
        var normal = new Mapping { From = 20, To = 100, Mode = "normal" };
        Equal(20, normal.Scale(-1), "low clamp");
        Equal(100, normal.Scale(128), "high clamp");

        var inverted = new Mapping { From = 100, To = 20, Mode = "normal" };
        Equal(100, inverted.Scale(0), "inverted low endpoint");
        Equal(20, inverted.Scale(127), "inverted high endpoint");
    }

    static void ConfigRoundTripAndBackup()
    {
        using var temp = new TempDirectory();
        string path = temp.File("map.json");
        var config = Config.CreateDefault();
        config.OutputPort = "first";
        config.Save(path);
        config.OutputPort = "second";
        config.Save(path);
        config.OutputPort = "third";
        config.Save(path);

        Equal("third", Config.LoadStrict(path).OutputPort, "current file");
        Equal("second", Config.LoadStrict(path + ".bak").OutputPort, "backup file");
        Equal(0, Directory.GetFiles(temp.Path, "*.tmp").Length, "temporary files");
    }

    static void StrictImportRejectsMalformedJson()
    {
        using var temp = new TempDirectory();
        string path = temp.File("broken.json");
        File.WriteAllText(path, "{ this is not JSON");
        Throws<InvalidDataException>(() => Config.LoadStrict(path));
    }

    static void StrictImportRejectsFutureSchema()
    {
        using var temp = new TempDirectory();
        string path = temp.File("future.json");
        var config = Config.CreateDefault();
        config.SchemaVersion = Config.CurrentSchemaVersion + 1;
        config.Save(path);
        Throws<InvalidDataException>(() => Config.LoadStrict(path));
    }

    static void StartupRestoresBackup()
    {
        using var temp = new TempDirectory();
        string path = temp.File("working.json");
        var config = Config.CreateDefault();
        config.OutputPort = "last-good";
        config.Save(path);
        config.OutputPort = "newer";
        config.Save(path);
        File.WriteAllText(path, "{ broken");

        var recovered = Config.Load(path);
        Equal("last-good", recovered.OutputPort, "restored output port");
        True(Config.LastLoadWarning.Contains("recovered", StringComparison.OrdinalIgnoreCase),
            "recovery warning");
        Equal(1, Directory.GetFiles(temp.Path, "*.corrupt-*").Length, "quarantined files");
        True(File.Exists(path), "working file was recreated");
        Equal("last-good", Config.LoadStrict(path).OutputPort, "recreated working file");
    }

    static void ShippedConfigRemainsCompatible()
    {
        string path = Fixture("pre-schema-config.json");
        True(!File.ReadAllText(path).Contains("schemaVersion", StringComparison.Ordinal),
            "fixture should represent a pre-schema release");
        var loaded = Config.LoadStrict(path);
        True(loaded.Banks.Count > 0, "legacy banks");
        Equal(Config.CurrentSchemaVersion, loaded.SchemaVersion, "migrated schema");
    }

    static void ShortEncoderArrayIsRepaired()
    {
        using var temp = new TempDirectory();
        string path = temp.File("short.json");
        var config = Config.CreateDefault();
        var encoders = config.Banks[0].Pages[0].Encoders;
        Array.Resize(ref encoders, 8);
        config.Banks[0].Pages[0].Encoders = encoders;
        config.Save(path);

        var loaded = Config.LoadStrict(path);
        Equal(10, loaded.Banks[0].Pages[0].Encoders.Length, "encoder count");
        Equal("none", loaded.Banks[0].Pages[0].Encoders[9].Send, "new encoder is silent");
    }

    static void UnknownSendTypeIsRejected()
    {
        using var temp = new TempDirectory();
        string path = temp.File("unknown-send.json");
        var config = Config.CreateDefault();
        config.Banks[0].Pages[0].Encoders[0].Send = "surprise";
        config.Save(path);
        Throws<InvalidDataException>(() => Config.LoadStrict(path));
    }

    // ---- restored 1.2.1 engine behaviour -----------------------------------
    //
    // The engine itself needs the instrument, so what is checked here is the part that
    // does not: the invariants the panel logic rests on, and the public surface the GUI
    // calls. Everything that touches a pin or a port stays for the hardware pass.

    /// <summary>
    /// Per-page switch state is a Dictionary keyed by the Mapping object, so the whole
    /// feature rests on Mapping using reference identity. Turning it into a record, or
    /// adding an Equals overload, would silently merge two pages' toggles back together
    /// - which is the bug that keying by button code caused in the first place.
    /// </summary>
    static void MappingIsAReferenceKey()
    {
        var one = new Mapping { Send = "cc", Channel = 1, Number = 21, Mode = "toggle" };
        var two = new Mapping { Send = "cc", Channel = 1, Number = 21, Mode = "toggle" };
        var states = new Dictionary<Mapping, bool> { [one] = true, [two] = false };
        Equal(2, states.Count, "identical mappings must be distinct keys");
        Equal(true, states[one], "first mapping keeps its own state");
        Equal(false, states[two], "second mapping keeps its own state");
    }

    static void PagesOwnTheirMappings()
    {
        var config = Config.CreateDefault();
        var bank = config.Banks[0];
        bank.Pages.Add(Config.NewPage(bank.Name, 2));
        var first = bank.Pages[0];
        var second = bank.Pages[1];

        True(!ReferenceEquals(first.Encoders[0], second.Encoders[0]),
            "encoder mappings are not shared between pages");
        foreach (var code in first.Buttons.Keys)
            if (second.Buttons.TryGetValue(code, out var other))
                True(!ReferenceEquals(first.Buttons[code], other),
                    $"button {code} is not shared between pages");
    }

    static void StepPositionsSpread()
    {
        var m = new Mapping { From = 0, To = 127, Points = 5 };
        Equal(0, m.StepValue(0), "first position");
        Equal(32, m.StepValue(1), "second position");
        Equal(64, m.StepValue(2), "third position");
        Equal(95, m.StepValue(3), "fourth position");
        Equal(127, m.StepValue(4), "last position");
        Equal(0, m.StepValue(5), "wraps to the first position");
        Equal(127, m.StepValue(-1), "negative index wraps to the last position");
    }

    /// <summary>
    /// What the GUI asks the engine in order to draw ON/OFF on a button tile. Only
    /// toggle and step have a state that stands; anything else has nothing to show.
    /// </summary>
    static void LatchingStateIsModeSpecific()
    {
        using var engine = new AutomapEngine();

        var toggle = new Mapping { Send = "cc", Mode = "toggle", From = 10, To = 110 };
        True(engine.TryGetPersistentSwitchState(toggle, out bool on, out int value), "toggle latches");
        Equal(false, on, "a fresh toggle is off");
        Equal(10, value, "an off toggle reads its Release value");

        var step = new Mapping { Send = "cc", Mode = "step", From = 0, To = 127, Points = 4 };
        True(engine.TryGetPersistentSwitchState(step, out on, out value), "step latches");
        Equal(false, on, "a fresh step is at its first position");
        Equal(0, value, "first step position");

        foreach (string mode in new[] { "momentary", "normal", "flash" })
            True(!engine.TryGetPersistentSwitchState(
                    new Mapping { Send = "cc", Mode = mode }, out _, out _),
                mode + " has no standing state");

        foreach (string send in new[] { "key", "transport", "none" })
            True(!engine.TryGetPersistentSwitchState(
                    new Mapping { Send = send, Mode = "toggle" }, out _, out _),
                send + " has no standing state");

        True(!engine.TryGetPersistentSwitchState(null, out _, out _), "null mapping");
    }

    /// <summary>
    /// Drives the same entry point the read thread uses, so the latch is genuinely up
    /// before it is released. Asserting on a fresh mapping proved nothing: the state is
    /// absent, so it reads as off whatever ReleaseNote does.
    /// </summary>
    static void ReleasingANoteClearsTheLatch()
    {
        using var engine = new AutomapEngine();
        var note = new Mapping { Send = "note", Mode = "toggle", Number = 60, From = 0, To = 127 };

        Equal(127, engine.SendSwitch(note, pressed: true), "press latches the switch on");
        True(engine.TryGetPersistentSwitchState(note, out bool on, out int value), "latching mapping");
        Equal(true, on, "the latch is up before the release");
        Equal(127, value, "and reads its Press value");

        engine.ReleaseNote(note);
        True(engine.TryGetPersistentSwitchState(note, out on, out value), "still a latching mapping");
        Equal(false, on, "the latch is down after a release");
        Equal(0, value, "and reads its Release value again");
    }

    /// <summary>
    /// The point of keying by mapping object: two pages, same button code, independent
    /// latches. Keyed by code, the second page would report the first page's position.
    /// </summary>
    static void LatchesDoNotLeakBetweenPages()
    {
        using var engine = new AutomapEngine();
        var onPageOne = new Mapping { Send = "cc", Mode = "toggle", Number = 21, From = 0, To = 127 };
        var onPageTwo = new Mapping { Send = "cc", Mode = "toggle", Number = 21, From = 0, To = 127 };

        engine.SendSwitch(onPageOne, pressed: true);
        engine.TryGetPersistentSwitchState(onPageOne, out bool first, out _);
        engine.TryGetPersistentSwitchState(onPageTwo, out bool second, out _);
        Equal(true, first, "the pressed switch is on");
        Equal(false, second, "the same button on another page is untouched");

        engine.ReleaseAllNotes();
        engine.TryGetPersistentSwitchState(onPageOne, out first, out _);
        Equal(true, first, "a cc toggle is not a note and keeps its latch");
    }

    static void StepAdvancesAndWraps()
    {
        using var engine = new AutomapEngine();
        var step = new Mapping { Send = "cc", Mode = "step", Number = 22, From = 0, To = 127, Points = 4 };

        // Four points across 0..127 sit 42.33 apart, rounded: 0, 42, 85, 127.
        Equal(42, engine.SendSwitch(step, pressed: true), "second position");
        Equal(85, engine.SendSwitch(step, pressed: true), "third position");
        Equal(127, engine.SendSwitch(step, pressed: true), "fourth position");
        Equal(0, engine.SendSwitch(step, pressed: true), "wraps to the first position");
        Equal(null, engine.SendSwitch(step, pressed: false), "the release does nothing");

        True(engine.TryGetPersistentSwitchState(step, out bool active, out int value), "step latches");
        Equal(false, active, "back at the first position, so dark");
        Equal(0, value, "and reads that position's value");
    }

    /// <summary>
    /// With no output open, nothing can have been delivered. This is the contract the
    /// note bookkeeping rests on - a Note On that never left must not be remembered as
    /// owing a release - and it used to report success merely because the call to a
    /// broken port did not throw.
    /// </summary>
    static void NothingIsDeliveredWithoutAnOutput()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";
        engine.ReopenOutputs();

        var note = new Mapping { Send = "note", Mode = "momentary", Number = 64, From = 0, To = 100 };
        Equal(100, engine.SendSwitch(note, pressed: true), "the switch still resolves its value");
        Equal(false, engine.OutputReady, "but no output is open");

        // Nothing to release, and asking must not throw or invent a Note Off.
        engine.ReleaseAllNotes();
        engine.ReleaseNote(note);

        using var output = new MidiOut();
        Equal(false, output.Send(0xB0, 21, 127), "a short message on a closed port reports failure");
        Equal(false, output.SendRaw(new byte[] { 0xF0, 0x00, 0x20, 0x29, 0xF7 }),
            "a SysEx on a closed port reports failure");
        Equal(false, output.SendRaw(Array.Empty<byte>()), "an empty message reports failure");
        Equal(0L, output.Sent, "and nothing was counted as sent");
    }

    /// <summary>
    /// The analog path must not hold _analogLock while it sends, because a send reaches
    /// the window's own handlers and the UI thread takes _analogLock from the other side.
    /// Two threads, two locks, opposite order - the application hung.
    ///
    /// Reproduced here without a GUI: park the read thread inside a handler that the send
    /// path invokes, then check from another thread that a method needing _analogLock
    /// still returns. Before the fix this test hangs on that check and fails by timeout.
    /// </summary>
    static void TheAnalogPathDoesNotSendUnderTheStateLock()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";

        // Sustain is the analog switch, so this takes the SendSwitch path; a transport
        // mapping makes that path log, which is the hook this test parks in.
        const int sustain = 4;
        engine.Config.Banks[0].Pages[0].Analog[sustain.ToString()] =
            new Mapping { Send = "transport", TransportCommand = "mmc-play", Mode = "momentary" };

        using var inHandler = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        engine.Log += (_, __) => { inHandler.Set(); release.Wait(3000); };

        var reader = new Thread(() => engine.OnAnalog(sustain, 127)) { IsBackground = true };
        reader.Start();
        try
        {
            True(inHandler.Wait(3000), "the send path reported through Log");

            // The read thread is now parked inside that handler, mid-send.
            var probe = new Thread(() => engine.AnalogPhysical(sustain)) { IsBackground = true };
            probe.Start();
            True(probe.Join(1500), "_analogLock was free while the send path was in a handler");
        }
        finally
        {
            release.Set();
            reader.Join(3000);
        }
    }

    /// <summary>
    /// A momentary switch asserts its value only while the control is physically down.
    /// A page change takes the mapping away while the pedal stays put, so the released
    /// value has to be sent - found on the instrument, where holding the sustain pedal
    /// across an Automap page change left CC 64 latched on for good.
    /// </summary>
    static void AHeldMomentarySwitchIsReleasedOnAPageChange()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";

        var pedal = new Mapping { Send = "cc", Channel = 1, Number = 64, Mode = "momentary", From = 0, To = 127 };
        Equal(127, engine.SendSwitch(pedal, pressed: true), "the pedal asserts its pressed value");
        Equal(1, engine.ReleaseHeldSwitches(), "one held switch was released");
        Equal(0, engine.ReleaseHeldSwitches(), "and only once");

        // Letting go by hand also clears it, so a page change afterwards has nothing to do.
        Equal(0, engine.SendSwitch(pedal, pressed: false), "releasing sends the released value");
        Equal(0, engine.ReleaseHeldSwitches(), "nothing left held");

        // Latches are not transient state: a toggle is meant to outlive the page.
        var toggle = new Mapping { Send = "cc", Number = 21, Mode = "toggle", From = 0, To = 127 };
        engine.SendSwitch(toggle, pressed: true);
        Equal(0, engine.ReleaseHeldSwitches(), "a toggle is a latch and is left alone");
        engine.TryGetPersistentSwitchState(toggle, out bool stillOn, out _);
        Equal(true, stillOn, "and stays on");

        // A note mapping is owned by the note bookkeeping, not counted twice here.
        var note = new Mapping { Send = "note", Number = 60, Mode = "momentary", From = 0, To = 100 };
        engine.SendSwitch(note, pressed: true);
        Equal(0, engine.ReleaseHeldSwitches(), "notes are released by ReleaseAllNotes");
    }

    /// <summary>
    /// Keyboard forwarding is a setting, and it also stands aside for Novation's own
    /// editor: that editor takes Local off and expects the DAW to listen to the
    /// instrument's own port, so forwarding on top of it doubles every note.
    /// </summary>
    static void KeyboardForwardingRespectsSettingAndNativeEditor()
    {
        using var engine = new AutomapEngine();

        Equal(true, engine.Config.ForwardKeyboardNotes, "forwarding is on by default");
        Equal(true, engine.Config.PauseForwardingForNativeEditor, "and pauses for the editor");

        Equal(true, engine.InstrumentPortRelayActive(nativeEditorOpen: false), "nothing in the way");
        Equal(false, engine.InstrumentPortRelayActive(nativeEditorOpen: true), "editor has it");

        engine.Config.PauseForwardingForNativeEditor = false;
        Equal(true, engine.InstrumentPortRelayActive(nativeEditorOpen: true),
            "the pause can be turned off for a DAW fed only from here");

        engine.Config.ForwardKeyboardNotes = false;
        Equal(false, engine.InstrumentPortRelayActive(nativeEditorOpen: false),
            "the setting wins whatever the editor is doing");
        Equal(false, engine.InstrumentPortRelayActive(nativeEditorOpen: true), "and both together");
    }

    /// <summary>
    /// The lock probe reads names and never creates them: creating one would make the
    /// next native instance believe the hardware is taken, which is the opposite of the
    /// point. Checked against a name this test owns, so no real application is disturbed.
    /// </summary>
    static void TheLockProbeReadsWithoutCreating()
    {
        string name = "UltraNovaCtl-test-" + Guid.NewGuid().ToString("N");
        Equal(false, NativeLocks.IsHeld(name), "an unheld name reads as free");

        using (var held = new Mutex(true, name))
            Equal(true, NativeLocks.IsHeld(name), "a held name reads as held");

        Equal(false, NativeLocks.IsHeld(name), "and is free again once released");

        // The probe itself must not have brought the name into existence.
        Equal(false, NativeLocks.IsHeld(name), "probing did not create it");
        Equal(false, NativeLocks.IsHeld(""), "an empty name is not a lock");
        Equal(false, NativeLocks.IsHeld(null), "nor is nothing at all");
    }

    /// <summary>
    /// More than thirteen lit lamps sags the instrument's power rail - the Demo path has
    /// always respected that ceiling, but the latched-switch path did not, so latching
    /// fourteen buttons on one page would have lit them all.
    /// </summary>
    static void LatchedLampsStayWithinTheRailBudget()
    {
        var wanted = Enumerable.Range(20, 25).ToArray();       // 25 codes, 20..44
        var shown = AutomapEngine.LampsToShow(wanted);

        Equal(PanelLamps.MaxAtOnce, shown.Length, "never more lamps than the rail allows");
        Equal(13, PanelLamps.MaxAtOnce, "the ceiling is thirteen");
        Equal(20, shown[0], "lowest code first, so the choice is stable");
        Equal(32, shown[^1], "and contiguous from there");

        // Under the ceiling nothing is dropped, and the order still holds.
        var few = new[] { 30, 21, 25 };
        var all = AutomapEngine.LampsToShow(few);
        Equal(3, all.Length, "nothing dropped below the ceiling");
        Equal(21, all[0], "sorted");
        Equal(30, all[^1], "sorted");

        Equal(0, AutomapEngine.LampsToShow(Array.Empty<int>()).Length, "nothing wanted, nothing lit");
    }

    /// <summary>
    /// A held momentary switch remembers the route it was pressed on. The user can change
    /// the assignment's channel or number while the pedal is still down; the release must
    /// reach the old route, or that control stays asserted with nothing left to clear it.
    /// Found by review, not on the instrument - the same family as the stuck sustain.
    /// </summary>
    static void AHeldSwitchKeepsTheRouteItWasPressedOn()
    {
        using var engine = new AutomapEngine();
        var pedal = new Mapping { Send = "cc", Channel = 1, Number = 64, Mode = "momentary", From = 0, To = 127 };
        engine.SendSwitch(pedal, pressed: true);

        pedal.Channel = 9;
        pedal.Number = 80;

        var route = engine.HeldRouteOf(pedal);
        True(route != null, "still held after the edit");
        Equal(1, route!.Channel, "release goes to the channel it was pressed on");
        Equal(64, route.Number, "and to the number it was pressed on");

        engine.ReleaseNote(pedal);                       // what the GUI calls on a routing edit
        True(engine.HeldRouteOf(pedal) == null, "the edit released it");
        Equal(0, engine.ReleaseHeldSwitches(), "and nothing is left for the page change");
    }

    /// <summary>
    /// With the relay off, a wheel or pedal on its factory route is a duplicate of what the
    /// DAW already gets from the instrument's own port and is held back - whichever of the
    /// two internal paths delivered it. An assignment the user changed is not a duplicate
    /// and keeps going. The first cut gated only one of the two paths, which changed
    /// almost nothing: the other path is the one that normally wins.
    /// </summary>
    static void FactoryRoutedWheelsAreHeldBackWithTheRelayOff()
    {
        var (mod, _, modCc) = Config.AnalogControls[0];
        var factory = new Mapping { Send = Config.AnalogSendKind(mod), Channel = 1, Number = modCc, Mode = "normal" };
        var custom  = new Mapping { Send = "cc", Channel = 1, Number = 74, Mode = "normal" };
        var otherCh = new Mapping { Send = Config.AnalogSendKind(mod), Channel = 2, Number = modCc, Mode = "normal" };

        True(Config.IsFactoryAnalogRoute(mod, factory), "the default assignment is the factory route");
        True(!Config.IsFactoryAnalogRoute(mod, custom), "another CC is not");
        True(!Config.IsFactoryAnalogRoute(mod, otherCh), "another channel is not");
        True(!Config.IsFactoryAnalogRoute(mod, new Mapping { Send = "none" }), "silent is not");

        Equal(true,  AutomapEngine.AnalogSendSuppressed(mod, factory, relayActive: false), "factory route, relay off: held back");
        Equal(false, AutomapEngine.AnalogSendSuppressed(mod, factory, relayActive: true),  "factory route, relay on: goes");
        Equal(false, AutomapEngine.AnalogSendSuppressed(mod, custom,  relayActive: false), "custom route, relay off: still goes");

        // Sustain's factory route is a CC too, so the pedal follows the same rule.
        var (sus, _, susCc) = Config.AnalogControls[3];
        var pedal = new Mapping { Send = "cc", Channel = 1, Number = susCc, Mode = "momentary" };
        Equal(true, AutomapEngine.AnalogSendSuppressed(sus, pedal, relayActive: false), "factory sustain, relay off: held back");
    }

    /// <summary>
    /// The Keys and Synth lamps share one wire, so the split is by message. Hands are
    /// notes, pressure, wheels and pedals; the instrument speaking for itself is SysEx,
    /// NRPN and other controllers, and program changes.
    /// </summary>
    static void ActivityLampsSplitHandsFromInstrument()
    {
        var K = MidiActivity.Source.Keys; var S = MidiActivity.Source.Synth;
        Equal(K, MidiActivity.ClassifyPort(0x90, 60), "note on");
        Equal(K, MidiActivity.ClassifyPort(0x81, 60), "note off, any channel");
        Equal(K, MidiActivity.ClassifyPort(0xA0, 60), "polyphonic pressure");
        Equal(K, MidiActivity.ClassifyPort(0xD1, 40), "channel pressure");
        Equal(K, MidiActivity.ClassifyPort(0xE0, 0),  "pitch bend");
        Equal(K, MidiActivity.ClassifyPort(0xB0, 1),  "mod wheel");
        Equal(K, MidiActivity.ClassifyPort(0xB0, 11), "expression");
        Equal(K, MidiActivity.ClassifyPort(0xB0, 64), "sustain");
        Equal(S, MidiActivity.ClassifyPort(0xF0, 0),  "SysEx");
        Equal(S, MidiActivity.ClassifyPort(0xB1, 99), "NRPN MSB is the instrument talking");
        Equal(S, MidiActivity.ClassifyPort(0xB1, 6),  "data entry too");
        Equal(S, MidiActivity.ClassifyPort(0xC0, 5),  "program change");
        Equal(S, MidiActivity.ClassifyPort(0xB0, 74), "a controller that is not a performance control");

        var act = new MidiActivity();
        Equal(false, act.Lit(K), "nothing has moved");
        Equal(0L, act.Count(K), "and nothing is counted");
        act.Touch(K);
        Equal(true, act.Lit(K), "lit right after a touch");
        Equal(1L, act.Count(K), "counted once");
        Equal(false, act.Lit(S), "the other lamp is unaffected");
    }

    /// <summary>
    /// In AUTOMAP mode the instrument reports a parameter edited from elsewhere - the
    /// native editor over its own transport - as a touch-on/touch-off pair in one packet
    /// on the encoder the parameter sits under. That must not fire the encoder's Touch
    /// assignment, or every edit in the plug-in reaches the DAW as a press. A hand, which
    /// holds for longer than the hold time, still does.
    /// </summary>
    static void ATouchPulseIsAReportNotAHand()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";
        var page = engine.CurrentPage;
        var touch = new Mapping { Send = "cc", Channel = 1, Number = 90, Mode = "momentary", From = 0, To = 127 };
        page.Touch["2"] = touch;

        // The instrument's report: on and off with nothing in between.
        engine.OnTouch(2, true);
        engine.OnTouch(2, false);
        Thread.Sleep(AutomapEngine.TouchHoldMs * 3);
        True(engine.HeldRouteOf(touch) == null, "a pulse asserted nothing");
        Equal(0, engine.ReleaseHeldSwitches(), "and left nothing to release");

        // A hand: touched, held past the hold time, then let go.
        engine.OnTouch(2, true);
        Thread.Sleep(AutomapEngine.TouchHoldMs * 3);
        True(engine.HeldRouteOf(touch) != null, "a held touch is a press");
        engine.OnTouch(2, false);
        True(engine.HeldRouteOf(touch) == null, "and letting go releases it");
    }


    // ---- patch protocol ----------------------------------------------------
    //
    // These are pure byte functions, so they need no instrument. The checksum vector
    // below is synthetic on purpose: the algorithm itself was verified against the
    // instrument's own answers for 508 of 512 slots, and the four that differed were
    // reproduced exactly by the genre-bit rule, but none of that patch data belongs in
    // this repository. What these checks defend is that the C# implementation still
    // computes what that verified algorithm computes.

    /// <summary>A synthetic dump with a deterministic payload, valid header and terminator.</summary>
    static byte[] SyntheticDump()
    {
        var d = new byte[PatchProtocol.DumpLength];
        byte[] head = { 0xF0, 0x00, 0x20, 0x29, 0x03, 0x01, 0x7F };
        head.CopyTo(d, 0);
        d[7] = PatchProtocol.CmdDumpReply;
        d[9] = 0x10;                                   // firmware 2.0.00
        for (int i = 13; i <= 524; i++) d[i] = (byte)((i * 7) & 0x7F);
        d[PatchProtocol.DumpLength - 1] = 0xF7;
        return d;
    }

    static void PatchCommandsHaveTheRightShape()
    {
        var m = PatchProtocol.Command(PatchProtocol.CmdStored, PatchProtocol.CtrlChecksum, 2, 78);
        Equal(PatchProtocol.CommandLength, m.Length, "command length");
        Equal((byte)0xF0, m[0], "starts with SysEx");
        Equal((byte)0x29, m[3], "manufacturer byte");
        Equal(PatchProtocol.CmdStored, m[7], "command byte");
        Equal(PatchProtocol.CtrlChecksum, m[8], "control byte");
        Equal((byte)2, m[11], "bank");
        Equal((byte)78, m[12], "program");
        Equal((byte)0xF7, m[13], "ends with EOX");

        bool threw = false;
        try { PatchProtocol.Command(0x40, 0, 0, 200); }
        catch (ArgumentOutOfRangeException) { threw = true; }
        True(threw, "a program number above 127 is refused rather than truncated");
    }

    static void ADumpIsRecognisedByShapeAndHeader()
    {
        var d = SyntheticDump();
        True(PatchProtocol.IsDump(d), "a well-formed dump is recognised");

        var shorter = new byte[d.Length - 1];
        Array.Copy(d, shorter, shorter.Length);
        shorter[shorter.Length - 1] = 0xF7;
        True(!PatchProtocol.IsDump(shorter), "a dump of the wrong length is refused");

        var alien = (byte[])d.Clone();
        alien[3] = 0x2A;                                // someone else's manufacturer
        True(!PatchProtocol.IsDump(alien), "another maker's SysEx is refused");

        var unterminated = (byte[])d.Clone();
        unterminated[unterminated.Length - 1] = 0x00;
        True(!PatchProtocol.IsDump(unterminated), "a message with no terminator is refused");

        True(!PatchProtocol.IsDump(null), "null is refused rather than throwing");
    }

    static void TheChecksumMatchesTheVerifiedAlgorithm()
    {
        var d = SyntheticDump();
        Equal(0xA01F9F20u, PatchProtocol.Checksum(d), "checksum of the known vector");
        Equal(0xA01F9FA0u, PatchProtocol.Checksum(d, setGenreHighBit: true),
              "the genre bit adds exactly 0x80");

        var moved = (byte[])d.Clone();
        moved[300] ^= 0x01;
        True(PatchProtocol.Checksum(moved) != PatchProtocol.Checksum(d),
             "one changed payload byte changes the checksum");

        // Bytes outside 13..524 are not covered, so touching them must not move it.
        var outside = (byte[])d.Clone();
        outside[12] = 77;
        Equal(PatchProtocol.Checksum(d), PatchProtocol.Checksum(outside),
              "the header is outside the checksummed payload");
    }

    static void TheNameLivesAtTableOffsetTwo()
    {
        var d = SyntheticDump();
        const string wanted = "Poly Pad";
        for (int i = 0; i < PatchProtocol.NameLength; i++)
        {
            char c = i < wanted.Length ? wanted[i] : ' ';
            d[PatchProtocol.MessageIndex(PatchProtocol.NameOffset + i)] = (byte)c;
        }
        Equal(wanted, PatchProtocol.NameOf(d), "the name reads back with blanks trimmed");
        Equal(15, PatchProtocol.MessageIndex(PatchProtocol.NameOffset), "the name starts at byte 15");
    }

    static void TableOffsetsCountFromByteThirteen()
    {
        Equal(13, PatchProtocol.MessageIndex(0), "offset zero is byte 13");
        Equal(92, PatchProtocol.MessageIndex(79), "Filter1 Frequency at offset 79 is byte 92");

        var d = SyntheticDump();
        d[PatchProtocol.MessageIndex(79)] = 64;
        True(PatchProtocol.TryValue(d, 79, out byte v), "a parameter inside the payload is readable");
        Equal((byte)64, v, "and reads back what was written");
        True(!PatchProtocol.TryValue(d, 5000, out _), "an offset past the payload is refused");
        True(!PatchProtocol.TryValue(d, -1, out _), "a negative offset is refused");
    }

    static void FirmwareVersionUnpacksFromThePackedByte()
    {
        var v20 = PatchProtocol.Firmware(0x10, 0x00);
        Equal(2, v20.Major, "major");
        Equal(0, v20.Minor, "minor");
        Equal(0, v20.Build, "build");

        var other = PatchProtocol.Firmware(0x0B, 0x05);
        Equal(1, other.Major, "0x0B is major 1");
        Equal(3, other.Minor, "and minor 3");
        Equal(5, other.Build, "and build 5");
    }

    static void AStatusReplyCarriesLocalControl()
    {
        True(PatchProtocol.IsStatusReply(StatusReply(1)), "a status reply is recognised");
        True(PatchProtocol.LocalOn(StatusReply(1)), "byte 12 of one means Local is on");
        True(!PatchProtocol.LocalOn(StatusReply(0)), "byte 12 of zero means Local is off");
        True(!PatchProtocol.IsStatusReply(SyntheticDump()), "a patch dump is not a status reply");

        var fw = PatchProtocol.SenderFirmware(StatusReply(1));
        Equal(2, fw.Major, "the reply reports the instrument's own firmware");
        Equal(0, fw.Minor, "minor of the reported firmware");
    }

    static byte[] StatusReply(byte local) => new byte[]
    { 0xF0, 0x00, 0x20, 0x29, 0x03, 0x01, 0x7F, 0x20, 0x00, 0x10, 0x00, 0x00, local, 0x01, 0xF7 };

    static void TheChecksumReplyIsFiveSevenBitGroups()
    {
        const uint value = 0x062F1492;
        var m = new byte[19];
        byte[] head = { 0xF0, 0x00, 0x20, 0x29, 0x03, 0x01, 0x7F };
        head.CopyTo(m, 0);
        long v = value;
        for (int i = 13; i <= 17; i++) { m[i] = (byte)(v & 0x7F); v >>= 7; }
        m[18] = 0xF7;

        True(PatchProtocol.TryReadChecksumReply(m, out uint got), "the reply decodes");
        Equal(value, got, "five seven-bit groups reassemble the instrument's value");

        m[15] = 0xFF;                                   // not seven-bit clean
        True(!PatchProtocol.TryReadChecksumReply(m, out _), "a byte with bit 7 set is refused");
    }

    static void NrpnBytesReassembleIntoOneParameter()
    {
        var r = new NrpnReader();
        True(!r.Feed(0xB1, 99, 62, out _), "selecting the parameter reports nothing yet");
        True(!r.Feed(0xB1, 98, 0, out _), "nor does the second half");
        True(r.Feed(0xB1, 6, 7, out var n), "the data byte completes it");
        Equal(62, n.Msb, "parameter high half");
        Equal(0, n.Lsb, "parameter low half");
        Equal((byte)7, n.Data, "value");
        Equal(2, n.Channel, "channel is reported one-based");
        True(!n.HasLsb, "with no second data byte");

        // The instrument sends several values against one selected parameter.
        True(r.Feed(0xB1, 6, 9, out var again), "a second value needs no reselection");
        Equal((byte)9, again.Data, "and carries the new value");
    }

    static void PatchSelectionIsBankThenSlotNotAWideValue()
    {
        var r = new NrpnReader();
        r.Feed(0xB1, 99, 63, out _);
        r.Feed(0xB1, 98, 1, out _);
        r.Feed(0xB1, 6, 3, out _);
        True(r.Feed(0xB1, 38, 79, out var n), "the second data byte completes the announcement");
        True(NrpnReader.IsPatchSelect(n), "it is recognised as patch selection");
        Equal((byte)3, n.Data, "Data Entry MSB is the bank");
        Equal((byte)79, n.DataLsb, "Data Entry LSB is the slot");
        True(n.Wide != 79, "reading the pair as one wide value would be wrong here");
    }

    static void NrpnIgnoresWhatIsNotItsOwn()
    {
        var r = new NrpnReader();
        True(!r.Feed(0xB1, 6, 5, out _), "data entry with no parameter selected is ignored");
        True(!r.Feed(0x91, 60, 100, out _), "a note is not an NRPN");
        True(!r.Feed(0xB1, 74, 90, out _), "an ordinary controller is not an NRPN");

        r.Feed(0xB1, 99, 62, out _);
        r.Feed(0xB1, 98, 0, out _);
        r.Reset();
        True(!r.Feed(0xB1, 6, 5, out _), "a reset forgets the selected parameter");
    }


    // ---- editor transport --------------------------------------------------
    //
    // The instrument's side of this was measured: on the private Port 1 pair a dump
    // answers a request, a status reply carries firmware and Local Control, and the panel
    // announces both parameter edits and patch selection on channel 2. These checks drive
    // the same sorting code with those exact message shapes, so the wiring can be trusted
    // without an instrument on the desk.

    static void ADumpRaisesPatchReceived()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";

        var d = SyntheticDump();
        d[11] = 3; d[12] = 79;
        const string wanted = "Poly Pad";
        for (int i = 0; i < PatchProtocol.NameLength; i++)
            d[PatchProtocol.MessageIndex(PatchProtocol.NameOffset + i)] =
                (byte)(i < wanted.Length ? wanted[i] : ' ');

        PatchEventArgs? seen = null;
        engine.PatchReceived += (_, e) => seen = e;
        engine.DispatchEditorSysEx(d);

        True(seen != null, "a dump is reported");
        Equal(wanted, seen!.Name, "with its name");
        Equal(3, seen!.Bank, "and its bank");
        Equal(79, seen!.Program, "and its slot");
        Equal(PatchProtocol.DumpLength, seen!.Data.Length, "and the whole message is kept");
    }

    static void AStatusReplyRaisesStatusReceived()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";

        StatusEventArgs? seen = null;
        engine.StatusReceived += (_, e) => seen = e;
        engine.DispatchEditorSysEx(StatusReply(1));

        True(seen != null, "a status reply is reported");
        True(seen!.LocalOn, "Local Control is read from it");
        Equal("2.0.00", seen!.Version, "and the firmware version");

        engine.DispatchEditorSysEx(StatusReply(0));
        True(!seen!.LocalOn, "Local off is read too");
    }

    static void SysExThatIsNeitherIsIgnored()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";

        int patches = 0, statuses = 0;
        engine.PatchReceived += (_, _) => patches++;
        engine.StatusReceived += (_, _) => statuses++;

        engine.DispatchEditorSysEx(new byte[] { 0xF0, 0x7E, 0x00, 0x06, 0x01, 0xF7 });
        engine.DispatchEditorSysEx(new byte[] { 0xF0, 0x00, 0x01, 0xF7 });
        Equal(0, patches, "an unrelated SysEx is not a patch");
        Equal(0, statuses, "nor a status reply");
    }

    static void PanelPatchSelectionIsReportedAsSuch()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";

        PatchSelectedEventArgs? picked = null;
        int parameters = 0;
        engine.PatchSelected += (_, e) => picked = e;
        engine.ParameterChanged += (_, _) => parameters++;

        // Exactly what the instrument sent when a patch was chosen on the panel.
        engine.DispatchEditorChannelMessage(0xB1, 99, 63);
        engine.DispatchEditorChannelMessage(0xB1, 98, 1);
        engine.DispatchEditorChannelMessage(0xB1, 6, 3);
        engine.DispatchEditorChannelMessage(0xB1, 38, 79);

        True(picked != null, "patch selection is reported");
        Equal(3, picked!.Bank, "bank comes from the Data Entry MSB");
        Equal(79, picked!.Program, "slot comes from the Data Entry LSB");
        Equal(0, parameters, "and it is not also reported as a parameter edit");
    }

    static void AnEditedParameterIsReported()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";

        var seen = new List<ParameterEventArgs>();
        engine.ParameterChanged += (_, e) => seen.Add(e);

        // Filter1 Frequency under the hand, as captured from the panel.
        engine.DispatchEditorChannelMessage(0xB1, 74, 90);
        engine.DispatchEditorChannelMessage(0xB1, 74, 94);
        Equal(2, seen.Count, "each controller value is reported");
        True(!seen[0].IsNrpn, "as a plain controller");
        Equal(74, seen[0].Controller, "with its number");
        Equal(94, seen[1].Value, "and its value");
        Equal(2, seen[0].Channel, "on channel 2, one-based");

        // An NRPN edit: the four carrier controllers must not also count as parameters.
        seen.Clear();
        engine.DispatchEditorChannelMessage(0xB1, 99, 62);
        engine.DispatchEditorChannelMessage(0xB1, 98, 0);
        engine.DispatchEditorChannelMessage(0xB1, 6, 7);
        Equal(1, seen.Count, "an NRPN is one event, not three");
        True(seen[0].IsNrpn, "reported as an NRPN");
        Equal(62, seen[0].Msb, "with its parameter number");
        Equal(7, seen[0].Value, "and its value");
    }

    static void AttachingWithoutAConnectionFailsQuietly()
    {
        using var engine = new AutomapEngine();
        engine.Config.OutputPort = "";
        True(!engine.EditorAttached, "a fresh engine is not attached");
        True(!engine.AttachEditor(), "attaching without a connection is refused");
        True(!engine.EditorAttached, "and leaves the flag alone");
        engine.DetachEditor();
        True(!engine.RequestPatch(), "requests go nowhere rather than throwing");
    }

    static void ReleaseWithoutConnectionIsHarmless()
    {
        using var engine = new AutomapEngine();
        engine.ReleaseAllNotes();
        engine.ReleaseNote(null);
        engine.RefreshPersistentButtonLeds();
        engine.RefreshPersistentButtonLeds(force: true);
        Equal(false, engine.OutputReady, "no output is open");
    }

    /// <summary>
    /// ReopenOutputs returns bool - the GUI watchdog branches on it. It used to return
    /// void, which is the single error that was not a missing member.
    /// </summary>
    static void ReopenOutputsReportsFailure()
    {
        using var engine = new AutomapEngine();

        engine.Config.OutputPort = "";
        Equal(false, engine.ReopenOutputs(), "nothing configured");
        Equal(false, engine.OutputReady, "nothing open");

        // A name no MIDI port can contain, so this is the same answer on any machine.
        engine.Config.OutputPort = "no-such-midi-port-a7f3c1";
        Equal(false, engine.ReopenOutputs(), "configured but absent");
        Equal(false, engine.OutputReady, "still nothing open");
        Equal(false, engine.SendTest(), "a test send reports no output rather than throwing");
    }

    static void UnopenedOutputIsNotUsable()
    {
        using var output = new MidiOut();
        Equal(false, output.IsUsable, "never opened");
        output.Send(0xB0, 21, 127);
        output.SendRaw(new byte[] { 0xF0, 0x00, 0x20, 0x29, 0xF7 });
        Equal(0L, output.Sent, "nothing was counted as sent");
        Equal(false, output.IsUsable, "still not usable");
    }

    /// <summary>
    /// The analog routing migration turns a factory row that is still silent into a real
    /// send. It has to run for files written before it existed and then stop: it used to
    /// run on every load, so a mod wheel silenced on purpose came back at every start.
    /// </summary>
    static void AnalogMigrationRunsOnce()
    {
        using var temp = new TempDirectory();
        var (code, _, cc) = Config.AnalogControls[0];
        string key = code.ToString();

        // A file from before the flag existed: the migration must run.
        string old = temp.File("old.json");
        var before = Config.CreateDefault();
        before.AnalogFactorySendsRepaired = false;
        var row = before.Banks[0].Pages[0].Analog[key];
        row.Send = "none";
        row.Channel = 1;
        row.Number = cc;
        before.Save(old);

        var migrated = Config.LoadStrict(old);
        Equal(Config.AnalogSendKind(code), migrated.Banks[0].Pages[0].Analog[key].Send,
            "a pre-flag file is migrated");
        Equal(true, migrated.AnalogFactorySendsRepaired, "and is marked as migrated");

        // Silenced deliberately, after the migration: it must stay silent.
        string chosen = temp.File("chosen.json");
        migrated.Banks[0].Pages[0].Analog[key].Send = "none";
        migrated.Save(chosen);
        Equal("none", Config.LoadStrict(chosen).Banks[0].Pages[0].Analog[key].Send,
            "a deliberate silence survives a reload");
    }

    /// <summary>
    /// A file we refuse to load is always moved aside and always preserved, and the name
    /// says which kind of refusal it was. Leaving it at its own path looked kinder but
    /// lost it: the session then runs on defaults, and the first autosave rotates those
    /// rejected bytes into `.bak` while a second drops them entirely.
    /// </summary>
    static void RejectedConfigIsPreservedAndNamedByReason()
    {
        using var temp = new TempDirectory();

        // Parses, fails validation: kept as .rejected-*, because it is a hand edit with
        // a mistake in it rather than damage.
        string path = temp.File("semantic.json");
        var config = Config.CreateDefault();
        config.Banks[0].Pages[0].Encoders[0].Send = "surprise";
        config.Save(path);
        string written = File.ReadAllText(path);

        Config.Load(path);
        var rejected = Directory.GetFiles(temp.Path, "semantic.json.rejected-*");
        Equal(1, rejected.Length, "the invalid file was set aside as rejected");
        Equal(0, Directory.GetFiles(temp.Path, "semantic.json.corrupt-*").Length,
            "and not labelled as damaged");
        Equal(written, File.ReadAllText(rejected[0]), "preserved byte for byte");
        True(!File.Exists(path),
            "and moved off its own path, so an autosave cannot rotate it into the backup");

        // Not JSON at all: kept as .corrupt-*.
        string broken = temp.File("broken.json");
        File.WriteAllText(broken, "{ not json");
        Config.Load(broken);
        Equal(1, Directory.GetFiles(temp.Path, "broken.json.corrupt-*").Length,
            "unparseable bytes are labelled as damaged");
    }

    /// <summary>
    /// Two refusals inside the same second used to collide: the stamp is only to the
    /// second, File.Move threw onto the existing name, and the exception was swallowed -
    /// losing the very file the call exists to preserve.
    /// </summary>
    static void TwoRefusalsInOneSecondBothSurvive()
    {
        using var temp = new TempDirectory();
        string path = temp.File("settings.json");

        File.WriteAllText(path, "{ first broken file");
        Config.Load(path);
        File.WriteAllText(path, "{ second broken file");
        Config.Load(path);

        var kept = Directory.GetFiles(temp.Path, "settings.json.corrupt-*");
        Equal(2, kept.Length, "both refusals were preserved");
        var contents = kept.Select(File.ReadAllText).OrderBy(t => t).ToArray();
        Equal("{ first broken file", contents[0], "first file intact");
        Equal("{ second broken file", contents[1], "second file intact");
    }

    /// <summary>
    /// The working file is recreated from the backup in both refusal cases, so the next
    /// start does not fail on the same bytes and warn for ever.
    /// </summary>
    static void RefusalRecoversTheWorkingFileFromBackup()
    {
        using var temp = new TempDirectory();
        string path = temp.File("working.json");
        var config = Config.CreateDefault();
        config.OutputPort = "last-good";
        config.Save(path);
        config.OutputPort = "newer";
        config.Save(path);

        // A hand edit that parses but does not validate.
        var invalid = Config.LoadStrict(path);
        invalid.Banks[0].Pages[0].Encoders[0].Send = "surprise";
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(invalid,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            }));

        var recovered = Config.Load(path);
        Equal("last-good", recovered.OutputPort, "recovered from the backup");
        True(File.Exists(path), "the working file was recreated");
        Equal("last-good", Config.LoadStrict(path).OutputPort, "and is loadable");
        Equal(1, Directory.GetFiles(temp.Path, "working.json.rejected-*").Length,
            "the hand edit is still on disk to fix");
    }

    static string Fixture(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    static void True(bool value, string context)
    {
        if (!value) throw new InvalidOperationException(context + ": expected true");
    }

    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"expected {typeof(T).Name}, got {e.GetType().Name}", e);
        }
        throw new InvalidOperationException($"expected {typeof(T).Name}, no exception was thrown");
    }

    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "UltraNovaCtl-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
