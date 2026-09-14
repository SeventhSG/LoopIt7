; LoopIt7 installer.
;
; Build it with build\build.ps1, which publishes the app first and then calls ISCC on
; this script. Installs per user by default so the common path never touches UAC; the
; wizard offers an all users install for anyone who wants one.

#define AppName        "LoopIt7"
#define AppVersion     "1.7.3"
#define AppPublisher   "SeventhSG"
#define AppUrl         "https://github.com/SeventhSG/LoopIt7"
#define AppExeName     "LoopIt7.exe"
#define RuntimeUrl     "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

; The cable is Virtual Audio Cable Lite, by Eugene Muzychenko: one cable, signed by Microsoft,
; free for private, non-commercial use. Its licence permits distributing the Lite version
; together with another product, unmodified and not for profit, so build\build.ps1 downloads
; the official package into installer\cable\vac\, checks its hash, and it goes in as is,
; licence file included. Without that folder the setup builds and works exactly the same: no
; task, and the app sends the user to VAC's download page itself.
;
; VAC's installer has no silent mode, so setup opens it and the user clicks through it, which
; also shows them VAC's own licence. Setup then runs LoopIt7 --name-cables, which waits for the
; endpoints, names them from the note this script leaves in installed-cable.txt, and reports the
; name for the last page. The app does the naming because it is also what puts every name back.
#define CableSetup     "cable\vac\setup.exe"
; The driver string VAC's endpoints report, and what setup looks for.
#define CableDriver    "Virtual Audio Cable"
; Must match the package build.ps1 downloads. It is what tells an upgrade that the cable
; already on the machine is out of date.
#define CableVersion   "4.71"
#if FileExists(AddBackslash(SourcePath) + CableSetup)
  #define BundleCable
#endif

