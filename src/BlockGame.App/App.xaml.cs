using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using BlockGame.Core.Services;
using Forms = System.Windows.Forms;

namespace BlockGame.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Global\BlockGame.App.SingleInstance";

    private Forms.NotifyIcon? _trayIcon;
    private Mutex? _singleInstanceMutex;
    private bool _exitRequested;
    private bool _openingWindow;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        bool autoStart = StartupArguments.IsAutoStart(eventArgs.Args);

        if (!TryClaimSingleInstance())
        {
            // 两个控制面板同时读改写配置会互相覆盖规则、锁状态和密码限流计数。
            if (!autoStart)
            {
                MessageBox.Show(
                    "BlockGame 已在运行。请通过任务栏托盘图标打开管理界面。",
                    "BlockGame",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        try
        {
            var paths = DataPaths.CreateDefault();
            var configStore = new ConfigStore(paths);
            var auditLog = new AuditLog(paths);
            var heartbeatStore = new HeartbeatStore(paths);
            var config = configStore.Load();
            bool setupCompletedNow = false;

            if (!config.SetupCompleted)
            {
                // 计划任务不应在尚未完成首次配置时弹出任何界面。
                if (autoStart)
                {
                    Shutdown();
                    return;
                }

                // Closing the modal first-run window must not end the entire
                // application before the main window has been created.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var setupWindow = new PasswordSetupWindow(configStore, auditLog);
                if (setupWindow.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }

                config = configStore.Load();
                setupCompletedNow = true;
            }

            bool configChanged = SafetyPolicy.NormalizeFileNameRulePatterns(config);
            configChanged |= SafetyPolicy.NormalizeDomainRulePatterns(config);
            int addedDefaultRules = DefaultRulePresets.Apply(config);
            if (addedDefaultRules > 0)
            {
                configChanged = true;
            }

            if (configChanged)
            {
                configStore.Save(config);
            }
            if (addedDefaultRules > 0)
            {
                auditLog.Append(new BlockGame.Core.Models.AuditEntry
                {
                    EventType = "DefaultRulesUpdated",
                    Message = $"已新增或更新 {addedDefaultRules} 条默认规则；新增规则默认为停用，已有规则保留原勾选状态。"
                });
            }

            GuardInstallResult guardInstall = GuardServiceInstaller.EnsureInstalled(paths);
            auditLog.Append(new BlockGame.Core.Models.AuditEntry
            {
                EventType = guardInstall.InstalledNow ? "GuardAutoInstalled" : "GuardAutoStart",
                Message = guardInstall.Message,
                Success = guardInstall.Success
            });
            if (!guardInstall.Success && !autoStart)
            {
                MessageBox.Show(
                    guardInstall.Message + "\n\n你仍可以查看和编辑规则，但后台拦截服务尚未运行。",
                    "后台守护服务",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            auditLog.Append(new BlockGame.Core.Models.AuditEntry
            {
                EventType = "ControlPanelStarted",
                Message = autoStart
                    ? "BlockGame 已通过开机自启动进入静默托盘模式。"
                    : "BlockGame 控制面板已启动。",
                Success = true
            });

            var mainWindow = new MainWindow(paths, configStore, auditLog, heartbeatStore);
            MainWindow = mainWindow;
            InitializeTrayIcon(mainWindow);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (setupCompletedNow)
            {
                RevealMainWindow(mainWindow);
            }
            else if (!autoStart)
            {
                TryOpenMainWindow(mainWindow);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            if (autoStart)
            {
                TryAppendStartupFailure(exception);
            }
            else
            {
                MessageBox.Show(
                    "无法访问 BlockGame 数据目录。请确认程序已使用管理员权限运行。\n\n" + exception.Message,
                    "BlockGame",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            Shutdown(1);
        }
        catch (Exception exception)
        {
            if (autoStart)
            {
                TryAppendStartupFailure(exception);
            }
            else
            {
                MessageBox.Show(
                    "程序初始化失败：\n\n" + exception.Message,
                    "BlockGame",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 未持有时直接释放句柄即可。
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(eventArgs);
    }

    private bool TryClaimSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                SingleInstanceMutexName,
                out bool createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            return createdNew;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException)
        {
            // 互斥体已被其他权限的实例创建：同样视为已在运行。
            return false;
        }
    }

    private void InitializeTrayIcon(MainWindow mainWindow)
    {
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("打开 BlockGame");
        openItem.Click += (_, _) => TryOpenMainWindow(mainWindow);

        var exitItem = new Forms.ToolStripMenuItem("退出控制面板（守护服务继续运行）");
        exitItem.Click += (_, _) => ExitControlPanel(mainWindow);

        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "BlockGame 游戏自律助手",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => TryOpenMainWindow(mainWindow);
        mainWindow.Closing += (_, closingArgs) => HideToTray(mainWindow, closingArgs);
    }

    private static Icon LoadTrayIcon()
    {
        var resource = GetResourceStream(
            new Uri("pack://application:,,,/Assets/BlockGame.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("无法加载 BlockGame 托盘图标。");
        using Stream stream = resource.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private void TryOpenMainWindow(MainWindow mainWindow)
    {
        if (mainWindow.IsVisible)
        {
            RevealMainWindow(mainWindow);
            return;
        }

        if (_openingWindow)
        {
            return;
        }

        _openingWindow = true;
        try
        {
            if (mainWindow.AuthenticateForOpen())
            {
                RevealMainWindow(mainWindow);
            }
        }
        finally
        {
            _openingWindow = false;
        }
    }

    private static void RevealMainWindow(MainWindow mainWindow)
    {
        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        mainWindow.Activate();
    }

    private void HideToTray(MainWindow mainWindow, CancelEventArgs closingArgs)
    {
        if (_exitRequested)
        {
            return;
        }

        closingArgs.Cancel = true;
        mainWindow.Hide();
    }

    private void ExitControlPanel(MainWindow mainWindow)
    {
        if (!mainWindow.AuthenticateForAction("退出 BlockGame 控制面板"))
        {
            return;
        }

        _exitRequested = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        mainWindow.Close();
        Shutdown();
    }

    private static void TryAppendStartupFailure(Exception exception)
    {
        try
        {
            var paths = DataPaths.CreateDefault();
            new AuditLog(paths).Append(new BlockGame.Core.Models.AuditEntry
            {
                EventType = "ControlPanelAutoStartFailed",
                Message = "静默托盘启动失败：" + exception.Message,
                Success = false
            });
        }
        catch
        {
            // 静默启动失败时不能再因日志写入失败弹窗或抛出二次异常。
        }
    }
}
