#ifndef MyAppVersion
  #define MyAppVersion "0.1.2"
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
DisableWelcomePage=no
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
CloseApplicationsFilter=BlockGame.App.exe,BlockGame.DropBridge.exe,BlockGame.Uninstall.exe
RestartApplications=no
UsePreviousAppDir=no
UsePreviousGroup=no
UsePreviousTasks=no

[Files]
Source: "..\artifacts\publish\app\*"; DestDir: "{tmp}\BlockGamePayload\app"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "..\artifacts\publish\dropbridge\*"; DestDir: "{tmp}\BlockGamePayload\dropbridge"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "..\artifacts\publish\guard\*"; DestDir: "{tmp}\BlockGamePayload\guard"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "..\artifacts\publish\uninstall\*"; DestDir: "{tmp}\BlockGamePayload\uninstall"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall
Source: "..\artifacts\publish\uninstall-installed.ps1"; DestDir: "{tmp}\BlockGamePayload"; Flags: ignoreversion deleteafterinstall
Source: "..\scripts\install.ps1"; DestDir: "{tmp}\BlockGameInstaller"; Flags: ignoreversion deleteafterinstall

[Run]
Filename: "{autopf}\BlockGame\BlockGame.App.exe"; Description: "安装完成后运行 BlockGame"; Flags: postinstall nowait runascurrentuser skipifsilent

