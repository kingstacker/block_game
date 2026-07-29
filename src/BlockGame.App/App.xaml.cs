using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using BlockGame.Core.Services;
using Forms = System.Windows.Forms;

namespace BlockGame.App;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private bool _openingWindow;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

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
            int removedWebsiteRules = SafetyPolicy.RemoveLegacyWebsiteRules(config);
            if (removedWebsiteRules > 0)
            {
                configChanged = true;
            }
            int addedDefaultRules = DefaultRulePresets.Apply(config);
            if (addedDefaultRules > 0)
            {
                configChanged = true;
            }

            if (configChanged)
            {
                configStore.Save(config);
            }
            if (removedWebsiteRules > 0)
            {
                auditLog.Append(new BlockGame.Core.Models.AuditEntry
                {
                    EventType = "WebsiteFeatureRemoved",
                    Message = $"网站屏蔽功能已移除，同时删除 {removedWebsiteRules} 条旧网站规则。"
                });
            }
            if (addedDefaultRules > 0)
            {
                auditLog.Append(new BlockGame.Core.Models.AuditEntry
                {
                    EventType = "DefaultRulesAdded",
                    Message = $"已添加 {addedDefaultRules} 条默认规则，初始状态为停用。"
                });
            }

            GuardInstallResult guardInstall = GuardServiceInstaller.EnsureInstalled(paths);
            auditLog.Append(new BlockGame.Core.Models.AuditEntry
            {
                EventType = guardInstall.InstalledNow ? "GuardAutoInstalled" : "GuardAutoStart",
                Message = guardInstall.Message,
                Success = guardInstall.Success
            });
            if (!guardInstall.Success)
            {
                MessageBox.Show(
                    guardInstall.Message + "\n\n你仍可以查看和编辑规则，但后台拦截服务尚未运行。",
                    "后台守护服务",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var mainWindow = new MainWindow(paths, configStore, auditLog, heartbeatStore);
            MainWindow = mainWindow;
            InitializeTrayIcon(mainWindow);
            if (setupCompletedNow)
            {
                RevealMainWindow(mainWindow);
            }
            else
            {
                TryOpenMainWindow(mainWindow);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            MessageBox.Show(
                "无法访问 BlockGame 数据目录。请确认程序已使用管理员权限运行。\n\n" + exception.Message,
                "BlockGame",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "程序初始化失败：\n\n" + exception.Message,
                "BlockGame",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

        base.OnExit(eventArgs);
    }

    private void InitializeTrayIcon(MainWindow mainWindow)
    {
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("打开 BlockGame");
        openItem.Click += (_, _) => TryOpenMainWindow(mainWindow);

        var resetItem = new Forms.ToolStripMenuItem("调试一键复位（最高优先级）")
        {
            ForeColor = Color.FromArgb(217, 45, 32)
        };
        resetItem.Click += (_, _) => mainWindow.RunDebugReset();

        var exitItem = new Forms.ToolStripMenuItem("退出控制面板（守护服务继续运行）");
        exitItem.Click += (_, _) => ExitControlPanel(mainWindow);

        menu.Items.Add(openItem);
        menu.Items.Add(resetItem);
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
        _exitRequested = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        mainWindow.Close();
        Shutdown();
    }
}
