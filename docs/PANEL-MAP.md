# UltraNova panel map

Every code below was measured on hardware: a button was pressed and the message written
down, or a lamp code was sent and someone looked at the panel. Nothing here is inferred
from the order of names in a manual — that was tried, and it was wrong.

Button codes arrive as `B2 <code> <1|0>`. Lamp codes are sent as `B0 <code> <1|0>`.

<img src="../img/panel-map.svg" alt="UltraNova control map" width="100%">

*The same information drawn from scratch, for anyone who wants a diagram that owes nothing
to the manufacturer's artwork.*

---

## Buttons

| Code | Button | Code | Button |
|---:|---|---:|---|
| 0 | LEARN | 20 | ENVELOPE |
| 1 | VIEW | 21 | MIXER (synth edit) |
| 2 | USER | 22 | LFO |
| 3 | FX | 23 | FILTER (synth edit) |
| 4 | INST | 24 | MODULATION |
| 5 | MIXER | 25 | VOICE |
| 6 | LOCK | 26 | EFFECTS |
| 7 | FILTER | 27 | VOCODER |
| 8 | AUDIO | 28 | ARP ON |
| 9 | OCTAVE − | 29 | ARP SETTINGS |
| 10 | GLOBAL | 30 | ARP LATCH |
| 11 | OCTAVE + | 31 | CHORD ON |
| 13 | PATCH | 32 | CHORD EDIT |
| 15 | COMPARE | 33 | ANIMATE TWEAK |
| 16 | WRITE | 34 | ANIMATE TOUCH |
| 17 | PAGE ◄ | 35 | VALUE + |
| 18 | PAGE ► | 36 | VALUE − |
| 19 | OSCILLATOR | 37 | SELECT ▲ |
| | | 38 | SELECT ▼ |
| | | 39 | patch dial push |

**12 (SYNTH) and 14 (AUTOMAP) never send a press.** They switch the instrument's mode, and
the mode change is all the host hears about. They do have lamps.

`LOCK` and `FILTER` belong to the synthesizer; Automap does not use them. They are still
reported, so they are available if you want them.

---

## Lamps

```
0–18    the Automap row, modes, pages, octave — same numbering as the buttons
19–27   OSCILLATOR, ENVELOPE, MIXER, LFO, FILTER, MODULATION, VOICE, EFFECTS, VOCODER
28–34   ARP ON / SETTINGS / LATCH, CHORD ON / EDIT, ANIMATE TWEAK / TOUCH
35      a second vocoder indicator
36–41   SELECT 1–6
42–49   the rings under encoders 1–8
```

> **Buttons and lamps share numbering only up to code 34.** Up to there a code is both the
> button and its own lamp. Past 34 the two numberings fork and the same digits mean two
> different things depending on which way the message is travelling:

| Code | As a button — `B2 <code>` from the synth | As a lamp — `B0 <code>` to the synth |
|---:|---|---|
| 35 | VALUE + | the second vocoder indicator |
| 36 | VALUE − | SELECT 1 |
| 37 | SELECT ▲ | SELECT 2 |
| 38 | SELECT ▼ | SELECT 3 |
| 39 | patch dial push | SELECT 4 |
| 40 | — | SELECT 5 |
| 41 | — | SELECT 6 |
| 42–49 | — | the rings under encoders 1–8 |

So a button above 34 has no lamp of its own, and a lamp above 34 has no button. Above 49
nothing new lights up.

The three `RATE` lamps on the LFO section are driven by the LFOs themselves and cannot be
addressed by the host.

---

## Encoders

| Index | Control |
|---:|---|
| 0–7 | the eight encoders under the display |
| 8 | filter knob |
| 9 | patch dial (push arrives separately as button 39) |

All ten report touch on channel 2 (`B1 <n> <1|0>`). There is no touch strip on this
instrument — the touch sensing is in the encoders.

---

## Analog controls

Reported on channel 4 (`B3 <n> <value>`), and only after the host sends `BF 04 01`.

| Index | Control |
|---:|---|
| 1 | modulation wheel |
| 3 | expression pedal (TRS pot on the rear jack) |
| 4 | sustain pedal (two-pole footswitch on the rear jack) |

Pitch bend, aftertouch and notes do **not** come this way — they stay on MIDI port 1
(interface IF1) and are readable there even while the instrument is in Automap mode.
The window still shows them on the same row so the five analog tiles stay together.

Expression is a continuous pedal. Sustain is a switch: same press/release path as a
panel button.
