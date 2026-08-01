using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using BlockGame.Core.Models;
using BlockGame.Core.Services;

namespace BlockGame.Uninstall;

public partial class UninstallWindow : Window
{
    private readonly DataPaths _paths;
    private readonly ConfigStore _configStore;
    private readonly AuditLog _auditLog;
    private bool _setupCompleted;

    public UninstallWindow()
    {
        InitializeComponent();
        _paths = DataPaths.CreateDefault();
        _configStore = new ConfigStore(_paths);
        _auditLog = new AuditLog(_paths);

        Loaded += UninstallWindow_Loaded;
    }

    private void UninstallWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AppConfig config = _configStore.Load();
            _setupCompleted = config.SetupCompleted;
            if (!_setupCompleted)
            {
                PasswordPanel.Visibility = Visibility.Collapsed;
                DescriptionText.Text =
                    "首次设置尚未完成，因此还没有管理密码。可以直接移除当前未配置的安装。";
                UninstallButton.Content = "确认卸载";
            }
            else
            {
                PasswordInput.Focus();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "无法读取 BlockGame 配置：\n\n" + exception.Message,
                "卸载 BlockGame",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        UninstallButton.IsEnabled = false;
        try
        {
            bool setupStateChanged = false;
            UninstallPreparationResult? preparation = null;
            AppConfig config = _configStore.Update(current =>
            {
                if (current.SetupCompleted != _setupCompleted)
                {
                    setupStateChanged = true;
                    return false;
                }

                preparation = PasswordProtectedUninstallService.Prepare(
                    current,
                    PasswordInput.Password,
                    DateTimeOffset.UtcNow);
                return true;
            });
            if (setupStateChanged)
            {
                MessageBox.Show(
                    "BlockGame 配置状态已变化，请重新打开卸载程序。",
                    "卸载 BlockGame",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (preparation is null)
            {
                throw new InvalidOperationException("无法生成卸载授权。");
            }

            if (config.SetupCompleted)
            {
                AppendAudit(
                    preparation.PasswordVerified
                        ? "已验证管理密码：卸载 BlockGame。"
                        : "卸载 BlockGame 时管理密码验证失败。",
                    preparation.PasswordVerified);
                PasswordInput.Clear();
            }

            if (!preparation.Success || string.IsNullOrWhiteSpace(preparation.Token))
            {
                MessageBox.Show(
                    preparation.Message,
                    preparation.ProtectionLocked ? "无法卸载" : "密码验证",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                "确定卸载 BlockGame 吗？\n\n本机规则、配置和审计日志也会被删除。",
                "确认卸载",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                _ = _configStore.Update(current =>
                {
                    ProtectionManager.ClearUninstallAuthorization(current);
                    return true;
                });
                return;
            }

            StartAuthorizedUninstall(
                preparation.Token,
                unconfiguredInstall: !config.SetupCompleted);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "卸载启动失败：\n\n" + exception.Message,
                "卸载 BlockGame",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            UninstallButton.IsEnabled = true;
        }
    }

    private void StartAuthorizedUninstall(string token, bool unconfiguredInstall)
    {
        string uninstallScript = Path.Combine(_paths.RootDirectory, "uninstall-installed.ps1");
        if (!File.Exists(uninstallScript))
        {
            throw new FileNotFoundException("找不到 BlockGame 卸载组件。", uninstallScript);
        }

        if (!unconfiguredInstall)
        {
            File.WriteAllText(_paths.UninstallTokenFile, token);
        }

        AppendAudit(
            unconfiguredInstall
                ? "首次设置尚未完成，已授权移除未配置的安装。"
                : "已通过管理密码授权卸载。",
            true);

        string powerShell = Path.Combine(
            Environment.SystemDirectory,
            @"WindowsPowerShell\v1.0\powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(uninstallScript);
        startInfo.ArgumentList.Add("-WaitForProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        if (unconfiguredInstall)
        {
            startInfo.ArgumentList.Add("-UnconfiguredInstall");
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 BlockGame 卸载组件。");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string standardOutput = standardOutputTask.GetAwaiter().GetResult();
            string standardError = standardErrorTask.GetAwaiter().GetResult();
            string detail = string.IsNullOrWhiteSpace(standardError)
                ? standardOutput
                : standardError;
            throw new InvalidOperationException(
                $"卸载组件返回错误代码 {process.ExitCode}。"
                + (string.IsNullOrWhiteSpace(detail)
                    ? string.Empty
                    : "\n\n" + detail.Trim()));
        }

        Application.Current.Shutdown(0);
    }

    private void AppendAudit(string message, bool success)
    {
        try
        {
            _auditLog.Append(new AuditEntry
            {
                EventType = "PasswordProtectedUninstall",
                Message = message,
                Success = success
            });
        }
        catch (IOException)
        {
            // Password verification and uninstall authorization remain authoritative.
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            UninstallButton_Click(sender, e);
        }
    }
}
