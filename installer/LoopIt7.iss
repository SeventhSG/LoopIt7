; LoopIt7 installer.
;
; Build it with build\build.ps1, which publishes the app first and then calls ISCC on
; this script. Installs per user by default so the common path never touches UAC; the
; wizard offers an all users install for anyone who wants one.

#define AppName        "LoopIt7"
#define AppVersion     "1.3.0"
#define AppPublisher   "SeventhSG"
#define AppUrl         "https://github.com/SeventhSG/LoopIt7"
#define AppExeName     "LoopIt7.exe"
#define RuntimeUrl     "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

; VB-Audio's cable, bundled only when their redistributable has been placed in
; installer\cable\. Shipping it needs a distribution agreement from VB-Audio, so the setup
; is written to build and work identically without it: no file, no task, and the app falls
; back to sending the user to vb-audio.com itself.
;
; Setup installs the driver and nothing else. The renaming happens on the app's first run,
; from the note this script leaves in installed-cable.txt: the endpoints do not exist until
; the driver's own installer has finished, and the app is the thing that knows how to put
; every name back when it is uninstalled.
#define CableSetup     "cable\VBCABLE_Setup_x64.exe"
; The driver string VB-Audio's plain cable reports, and the thing setup looks for. Their
; A+B pack and VoiceMeeter's VAIO are different drivers that also say VB-Audio, so this
; is deliberately the whole product name and not just the vendor.
#define CableDriver    "VB-Audio Virtual Cable"
; Bump this whenever the redistributable in installer\cable\ is replaced with a newer
; one. It is what tells an upgrade that the cable already on the machine is out of date.
#define CableVersion   "1.0.3.8"
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
; Offered whenever this exact cable is missing, even on a machine that already has cables of
; another make. Those belong to somebody else's setup, and a cable LoopIt7 may not rename is a
; cable that cannot carry the user's own name into Discord.
Name: "cable"; Description: "Install a virtual audio cable. LoopIt7 names it ""LoopIt7 Cable"", and that is what you pick in a DAW, Discord or OBS to play into LoopIt7"; GroupDescription: "Virtual audio cable:"; Check: not CableInstalled
; A cable LoopIt7 installed is LoopIt7's to keep current. Without this the check above
; would see a cable present on every later upgrade and quietly skip it forever.
Name: "cableupdate"; Description: "Update the virtual audio cable LoopIt7 installed"; GroupDescription: "Virtual audio cable:"; Check: OurCableIsOutOfDate
#endif

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.txt"; DestDir: "{app}"; Flags: ignoreversion
#ifdef BundleCable
Source: "{#CableSetup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Tasks: cable cableupdate
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
#ifdef BundleCable
; VB-Audio's installer asks for administrator rights itself, which is why this goes through
; the shell rather than being run directly: LoopIt7's own setup stays unelevated.
Filename: "{tmp}\VBCABLE_Setup_x64.exe"; Parameters: "-i -h"; StatusMsg: "Installing the virtual audio cable..."; Flags: shellexec waituntilterminated; Tasks: cable cableupdate
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

var
  DownloadPage: TDownloadWizardPage;
  InstalledVersion: String;

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

{ Whether the cable LoopIt7 put here is older than the one this setup carries. Only ever true
  for a cable we installed: one the user had already is not ours to update. }
function OurCableIsOutOfDate(): Boolean;
var
  Marker: String;
begin
  Marker := ExpandConstant('{autoappdata}\LoopIt7\installed-cable.txt');
  if not FileExists(Marker) then
    Marker := ExpandConstant('{commonappdata}\LoopIt7\installed-cable.txt');

  Result := FileExists(Marker) and (OurCableVersion() <> '{#CableVersion}');
end;

{ Whether VB-Audio's plain cable is already on this machine.
  Deliberately not "any cable at all". Somebody running Wave Link, NVIDIA Broadcast or
  VoiceMeeter has cables, but they are wired into their own setup and are not ours to rename,
  so without one of our own LoopIt7 could never put its name on anything. They get the offer
  too. The one case to avoid is this exact cable already being here: running its installer
  again would not add a second, it would hand us the user's own cable to rename. }
function CableInstalled(): Boolean;
var
  Endpoints: TArrayOfString;
  I: Integer;
  Iface: String;
begin
  Result := False;

  if not RegGetSubkeyNames(HKLM64, RenderEndpoints, Endpoints) then Exit;

  for I := 0 to GetArrayLength(Endpoints) - 1 do
  begin
    if RegQueryStringValue(HKLM64, RenderEndpoints + '\' + Endpoints[I] + '\Properties',
        InterfaceNameValue, Iface) then
    begin
      if Pos(Lowercase('{#CableDriver}'), Lowercase(Iface)) > 0 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
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
  alike, and only the first is ours to rename. }
procedure CurStepChanged(CurStep: TSetupStep);
#ifdef BundleCable
var
  MarkerDir: String;
#endif
begin
  if CurStep <> ssPostInstall then Exit;

#ifdef BundleCable
  if not (WizardIsTaskSelected('cable') or WizardIsTaskSelected('cableupdate')) then Exit;

  MarkerDir := ExpandConstant('{autoappdata}\LoopIt7');
  if not DirExists(MarkerDir) then
    ForceDirectories(MarkerDir);

  SaveStringToFile(MarkerDir + '\installed-cable.txt', '{#CableDriver}' + #13#10 + '{#CableVersion}', False);
#endif
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
