; Aster for Plex for Windows: the container's program, run as a Windows service on the PC beside Plex.
;
; Built by .github/workflows/plex-image.yml from a self-contained win-x64 publish of Aster.Plex:
;   iscc /DAppVersion=0.2.0 /DSource=..\publish AsterForPlex.iss
;
; - The program goes in Program Files; its data (settings.json, users\, as /config in Docker) in
;   ProgramData\Aster for Plex, which only the service, SYSTEM and administrators can read, as it holds the owner's
;   Plex token. Updates and uninstalls keep it unless asked.
; - The service, AsterForPlex, runs as LocalService, starts with Windows, and restarts if it stops.
; - A firewall rule lets the Aster apps on the network reach it.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Source
  #define Source "..\publish"
#endif

#define Service "AsterForPlex"
#define Rule "Aster for Plex"
#define Data "{commonappdata}\Aster for Plex"

[Setup]
AppId={{5B0F3C1E-8A7D-4F1B-9C2E-6D4A8B7E3F10}
AppName=Aster for Plex
AppVersion={#AppVersion}
AppVerName=Aster for Plex {#AppVersion}
AppPublisher=Aster Media Player
AppPublisherURL=https://github.com/AsterMediaPlayer/aster-companion-plugins
AppSupportURL=https://github.com/AsterMediaPlayer/aster-companion-plugins/tree/main/AsterPlex
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Aster for Plex
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 / Server 2019, the oldest .NET 10 runs on.
MinVersion=10.0.17763
OutputBaseFilename=AsterForPlex-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
UninstallDisplayName=Aster for Plex
UninstallDisplayIcon={app}\Aster.Plex.exe

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Excludes: "*.pdb,web.config"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{#Data}"; Flags: uninsneveruninstall

; The settings page, from the Start menu.
[INI]
Filename: "{app}\Aster for Plex settings.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://localhost:{code:Port}/aster/"

[Icons]
Name: "{autoprograms}\Aster for Plex"; Filename: "{app}\Aster for Plex settings.url"; Comment: "Aster for Plex's settings page"

[UninstallDelete]
Type: files; Name: "{app}\Aster for Plex settings.url"

[Run]
Filename: "http://localhost:{code:Port}/aster/"; Description: "Open the settings page, to sign in with Plex"; Flags: shellexec postinstall skipifsilent nowait

[Code]
var
  PortPage: TInputQueryWizardPage;

function Port(Param: String): String;
begin
  Result := Trim(PortPage.Values[0]);
end;

procedure InitializeWizard;
begin
  PortPage := CreateInputQueryPage(wpSelectDir, 'Port',
    'Which port should Aster for Plex use?',
    'The Aster apps and the settings page reach Aster for Plex at this port on this PC. Keep 8098 unless something else already uses it.');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := GetPreviousData('Port', '8098');
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'Port', Port(''));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Value: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    Value := StrToIntDef(Port(''), 0);
    if (Value < 1) or (Value > 65535) then
    begin
      MsgBox('The port is a number from 1 to 65535, such as 8098.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function Run(const FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

function Sc(const Params: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\sc.exe'), Params);
end;

function Netsh(const Params: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\netsh.exe'), Params);
end;

// Stop-Service waits until it has stopped, so its files can be replaced; sc stop doesn't.
procedure StopService;
begin
  Run(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Stop-Service -Name {#Service} -Force -ErrorAction SilentlyContinue"');
end;

function ServiceExists: Boolean;
begin
  // 1060: no such service.
  Result := Sc('query {#Service}') <> 1060;
end;

procedure SetUpService;
var
  BinPath: String;
begin
  BinPath := 'binPath= "\"' + ExpandConstant('{app}\Aster.Plex.exe') + '\" --port ' + Port('') + '"';
  if ServiceExists then
    Sc('config {#Service} ' + BinPath + ' start= delayed-auto obj= "NT AUTHORITY\LocalService" password= "" DisplayName= "Aster for Plex"')
  else
    Sc('create {#Service} ' + BinPath + ' start= delayed-auto obj= "NT AUTHORITY\LocalService" password= "" DisplayName= "Aster for Plex"');
  Sc('description {#Service} "Tracking, Add to Watchlist and Channels for Aster Media Player on this PC''s Plex server. Settings: http://localhost:' + Port('') + '/aster/"');
  Sc('failure {#Service} reset= 86400 actions= restart/5000/restart/5000/restart/60000');

  // Its data: SYSTEM and administrators in full, the service (LocalService, S-1-5-19) to read and write; no one else.
  Run(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{#Data}') + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-19:(OI)(CI)M');

  Netsh('advfirewall firewall delete rule name="{#Rule}"');
  Netsh('advfirewall firewall add rule name="{#Rule}" description="The Aster apps reach Aster for Plex here." dir=in action=allow protocol=TCP localport=' + Port('') + ' program="' + ExpandConstant('{app}\Aster.Plex.exe') + '" profile=any');

  if Sc('start {#Service}') <> 0 then
    SuppressibleMsgBox('Aster for Plex is installed, but its service didn''t start. Check that nothing else uses port ' + Port('') +
      ', then start "Aster for Plex" in Services, or run the installer again with another port.', mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    StopService
  else if CurStep = ssPostInstall then
    SetUpService;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopService;
    Sc('delete {#Service}');
    Netsh('advfirewall firewall delete rule name="{#Rule}"');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent and
      (MsgBox('Also remove Aster for Plex''s settings and everyone''s linked accounts?' + #13#10#13#10 +
        'Keep them to pick up where you left off if you install it again.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{#Data}'), True, True, True);
  end;
end;
