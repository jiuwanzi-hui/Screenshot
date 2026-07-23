#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Screenshot"
#define AppExeName "Screenshot.exe"
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
OutputBaseFilename=Screenshot-Setup-{#AppVersion}-win-x64
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

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 Screenshot"; Flags: nowait postinstall skipifsilent

[Code]
procedure InitializeWizard;
begin
  WizardForm.Caption := '安装 Screenshot';
  WizardForm.WelcomeLabel1.Caption := '安装 Screenshot';
  WizardForm.WelcomeLabel2.Caption :=
    '此向导将安装 Screenshot。' + #13#10 + #13#10 +
    '你可以在下一步选择安装位置。';
  WizardForm.FinishedHeadingLabel.Caption := 'Screenshot 安装完成';
end;
