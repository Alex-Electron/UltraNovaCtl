; UltraNovaCtl installer — Inno Setup 6
;
; Offers both: just for me, into %LOCALAPPDATA%\Programs with no administrator prompt, or
; for everyone, into Program Files. {autopf} follows whichever the user picks, so the path
; on screen always matches the choice.
;
; A machine-wide install used to be a trap, because the program kept its settings beside its
; own executable and a standard user cannot write inside Program Files. The program now moves
; that file to the roaming profile when the install directory is read-only, so both work.
;
; Two things this cannot install and does not pretend to: the Novation USB driver, which
; is Novation's own redistributable, and loopMIDI, whose licence forbids redistribution.
; It detects both and points at the download pages instead.
;
; Build:  ISCC.exe installer\UltraNovaCtl.iss
; Expects the published application in dist\UltraNovaCtl-win-x64\.

#define AppName        "UltraNovaCtl"
#define AppVersion     "1.0.0"
#define AppPublisher   "Alexander Lavrinovich"
#define AppURL         "https://github.com/Alex-Electron/UltraNovaCtl"
#define AppExe         "UltraNovaCtl.exe"
#define SourceDir      "..\dist\UltraNovaCtl-win-x64"

#define DriverURL   "https://downloads.focusrite.com/novation/synthesisers/ultranova"
#define LoopMidiURL "https://www.tobias-erichsen.de/software/loopmidi.html"

[Setup]
AppId={{1fcc0411-3a53-4f50-b6d3-73a06b82d8bd}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} setup

; Default to no elevation, but let the user ask for a machine-wide install. {autopf} is
; Program Files in that case and {localappdata}\Programs otherwise, so the path shown
; always matches the choice made on the previous page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExe}

; The hardware layer is Kernel Streaming and WinMM — 64-bit Windows only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\src\Gui\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "autostart";   Description: "Start with Windows, straight into the tray"

