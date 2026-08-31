# Building

## What you need

- **.NET SDK 8.0** or newer — <https://dotnet.microsoft.com/download>
- Nothing else. Avalonia and its dependencies come from NuGet on the first restore.

## Build and run

```powershell
dotnet build UltraNovaCtl.sln -c Release
dotnet run  --project src/Gui/UltraNovaCtl.Gui.csproj -c Release
```

## A self-contained build

One `.exe` that runs on a machine with no .NET installed:

```powershell
dotnet publish src/Gui/UltraNovaCtl.Gui.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o dist/UltraNovaCtl-win-x64
```

About 45 MB, because the runtime and Skia are inside it.

## The installer

[Inno Setup 6](https://jrsoftware.org/isdl.php) builds it. The script expects the published
application already sitting in `dist\UltraNovaCtl-win-x64\`, so publish first, then:

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\UltraNovaCtl.iss
```

Out comes `dist\UltraNovaCtl-1.1.1-setup.exe`. CI does the same on every push and keeps it
as an artifact.

To exercise it without clicking through the wizard:

```powershell
.\dist\UltraNovaCtl-1.1.1-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /TASKS=autostart
& "$env:LOCALAPPDATA\Programs\UltraNovaCtl\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES
```

## Projects

| Project | What it is |
|---|---|
| `src/Core` | the protocol engine, Kernel Streaming interop, the configuration model, MIDI output |
| `src/Gui` | the Avalonia window and tray icon |
| `src/KsMidiMon` | a console tool for poking at the hardware: list KS filters, enumerate pins, dump the raw stream, sweep the lamps |

`src/Core` has no dependency on the GUI, so a headless build or a different front end can
use it as it is.

## Where the settings live

`ultranovactl.json`, next to the executable. Delete it to get the defaults back.

## Other platforms

The GUI is cross-platform already; the hardware layer is not. `src/Core/Ks.cs` and
`Pins.cs` are Windows Kernel Streaming, and `MidiOut.cs` / `MidiIn.cs` are WinMM. A Linux
or macOS port needs those two seams replaced — libusb for the vendor endpoint, ALSA
sequencer or CoreMIDI for the output. Nothing above them assumes Windows.
