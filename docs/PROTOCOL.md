# The Automap protocol, as spoken by the UltraNova

Reconstructed from USB captures of the stock Novation Automap 4.12 driving a real
UltraNova, then checked against a second, independent capture. Everything below was seen
on the wire; where a claim rests on correlation rather than a direct observation, it says so.

The point of this document: in Automap mode the encoders, the touch sensors and most of
the panel do **not** appear on any MIDI port — not USB-MIDI, not the DIN sockets, not the
`Automap MIDI` virtual port the stock software creates. They travel on a private
vendor endpoint, in a private dialect. That is why nothing you can plug in sees them.

---

## 1. Transport

| | |
|---|---|
| Device | VID `0x1235` PID `0x0011` (Focusrite–Novation UltraNova) |
| Endpoint | **interrupt**, OUT `0x05` host→synth, IN `0x85` synth→host, 24-byte packets, `bInterval` 1 |
| Payload | a **raw MIDI byte stream** — not USB-MIDI class, no 4-byte CIN packets |

The device exposes four interfaces and **none of them is USB-MIDI class**; all four are
vendor-specific (`bInterfaceClass 0xFF`):

| Interface | Endpoints | What it carries |
|---|---|---|
| IF0 alt1 | iso OUT `0x01` / IN `0x82` | audio |
| IF1 | int OUT `0x03` / IN `0x83` | MIDI port 1 — notes, wheels, aftertouch |
| IF2 | int OUT `0x04` / IN `0x84` | MIDI port 2 — silent in every capture |
| IF3 | int OUT `0x05` / IN `0x85` | **Automap** |

Messages are cut across USB packet boundaries at arbitrary points, so the receiver has to
reassemble the stream and parse it as MIDI. Example of a split seen in a capture:
`bf 04 00 bf 05 00 bf` followed by `06 00 bf 00 00 …`.

Running status is never used: every message carries its own status byte. A parser is still
better off tolerating it, but do not *depend* on it — and in particular, do not treat a
stray `FF` as System Reset. Data bytes of `0x7F` and status-looking values appear inside
legitimate three-byte messages (`B0 FF 7F` is a real message), so decode strictly on the
three-byte grid rather than re-synchronising on high bits.

---

## 2. Mode

```
F0 00 01 F7     in Automap
F0 00 00 F7     left Automap
```

Both directions send it, and both repeat it — the synth re-announces its state as a
keepalive, so treat a repeat as "still there", not as a transition.

**The host cannot push the synth out of Automap mode.** Across every capture the host sent
`F0 00 01 F7` twelve times and `F0 00 00 F7` never; `00` only ever arrives *from* the
synth, when someone presses the SYNTH button. The stock Automap has no such command either.

If nothing answers the synth, it puts *"Automap is not running"* on its own display.

There is also a low-rate poll on channel 16 that keeps the link alive. It is a heartbeat,
not part of a handshake — no reply of any particular shape is required.

---

## 3. Synth → host

Each control class gets its own MIDI channel. All messages are three bytes.

| Message | Meaning |
|---|---|
| `B0 <n> <delta>` | encoder `n` turned |
| `B1 <n> <1\|0>` | encoder `n` touched / released |
| `B2 <code> <1\|0>` | button pressed / released |
| `B3 <n> <value>` | wheels and pedals, absolute 0…127 |
| `BF <reg> <value>` | keyboard state (see §5) |

**Encoder deltas** are 7-bit signed and accelerate with turn speed:

```
clockwise         1, 2, 4, 6, 12, 18, 24
counter-clockwise 127 = -1, 126 = -2, 124 = -4, 122 = -6
```

There are ten encoders: `0…7` under the display, `8` the filter knob, `9` the patch dial.
All ten are touch sensitive. The patch dial additionally has a push, which arrives as
button code 39.

Holding a button while turning an encoder changes nothing in either message — the two are
independent, so any button can be used as a modifier if the host wants one.

---

## 4. Host → synth

### Lamps

```
B0 <lampCode> <1|0>
```

### Display

```
F0 02 <row> <pos> <ascii bytes…> F7
```

Two rows of 72 characters: row `0` is the label line, row `1` the value line. Each encoder
owns a 9-character field starting at `n × 9`. The stock software writes an 8-character
label and an 8-character value, and brackets the value — `[  10  ]` — while a finger is on
that encoder. Arbitrary text across the whole row also works; Automap used that for its own
warnings.

### Enabling the wheels and pedals

```
BF 04 01     start sending channel-3 analog messages
BF 04 00     stop
```

This one is inferred rather than documented, but the correlation is total: 455 wheel events
in the capture where it was sent, all of them after it, and zero in the capture where it
was not. Without it the analog controls are simply silent — which makes it easy to conclude,
wrongly, that the hardware does not report them.

---

## 5. Keyboard state (channel 16)

Two-way: the same message reports the setting and sets it.

| Register | Meaning |
|---|---|
| `0` | keyboard MIDI channel |
| `1` | octave shift, offset by 64 |
| `2` | transpose, offset by 64 |
| `3` | aftertouch |

These are exactly the fields the stock Automap window offers, which is a good check that
the register numbering is right.

---

## 6. Notes for anyone implementing this

- **Stop the stock software first.** `AutomapServer.exe` and `MidiAutomapClient.exe` hold
  the same endpoint and will answer the synth before you do.
- **Reading is blocking.** The interrupt endpoint delivers when the hardware has something;
  polling loops and sleeps only add latency. Measured on hardware, 80 of 80 reads returned data.
- **Do not paint the display from the reading thread.** Display writes go out over USB and
  the reading thread will sit on them. Use a separate writer and coalesce updates.
- The synth's own DSP handles both synthesis and this channel, so timing wanders on heavy
  patches. Measure on hardware rather than trusting a number from a datasheet.
