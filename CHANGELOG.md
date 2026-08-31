# Changelog

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
