UltraNovaCtl 1.0.0 — Automap replacement for the Novation UltraNova
https://github.com/Alex-Electron/UltraNovaCtl

Nothing to install. Unpack anywhere and run UltraNovaCtl.exe.
Settings are written to ultranovactl.json next to the executable.

Before it can work:

  1. Install the Novation USB driver 2.30. The UltraNova has no USB-MIDI class
     interfaces, so Windows cannot bind a driver of its own and there is nothing
     for this program to open without it.
     https://downloads.focusrite.com/novation/synthesisers/ultranova

  2. Quit the stock Automap. AutomapServer.exe and MidiAutomapClient.exe hold
     the same USB endpoint and answer the synth before this program does.

  3. Create a virtual MIDI port with loopMIDI (free for personal use):
     https://www.tobias-erichsen.de/software/loopmidi.html
     One click on "+" is enough.

  4. Run UltraNovaCtl.exe, pick that port under "MIDI out", and press AUTOMAP
     on the instrument.

  5. In your DAW, enable the loopMIDI port as an input. Ableton Live builds its
     device list at startup — if the port is newer than the running Live,
     restart Live, then tick both "Remote" and "Track" for that input.

Starting with Windows: tick "Start with Windows" in the tray menu. It writes a
per-user entry under HKCU\Software\Microsoft\Windows\CurrentVersion\Run that
points here with --tray, so at login the program comes up in the tray with no
window. No administrator rights are involved. Scriptable too:

    UltraNovaCtl.exe --autostart on
    UltraNovaCtl.exe --autostart off
    UltraNovaCtl.exe --tray

tools\KsMidiMon.exe is a console utility for probing the hardware: list Kernel
Streaming filters, enumerate pins, dump the raw stream, sweep the lamps. It is
not needed for normal use.

Close the window to hide to the tray; Quit is in the tray menu. Killing the
process leaves the MIDI port busy for a while, which looks like the DAW quietly
stopping.

MIT. Alexander Lavrinovich <lavrinovich.alex@gmail.com>
Not affiliated with or endorsed by Focusrite or Novation.
