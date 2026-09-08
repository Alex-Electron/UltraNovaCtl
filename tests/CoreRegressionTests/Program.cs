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

        byte[] Ask(byte[] request, int expectLength, int waitMs)
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
