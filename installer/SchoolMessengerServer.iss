#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PackageDirectory
  #error PackageDirectory is required
#endif
#ifndef ArtifactDirectory
  #error ArtifactDirectory is required
#endif
#ifndef InstallerAppId
  #define InstallerAppId "{AE5EF18D-119A-4E9F-A7F3-E078F33E2B18}"
#endif
#define NumericVersion GetVersionNumbersString(PackageDirectory + "\server\SchoolMessenger.Server.exe")
#ifndef StartupEntryName
  #define StartupEntryName "YeoyangMessengerServerManager"
#endif
#ifndef DesktopLinkName
  #define DesktopLinkName "교무톡 관리자"
#endif

[Setup]
AppId={{#InstallerAppId}
AppName=교무톡 관리자 및 서버
AppVersion={#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=교무톡 프로젝트
DefaultDirName={autopf}\YeoyangMessengerServer
DefaultGroupName=교무톡 관리자 및 서버
#ifdef InstallerTest
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#ArtifactDirectory}
OutputBaseFilename=yymessenger-{#AppVersion}-server-setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern windows11
SetupIconFile=..\src\SchoolMessenger.Shared\Branding\brand_mark.ico
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=no
UsePreviousAppDir=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
InfoBeforeFile=server-information.txt

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Types]
Name: "office"; Description: "교내 업무 서버 PC · 관리자 프로그램 포함"
Name: "announcements"; Description: "학생·학부모용 학교 소식 서버 PC"
Name: "administrator"; Description: "관리자 전용 PC · 서버 설치 없이 학교 서버에 연결"
Name: "custom"; Description: "사용자 지정 · 시험 환경"; Flags: iscustom

[Components]
Name: "office"; Description: "교내 업무 서버 · 교직원 메시지·업무"; Types: office
Name: "announcements"; Description: "학교 소식 서버 · 학생·학부모 앱"; Types: announcements
Name: "admin"; Description: "관리자 프로그램 · 서버·구성원·학교 설정"; Types: office announcements administrator custom; Flags: fixed

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 관리자 프로그램 바로가기 만들기"; GroupDescription: "바로가기:"; Flags: unchecked
Name: "autostart"; Description: "Windows 로그인 시 관리자 프로그램 자동 실행"; GroupDescription: "자동 실행:"; Flags: unchecked

[Files]
Source: "{#PackageDirectory}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\server\*"; DestDir: "{app}\server"; Components: office; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDirectory}\announcement-server\*"; DestDir: "{app}\announcement-server"; Components: announcements; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDirectory}\client\*"; DestDir: "{app}\admin"; Components: admin; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDirectory}\scripts\*"; DestDir: "{app}\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDirectory}\config\*"; DestDir: "{app}\config"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDirectory}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDirectory}\VERSION.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDirectory}\docs\server-installer.md"; DestDir: "{app}"; DestName: "서버 설치 안내.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\서버 설치·설정 안내"; Filename: "{app}\서버 설치 안내.txt"
Name: "{group}\서버 프로그램 폴더"; Filename: "{app}"
Name: "{group}\관리자 프로그램"; Filename: "{app}\admin\SchoolMessenger.exe"; Parameters: "--server-manager"; Components: admin
#ifdef InstallerTest
Name: "{userdesktop}\{#DesktopLinkName}"; Filename: "{app}\admin\SchoolMessenger.exe"; Parameters: "--server-manager"; Components: admin; Tasks: desktopicon
#else
Name: "{commondesktop}\{#DesktopLinkName}"; Filename: "{app}\admin\SchoolMessenger.exe"; Parameters: "--server-manager"; Components: admin; Tasks: desktopicon
#endif
Name: "{group}\서버 프로그램 제거"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\admin\SchoolMessenger.exe"; Parameters: "--server-manager --configure-startup enable --startup-entry-name {#StartupEntryName}"; Components: admin; Tasks: autostart; Flags: runhidden runasoriginaluser
Filename: "{app}\admin\SchoolMessenger.exe"; Parameters: "--server-manager --configure-startup disable --startup-entry-name {#StartupEntryName}"; Components: admin; Tasks: not autostart; Flags: runhidden runasoriginaluser
Filename: "{app}\admin\SchoolMessenger.exe"; Parameters: "--server-manager"; Components: admin; Description: "관리자 프로그램 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\서버 설치 안내.txt"; Description: "서버 설치·설정 안내 열기"; Flags: shellexec postinstall skipifsilent runasoriginaluser unchecked

[InstallDelete]
Type: files; Name: "{group}\이 PC에서만 시험 서버 켜기.lnk"
Type: files; Name: "{group}\관리자 메신저.lnk"
Type: files; Name: "{group}\서버 관리 프로그램.lnk"

[Code]
function MaintenanceBlock(Uninstalling: Boolean): String;
var Locator, Services, Processes, Process, Scheduler, Tasks, Task, Actions, Action: Variant;
    I, J: Integer; Base, Executable, Arguments, Name: String;
begin
  Result := '';
  Base := Lowercase(AddBackslash(ExpandConstant('{app}')));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name=''SchoolMessenger.Server.exe'' OR Name=''SchoolMessenger.AnnouncementServer.exe'' OR Name=''SchoolMessenger.exe''');
    for I := 0 to Processes.Count - 1 do begin
      Process := Processes.ItemIndex(I);
      if not VarIsNull(Process.ExecutablePath) then begin
        Executable := Process.ExecutablePath;
        Executable := Lowercase(Executable);
        if Pos(Base, Executable) = 1 then begin
          Result := '이 설치 경로의 서버 또는 관리자 메신저가 실행 중입니다. 운영 담당자가 자동 시작·감시 작업을 중지하고 정상 종료한 뒤 다시 시도하세요. 프로그램을 강제 종료하지 않습니다.';
          Exit;
        end;
      end;
    end;
    Scheduler := CreateOleObject('Schedule.Service');
    Scheduler.Connect;
    Tasks := Scheduler.GetFolder('\').GetTasks(1);
    for I := 1 to Tasks.Count do begin
      Task := Tasks.Item(I);
      Name := Task.Name;
      Name := Lowercase(Name);
      if Pos('yeoyangmessenger-', Name) = 1 then begin
        Actions := Task.Definition.Actions;
        for J := 1 to Actions.Count do begin
          Action := Actions.Item(J);
          begin
            Arguments := Action.Arguments;
            Arguments := Lowercase(Arguments);
            Executable := Action.Path;
            Executable := Lowercase(Executable);
            if (Pos(Base, Arguments) > 0) or (Pos(Base, Executable) = 1) then
              if Uninstalling or Task.Enabled or (Task.State = 4) then begin
                Result := '이 설치 경로에 연결된 서버 예약 작업이 있습니다. 업데이트 전에는 시작·감시 작업을 모두 중지·사용 안 함으로 바꾸세요. 제거 전에는 담당자가 해당 작업을 등록 해제해야 합니다. 다른 경로의 작업은 변경하지 않습니다.';
                Exit;
              end;
          end;
        end;
      end;
    end;
  except
    Result := '실행 중인 서버 또는 예약 작업을 확인할 수 없습니다. Windows 관리 권한·WMI·작업 스케줄러를 담당자와 확인한 뒤 다시 시도하세요.';
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Command: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupEntryName}', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\admin\SchoolMessenger.exe') + '" --server-manager --startup') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupEntryName}');
end;

function InitializeSetup(): Boolean;
var Installed: String; OldVersion, NewVersion: Int64; Suffix: Integer;
begin
  Result := True;
  if RegQueryStringValue(HKA, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#InstallerAppId}_is1', 'DisplayVersion', Installed) then begin
    Suffix := Pos('-', Installed);
    if Suffix > 0 then Installed := Copy(Installed, 1, Suffix - 1);
    if not StrToVersion(Installed, OldVersion) or not StrToVersion('{#NumericVersion}', NewVersion) then Result := False
    else Result := ComparePackedVersion(OldVersion, NewVersion) <= 0;
    if not Result then SuppressibleMsgBox('설치된 버전이 더 새롭거나 버전을 확인할 수 없습니다. 최신 서버 설치파일을 사용하세요.', mbError, MB_OK, IDOK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := MaintenanceBlock(False);
end;

function InitializeUninstall(): Boolean;
var Block: String;
begin
  Block := MaintenanceBlock(True);
  Result := Block = '';
  if not Result then SuppressibleMsgBox(Block, mbError, MB_OK, IDOK);
end;
