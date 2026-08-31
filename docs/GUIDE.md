# UltraNovaCtl — the full guide

[Русский](GUIDE.ru.md)

Everything from an empty machine to eight encoders driving a plug-in.

---

## 1. What you need to install

Three things, in this order. The first two are not optional — the program cannot reach the
instrument without them.

### 1.1 Novation USB MIDI driver — required

The UltraNova has **no USB-MIDI class interfaces**. Every one of its four USB interfaces is
vendor-specific, so Windows cannot bind its own in-box driver to it. Without Novation's
driver the operating system exposes no MIDI device and no Kernel Streaming filter, and
there is simply nothing for this program to open.

Get **Novation USB Driver 2.30** (`NovationUsbMidi_2.30.0.83`) from
<https://downloads.focusrite.com/novation/synthesisers/ultranova> and install it before
plugging the synth in.

Check it took:

```powershell
Get-PnpDevice | Where-Object InstanceId -like "*VID_1235&PID_0011*" |
    ForEach-Object { (Get-PnpDeviceProperty -InstanceId $_.InstanceId `
        -KeyName DEVPKEY_Device_Service).Data }
```

You want `NovationUsbMidi`. If nothing comes back, the driver did not bind — reinstall it,
then unplug and replug the instrument.

### 1.2 A virtual MIDI port — required

This program does not install a driver of its own and does not create ports. It sends to a
port that already exists, and your DAW listens to the other end of it. On Windows the
practical choice is **loopMIDI**:

<https://www.tobias-erichsen.de/software/loopmidi.html>

Free for personal use. Install it, run it, click **`+`** — that is the whole setup. Leave it
running; the port disappears when it closes.

> **Do not use `midi.exe loopback create` from Windows MIDI Services.** It works, and it
> will quietly ruin the rest of your MIDI. On the machine this was developed on, two of its
> loopback endpoints pushed port enumeration from 90 ms to 265 ms and stopped Ableton Live
> opening *any* MIDI input at all — the synth's own port included. Those endpoints route
> through the `MidiSrv` service that now backs all of WinMM, so they are not isolated from
> anything. loopMIDI has its own driver and does not touch that service.

### 1.3 UltraNovaCtl itself

Two ways, both on [Releases](https://github.com/Alex-Electron/UltraNovaCtl/releases):

**The installer** — `UltraNovaCtl-1.1.0-setup.exe`. Installs into
`%LOCALAPPDATA%\Programs\UltraNovaCtl` **without asking for administrator rights**, adds a
Start menu shortcut and an entry in Installed apps, offers to start with Windows, and tells
you whether the driver and a virtual port are there before it finishes. Per-user on purpose:
settings live beside the executable, and a standard user cannot write inside Program Files.

**Or the zip** — unpack anywhere, run `UltraNovaCtl.exe`. Nothing else to do.

Either way it is a single self-contained executable: **.NET does not need to be installed**,
the runtime is inside it.

> The installer is not code-signed, so Windows SmartScreen will say *"Windows protected your
> PC"*. **More info → Run anyway.** Signing needs a certificate issued to a named person or
> company, and there is not one here.

Building from source instead? Then you need the .NET SDK 8.0 and nothing else — see
[BUILD.md](BUILD.md).

### 1.4 And one thing to un-install, or at least quit

**The stock Automap must not be running.** `AutomapServer.exe` and `MidiAutomapClient.exe`
hold the same USB endpoint and answer the synth before this program can. While they are
alive, nothing here works.

```powershell
Get-Process AutomapServer, MidiAutomapClient -ErrorAction SilentlyContinue | Stop-Process
```

If you never want to see it again, uninstall Automap 4 from Windows' app list. Nothing here
needs it — not for the driver, not for anything.

---

## 2. First run

1. **Start loopMIDI** and make sure a port exists.
2. **Start your DAW** *after* the port exists. Ableton Live builds its MIDI device list once
   at startup and never notices a port that appears later.
3. **Start `UltraNovaCtl.exe`.**
4. Pick your port in **MIDI out** at the top.
5. Press **AUTOMAP** on the instrument.

The status line changes to `connected to UltraNova`, the synth's display fills with labels,
and the rings under the encoders answer your fingers. In the DAW, enable the loopMIDI port
as an input and tick both **Remote** and **Track** for it.

Turn encoder 1. The DAW should show CC 21 moving.

That is the default map: the ten encoders on **channel 1**, CC 21–30 in panel order — the
eight on 21–28, the filter knob on 29, the patch dial on 30 — and the panel buttons on
**channel 2**, each one at CC 20 + its button code, so `LOCK` (code 6) is CC 26 and the dial
push (code 39) is CC 59. Touch, wheels and pedals start disabled. Change any of it as below.

---

## 3. The window

<img src="../img/app.png" alt="UltraNovaCtl" width="100%">

### Top row

| Control | What it does |
|---|---|
| **Connect / Disconnect** | Connects by itself at startup and keeps retrying. Disconnecting stops that until you press it again. |
| **MIDI out** | Where mapped controls are sent. Point it at your virtual port. |
| **Learn in** | Which port to listen to while learning — see §8. |
| **Test** | Sends CC 21 at 127 then 0. If a monitor sees it and the DAW does not, the DAW's own connection is broken and only the DAW can fix that. |
| **Reinit** | Reopens the MIDI ports and repaints the synth. Use it if the DAW went quiet or the synth was replugged. |
| **↻** | Rescan MIDI devices, for an interface plugged in after startup. |
| **Learn** | Off / On / Latch — see §8. |
| **Save** | Writes the working configuration, the one loaded at startup. |
| **Export / Import** | Mapping files. Import also reads Novation `.automap` files. |

### Second row — the instrument's own state

`Kbd ch`, `Octave`, `Transpose`, `Aftertouch` read the synth's settings and write them back.
Change them here and the instrument changes. `Mode` shows `SYNTH` or `AUTOMAP`.

### Third row — banks and pages

`USER`, `FX`, `INST`, `MIXER` are the four banks, and they are the same four buttons on the
panel. `+ page` adds a page to the current bank, `− page` removes one; step through them with
the arrows or with `PAGE ◄` / `PAGE ►` on the instrument. There is no page limit.

### The edit panel

Whatever you clicked last is shown here, on three tabs: **Parameter**, **Touch**, **Range**.

### The control areas

**ENCODERS** — the eight, plus the filter knob and the patch dial. Each shows its label, its
live value and its assignment. `Zero all` resets the values, not the assignments.

**WHEELS & PEDALS** — modulation wheel, pitch bend, aftertouch, expression and sustain.

**BUTTONS** — all the assignable panel buttons. `Light buttons when pressed` echoes each
press on that button's own lamp.

**Reserved for navigation** and **Panel LEDs** are collapsed by default. The first lists the
buttons this program keeps for itself. The second is a lamp prober: type a code, press
`Light`, look at the instrument, and name what lit up.

**Log** — everything that happened. Drag the divider to resize it, select text with the
mouse, `Ctrl+C` or the right-click menu to copy, `Save…` to write the lot to a file.

---

## 4. Assigning a control

Click a knob or a button. It highlights, and on the instrument its ring lights or the button
blinks three times so your hand finds the right one. Then on the **Parameter** tab:

- **Label** — up to 8 characters, shown on the synth's own display. Latin letters only, since
  that is all the display has. Leave it empty and the display shows the assignment instead —
  `CC#102`, or `N026-C#4` for a note.
