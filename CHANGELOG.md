# Changelog

## 1.2.2 — 2026-09-09

Tried on the instrument before release: with the relay unticked, the mod wheel on its
factory route no longer reaches the output, while the same wheel reassigned to another
CC still does.

- The keyboard-and-wheels relay switch now does what its label says. The first cut gated
  only the Port 1 copy of the wheels, and the Automap stream - the copy that normally wins -
  went on regardless, so unticking it changed almost nothing for wheels. The rule is now
  applied to the assignment rather than the path: with the relay off, a wheel or pedal on
  its factory route is held back whichever path delivered it, and an assignment the user
  changed is never held back, because it is not something the instrument's own port sends.
- A momentary switch held down while its assignment is edited is released on the route it
  was pressed on. The held state remembered only the release value, so changing the
  channel or number under a held pedal sent the release to the new route and left the old
  control asserted for good - the same family as the stuck sustain, found by review. The
  GUI now releases whatever a mapping asserts on any routing change, not only when leaving
  a note.
- The `--echo local-off` probe says which values it actually sends (33 and 99, not 0 and
  127) and puts Local back the way it found it instead of always on.

## 1.2.1 — 2026-09-09

Core work dates from 2026-09-01/02. The engine half of it was lost to a file-level
revert on 2026-09-07 and restored on 2026-09-08 from the surviving compiled assembly;
see `docs/RESTORE-1.2.1.md`.

Tried on the instrument before release: Automap connects and hands the panel over;
the private Port 1 pin is taken with the public WinMM pair left free for the native
editor; keyboard notes reach the DAW; a held sustain pedal is released on a page
change; latched buttons keep their lamps across a page change; forwarding pauses by
itself while Novation's editor holds the instrument and resumes when it lets go; and
quitting does not disturb MIDI inputs a DAW already has open.

- Settings are written through a flushed temporary file and atomically replaced. The
  previous valid file is retained as `.bak`.
- A damaged working configuration is quarantined instead of silently discarded. Startup
  recovers the backup, recreates the working file, and reports what happened in the log.
- JSON imports are strict: malformed, structurally invalid, or newer-schema files no
  longer replace the current map with factory defaults.
- WinMM SysEx buffers stay alive until the driver completes and releases them. Output
  sends, port rescans, and disposal are serialised to avoid races between reader and UI threads.
- WinMM input SysEx buffers are no longer returned to the allocator while callbacks may
  still own them, including during input rescan and shutdown.
- A virtual MIDI output that is still busy during a fast application restart is retried
  automatically. Failed/stale WinMM output handles are also closed and recovered without
  reopening USB or requiring the Reinit button.
- Repeated MIDI rescans no longer reopen an unchanged output or stall encoder feedback.
- Debug tools now logs outgoing encoder and control messages, including their encoded
  CC, Note, Bend, NRPN/RPN, CC14, Program, Aftertouch, and SysEx values.
- Raw MIDI traffic from the synth's ordinary port, including the NRPN burst emitted by
  SYNTH/AUTOMAP mode changes, is now shown only while Debug tools is enabled.
- Keyboard notes, velocity, and polyphonic key pressure are forwarded from the UltraNova
  to the selected virtual MIDI output. The DAW can now use loopMIDI as its only input and
  avoid the Novation WinMM handle that becomes invalid when an Automap host restarts.
- The keyboard listener prefers the private vendor Port 1 pin (pin 6, SubFormat
  7b80f763-4fdb-4168-9e5e-24b8f695acbb) over the standard MIDI pin (pin 4), keeping the
  public WinMM "UltraNova" device completely free for DAWs and official Novation software.
- Mod wheel, pitch bend, expression, sustain, and channel aftertouch use that same route
  and remain visible/configurable in the WHEELS & PEDALS section.
- Shutdown now cancels pending Kernel Streaming reads and moves each Automap pin through
  PAUSE, ACQUIRE, and STOP before closing it. This prevents the Novation driver from
  resetting the entire MIDI filter and stranding every input already open in a DAW.
- Toggle and Step state is kept per mapping/page rather than leaking between pages, and
  the UI now restores that state with a persistent illuminated button when pages change.
  Note mappings emit a real Note Off on release even when the configured release velocity is nonzero.
- Note buttons expose their values as Attack/Release velocity and offer only Momentary
  and Toggle. Active mapped notes are released before page, mapping, output, mode, or
  shutdown changes so a Note On cannot be left hanging in the destination.
- Removed a developer-machine probe path, obsolete Avalonia properties, and platform
  analyzer warnings under the application's control.
- Added zero-dependency core regression checks and made them part of CI. Their fixture
  now travels with the test binary instead of being read out of the ignored `dist/`
  folder, so the step also passes on a clean checkout.