[Setup]
AppId={{7C6C6E2A-5B4D-4F1E-9C21-0A7F5B0E4D71}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup
SetupIconFile=..\src\LoopIt7\Assets\LoopIt7.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
WizardImageStretch=no
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; LoopIt7 turns a close request into a hide to tray, so the Restart Manager cannot shut it
; down on its own. PrepareToInstall below does it instead.
CloseApplications=no
RestartApplications=no
AllowNoIcons=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupMutex=LoopIt7Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
#ifdef BundleCable
; Always offered. Guessing whether a machine already has the cable went wrong too often: a
; leftover Programs entry with no device behind it was enough to hide the offer. It starts
; unticked when a working cable is already here (see CurPageChanged), so an upgrade does not
; open VAC's installer every time, and the same box brings an out of date cable current.
Name: "cable"; Description: "Install Virtual Audio Cable Lite (free for private, non-commercial use). LoopIt7 names it ""LoopIt7 Cable"", and that is what you pick in a DAW, Discord or OBS to play into or record from LoopIt7. If you already have Virtual Audio Cable, you can leave this unticked"; GroupDescription: "Virtual audio cable:"
#endif

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.txt"; DestDir: "{app}"; Flags: ignoreversion
#ifdef BundleCable
; The whole package, folder structure intact: VAC's installer needs its x86, x64, arm64 and
; tools subfolders beside it.
Source: "cable\vac\*"; DestDir: "{tmp}\vac"; Flags: recursesubdirs createallsubdirs deleteafterinstall; Tasks: cable
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
#ifdef BundleCable
; A new cable can become the default playback device, which would send everything the user
; plays into the cable instead of their speakers. The defaults are written down first, and the
; naming step below puts back any that moved onto the cable.
Filename: "{app}\{#AppExeName}"; Parameters: "--save-defaults"; StatusMsg: "Preparing the virtual audio cable..."; Flags: runhidden waituntilterminated; Tasks: cable
; VAC's installer asks for administrator rights itself, which is why this goes through the
; shell rather than being run directly: LoopIt7's own setup stays unelevated.
Filename: "{tmp}\vac\setup.exe"; WorkingDir: "{tmp}\vac"; StatusMsg: "Installing Virtual Audio Cable Lite. Finish its installer, then setup continues..."; Flags: shellexec waituntilterminated; Tasks: cable; BeforeInstall: NoteCablesBefore; AfterInstall: WriteCableMarker
; Setup pauses here until the cable carries LoopIt7's name. The app waits for Windows to create
; the endpoints, names them through its ownership rules, and writes the name for the last page.
; Skipped when the cable is not ours to name: the user's own, or none because VAC was cancelled.
Filename: "{app}\{#AppExeName}"; Parameters: "--name-cables"; StatusMsg: "Naming the cable LoopIt7 Cable..."; Flags: runhidden waituntilterminated; Tasks: cable; Check: CableIsOurs
#endif
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Hand back any device LoopIt7 renamed, before the executable that knows how to do it is
; deleted. A machine that no longer has LoopIt7 on it must not be left with a device called
; "LoopIt7 Cable" in every program's list and nothing to explain it.
Filename: "{app}\{#AppExeName}"; Parameters: "--release-cables"; RunOnceId: "ReleaseCables"; Flags: waituntilterminated runhidden skipifdoesntexist

[Registry]
; The app writes its own autostart entry when the option is switched on. Uninstalling
; must not leave that behind pointing at a path that no longer exists.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "LoopIt7"; \
    Flags: deletevalue uninsdeletevalue; ValueType: none

[Code]
const
  { The running app holds this. Its presence is how setup knows to close it first. }
  AppMutexName = 'Local\LoopIt7.SingleInstance';

  { Inno writes the install it made under its own AppId with an _is1 suffix. Reading the
    version back out is what turns "install" into "upgrade" in the wizard. }
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C6C6E2A-5B4D-4F1E-9C21-0A7F5B0E4D71}_is1';

  { Where Windows keeps the endpoint list. A cable is only a cable once there is an endpoint
    for it, so this is the same place the app looks rather than a guess at file paths. }
  RenderEndpoints = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render';

  { PKEY_DeviceInterface_FriendlyName: the driver behind an endpoint, "VB-Audio Virtual
    Cable" and the like. }
  InterfaceNameValue = '{b3f8fa53-0004-438e-9003-51a46e139bfc},6';

  { DEVICE_STATE_NOTPRESENT in an endpoint's DeviceState value. }
  DeviceStateNotPresent = $4;

var
  DownloadPage: TDownloadWizardPage;
  InstalledVersion: String;

  { What the cable step found, so the marker and the last page can tell a cable we installed
    from one the user already had. }
  CablesBefore: Integer;
  MarkerBefore: Boolean;
  CableIsOursNow: Boolean;
  CableTaskDefaulted: Boolean;

{ The version already on this machine, per user or for all users, or empty when LoopIt7 has
  never been installed here. }
{ The cable version LoopIt7 last installed, from the note setup left beside the settings.
  Empty when we installed none, which is the ordinary case on a machine that already had one. }
function OurCableVersion(): String;
var
  Lines: TArrayOfString;
  Marker: String;
begin
  Result := '';

  Marker := ExpandConstant('{autoappdata}\LoopIt7\installed-cable.txt');
  if not FileExists(Marker) then
    Marker := ExpandConstant('{commonappdata}\LoopIt7\installed-cable.txt');

  if not FileExists(Marker) then Exit;
  if not LoadStringsFromFile(Marker, Lines) then Exit;

  { First line is the family, second the version. An older note has no second line, which
    reads as "unknown" and therefore as out of date, which is the safe way round. }
  if GetArrayLength(Lines) >= 2 then
    Result := Trim(Lines[1]);
end;

function CableMarkerExists(): Boolean;
begin
  Result := FileExists(ExpandConstant('{autoappdata}\LoopIt7\installed-cable.txt')) or
            FileExists(ExpandConstant('{commonappdata}\LoopIt7\installed-cable.txt'));
end;

{ How many playback endpoints that are actually present report a driver containing Text.
  Counts devices, not Programs entries: a leftover entry with no cable behind it once hid the
  offer from someone who had no cable at all. }
function PresentCables(Text: String): Integer;
var
  Endpoints: TArrayOfString;
  I: Integer;
  Iface: String;
  State: Cardinal;
begin
  Result := 0;
  if not RegGetSubkeyNames(HKLM64, RenderEndpoints, Endpoints) then Exit;

  for I := 0 to GetArrayLength(Endpoints) - 1 do
  begin
    { Windows keeps an endpoint's key after its driver is uninstalled, marked not present. }
    if RegQueryDWordValue(HKLM64, RenderEndpoints + '\' + Endpoints[I], 'DeviceState', State) and
        ((State and DeviceStateNotPresent) <> 0) then
      Continue;

    if RegQueryStringValue(HKLM64, RenderEndpoints + '\' + Endpoints[I] + '\Properties',
        InterfaceNameValue, Iface) and (Pos(Lowercase(Text), Lowercase(Iface)) > 0) then
      Result := Result + 1;
  end;
end;

{ Whether a cable that works is already here: the user's own VAC, or the one LoopIt7 installed,
  current, and carrying our name (its driver string then reads LoopIt7 rather than VAC's). Only
  decides whether the box starts ticked; the offer itself is always there. }
function WorkingCableHere(): Boolean;
begin
  Result := (PresentCables('{#CableDriver}') > 0) or
            ((OurCableVersion() = '{#CableVersion}') and (PresentCables('{#AppName}') > 0));
end;

{ Runs just before VAC's installer. Whatever VAC device is here now was not put here by this
  setup, and must not be handed to LoopIt7 to rename. }
procedure NoteCablesBefore();
begin
  CablesBefore := PresentCables('{#CableDriver}');
  MarkerBefore := CableMarkerExists();
  CableIsOursNow := False;

  { Written by the naming step. A stale one from an earlier install would make the last page
    report a name this run never gave. }
  DeleteFile(ExpandConstant('{userappdata}\LoopIt7\named-cable.txt'));
end;

function CableIsOurs(): Boolean;
begin
  Result := CableIsOursNow;
end;

function PreviousVersion(): String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Result) then Exit;
  if RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', Result) then Exit;
  if IsWin64 and RegQueryStringValue(HKLM32, UninstallKey, 'DisplayVersion', Result) then Exit;
  Result := '';
end;

function IsUpgrade(): Boolean;
begin
  Result := InstalledVersion <> '';
end;

{ True when a .NET 10 desktop runtime is present. LoopIt7 is framework dependent, which is
  what keeps the download to a couple of megabytes instead of a hundred and fifty. }
function DesktopRuntimeInstalled(): Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  Root := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if FindFirst(Root + '\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    'Windows components',
    'LoopIt7 needs the .NET 10 desktop runtime.',
    nil);

end;

{ The Ready page, because an upgrade skips the welcome and the folder pages and this is the
  last thing anybody reads before files start moving. }
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';

  if IsUpgrade then
    Result := 'Upgrading LoopIt7 ' + InstalledVersion + ' to {#AppVersion}.' + NewLine +
              Space + 'Your patchbay, presets and options are kept exactly as they are.' +
              NewLine + NewLine;

  Result := Result + MemoDirInfo;

  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

function InitializeSetup(): Boolean;
begin
  InstalledVersion := PreviousVersion;
  Result := True;
end;

{ The old copy has to let go of its own files before they can be replaced. LoopIt7 hides to
  the tray rather than closing, so asking it politely through the Restart Manager does not
  work and setup closes it outright. Settings are written as they change, so nothing is lost. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;

  if not CheckForMutexes(AppMutexName) then Exit;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName} /F', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { Give Windows a moment to release the file handles the process was holding. }
  Sleep(1200);

  if CheckForMutexes(AppMutexName) then
    Result := 'LoopIt7 is still running and setup could not close it. Quit it from the tray icon, then try again.';
end;

{ Runs on the Ready page, so a silent install skips it. That is deliberate: a scripted
  deployment should not start a 60 MB download nobody asked for, and whoever wrote the script
  is the right person to have put the runtime there already. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
  RuntimePath: String;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;
  if DesktopRuntimeInstalled then Exit;

  if MsgBox(
      'LoopIt7 needs the .NET 10 desktop runtime from Microsoft, which is not on this PC yet.' + #13#10#13#10 +
      'Setup can download and install it now. That is about 60 MB and Windows will ask for permission.' + #13#10#13#10 +
      'Download it now?',
      mbConfirmation, MB_YESNO) = IDNO then
  begin
    MsgBox('LoopIt7 will not start until the runtime is installed. You can get it from https://dotnet.microsoft.com/download/dotnet/10.0',
      mbInformation, MB_OK);
    Exit;
  end;

  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      RuntimePath := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');

      if not Exec(RuntimePath, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
        MsgBox('The runtime installer could not be started. LoopIt7 will be installed anyway, but it will not run until the runtime is present.',
          mbError, MB_OK)
      else if (ResultCode <> 0) and (ResultCode <> 3010) then
        MsgBox('The runtime installer reported code ' + IntToStr(ResultCode) + '. LoopIt7 will be installed anyway, but check the runtime before running it.',
          mbError, MB_OK);
    except
      MsgBox('The runtime could not be downloaded: ' + GetExceptionMessage + #13#10#13#10 +
        'LoopIt7 will be installed anyway. Install the .NET 10 desktop runtime by hand before running it.',
        mbError, MB_OK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

{ Tell the app that the cable on this machine is one setup installed. By the time the app
  runs, a cable installed thirty seconds ago and one the user has had for years look exactly
  alike, and only the first is ours to rename.
  Runs as soon as the cable's own installer closes, because the step after it names the cable
  and needs to know it is ours. Now that the offer is always made, this is where the line is
  held: the note is only written for a cable that arrived during this setup, or one we already
  owned. Never for the user's own VAC, and never when VAC's installer was cancelled. }
procedure WriteCableMarker();
var
  MarkerDir: String;
  Waited: Integer;
begin
  if not MarkerBefore then
  begin
    { A VAC device was already here, so reinstalling VAC over it made nothing ours. }
    if CablesBefore > 0 then Exit;

    { Windows creates the endpoints a few seconds after the driver's installer returns. }
    Waited := 0;
    while (PresentCables('{#CableDriver}') = 0) and (Waited < 30) do
    begin
      Sleep(1000);
      Waited := Waited + 1;
    end;

    { Still nothing: the installer was cancelled or failed. }
    if PresentCables('{#CableDriver}') = 0 then Exit;
  end;

  CableIsOursNow := True;

  MarkerDir := ExpandConstant('{autoappdata}\LoopIt7');
  if not DirExists(MarkerDir) then
    ForceDirectories(MarkerDir);

  SaveStringToFile(MarkerDir + '\installed-cable.txt', '{#CableDriver}' + #13#10 + '{#CableVersion}', False);
end;

{ The last page says whether the cable is ready, and under which name, since that name is
  what the user now looks for in Discord, OBS or a DAW. The app writes it after naming. }
procedure CurPageChanged(CurPageID: Integer);
var
  Named: AnsiString;
begin
#ifdef BundleCable
  { The first time the tasks page shows, and only then, so a choice the user makes survives
    going Back and Next again. Unticked when a working cable is already here, ticked otherwise,
    whatever an earlier install remembered. }
  if (CurPageID = wpSelectTasks) and not CableTaskDefaulted then
  begin
    CableTaskDefaulted := True;
    if WorkingCableHere() then
      WizardSelectTasks('!cable')
    else
      WizardSelectTasks('cable');
  end;
#endif

  if CurPageID <> wpFinished then Exit;
  if not WizardIsTaskSelected('cable') then Exit;

  if LoadStringFromFile(ExpandConstant('{userappdata}\LoopIt7\named-cable.txt'), Named) and (Named <> '') then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Your virtual cable is installed and named "' + String(Named) + '". Pick it as the output ' +
      'or microphone in Discord, OBS or your DAW to send audio into or out of LoopIt7.'
  else if (CablesBefore > 0) and not MarkerBefore then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Virtual Audio Cable was already on this PC, so LoopIt7 left its name alone, because other ' +
      'programs may be using it. To name it LoopIt7 Cable, open LoopIt7 and use the Devices page.'
  else if CableIsOursNow then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'The virtual cable is installed but could not be named yet. LoopIt7 names it the first ' +
      'time it starts.'
  else
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Virtual Audio Cable was not installed. If you cancelled its installer, run this setup ' +
      'again to get the cable.';
end;

{ Settings and presets live outside the install directory. Removing them is the user's call. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  { A silent uninstall must never stop on a question. Leaving the settings behind is the
    safe answer: a reinstall then picks the patchbay back up where it was. }
  if UninstallSilent then Exit;

  { The cable itself stays. It is a driver the user agreed to install and may well be using
    for something else by now; silently pulling it out from under them would be worse than
    leaving it. The note saying it was ours goes, so a later reinstall treats it as theirs. }
  DeleteFile(ExpandConstant('{autoappdata}\LoopIt7\installed-cable.txt'));

  DataDir := ExpandConstant('{userappdata}\LoopIt7');
  if not DirExists(DataDir) then Exit;

  if SuppressibleMsgBox('Remove your LoopIt7 routing setup and saved presets as well?',
      mbConfirmation, MB_YESNO, IDNO) = IDYES then
    DelTree(DataDir, True, True, True);
end;
