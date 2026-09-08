; LoopIt7 installer.
;
; Build it with build\build.ps1, which publishes the app first and then calls ISCC on
; this script. Installs per user by default so the common path never touches UAC; the
; wizard offers an all users install for anyone who wants one.

#define AppName        "LoopIt7"
#define AppVersion     "1.2.0"
#define AppPublisher   "SeventhSG"
#define AppUrl         "https://github.com/SeventhSG/LoopIt7"
#define AppExeName     "LoopIt7.exe"
#define RuntimeUrl     "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

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

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

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

var
  DownloadPage: TDownloadWizardPage;
  InstalledVersion: String;

{ The version already on this machine, per user or for all users, or empty when LoopIt7 has
  never been installed here. }
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

{ Settings and presets live outside the install directory. Removing them is the user's call. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  { A silent uninstall must never stop on a question. Leaving the settings behind is the
    safe answer: a reinstall then picks the patchbay back up where it was. }
  if UninstallSilent then Exit;

  DataDir := ExpandConstant('{userappdata}\LoopIt7');
  if not DirExists(DataDir) then Exit;

  if SuppressibleMsgBox('Remove your LoopIt7 routing setup and saved presets as well?',
      mbConfirmation, MB_YESNO, IDNO) = IDYES then
    DelTree(DataDir, True, True, True);
end;
