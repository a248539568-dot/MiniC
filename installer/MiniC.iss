#define AppName "MiniC"
#define AppVersion "0.58.9"
#define AppExeName "MiniC.exe"
#define PublishDir "..\dist\MiniC-v0.58.9-win-x64"

[Setup]
AppId={{B9E2891D-9EC8-4C33-8B0B-58F25767C4D1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=MiniC
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=MiniC-Setup-v{#AppVersion}
SetupIconFile=..\src\MiniC\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=MiniC
VersionInfoDescription=MiniC 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Check: StartMenuShortcutMissing
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon; Check: DesktopShortcutMissing

[InstallDelete]
Type: files; Name: "{app}\MiniC桌面.*"
Type: files; Name: "{app}\DeskNest.*"
Type: files; Name: "{autodesktop}\MiniC桌面.lnk"
Type: files; Name: "{autoprograms}\MiniC桌面.lnk"
Type: files; Name: "{autodesktop}\DeskNest.lnk"
Type: files; Name: "{autoprograms}\DeskNest.lnk"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "MiniC"; ValueType: string; ValueData: """{app}\{#AppExeName}"" --autostart"; Flags: uninsdeletevalue; Check: ShouldMigrateStartup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "MiniC桌面"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "DeskNest"; Flags: deletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  { 与历史版本共享互斥量，避免升级期间两个桌面层同时运行。 }
  AppMutexName = 'Local\DeskNest.SingleInstance.4E31D7B6';

var
  LegacyStartupEnabled: Boolean;

function InitializeSetup(): Boolean;
begin
  LegacyStartupEnabled :=
    RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MiniC桌面') or
    RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DeskNest');
  Result := True;
end;

function ShouldMigrateStartup(): Boolean;
begin
  Result := LegacyStartupEnabled;
end;

function FindWindow(lpClassName, lpWindowName: String): HWND;
  external 'FindWindowW@user32.dll stdcall';
function FindWindowEx(hwndParent, hwndChildAfter: HWND;
  lpszClass, lpszWindow: String): HWND;
  external 'FindWindowExW@user32.dll stdcall';
function ShowWindow(hWnd: HWND; nCmdShow: Integer): Boolean;
  external 'ShowWindow@user32.dll stdcall';

procedure RestoreExplorerDesktopIcons();
var
  ProgmanWindow: HWND;
  WorkerWindow: HWND;
  ShellViewWindow: HWND;
  ListViewWindow: HWND;
begin
  ProgmanWindow := FindWindow('Progman', '');
  ShellViewWindow := FindWindowEx(ProgmanWindow, 0, 'SHELLDLL_DefView', '');
  if ShellViewWindow = 0 then
  begin
    WorkerWindow := 0;
    repeat
      WorkerWindow := FindWindowEx(0, WorkerWindow, 'WorkerW', '');
      if WorkerWindow <> 0 then
        ShellViewWindow := FindWindowEx(WorkerWindow, 0, 'SHELLDLL_DefView', '');
    until (WorkerWindow = 0) or (ShellViewWindow <> 0);
  end;

  if ShellViewWindow <> 0 then
  begin
    ListViewWindow := FindWindowEx(ShellViewWindow, 0, 'SysListView32', '');
    if ListViewWindow <> 0 then
      ShowWindow(ListViewWindow, 5);
  end;
end;

function StartMenuShortcutMissing(): Boolean;
begin
  Result := not FileExists(ExpandConstant('{autoprograms}\{#AppName}.lnk'));
end;

function DesktopShortcutMissing(): Boolean;
begin
  Result := not FileExists(ExpandConstant('{autodesktop}\{#AppName}.lnk'));
end;

procedure ForceCloseApplication(const ImageName: String);
var
  ResultCode: Integer;
begin
  { 进程未运行时 taskkill 会返回非零结果，此处属于正常情况。 }
  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/F /IM "' + ImageName + '"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { 替换运行中的旧版本前，先确保 Explorer 桌面图标层可见。 }
  RestoreExplorerDesktopIcons();
  { 支持一键升级：同时结束当前程序名和历史程序名对应的进程。 }
  ForceCloseApplication('{#AppExeName}');
  ForceCloseApplication('MiniC桌面.exe');
  ForceCloseApplication('DeskNest.exe');
  Sleep(350);

  if CheckForMutexes(AppMutexName) then
    Result := '无法自动结束正在运行的 MiniC，请稍后重试。'
  else
    Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  { 卸载前仍要求从托盘正常退出，保证已收纳文件安全返回桌面。 }
  Result := not CheckForMutexes(AppMutexName);
  if not Result then
    MsgBox('请先从托盘退出 MiniC，再继续卸载。这样收纳的文件会安全返回桌面。',
      mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MiniC');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MiniC桌面');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DeskNest');
  end;
end;
