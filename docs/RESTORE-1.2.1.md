# Recovering the 1.2.1 engine from its own build output

On 2026-09-07 the work in `src/Core/AutomapEngine.cs` was lost to a file-level revert.
The GUI that depended on it was not reverted, so the solution stopped compiling. This is
the record of how that was diagnosed and undone, kept because the technique generalises:
**a build directory is a backup, and a decompiler turns it back into a specification.**

## 1. What the damage looked like

Eight compiler errors, all in one file:

| error | site | missing |
|---|---|---|
| CS1061 ×2 | `MainWindow.axaml.cs:235`, `:1458` | `RefreshPersistentButtonLeds` |
| CS1061 | `:333` | `event MidiSent` |
| CS1061 | `:1432` | `ReleaseNote` |
| CS1061 | `:1569` | `TryGetPersistentSwitchState` |
| CS1061 | `:1601` | `OutputReady` |
| CS1061 | `:1764` | `ReleaseAllNotes` |
| CS0029 | `:1605` | `ReopenOutputs` returned `void`, used as `bool` |

Six members absent, one signature wrong. Seven edits, eight diagnostics.

## 2. Why "no local changes" was the wrong conclusion

`git status` called `src/Core/AutomapEngine.cs` unmodified while its mtime was newer than
the last commit. Both facts were true: git compares content, not time, and the content had
been overwritten with the content of the tag.

```
git hash-object src/Core/AutomapEngine.cs   -> ee8bddc3b8b01b5a386046935187ec86a587bbbf
git rev-parse HEAD:src/Core/AutomapEngine.cs -> ee8bddc3b8b01b5a386046935187ec86a587bbbf
```

Identical blobs, mtime 21:37:10, four minutes *after* a successful build at 21:33. So the
file had been written, compiled, and then replaced by the 1.2.0 version. `.git/logs/HEAD`
held only commit entries — HEAD never moved — so this was a working-tree `restore` or a
copy over the top, and git has no record of either.

**The reflog cannot help with a file-level revert.** What can is the build output.

## 3. Where the code survived

`UltraNovaCtl.Core.dll` is built into two places, and the linker had already written both
before the source was replaced:

- `src/Gui/bin/Debug/net8.0/UltraNovaCtl.Core.dll` — 07-09 21:33, **survived**
- `src/Gui/bin/Release/net8.0/UltraNovaCtl.Core.dll` — 07-09 21:35, **destroyed**

The Release copy was removed by a diagnostic `dotnet build -c Release` the next day: the
GUI failed to compile, MSBuild's `IncrementalClean` ran, and the copied reference
assemblies went with it. Of thirteen `UltraNovaCtl.Core.dll` files in the tree, exactly
one still contained the missing members.

> **Rule that follows from this.** When source is missing and a build directory may hold
> it, copy the assembly out of the tree *before* building anything. A failing build is not
> a read-only operation.

Both files are now kept at `research-patch-editor/rescue-1.2.1/`.

## 4. Turning the assembly back into a specification

`ilspycmd` was already installed (`~/.dotnet/tools/ilspycmd`, 9.0.0.7889).

Decompiling the rescued assembly alone gives working code, but pasting it into the project
would be a bad trade: no comments, compiler-shaped control flow, generated identifiers.
The trick is a **differential decompilation** — decompile the rescued assembly *and* an
assembly built from the current tree, then diff the two. Decompiler noise is identical on
both sides and cancels out; what remains is exactly the lost work.

```sh
ilspycmd -p -o <out>/rescued  research-patch-editor/rescue-1.2.1/UltraNovaCtl.Core.dll
dotnet build src/Core/UltraNovaCtl.Core.csproj -c Debug
ilspycmd -p -o <out>/current  src/Core/bin/Debug/net8.0/UltraNovaCtl.Core.dll
diff -rq <out>/current/UltraNovaCtl.Core <out>/rescued/UltraNovaCtl.Core
```

Use the same configuration on both sides — Debug against Debug — or optimisation
differences swamp the signal.

That `diff -rq` answered a question worth more than the diff itself: of 25 decompiled
modules **only `AutomapEngine.cs` differed.** `Config.cs`, `MidiOut.cs`, `MidiIn.cs`,
`Ks.cs` and the rest were byte-identical, which proves the rescued assembly was built from
the *current* uncommitted sources plus a newer engine. Restoring one file therefore yields
a coherent tree, not a hybrid of two eras. Without that check, the restoration would have
been a guess.

The per-file diff came to 611 added and 154 removed lines: the specification.

## 5. Writing it back

