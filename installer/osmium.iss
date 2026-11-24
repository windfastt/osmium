; Osmium installer (Inno Setup 6.4+). Built by CI:
;   ISCC /DAppVersion=1.2.3 /DNumericVersion=1.2.3.0 /DArch=x64 installer\osmium.iss
; Expects the co-published App + CLI folder at ..\artifacts\publish\win-<Arch>.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#define AppExe "Osmium.App.exe"
#define CliExe "osmium.exe"
#define SourceDir "..\artifacts\publish\win-" + Arch

[Setup]
; Never change AppId: it is how upgrades find the previous installation.
AppId={{4D1B6E1C-7A0F-4E7B-9C1E-2B6F0A9E5D31}
AppName=Osmium
AppVersion={#AppVersion}
AppVerName=Osmium {#AppVersion}
AppPublisher=Osmium contributors
AppComments=Maximum-ratio file compressor
VersionInfoVersion={#NumericVersion}
DefaultDirName={autopf}\Osmium
DefaultGroupName=Osmium
DisableProgramGroupPage=yes
; Per-user by default (no admin prompt); the user may choose an all-users install.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=Osmium-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\assets\osmium.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Osmium
LicenseFile=..\LICENSE
ChangesAssociations=yes
ChangesEnvironment=yes
CloseApplications=force
RestartApplications=no
SetupMutex=OsmiumSetup,Global\OsmiumSetup
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#endif

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: startup; Description: "Start Osmium in the notification area when I sign in"; GroupDescription: "Integration:"
Name: contextmenu; Description: "Add Osmium to the Explorer context menu"; GroupDescription: "Integration:"
Name: assoc; Description: "Open .osa archives with Osmium"; GroupDescription: "Integration:"
Name: sendto; Description: "Add Osmium to the ""Send to"" menu"; GroupDescription: "Integration:"; Flags: unchecked; Check: not IsAdminInstallMode
Name: addtopath; Description: "Add the 'osmium' command-line tool to PATH"; GroupDescription: "Command line:"; Flags: unchecked
Name: firewall; Description: "Allow other PCs on private networks to use this PC as a cluster worker (firewall rule)"; GroupDescription: "Cluster:"; Flags: unchecked; Check: IsAdminInstallMode

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\assets\archive.ico"; DestDir: "{app}\icons"; Flags: ignoreversion
Source: "..\assets\osmium.ico"; DestDir: "{app}\icons"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Osmium"; Filename: "{app}\{#AppExe}"; AppUserModelID: "Osmium.Desktop"; Comment: "Maximum-ratio file compressor"
Name: "{autodesktop}\Osmium"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{usersendto}\Osmium (compress)"; Filename: "{app}\{#AppExe}"; Parameters: "--shell sendto"; Tasks: sendto

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Description: "Start Osmium"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "OsmiumQuit"
Filename: "{app}\{#AppExe}"; Parameters: "--unregister --scope {code:Scope}"; Flags: runhidden waituntilterminated; RunOnceId: "OsmiumUnregister"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Osmium cluster worker"""; Flags: runhidden waituntilterminated; RunOnceId: "OsmiumFirewall"; Check: IsAdminInstallMode

[Code]
function Scope(Param: String): String;
begin
  if IsAdminInstallMode then Result := 'machine' else Result := 'user';
end;

function Features(): String;
begin
  Result := '';
  if WizardIsTaskSelected('contextmenu') then Result := Result + 'contextmenu,';
  if WizardIsTaskSelected('assoc') then Result := Result + 'assoc,';
  if WizardIsTaskSelected('startup') then Result := Result + 'startup,';
end;

{ Ask a running Osmium to exit gracefully (it cancels nothing silently: jobs are stopped cleanly). }
procedure StopRunning();
var
  Code, I: Integer;
begin
  if CheckForMutexes('Osmium.App.Running') and FileExists(ExpandConstant('{app}\{#AppExe}')) then
  begin
    Exec(ExpandConstant('{app}\{#AppExe}'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    for I := 1 to 30 do
    begin
      if not CheckForMutexes('Osmium.App.Running') then Break;
      Sleep(500);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunning();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunning();
  Result := True;
end;

function EnvKey(): String;
begin
  if IsAdminInstallMode then
    Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else
    Result := 'Environment';
end;

procedure AddToPath(Dir: String);
var
  CurPath: String;
begin
  if not RegQueryStringValue(HKA, EnvKey(), 'Path', CurPath) then CurPath := '';
  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(CurPath) + ';') > 0 then Exit;
  if (CurPath <> '') and (Copy(CurPath, Length(CurPath), 1) <> ';') then CurPath := CurPath + ';';
  RegWriteExpandStringValue(HKA, EnvKey(), 'Path', CurPath + Dir);
end;

procedure RemoveFromPath(Dir: String);
var
  CurPath: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKA, EnvKey(), 'Path', CurPath) then Exit;
  CurPath := ';' + CurPath + ';';
  P := Pos(';' + Uppercase(Dir) + ';', Uppercase(CurPath));
  if P = 0 then Exit;
  Delete(CurPath, P, Length(Dir) + 1);
  CurPath := Copy(CurPath, 2, Length(CurPath) - 2);
  RegWriteExpandStringValue(HKA, EnvKey(), 'Path', CurPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  F: String;
begin
  if CurStep = ssPostInstall then
  begin
    { Clean previous registrations first so unticked tasks are really removed on upgrade. }
    Exec(ExpandConstant('{app}\{#AppExe}'), '--unregister --scope ' + Scope(''), '', SW_HIDE, ewWaitUntilTerminated, Code);
    F := Features();
    if F <> '' then
      if not Exec(ExpandConstant('{app}\{#AppExe}'), '--register --scope ' + Scope('') + ' --features ' + F, '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
        SuppressibleMsgBox('Explorer integration could not be registered. You can repair it later in Settings.', mbError, MB_OK, IDOK);
    if WizardIsTaskSelected('addtopath') then AddToPath(ExpandConstant('{app}'));
    if WizardIsTaskSelected('firewall') then
      Exec(ExpandConstant('{sys}\netsh.exe'),
        'advfirewall firewall add rule name="Osmium cluster worker" dir=in action=allow protocol=TCP localport=46510 profile=private program="' + ExpandConstant('{app}\{#AppExe}') + '"',
        '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Fallback in case --unregister could not run. }
    RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\Osmium.Archive');
    RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\*\shell\Osmium');
    RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\Directory\shell\Osmium');
    RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\Directory\Background\shell\Osmium');
    RegDeleteValue(HKA, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Osmium');
    RemoveFromPath(ExpandConstant('{app}'));
    if (not UninstallSilent) and (MsgBox('Also remove Osmium settings, rules, job history and logs?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      DelTree(ExpandConstant('{userappdata}\Osmium'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\Osmium'), True, True, True);
    end;
  end;
end;
