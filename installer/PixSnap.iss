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
#define MyAppId "{A7E3C4B2-9F1D-4E8A-B6C5-2D1F0E9A8B7C}"
#define MyAppPublisher "fallssyj"
#define MyAppURL "https://github.com/fallssyj/PixSnap"
#define MyAppSupportURL "https://github.com/fallssyj/PixSnap/issues"

[Setup]
AppId={{A7E3C4B2-9F1D-4E8A-B6C5-2D1F0E9A8B7C}}
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
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.PDB"

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
const
  UninstallRegSubkey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1';

function IsAppRunning(): Boolean;
begin
  Result := CheckForMutexes('Local\PixSnap_SingleInstance');
end;

function GetInstalledUninstallString(): String;
var
  s: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallRegSubkey, 'UninstallString', s) or
     RegQueryStringValue(HKLM, UninstallRegSubkey, 'UninstallString', s) then
    Result := s;
end;

function GetInstalledDisplayVersion(): String;
var
  s: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstallRegSubkey, 'DisplayVersion', s) or
     RegQueryStringValue(HKLM, UninstallRegSubkey, 'DisplayVersion', s) then
    Result := s;
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
    '请先退出程序（右键系统托盘图标 → 退出），或选择「是」由安装程序结束进程后继续。',
    mbConfirmation, MB_YESNO) = IDNO then
  begin
    Result := False;
    Exit;
  end;

  Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);

  if IsAppRunning() then
  begin
    MsgBox('无法结束 PixSnap。请手动退出程序后，重新运行安装程序。', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

function ParseVersionPart(var S: String): Integer;
var
  P: Integer;
  Part: String;
begin
  P := Pos('.', S);
  if P > 0 then
  begin
    Part := Copy(S, 1, P - 1);
    Delete(S, 1, P);
  end
  else
  begin
    Part := S;
    S := '';
  end;
  Result := StrToIntDef(Part, 0);
end;

function IsVersionGreater(const A, B: String): Boolean;
var
  SA, SB: String;
  I, VA, VB: Integer;
begin
  SA := A;
  SB := B;
  for I := 1 to 4 do
  begin
    VA := ParseVersionPart(SA);
    VB := ParseVersionPart(SB);
    if VA > VB then
    begin
      Result := True;
      Exit;
    end;
    if VA < VB then
    begin
      Result := False;
      Exit;
    end;
  end;
  Result := False;
end;

function IsAlreadyInstalled(): Boolean;
begin
  Result := RegKeyExists(HKCU, UninstallRegSubkey) or RegKeyExists(HKLM, UninstallRegSubkey);
end;

function PromptDowngradeIfNeeded(): Boolean;
var
  InstalledVersion: String;
begin
  Result := True;
  if not IsAlreadyInstalled() then
    Exit;

  InstalledVersion := GetInstalledDisplayVersion();
  if (InstalledVersion <> '') and IsVersionGreater(InstalledVersion, '{#MyAppVersion}') then
  begin
    MsgBox(
      '已安装较新版本 PixSnap（' + InstalledVersion + '）。' + #13#10 +
      '无法安装旧版本（{#MyAppVersion}）。',
      mbError, MB_OK);
    Result := False;
  end;
end;

function PromptReinstallOrUninstall(): Boolean;
var
  Response: Integer;
  UninstallCmd: String;
  ResultCode: Integer;
  InstalledVersion: String;
  VersionLine: String;
begin
  if not IsAlreadyInstalled() then
  begin
    Result := True;
    Exit;
  end;

  InstalledVersion := GetInstalledDisplayVersion();
  if InstalledVersion <> '' then
    VersionLine := '当前已安装版本：' + InstalledVersion + '，将安装 {#MyAppVersion}。' + #13#10#13#10
  else
    VersionLine := '';

  Response := MsgBox(
    '检测到本机已安装 PixSnap。' + #13#10#13#10 +
    VersionLine +
    '【是】继续安装 — 覆盖更新程序文件，保留设置与模型数据' + #13#10 +
    '【否】卸载 PixSnap' + #13#10 +
    '【取消】退出安装程序',
    mbConfirmation, MB_YESNOCANCEL);

  case Response of
    IDYES:
      Result := True;
    IDNO:
      begin
        UninstallCmd := GetInstalledUninstallString();
        if UninstallCmd = '' then
        begin
          MsgBox('未找到卸载程序。请在「设置 → 应用」中手动卸载 PixSnap。', mbError, MB_OK);
          Result := False;
        end
        else if Exec(RemoveQuotes(UninstallCmd), '', '', SW_SHOW, ewNoWait, ResultCode) then
          Result := False
        else
        begin
          MsgBox('无法启动卸载程序。', mbError, MB_OK);
          Result := False;
        end;
      end;
  else
    Result := False;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := False;
  if not PromptCloseRunningApp() then
    Exit;
  if not PromptDowngradeIfNeeded() then
    Exit;
  Result := PromptReinstallOrUninstall();
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