[Files]
Source: "{#SourceDir}\{#AppExe}";          DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.txt";         DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "{#SourceDir}\tools\KsMidiMon.exe"; DestDir: "{app}\tools"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}";     Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}";      Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The same value the program's own tray tick writes, so the two never disagree.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "UltraNovaCtl"; \
    ValueData: """{app}\{#AppExe}"" --tray"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Run {#AppName} now"; \
    Flags: nowait postinstall skipifsilent

[Code]

{ ---------------------------------------------------------------- detection }

function DriverPresent: Boolean;
begin
  { The driver registers a service; the device binds to it as DEVPKEY_Device_Service. }
  Result := RegKeyExists(HKEY_LOCAL_MACHINE,
                         'SYSTEM\CurrentControlSet\Services\NovationUsbMidi');
end;

function LoopMidiPresent: Boolean;
var
  P32, P64: String;
begin
  { loopMIDI leaves no key of its own worth trusting, so look for where it lands. }
  P32 := GetEnv('ProgramFiles(x86)');
  P64 := GetEnv('ProgramFiles');
  Result := ((P32 <> '') and DirExists(P32 + '\Tobias Erichsen\loopMIDI'))
         or ((P64 <> '') and DirExists(P64 + '\Tobias Erichsen\loopMIDI'));
end;

function ProcessRunning(const ExeName: String): Boolean;
var
  Code: Integer;
begin
  { No process list in Inno, so ask tasklist and read find's exit code. }
  Result := Exec(ExpandConstant('{cmd}'),
                 '/C tasklist /FI "IMAGENAME eq ' + ExeName + '" | find /I "' + ExeName + '"',
                 '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function StockAutomapRunning: Boolean;
begin
  Result := ProcessRunning('AutomapServer.exe') or ProcessRunning('MidiAutomapClient.exe');
end;

{ ------------------------------------------------------- the checklist page }

var
  ReqPage: TWizardPage;
  DriverLink, LoopLink: TNewStaticText;

procedure OpenDriverPage(Sender: TObject);
var Code: Integer;
begin
  ShellExec('open', '{#DriverURL}', '', '', SW_SHOW, ewNoWait, Code);
end;

procedure OpenLoopMidiPage(Sender: TObject);
var Code: Integer;
begin
  ShellExec('open', '{#LoopMidiURL}', '', '', SW_SHOW, ewNoWait, Code);
end;

function AddHeading(Y: Integer; const S: String): TNewStaticText;
begin
  Result := TNewStaticText.Create(ReqPage);
  Result.Parent := ReqPage.Surface;
  Result.Top := Y;
  Result.Width := ReqPage.SurfaceWidth;
  Result.WordWrap := True;
  Result.Caption := S;
  Result.Font.Style := [fsBold];
end;

function AddBody(Y: Integer; const S: String): TNewStaticText;
begin
  Result := TNewStaticText.Create(ReqPage);
  Result.Parent := ReqPage.Surface;
  Result.Top := Y;
  Result.Width := ReqPage.SurfaceWidth;
  Result.WordWrap := True;
  Result.Caption := S;
end;

function AddLink(Y: Integer; const S: String; Handler: TNotifyEvent): TNewStaticText;
begin
  Result := TNewStaticText.Create(ReqPage);
  Result.Parent := ReqPage.Surface;
  Result.Top := Y;
  Result.Width := ReqPage.SurfaceWidth;
  Result.Caption := S;
  Result.Cursor := crHand;
  Result.Font.Color := clBlue;
  Result.Font.Style := [fsUnderline];
  Result.OnClick := Handler;
end;

procedure InitializeWizard;
var
  Y: Integer;
begin
  ReqPage := CreateCustomPage(wpSelectTasks,
    'What else this needs',
    'Two components cannot be included here, and the program will not reach the synthesizer without them.');

  Y := 0;

  if DriverPresent then
  begin
    AddHeading(Y, 'Novation USB driver — found');
    Y := Y + 18;
    AddBody(Y, 'The service NovationUsbMidi is registered. Nothing to do.');
    Y := Y + 34;
  end
  else
  begin
    AddHeading(Y, 'Novation USB driver — MISSING');
    Y := Y + 18;
    AddBody(Y, 'The UltraNova has no USB-MIDI class interfaces, so Windows cannot bind a driver'
             + ' of its own and there is nothing for this program to open. Install Novation''s'
             + ' driver, then unplug and replug the instrument.');
    Y := Y + 48;
    DriverLink := AddLink(Y, '{#DriverURL}', @OpenDriverPage);
    Y := Y + 30;
  end;

  if LoopMidiPresent then
  begin
    AddHeading(Y, 'A virtual MIDI port — loopMIDI found');
    Y := Y + 18;
    AddBody(Y, 'Remember to run it and create a port with "+", and to start your DAW after'
             + ' the port exists.');
    Y := Y + 34;
  end
  else
  begin
    AddHeading(Y, 'A virtual MIDI port — MISSING');
    Y := Y + 18;
    AddBody(Y, 'This program creates no ports of its own: it sends to one that exists and your'
             + ' DAW listens to the other end. loopMIDI is free for personal use — install it,'
             + ' run it, click "+".');
    Y := Y + 48;
    LoopLink := AddLink(Y, '{#LoopMidiURL}', @OpenLoopMidiPage);
    Y := Y + 30;
  end;

  AddBody(Y + 8, 'Installation carries on either way. The full guide is on the project page.');
end;

{ --------------------------------------------------- the stock Automap first }

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if not StockAutomapRunning then
    Exit;

  if MsgBox('Novation''s own Automap is running.'#13#10#13#10
          + 'It holds the same USB endpoint and answers the synthesizer first, so nothing'
          + ' here will work while it is alive.'#13#10#13#10
          + 'Close it now?', mbConfirmation, MB_YESNO) = IDYES then
  begin
    Exec(ExpandConstant('{cmd}'),
         '/C taskkill /F /IM AutomapServer.exe /IM MidiAutomapClient.exe',
         '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;

{ ------------------------------------------------------------- uninstalling }

procedure CurUninstallStepChanged(CurStep: TUninstallStep);
var
  Settings: String;
begin
  if CurStep <> usUninstall then
    Exit;

  { Beside the executable after a per-user install, in the roaming profile when the
    install directory turned out to be read-only. Look in both. }
  Settings := ExpandConstant('{app}\ultranovactl.json');
  if not FileExists(Settings) then
    Settings := ExpandConstant('{userappdata}\UltraNovaCtl\ultranovactl.json');
  if not FileExists(Settings) then
    Exit;

  if MsgBox('Keep your mappings?'#13#10#13#10
          + Settings + #13#10#13#10
          + 'That file holds every assignment, bank and page you have made.'#13#10#13#10
          + 'Yes keeps it, No deletes it.',
            mbConfirmation, MB_YESNO) = IDNO then
  begin
    DeleteFile(Settings);
    RemoveDir(ExpandConstant('{userappdata}\UltraNovaCtl'));
  end;
end;