The restoration was typed by hand, in the file's own style, from that diff — not pasted.
Comments explain why each piece exists, which decompiled output cannot recover, and two
places were deliberately written differently:

- the twice-inlined keyboard-pin condition became one `IsKeyboardPin` helper;
- `ClosePin` uses `Ks.KSSTATE_PAUSE, KSSTATE_ACQUIRE, KSSTATE_STOP` where the decompiler
  printed `{ 2u, 1u, 0u }`.

What came back, by theme:

**Per-page switch state.** `_toggles` and `_steps` are keyed by the `Mapping` object
instead of the button code, so a toggle on page two no longer inherits where the same
button stands on page one. This rests on `Mapping` using reference identity — a regression
check guards that, because making it a `record` would silently merge pages back together.

**Panel lamps that tell the truth.** `_persistentLedCodes`, `TryGetPersistentSwitchState`
and `RefreshPersistentButtonLeds(force)` light latched switches and put out the ones a page
change left lit. Called at all five points where the panel is repainted.

**Notes that get released.** `_activeNotes` (per mapping) and `_forwardedNotes` (keyboard
pass-through) with `SendTrackedNote`, `ReleaseNote`, `ReleaseAllNotes` and
`ReleaseForwardedNotes`, hooked into page change, bank change, leaving Automap, reopening
outputs and `Stop`. Release velocity is taken from what was actually sent, so editing an
assignment under a held button cannot strand a note.

**The keyboard reaching the DAW.** `ForwardKeyboardMidi` passes Note On/Off and
polyphonic pressure to the outputs; wheels and pedals stay on the assignment path so they
are not sent twice.

**Coexistence.** The keyboard reader now prefers the private vendor Port 1 pin
(`KSDATAFORMAT_SUBTYPE_NOVATION_PORT1`, pin 6) and falls back to the public pin only when
it is absent, saying which in the log. Pins 4 and 8 are what `wdmaud` publishes as the
system `UltraNova` device; taking them shuts out DAWs and Novation's own software.

**Shutdown that lets go.** `Stop` cancels the pending overlapped reads with `CancelIoEx`
before joining the threads — closing a handle under a thread parked in the driver is what
used to hang — and `ClosePin` walks each pin back down the state machine before closing.

**Output plumbing.** `_outsLock`, `SendToOutputs` (reports whether anything received the
send, which the note bookkeeping needs), `OutputReady`, `bool OpenOutputs`,
`CloseOutputs`, deduplicated open failures, and `MidiSent` for the debug view.

## 6. Verifying the restoration

The same technique verifies its own result. Decompile the rebuilt assembly and diff it
against the rescued one:

```sh
diff -u <out>/rescued/UltraNovaCtl.Core/AutomapEngine.cs \
        <out>/restored/UltraNovaCtl.Core/AutomapEngine.cs
```

Every remaining hunk was member ordering, local-variable names, or an equivalent shape of
the same control flow — `if/else` against a ternary, an early `return` against a flag,
split string concatenation. No behavioural difference. Each hunk was read individually
rather than counted.

Then:

- `dotnet build UltraNovaCtl.sln -c Release` — succeeded, 0 errors (the two `CS9057`
  analyzer-version warnings are pre-existing and harmless).
- `dotnet run --project tests/CoreRegressionTests -c Release --no-build` — 18/18 passed.

## 7. Deliberately not the same as the rescued artefact

These are new code, so the rescued assembly is no help in reviewing them — see the
CHANGELOG entries.

1. **`MidiOut`** — a timed-out SysEx no longer falls through to `Sent++`/`PublishSent`;
   `Send` and `SendRaw` report whether the driver took the message, so `SendToOutputs`
   can stop treating "did not throw" as delivery; `Open` on a failed handle closes and
   re-opens it instead of returning success; `CloseHandleLocked` forgets buffers the
   driver still owns rather than carrying them to a future handle, where they would be
   unprepared against the wrong one.
2. **`Config`** — a file we refuse to load is moved aside and preserved under a name that
   says why (`.corrupt-` / `.rejected-`), and the timestamp no longer collides when two
   refusals land in the same second; the analog routing migration runs once per file,
   recorded by `AnalogFactorySendsRepaired`.
3. **Tests** — the fixture moved from the ignored `dist/` into
   `tests/CoreRegressionTests/fixtures/` and ships with the binary; fifteen checks added,
   and `SendSwitch` is `internal` with `InternalsVisibleTo` so the latch and note paths
   are driven through the same entry point the read thread uses.

### What the adversarial review changed

Four independent reviews ran over this restoration — fidelity against the rescued
artefact, concurrency, note and latch logic, and the new fixes — each finding then handed
to a separate agent told to refute it. Fidelity came back clean. Three findings about the
new code were confirmed and are fixed above; two are worth recording as method:

