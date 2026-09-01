# Changelog

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
