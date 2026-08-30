# UltraNovaCtl

**English** · [Русский](./README.ru.md)

A replacement for Novation's **Automap** for the **UltraNova** synthesizer: it takes the
eight encoders, the filter knob, the patch dial, the touch sensors and the whole front
panel, and turns them into ordinary MIDI your DAW can map.

<img src="img/app.png" alt="UltraNovaCtl" width="100%">

![license](https://img.shields.io/badge/license-MIT-blue)
![platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows&logoColor=white)
![runtime](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![ui](https://img.shields.io/badge/UI-Avalonia%2012-8B5CF6)
![device](https://img.shields.io/badge/device-Novation%20UltraNova-orange)

---

## Why this exists

Put an UltraNova in Automap mode and its most useful controls vanish. The encoders send
nothing on the USB MIDI ports, nothing on the DIN sockets, and nothing on the `Automap MIDI`
virtual port the stock software installs. They are not idle — they are talking on a private
vendor USB endpoint in a private dialect, to a piece of software that will only hand the
result to plug-ins as host automation, never as MIDI you can route.

So the encoders are unusable for anything Automap did not think of, and Automap itself is
long unmaintained.

This program speaks that dialect. It reads the panel directly, writes to the instrument's
display and lamps, and sends whatever you assign to a normal MIDI port:

```
UltraNova ──USB port 3──► UltraNovaCtl ──► virtual MIDI port ──► your DAW
```

The protocol is written up in [docs/PROTOCOL.md](docs/PROTOCOL.md) and the panel in
[docs/PANEL-MAP.md](docs/PANEL-MAP.md), so the reverse-engineering is reusable even if you
never run this application.

---

## What it does

**Controls it reads**

- the eight encoders, with acceleration, plus the filter knob and the patch dial
- touch on all ten encoders — assignable separately from the turn
- 40 panel buttons, including the patch dial push
- modulation wheel, pitch bend, aftertouch, expression and sustain pedals
- keyboard channel, octave, transpose and aftertouch settings — readable and settable

**What you can send**

| | |
|---|---|
| Control Change | any controller, any channel, with a working range |
| Note On/Off | picked by name, `Note 042 (F#1)` |
| Pitch Bend | full 14-bit |
| Keystroke | any key combination, typed into the focused window |
| Transport | Start / Stop / Continue, and MMC play, stop, pause, record, record exit, fast forward, rewind, return to zero |
| Disabled | leaves the control alone |

**How a control behaves** — knobs: Normal, Inverted, and four relative encodings (Two's
Complement, Signed Bit, Signed Bit 2, Binary Offset). Buttons: Momentary, Normal, Toggle,
and Step with any number of positions.

**Banks and pages** — four banks reached from the panel's USER, FX, INST and MIXER buttons,
each with as many pages as you like, stepped with the panel's page buttons.

**Feedback on the instrument** — labels and live values on the synth's own display, rings
lit under the encoders you touch, the active bank lit on its button, page buttons lit only
when there is somewhere to go, VIEW lit while the editor window is open. Button lamps follow
the assignment: a momentary lights while held, a toggle stays lit while it is on. Pick a
button in the window and it blinks on the panel so your hand finds it.

**Learn** — listen on any MIDI input and take the next controller that moves, for mapping
against a plug-in or a second instrument.

**Import** — reads Novation `.automap` files, so existing maps come across.

---

## Getting started

1. **Quit the stock Automap.** `AutomapServer.exe` and `MidiAutomapClient.exe` hold the same
   USB endpoint and will answer the synth before this program does. Nothing works until they
   are gone.

2. **Make a virtual MIDI port.** This program does not install a driver; it sends to a port
   you already have. [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) is
   free for personal use, and creating a port is one click on `+`.

   > Do not use `midi.exe loopback create` from Windows MIDI Services on Windows 11. It
   > works, but its endpoints route through the `MidiSrv` service that now backs all of
   > WinMM: port enumeration went from 90 ms to 265 ms on the test machine and Ableton Live
   > stopped opening any MIDI input at all, including the synth's own.

3. **Run `UltraNovaCtl.exe`**, pick your port in **MIDI out**, and press **AUTOMAP** on the
   instrument. The title strip says `connected to UltraNova` and the display fills with your
   labels.

4. **In your DAW**, enable the loopMIDI port as an input. Ableton Live builds its device
   list at startup, so if you created the port after Live was running, restart Live — then
   tick both `Remote` and `Track` for that input.

Click any knob or button in the window to assign it. Settings live in `ultranovactl.json`
next to the executable.

---

## Repository layout

```
UltraNovaCtl/
├── README.md                  # this file (English)
├── README.ru.md               # Russian
├── LICENSE                    # MIT
├── UltraNovaCtl.sln
├── docs/
│   ├── PROTOCOL.md            # the Automap protocol, as captured from the wire
│   ├── PANEL-MAP.md           # every button, lamp and encoder code
│   └── BUILD.md               # how to build it
├── img/
└── src/
    ├── Core/                  # protocol engine, Kernel Streaming, config model, MIDI out
    ├── Gui/                   # Avalonia window and tray icon
    └── KsMidiMon/             # console tool: list filters, enumerate pins, dump the stream
```

Build instructions: [docs/BUILD.md](docs/BUILD.md). Short version: `dotnet build`.

---

## Known limits

- **Windows only for now.** The GUI is cross-platform; the hardware layer is Kernel
  Streaming and WinMM. Two files stand between this and a Linux or macOS build — see
  [docs/BUILD.md](docs/BUILD.md).
- **The synth cannot be pushed out of Automap mode.** Only its own SYNTH button does that.
  The stock software could not do it either.
- **The DIN sockets are not bridged to USB.** The instrument's own guide is explicit that
  it is not a computer MIDI interface, so driving external hardware needs a separate
  USB-MIDI interface.
- **Killing the process leaves the MIDI port busy** for a while, which looks like the DAW
  quietly stopping. Close the window instead — it hides to the tray, and Exit is in the
  tray menu.

## Roadmap

Start with Windows, an installer that sets up the virtual port, mass clear and revert of
assignments, pickup for wheels and pedals, drag-and-drop to swap assignments, MIDI clock
output.

---

## License

MIT. See [LICENSE](LICENSE).

Not affiliated with, endorsed by, or supported by Focusrite or Novation. *Automap*,
*Novation* and *UltraNova* are their trademarks, used here only to say what this works with.

## Author

**Alexander Lavrinovich** · <lavrinovich.alex@gmail.com> · [github.com/Alex-Electron](https://github.com/Alex-Electron)
Co-author: AI.