[Messages]
SetupAppTitle=安装程序
SetupWindowTitle=%1 安装程序
InformationTitle=提示
ConfirmTitle=确认
ErrorTitle=错误
SetupLdrStartupMessage=即将安装 %1。是否继续？
LdrCannotCreateTemp=无法创建临时文件，安装已中止
LdrCannotExecTemp=无法执行临时目录中的文件，安装已中止
WindowsVersionNotSupported=此程序不支持当前 Windows 版本。
AdminPrivilegesRequired=安装此程序需要管理员权限。
ExitSetupTitle=退出安装程序
ExitSetupMessage=安装尚未完成。如果现在退出，BlockGame 将不会被安装。%n%n以后可以重新运行安装包继续安装。%n%n确定退出安装程序吗？
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全部是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全部否(&O)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ButtonNewFolder=新建文件夹(&M)
ClickNext=单击“下一步”继续，或单击“取消”退出安装程序。
BrowseDialogTitle=选择文件夹
BrowseDialogLabel=请从下方列表中选择文件夹，然后单击“确定”。
NewFolderName=新建文件夹
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=BlockGame 是一款 Windows 自律辅助工具，用于在打开游戏、影音应用或指定网站前增加可配置的阻力。%n%n主要功能：%n• 支持按程序文件名、完整路径和网站域名设置规则，也可拖入软件快捷方式自动生成规则%n• 预览屏蔽模式可真实测试规则并立即启停，严格模式通过冷静期防止冲动解除%n• 后台守护服务持续执行规则，并在异常退出后自动恢复%n• 提供审计日志与诊断导出、规则导入导出和受保护卸载%n%n本工具不会剥夺本机管理员的最终恢复能力。
WizardSelectDir=选择安装位置
SelectDirDesc=[name] 将安装到哪里？
SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹。
SelectDirBrowseLabel=单击“下一步”继续；如需更改安装位置，请单击“浏览”。
DiskSpaceGBLabel=至少需要 [gb] GB 可用磁盘空间。
DiskSpaceMBLabel=至少需要 [mb] MB 可用磁盘空间。
DiskSpaceWarningTitle=磁盘空间不足
DiskSpaceWarning=安装至少需要 %1 KB 可用空间，但所选驱动器只有 %2 KB。%n%n仍要继续吗？
WizardSelectProgramGroup=选择开始菜单文件夹
WizardReady=准备安装
ReadyLabel1=安装程序已准备好在此计算机上安装 [name]。
ReadyLabel2a=单击“安装”开始；如需检查或更改设置，请单击“上一步”。
ReadyLabel2b=单击“安装”开始安装。
ReadyMemoUserInfo=用户信息：
ReadyMemoDir=安装位置：
ReadyMemoType=安装类型：
ReadyMemoComponents=所选组件：
ReadyMemoGroup=开始菜单文件夹：
ReadyMemoTasks=附加任务：
WizardPreparing=正在准备安装
PreparingDesc=安装程序正在准备在此计算机上安装 [name]。
PreviousInstallNotCompleted=之前的软件安装或卸载尚未完成，需要重新启动计算机。%n%n重新启动后，请再次运行安装包以完成 [name] 的安装。
CannotContinue=安装程序无法继续。请单击“取消”退出。
ApplicationsFound=以下程序正在使用安装时需要更新的文件，建议允许安装程序自动关闭这些程序。
ApplicationsFound2=以下程序正在使用安装时需要更新的文件，建议允许安装程序自动关闭这些程序。安装完成后，安装程序会尝试重新启动它们。
CloseApplications=自动关闭这些程序(&A)
DontCloseApplications=不关闭这些程序(&D)
ErrorCloseApplications=安装程序无法自动关闭全部相关程序。继续前，请手动关闭正在使用待更新文件的程序。
PrepareToInstallNeedsRestart=安装前必须重新启动计算机。重新启动后，请再次运行安装包以完成 [name] 的安装。%n%n现在重新启动吗？
WizardInstalling=正在安装
InstallingLabel=请稍候，安装程序正在安装 [name]。
FinishedHeadingLabel=[name] 安装完成
FinishedLabelNoIcons=[name] 已成功安装到此计算机。
FinishedLabel=[name] 已成功安装到此计算机，可以通过已创建的快捷方式启动程序。
ClickFinish=单击“完成”退出安装程序。
FinishedRestartLabel=必须重新启动计算机才能完成 [name] 的安装。现在重新启动吗？
FinishedRestartMessage=必须重新启动计算机才能完成 [name] 的安装。%n%n现在重新启动吗？
YesRadio=是，现在重新启动计算机(&Y)
NoRadio=否，稍后手动重新启动(&N)
RunEntryExec=运行 %1
RunEntryShellExec=查看 %1
SetupAborted=安装未完成。%n%n请解决问题后重新运行安装包。
AbortRetryIgnoreSelectAction=请选择操作
AbortRetryIgnoreRetry=重试(&T)
AbortRetryIgnoreIgnore=忽略错误并继续(&I)
AbortRetryIgnoreCancel=取消安装
RetryCancelSelectAction=请选择操作
RetryCancelRetry=重试(&T)
RetryCancelCancel=取消
StatusClosingApplications=正在关闭相关程序...
StatusCreateDirs=正在创建目录...
StatusExtractFiles=正在解压文件...
StatusDownloadFiles=正在下载文件...
StatusCreateIcons=正在创建快捷方式...
StatusCreateIniEntries=正在写入配置...
StatusCreateRegistryEntries=正在写入注册表...
StatusRegisterFiles=正在注册文件...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成安装...
StatusRestartingApplications=正在重新启动相关程序...
StatusRollback=正在回滚更改...
ErrorInternal2=内部错误：%1
ErrorFunctionFailedNoCode=%1 执行失败
ErrorFunctionFailed=%1 执行失败；错误代码 %2
ErrorFunctionFailedWithMessage=%1 执行失败；错误代码 %2。%n%3
ErrorExecutingProgram=无法执行文件：%n%1

[Code]
procedure InitializeWizard;
begin
  { Inno Setup 默认给准备页正文增加缩进；扩宽控件后贴齐内容区左侧。 }
  WizardForm.ReadyLabel.Width := WizardForm.ReadyLabel.Width + WizardForm.ReadyLabel.Left;
  WizardForm.ReadyLabel.Left := 0;
  WizardForm.ReadyMemo.Width := WizardForm.ReadyMemo.Width + WizardForm.ReadyMemo.Left;
  WizardForm.ReadyMemo.Left := 0;
end;

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