- **Sends** — what kind of message. See §5.
- **Ch** — MIDI channel 1–16. Different channels on different banks let one instrument drive
  several devices.
- **Number** — the controller or the note, picked from a list with the usual names, the way
  MIDI-OX shows them.

Press **Save** when you are happy. Nothing is written until you do.

---

## 5. Send types

| Type | What it sends |
|---|---|
| **Control Change** | The ordinary one. Any controller, any channel, with a working range. |
| **Note On/Off** | Chosen by name, `Note 042 (F#1)`. Buttons play, knobs sweep. |
| **Pitch Bend** | Full 14-bit, no controller number. |
| **Keystroke** | Any key combination, typed into whatever window has focus. Click the field and press the keys. Buttons only — a knob would fire on every click of travel. |
| **Transport** | Start / Stop / Continue as real-time messages, plus MMC play, stop, pause, record, record exit, fast forward, rewind and return to zero. Buttons only. Which family your software obeys depends on the software: real-time drives a sequencer slaved to external clock, MMC is remote control of a DAW's own transport. |
| **Disabled** | Sends nothing, and shows a dash on the synth's display. |

---

## 6. Modes

### Knobs, wheels and pedals

| Mode | Behaviour |
|---|---|
| **Normal** | Sends a position: the value climbs from `From` to `To`. |
| **Inverted** | The same, backwards. (Setting `From` above `To` does this too.) |
| **Relative (Two's Comp)** | Sends the *movement*, not the position, so the control never hits an end. |
| **Relative (Signed Bit)** | Same idea, different encoding. |
| **Relative (Signed Bit 2)** | Same idea, third encoding. |
| **Relative (Bin Offset)** | Same idea, fourth encoding. |

The four relative encodings exist because nobody agreed on one. They are not
interchangeable — pick the one your plug-in or DAW expects, and if the parameter jumps
around or refuses to move, try the next.

Relative mode is the reason a physical encoder beats a fader for plug-in control: the knob
has no position of its own to disagree with the parameter's.

### Buttons and touch

| Mode | Behaviour |
|---|---|
| **Momentary** | `To` while held, `From` on release. |
| **Normal** | A plain 127/0 switch. |
| **Toggle** | Alternates on each press and stays there. The lamp stays lit while it is on. |
| **Step** | Walks through `Points` positions spread evenly over the range. Five points across 0–127 give 0, 32, 64, 95, 127. |

---

## 7. Range and touch

**Range** tab: `From` and `To` bound what the control actually sends. For a switch they are
the released and pressed values. Put `From` above `To` and the control runs backwards.
`Points` appears when the mode is Step.

**Touch** tab: every one of the ten encoders is touch sensitive, and a finger landing on one
can send something entirely separate from turning it. Off by default. `Touched` and
`Released` set the two values, and the same four switch modes apply.

This is the one thing no ordinary controller can do: a knob that reports being *held* before
it is moved. Assign touch to a filter bypass and the sound opens the moment your hand
arrives.

---

## 8. Learn

For mapping against something whose controller numbers you do not know — a plug-in with a
MIDI-learn of its own, or a second piece of hardware.

1. Plug the other device into any MIDI interface and pick it under **Learn in**.
2. Select the control here that you want to teach.
3. Set **Learn** to **On** (takes one message, then switches itself off) or **Latch** (keeps
   going).
4. Move the control on the other device. Its message is copied onto the selected control.

For a plug-in it is the other way round: set the assignment here, then use the plug-in's own
learn and wiggle the encoder.

---

## 9. What the instrument shows you

The panel is not just an input device — this program writes to it constantly.

- **Display**: your labels on the top row, live values underneath, and the value framed in
  brackets while your finger is on that encoder.
- **Rings** under the eight encoders light on touch, and also when you select a knob in the
  window.
- **Bank button** of the current bank stays lit.
- **Page buttons** light only when there is a page to move to.
- **VIEW** is lit while the editor window is open. Press it on the instrument and the window
  hides to the tray; press it again and the window comes back.
- **Button lamps** follow the assignment, not just the press: a momentary lights while held,
  a toggle stays lit while it is on, a step lights past the first position, a keystroke or
  transport button flashes once. Turn this off with `Light buttons when pressed`.

---

## 10. Files

**Save** writes `ultranovactl.json` next to the executable. That is the working
configuration, loaded at startup. Delete it to get the defaults back.

**Export** writes the same thing anywhere you like, so you can keep a set per project.

**Import** reads both that format and Novation's own `.automap` files. Three things in the
original format are easy to get wrong and are handled properly here: an empty `<Param />`
means *unassigned* rather than CC 0; `Type=2` entries are plug-in automation, not MIDI, and
are refused rather than folded into CC 127; and `step` is the only thing in the whole format
that distinguishes a switch from a continuous control, so it decides how many positions the
imported control has.

---

## 11. Tray and window

Closing the window hides it to the tray and the server keeps running. The tray menu has
**Show window**, **Reinitialise MIDI**, **Start with Windows** and **Quit**. Clicking the tray
icon toggles the window, and so does the `VIEW` button on the instrument.

**Start with Windows** ticks a per-user entry under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, pointing at this copy of the program
with `--tray`, so at login it comes up in the tray with no window. No administrator rights are
involved, and nothing is written anywhere else. The tick reads the registry rather than a saved
setting, so if you clear the entry by hand — with Task Manager's Startup tab, say — the menu
tells the truth next time you open it.

Scriptable, which is what an installer would use:

```powershell
UltraNovaCtl.exe --autostart on     # exit code 0 if it took
UltraNovaCtl.exe --autostart off
UltraNovaCtl.exe --tray             # start hidden, as the Run key does
```

Move the program to a different folder and the old entry points at nothing. Tick it off and on
again to repoint it.

The window remembers where it was and how big it was. If the monitor it was on has since
gone, it opens on one that exists rather than off-screen.

**Quit from the tray menu, not with Task Manager.** Killing the process leaves the MIDI port
busy for a while, and that looks exactly like the DAW deciding to stop listening.

---

## 12. When something is wrong

| What you see | What it is |
|---|---|
| *"Automap is not running"* on the synth's display | Nothing is answering it. Either this program is not connected, or the stock Automap is still running and got there first. |
| Status stays `not connected` | The Novation driver is missing (§1.1), or the stock Automap has the endpoint (§1.4). |
| The synth responds but the DAW hears nothing | Press **Test**. If a MIDI monitor sees CC 21 and the DAW does not, the DAW's own port connection is broken — restart the DAW. Nothing here can repair a connection from the outside. |
| The DAW stopped receiving after restarting this program | The previous instance was killed rather than closed, and the port is still held. Wait, or press **Reinit**. |
| loopMIDI port is missing from the DAW | The DAW was started before the port existed. Restart the DAW. |
| Nothing from the mod wheel or the pedals | They are silent until the host enables them. That happens automatically on connect; press **Reinit** if it did not. |
| Encoders work, notes do not | Notes travel on the instrument's ordinary MIDI port, not through this program. Enable `UltraNova` as a second input in the DAW alongside the loopMIDI port. |
| The synth is stuck in Automap mode | Press **SYNTH** on the instrument. It cannot be done from software — the stock Automap could not do it either. |
| A DIN-connected synth does not respond | The UltraNova's DIN sockets are not bridged to USB. Its own guide says so plainly: it is not a computer MIDI interface. Use a separate USB-MIDI interface. |
