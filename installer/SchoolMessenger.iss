#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef ClientDirectory
  #error ClientDirectory is required
#endif
#ifndef ArtifactDirectory
  #error ArtifactDirectory is required
#endif
#ifndef InstallerAppId
  #define InstallerAppId "{4E29190A-53D1-491F-BB35-226A349FF37E}"
#endif
#define NumericVersion GetVersionNumbersString(ClientDirectory + "\SchoolMessenger.exe")
#ifndef StartupEntryName
  #define StartupEntryName "YeoyangSchoolMessenger"
#endif
#ifndef DesktopLinkName
  #define DesktopLinkName "교무톡"
#endif

[Setup]
AppId={{#InstallerAppId}
AppName=교무톡
AppVersion={#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher=교무톡 프로젝트
AppPublisherURL=https://github.com/yd043105-pixel/yymessenger
DefaultDirName={localappdata}\Programs\YeoyangSchoolMessenger
DefaultGroupName=교무톡
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#ArtifactDirectory}
OutputBaseFilename=yymessenger-{#AppVersion}-setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern windows11
SetupIconFile=..\src\SchoolMessenger.Shared\Branding\brand_mark.ico
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=no
UsePreviousAppDir=yes
UninstallDisplayIcon={app}\SchoolMessenger.exe
AppMutex=Local\YeoyangSchoolMessenger.Running
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면 바로가기 만들기"; GroupDescription: "바로가기:"; Flags: unchecked
Name: "autostart"; Description: "Windows 로그인 시 메신저 자동 실행"; GroupDescription: "자동 실행:"; Flags: unchecked

[Files]
Source: "{#ClientDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\교무톡"; Filename: "{app}\SchoolMessenger.exe"
Name: "{group}\교무톡 제거"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#DesktopLinkName}"; Filename: "{app}\SchoolMessenger.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SchoolMessenger.exe"; Parameters: "--configure-startup enable --startup-entry-name {#StartupEntryName}"; Tasks: autostart; Flags: runhidden
Filename: "{app}\SchoolMessenger.exe"; Parameters: "--configure-startup disable --startup-entry-name {#StartupEntryName}"; Tasks: not autostart; Flags: runhidden
Filename: "{app}\SchoolMessenger.exe"; Description: "교무톡 실행"; Flags: nowait postinstall skipifsilent

[Messages]
SetupAppRunningError=교무톡가 실행 중입니다.%n%n작업 표시줄 알림 영역의 메신저 아이콘을 우클릭하여 '종료'한 뒤 다시 시도하세요.

[Code]
function InitializeSetup(): Boolean;
var Installed: String; OldVersion, NewVersion: Int64; Suffix: Integer;
begin
  Result := True;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#InstallerAppId}_is1', 'DisplayVersion', Installed) then begin
    Suffix := Pos('-', Installed);
    if Suffix > 0 then Installed := Copy(Installed, 1, Suffix - 1);
    if not StrToVersion(Installed, OldVersion) or not StrToVersion('{#NumericVersion}', NewVersion) then begin
      SuppressibleMsgBox('설치된 버전을 확인할 수 없습니다. 학교 담당자에게 문의하세요.', mbError, MB_OK, IDOK);
      Result := False;
    end else if ComparePackedVersion(OldVersion, NewVersion) > 0 then begin
      SuppressibleMsgBox('더 새로운 버전이 설치되어 있습니다. 최신 설치 파일을 사용하세요.', mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var StartupCommand: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupEntryName}', StartupCommand) then
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\SchoolMessenger.exe') + '" --startup') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#StartupEntryName}');
end;
