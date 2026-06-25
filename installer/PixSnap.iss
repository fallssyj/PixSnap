; PixSnap 安装包脚本（Inno Setup 6）
; 构建：scripts\build-installer.ps1

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef StagingDir
  #define StagingDir "staging"
#endif

#define MyAppName "PixSnap"
#define MyAppExeName "PixSnap.exe"
#define MyAppPublisher "fallssyj"
#define MyAppURL "https://github.com/fallssyj/PixSnap"
#define MyAppSupportURL "https://github.com/fallssyj/PixSnap/issues"

[Setup]
AppId={{A7E3C4B2-9F1D-4E8A-B6C5-2D1F0E9A8B7C}
AppName={#MyAppName}
AppVerName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppSupportURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=
OutputDir=output
OutputBaseFilename=PixSnap-Setup-{#MyAppVersion}-x64
SetupIconFile=..\src\PixSnap\PixSnap\Assets\icons\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=lowest
AppMutex=Local\PixSnap_SingleInstance,{#MyAppName}
ShowLanguageDialog=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: checkedonce
Name: "launchapp"; Description: "安装完成后启动 {#MyAppName}"; GroupDescription: "附加任务:"; Flags: checkedonce

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent; Tasks: launchapp

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\PixSnap"

[Messages]
chinesesimplified.SetupWindowTitle=安装 - {#MyAppName}
chinesesimplified.WelcomeLabel2=这将在您的计算机上安装 [name]。%n%nPixSnap 是一款截图、录屏与本地 AI 编辑工具，需要 Windows 10 19041 及以上版本。

[Code]
function IsAppRunning(): Boolean;
begin
  Result := CheckForMutexes('Local\PixSnap_SingleInstance');
end;

function PromptCloseRunningApp(): Boolean;
var
  ResultCode: Integer;
begin
  if not IsAppRunning() then
  begin
    Result := True;
    Exit;
  end;

  if MsgBox(
    'PixSnap 正在运行。' + #13#10#13#10 +
    '请先退出程序（右键系统托盘图标 → 退出），或选择「是」由卸载程序结束进程后继续。',
    mbConfirmation, MB_YESNO) = IDNO then
  begin
    Result := False;
    Exit;
  end;

  Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);

  if IsAppRunning() then
  begin
    MsgBox('无法结束 PixSnap。请手动退出程序后，重新运行卸载程序。', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

function InitializeSetup(): Boolean;
begin
  Result := PromptCloseRunningApp();
end;

function InitializeUninstall(): Boolean;
begin
  Result := PromptCloseRunningApp();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PixSnap');

    DataDir := ExpandConstant('{localappdata}\PixSnap');
    if DirExists(DataDir) then
      DelTree(DataDir, True, True, True);
  end;
end;
