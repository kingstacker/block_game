#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "BlockGame"
#define MyAppPublisher "BlockGame"
#define MyAppExeName "BlockGame.App.exe"
#define MyAppId "{{B64E1DB8-6727-4CF5-BBC9-94871D8CF38C}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=BlockGame 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={autopf}\BlockGame
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\src\BlockGame.App\Assets\BlockGame.ico
OutputDir=..\artifacts\release
OutputBaseFilename=BlockGame-Setup
Compression=lzma2/ultra64
SolidCompression=yes
Uninstallable=no
CreateAppDir=no
CloseApplications=yes
CloseApplicationsFilter=BlockGame.App.exe,BlockGame.Uninstall.exe
RestartApplications=no
UsePreviousAppDir=no
UsePreviousGroup=no
UsePreviousTasks=no

[Files]
Source: "..\artifacts\publish\app\*"; DestDir: "{tmp}\BlockGamePayload\app"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "..\artifacts\publish\guard\*"; DestDir: "{tmp}\BlockGamePayload\guard"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "..\artifacts\publish\uninstall\*"; DestDir: "{tmp}\BlockGamePayload\uninstall"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "..\artifacts\publish\uninstall-installed.ps1"; DestDir: "{tmp}\BlockGamePayload"; Flags: ignoreversion deleteafterinstall
Source: "..\scripts\install.ps1"; DestDir: "{tmp}\BlockGameInstaller"; Flags: ignoreversion deleteafterinstall

[Run]
Filename: "{autopf}\BlockGame\BlockGame.App.exe"; Description: "安装完成后运行 BlockGame"; Flags: postinstall nowait runascurrentuser skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  PowerShellPath: String;
  InstallScriptPath: String;
  PublishRootPath: String;
  Parameters: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  WizardForm.StatusLabel.Caption := '正在安装 BlockGame 服务和管理程序...';
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  InstallScriptPath := ExpandConstant('{tmp}\BlockGameInstaller\install.ps1');
  PublishRootPath := ExpandConstant('{tmp}\BlockGamePayload');
  Parameters :=
    '-NoProfile -ExecutionPolicy Bypass -File "' + InstallScriptPath +
    '" -PublishRoot "' + PublishRootPath + '" -Version "{#MyAppVersion}"';

  if not Exec(
    PowerShellPath,
    Parameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  ) then
    RaiseException('无法启动 BlockGame 安装脚本。');

  if ResultCode <> 0 then
    RaiseException(Format('BlockGame 安装失败，错误代码：%d。', [ResultCode]));
end;