- **The first version of the `Config` change was wrong.** It left a file that parses but
  fails validation at its own path, on the theory that renaming it aside "throws away the
  user's work". It does not — the old code renamed it to `.corrupt-<stamp>`, where it
  survives. Leaving it in place is what loses it: the session runs on defaults or the
  backup, and `Save` rotates the working file into `.bak`, so two autosaves later the hand
  edit is gone with no copy anywhere. The fix keeps the real improvement (a name that
  distinguishes damage from a mistake) and drops the regression.
- **Two of the added checks proved nothing.** `ReleasingANoteClearsTheLatch` asserted that
  a latch was down after `ReleaseNote` — but it read a mapping that had never been
  pressed, so the state was absent and read as down whatever the method did; it passed
  with the body emptied. And no check touched the `MidiOut` fix at all: reverting it left
  the suite green. That is the standard to hold new checks to — revert the fix and watch
  the check fail. Done here by mutation: breaking the `Config` fix drops the suite to
  21/23, restoring it returns 23/23.

## 8. The lock-order inversion the review found — fixed

The concurrency review found, and an independent refutation attempt confirmed, a
**lock-order inversion that hangs the whole application**. It was not a defect of the
restoration — the rescued artefact has the identical structure, so it had been in the
1.2.1 work from the start — and it would have been met at the bench rather than here.

```
read thread   OnAnalog holds _analogLock  ──►  SendMapped ──► SendToOutputs (_outsLock)
                                               ──► MidiOut.Send ──► PublishSent
                                               ──► MidiSent handler ──► Enqueue (GUI _lock)

UI thread     ReloadPage holds GUI _lock  ──►  AutomapEngine.AnalogPhysical (_analogLock)
```

Classic ABBA: two threads, two locks, opposite order. Reachable with Debug tools on, by
moving a wheel or pedal while pressing a bank or page button on the panel — the read
thread waits on the GUI lock while the UI thread waits on `_analogLock`, and neither
returns. `Say(...)` from a `transport` or `key` mapping reaches the same GUI lock, so the
debug view is not the only door.

The fix is in `OnAnalog`: `_analogLock` guards `_analogRaw`, `_analogPick` and `_values`,
not the sending. What to send is now resolved inside the lock and sent after it is
released, so nothing that can call out of the class is reached while it is held.
`HoldOrCatchAnalog` changed with it — the reading that finally catches the page value now
returns false and lets the caller send, instead of sending from inside the lock itself.

Both read threads can reach the analog path (wheels ride the Automap stream *and* Port 1),
so the sends are serialised by a separate `_analogSendLock`. That one is deliberately not
`_analogLock`: nothing outside `OnAnalog` takes it and the UI never takes it at all, so it
cannot be one side of a cycle the way `_analogLock` was.

A regression check covers it — `the analog path does not send under the state lock`. It
parks the read thread inside a handler the send path invokes, then asserts from another
thread that a method needing `_analogLock` still returns. Verified by mutation: putting the
send back under `_analogLock` fails exactly that check (23/24), restoring it gives 24/24.

## 9. What the hardware pass still has to answer

Nothing in section 6 touches a pin or a port. Two of the fixes above also remain
unguarded by any check, because both need a driver that misbehaves: the SysEx timeout
branch in `MidiOut.SendRaw`, and re-opening a handle that has already failed. Their
surrounding contract is covered — a closed port reports failure and counts nothing — but
the failure branches themselves are bench-only. These need the instrument:

- Keyboard notes arrive in the DAW through loopMIDI, with the public WinMM `UltraNova`
  device still openable by the native Editor/Librarian at the same time — the point of
  preferring pin 6.
- The log says `private port, WinMM free` rather than `standard MIDI`.
- Latched buttons keep their lamps across a page change, and lit lamps stay within the
  13-lamp rail budget.
- No hanging note after a page change, a bank change, leaving Automap, or quitting.
- Exit does not reset the MIDI filter: inputs already open in a DAW survive our shutdown.
- One known cost, unresolved: `MidiOut.SendRaw` waits for `MHDR_DONE` for up to
  `LongMessageTimeoutMs` (5 s) while holding both its own `_sync` and the caller's
  `_outsLock`. On the SysEx path — a transport-mapped button — a wedged virtual port can
  therefore stall the KS read thread for seconds. Restructuring that lock was left alone
  on purpose: the buffer-lifetime handling it protects is exactly what this release fixed,
  and it cannot be re-verified without the instrument. Measure it on the bench first.
