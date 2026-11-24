; UniDashboard installer (Inno Setup 6). Build with installer\build-installer.ps1 (it passes AppVersion and PublishDir).
; Per-user install into %LOCALAPPDATA%\UniDashboard\bin, no admin rights. User data lives one level up
; (%LOCALAPPDATA%\UniDashboard: database, tokens, logs) and survives updates; uninstalling asks before deleting it.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{8B3C2F6A-5D1E-4A57-9C0B-2F5E7D1A4C93}
AppName=UniDashboard
AppVersion={#AppVersion}
AppVerName=UniDashboard {#AppVersion}
AppPublisher=UniDashboard contributors
AppPublisherURL=https://github.com/TheRealCheeseLord/UniDashboard
AppSupportURL=https://github.com/TheRealCheeseLord/UniDashboard/issues
AppUpdatesURL=https://github.com/TheRealCheeseLord/UniDashboard/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\UniDashboard\bin
DisableDirPage=yes
DefaultGroupName=UniDashboard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=UniDashboard-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\UniDashboard.App.exe
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[InstallDelete]
; The folder only holds the program (data is one level up), so start clean: no stale files from older versions.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\UniDashboard"; Filename: "{app}\UniDashboard.App.exe"
Name: "{autodesktop}\UniDashboard"; Filename: "{app}\UniDashboard.App.exe"; Tasks: desktopicon

[Run]
; Interactive install: offer to start the app. Silent update (started by the app itself with /LAUNCH): start the new version.
Filename: "{app}\UniDashboard.App.exe"; Description: "Launch UniDashboard"; Flags: nowait postinstall skipifsilent
; Started through Explorer so the app does not inherit the installer's process state (launched directly it fails with "untrusted mount point").
Filename: "{win}\explorer.exe"; Parameters: """{app}\UniDashboard.App.exe"""; Flags: nowait; Check: WantsLaunch

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM UniDashboard.Agent.exe"; Flags: runhidden; RunOnceId: "StopAgent"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM UniDashboard.App.exe"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""UniDashboard Agent"" /F"; Flags: runhidden; RunOnceId: "RemoveAgentTask"

[Code]
function WantsLaunch: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/LAUNCH') = 0 then Result := True;
end;

{ The hidden background agent has no window, so Windows cannot ask it to close: stop it before files are replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM UniDashboard.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure DeleteUserData;
var
  Root, Path: String;
  FR: TFindRec;
  ResultCode: Integer;
begin
  Root := ExpandConstant('{localappdata}\UniDashboard');
  if FindFirst(Root + '\*', FR) then
  try
    repeat
      if (FR.Name <> '.') and (FR.Name <> '..') and (CompareText(FR.Name, 'bin') <> 0) then
      begin
        Path := Root + '\' + FR.Name;
        if (FR.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then DelTree(Path, True, True, True)
        else DeleteFile(Path);
      end;
    until not FindNext(FR);
  finally
    FindClose(FR);
  end;
  { the saved u:account password }
  Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:UniDashboard/univie', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox('Also delete your UniDashboard data (database, saved logins, logs)?' + #13#10 + #13#10 +
              'Choose No to keep it, for example if you plan to reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DeleteUserData;
end;
