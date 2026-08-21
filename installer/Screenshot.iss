#ifndef AppVersion
  #define AppVersion "3.4.2"
#endif

#define AppName "SnapCut"
#define AppExeName "SnapCut.exe"
#define AppId "{{1CFE5EF2-3645-4BA7-B96F-4DBDA9C3D2B5}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir=dist
OutputBaseFilename=SnapCut-Setup-{#AppVersion}-win-x64
SetupIconFile=..\src\Screenshot.App\Assets\Screenshot.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
MinVersion=10.0
WizardStyle=modern dynamic polar includetitlebar hidebevels
WizardBackColor=#E8F3F1
WizardBackColorDynamicDark=#0D1B1E
WizardSmallImageFile=..\src\Screenshot.App\Assets\Screenshot.png
WizardSmallImageFileDynamicDark=..\src\Screenshot.App\Assets\Screenshot.png
WizardSizePercent=120,120
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "在桌面创建快捷方式"; GroupDescription: "附加选项："; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed.marker"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; 从旧版本 (Screenshot.exe) 升级时清理旧名产物，避免新旧两份程序并存。
Type: files; Name: "{app}\Screenshot.exe"
Type: files; Name: "{app}\Screenshot.dll"
Type: files; Name: "{app}\Screenshot.pdb"
Type: files; Name: "{app}\Screenshot.deps.json"
Type: files; Name: "{app}\Screenshot.runtimeconfig.json"

[UninstallDelete]
; Offline language packs are downloaded application assets rather than user data.
Type: filesandordirs; Name: "{app}\TranslationModels"

[Dirs]
Name: "{app}\ScreenshotData"; Permissions: users-modify

[Registry]
; 在线更新时保留用户现有的开机启动选择，并刷新可能已过期的程序路径。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Screenshot.App"; ValueData: """{app}\{#AppExeName}"" --background"; Flags: uninsdeletevalue; Check: ShouldRefreshStartupRegistration

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 SnapCut"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExeName}"; Parameters: "--updated {#AppVersion} --cleanup-update-package ""{param:UPDATEPACKAGE|}"""; Flags: nowait skipifnotsilent; Check: IsUpdateMode

[Code]
procedure InitializeWizard;
begin
  WizardForm.Caption := '安装 SnapCut';
  WizardForm.WelcomeLabel1.Caption := '安装 SnapCut';
  WizardForm.WelcomeLabel2.Caption :=
    '此向导将安装 SnapCut。' + #13#10 + #13#10 +
    '你可以在下一步选择安装位置。';
  WizardForm.FinishedHeadingLabel.Caption := 'SnapCut 安装完成';
end;

function IsUpdateMode: Boolean;
begin
  Result := CompareText(ExpandConstant('{param:UPDATE|0}'), '1') = 0;
end;

function ShouldRefreshStartupRegistration: Boolean;
begin
  Result := IsUpdateMode and RegValueExists(
    HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    'Screenshot.App');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DeleteUserData: Boolean;
begin
  if CurUninstallStep <> usUninstall then
  begin
    exit;
  end;

  RegDeleteValue(
    HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    'Screenshot.App');

  DeleteUserData := CompareText(
    ExpandConstant('{param:DELETEUSERDATA|0}'),
    '1') = 0;
  if (not DeleteUserData) and (not UninstallSilent) then
  begin
    DeleteUserData := MsgBox(
      '是否同时删除 Screenshot 的用户数据？' + #13#10 + #13#10 +
      '包括设置、历史、诊断文件，以及默认 ScreenshotData 目录内保存的截图。' + #13#10 +
      '选择“否”只会保留 ScreenshotData，其他程序文件和快捷方式仍会全部卸载。',
      mbConfirmation,
      MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;

  if DeleteUserData then
  begin
    DelTree(ExpandConstant('{app}\ScreenshotData'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\Screenshot'), True, True, True);
  end;
end;