- The check runner gained hardware capture modes: `--dump` and `--scan` request patch
  dumps and checksums over the instrument's public WinMM port, `--echo` asks whether the
  instrument passes received MIDI back to its own output, and `--locks` reports which of
  Novation's named locks are held. They confirmed the patch checksum algorithm against
  the device itself for 508 of 512 slots; the write-up is
  [docs/PATCH-PROTOCOL.ru.md](docs/PATCH-PROTOCOL.ru.md).
- A SysEx send that times out is no longer counted as sent. The send counter and the
  outgoing MIDI log used to record it as success, which made a dead virtual port look
  healthy to everything upstream.
- Re-opening an output whose handle had already failed now really re-opens it. `Open`
  returned success for the dead handle, so `IsUsable` stayed false for the life of the
  object and the automatic recovery above never took effect.
- A configuration we refuse to load is preserved under a name that says why: `.corrupt-`
  for bytes that are not JSON, `.rejected-` for a file that parses but fails validation -
  a hand edit with a mistake in it, worth renaming back and fixing. Both are moved off
  the working path, because leaving one there let the first autosave rotate those bytes
  into `.bak` and a second drop them entirely. Two refusals inside the same second no
  longer collide on the timestamp and lose one of the files.
- Fixed a lock-order inversion that could hang the application. The wheel and pedal path
  held the lock guarding its own state while sending, and a send reaches the window's log
  and outgoing-MIDI handlers, which take the window's lock; the UI thread takes them the
  other way round when it repaints a page. Moving a wheel while pressing a bank or page
  button could stop both threads for good. What to send is now decided under the lock and
  sent after it is released.
- A send reports whether the driver actually took it, and `SendToOutputs` believes that
  answer rather than the absence of an exception. An output that is open but broken now
  counts as a failure, so a Note On that never left is not remembered as owing a release,
  and the Test button no longer claims success into a dead port. Multi-message sends
  (CC14, NRPN/RPN) count as delivered only if every part landed.
- The one-off analog routing migration runs once per file rather than on every load. A
  mod wheel or pitch bend silenced on purpose - to avoid doubled messages next to the
  instrument's own port - used to come back at every start.

## 1.2.0 — 2026-09-01

Functional 1.x release: pickup, per-page memory, and a quieter window.

- **Window title** is Automap on the synth: `connected` / `not connected`. The USB host
  stays up on its own; a small **USB** button remains if you need to close it.
- One toolbar row. Save / Export / Import stay on the right so Import is never off-screen.
- A second copy raises the existing window. The MIDI port is not taken twice.
- **Pickup** on assigned wheels and pedals after a bank or page change: nothing is sent
  until the physical position matches the last value sent on that page. Optional per
  control (Range tab). An amber tick marks the catch point (`2 → 100`). No assignment,
  nothing to remember. Sustain is a switch and is not held.
- Encoder values are remembered per page. `Clear` strips every assignment on the bank;
  `Revert` restores the current page to the factory map.
- Expression (CC 11), sustain (CC 64) and aftertouch send by default. Mod wheel and pitch
  bend stay disabled here — they already leave the UltraNova's own MIDI port.
- Sustain is a footswitch (Momentary / Normal / Toggle / Step, including keystroke and
  transport). Expression stays a continuous pedal.
- USER / FX / INST / MIXER / PAGE sit with LEARN and VIEW on the Debug tools bench. They
  still change bank and page; assign MIDI and they send that too.
- Button 39 is labelled **PATCH KNOB PUSH**. LEARN's lamp follows learn arming.
- New send types: NRPN, RPN, CC 14-bit, Program Change, Aftertouch.
- Log: SysEx, NRPN/RPN, bank select, program change, Start/Stop/Continue. MIDI clock is
  summarised as BPM; we do not generate clock.
- Debug **Demo**: one film; `Alex.Electron` stays centred. Never more than 13 lamps at
  once (14 sags the analogue rail). Automap entry does not blink PAGE BACK/NEXT.
- Touch tab redraws when you pick Transport or Keystroke.

## 1.1.1 — 2026-08-31

The lamp walker follows the same rules as LEARN / VIEW assignments.

- Light, Clear, previous, next and All off only while Debug tools is on.
- The Debug tools tick is not saved with the map. Next session starts with the bench hidden unless you pass `--debug` or tick the tray again.

## 1.1.0 — 2026-08-31

Functional release on the 1.0 window. A later 2.x will redo the layout and colours.

- **LEARN and VIEW** send no MIDI by default. They still arm learn and show the window.
- **Debug tools** in the tray (or `--debug`): the panel-lamp walker, All off, and assignments for LEARN / VIEW. Mapped LEARN / VIEW send MIDI on top of their usual jobs.
- Maps from 1.0.0 that still have the factory LEARN/VIEW rows (CC 20/21 on channel 2) are silenced on load. A row you changed yourself is left alone.
- **Touch → Momentary** now stores `momentary`, so Released / Touched are honoured. Older maps that were saved as `normal` from that tab are rewritten on load.

## 1.0.0 — 2026-08-31

First public release.
