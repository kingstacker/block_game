using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BlockGame.Core.Models;
using BlockGame.Core.Services;
using Microsoft.Win32;

namespace BlockGame.App;

public partial class MainWindow : Window
{
    private readonly DataPaths _paths;
    private readonly ConfigStore _configStore;
    private readonly AuditLog _auditLog;
    private readonly HeartbeatStore _heartbeatStore;
    private readonly ObservableCollection<RuleRow> _rules = [];
    private readonly ObservableCollection<AuditRow> _auditRows = [];
    private readonly HashSet<string> _knownBlockEvents = new(StringComparer.Ordinal);
    private readonly Queue<string> _pendingBlockNotifications = new();
    private readonly DispatcherTimer _statusTimer;
    private bool _showingBlockNotification;
    private AppConfig _config;

    public MainWindow(
        DataPaths paths,
        ConfigStore configStore,
        AuditLog auditLog,
        HeartbeatStore heartbeatStore)
    {
        _paths = paths;
        _configStore = configStore;
        _auditLog = auditLog;
        _heartbeatStore = heartbeatStore;
        _config = _configStore.Load();

        InitializeComponent();
        RulesDataGrid.ItemsSource = _rules;
        AuditDataGrid.ItemsSource = _auditRows;
        DataDirectoryText.Text = "数据目录：" + _paths.RootDirectory;

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statusTimer.Tick += (_, _) =>
        {
            RefreshStatus(reloadConfig: true);
            RefreshLogs(notifyNewBlocks: true);
        };
        _statusTimer.Start();

        RefreshRules();
        RefreshLogs(notifyNewBlocks: false);
        RefreshStatus(reloadConfig: false);
    }

    private void RefreshStatus(bool reloadConfig)
    {
        if (reloadConfig)
        {
            try
            {
                _config = _configStore.Load();
            }
            catch
            {
                // Keep displaying the last known good state while a file replacement is in progress.
            }
        }

        GuardHeartbeat? heartbeat = _heartbeatStore.Read();
        bool guardOnline = heartbeat is not null
            && DateTimeOffset.UtcNow - heartbeat.TimestampUtc < TimeSpan.FromSeconds(6);
        HeaderGuardStatusText.Text = guardOnline
            ? $"守护程序运行中 · PID {heartbeat!.ProcessId}"
            : "未检测到守护程序";

        ProtectionStatusText.Text = _config.ProtectionEnabled ? "正在拦截" : "已暂停";
        LockStatusText.Text = _config.ProtectionLocked ? "已锁定" : "可管理";
        RuleCountText.Text = _config.Rules.Count(rule => rule.Enabled).ToString(CultureInfo.InvariantCulture);

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (_config.UnlockAvailableAtUtc is { } availableAt)
        {
            TimeSpan remaining = availableAt - nowUtc;
            UnlockStatusText.Text = remaining > TimeSpan.Zero
                ? $"解除申请已提交。剩余 {FormatDuration(remaining)}，到期后还需再次输入密码。"
                : "冷静期已经结束，可以点击“完成解除”。拦截不会自动暂停。";
        }
        else if (_config.ProtectionLocked)
        {
            UnlockStatusText.Text = $"设置已锁定。解除需要密码、确认文本和 {FormatDuration(TimeSpan.FromMinutes(_config.UnlockDelayMinutes))} 冷静期。";
        }
        else
        {
            UnlockStatusText.Text = "当前设置可管理。启用并锁定后，削弱保护的操作必须经过解除流程。";
        }

        EnableLockButton.IsEnabled = !_config.ProtectionLocked;
        RequestUnlockButton.IsEnabled = _config.ProtectionLocked && _config.UnlockAvailableAtUtc is null;
        CompleteUnlockButton.IsEnabled = _config.ProtectionLocked
            && _config.UnlockAvailableAtUtc is { } unlockAt
            && unlockAt <= nowUtc;
        DisableProtectionButton.IsEnabled = !_config.ProtectionLocked && _config.ProtectionEnabled;

        if (!UnlockDelayHoursTextBox.IsKeyboardFocused)
        {
            UnlockDelayHoursTextBox.Text = (_config.UnlockDelayMinutes / 60d).ToString("0.##", CultureInfo.CurrentCulture);
        }
    }

    private void RefreshRules()
    {
        _config = _configStore.Load();
        _rules.Clear();
        foreach (BlockRule rule in _config.Rules.OrderBy(rule => rule.CreatedAtUtc))
        {
            _rules.Add(new RuleRow(rule));
        }

        RefreshStatus(reloadConfig: false);
    }

    private void RefreshLogs(bool notifyNewBlocks = true)
    {
        IReadOnlyList<AuditEntry> entries = _auditLog.ReadRecent();
        _auditRows.Clear();
        foreach (AuditEntry entry in entries)
        {
            _auditRows.Add(new AuditRow(entry));
        }

        var newBlockNames = new List<string>();
        foreach (AuditEntry entry in entries
                     .Where(entry =>
                         entry.Success
                         && entry.EventType is "ProcessBlocked" or "WebsiteBlocked"
                         && entry.DesktopNotificationSent != true)
                     .Reverse())
        {
            string signature = CreateAuditSignature(entry);
            if (!_knownBlockEvents.Add(signature) || !notifyNewBlocks)
            {
                continue;
            }

            newBlockNames.Add(entry.EventType == "WebsiteBlocked"
                ? $"{entry.Domain ?? "该网站"} 网站已被拦截。"
                : $"{GetBlockedApplicationName(entry)} 软件已被拦截。");
        }

        foreach (string message in newBlockNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _pendingBlockNotifications.Enqueue(message);
        }

        ShowPendingBlockNotifications();
    }

    public bool AuthenticateForOpen()
        => AuthenticateForAction("打开 BlockGame 管理界面");

    public bool AuthenticateForAction(string actionDescription)
    {
        ReloadConfig();
        var dialog = new PasswordPromptWindow(actionDescription);
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        return VerifyPasswordValue(dialog.Password, actionDescription);
    }

    private bool VerifyPasswordValue(string password, string actionDescription)
    {
        PasswordVerificationResult result = PasswordGate.Verify(_config, password, DateTimeOffset.UtcNow);
        _configStore.Save(_config);
        AppendAudit(
            "PasswordVerification",
            result.Success ? $"已验证管理密码：{actionDescription}。" : $"管理密码验证失败：{actionDescription}。",
            result.Success);

        if (!result.Success)
        {
            MessageBox.Show(result.Message, "密码验证", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void EnableLockButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        if (_config.Rules.All(rule => !rule.Enabled))
        {
            MessageBox.Show("请先添加至少一条启用的拦截规则。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ProtectionManager.EnableAndLock(_config);
        _configStore.Save(_config);
        AppendAudit("ProtectionLocked", "保护已启用并锁定。", true);
        RefreshRules();
    }

    private void RequestUnlockButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        var dialog = new UnlockRequestWindow { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            ProtectionManager.RequestUnlock(_config, dialog.ConfirmationText, DateTimeOffset.UtcNow);
            _configStore.Save(_config);
            AppendAudit(
                "UnlockRequested",
                $"已申请解除，冷静期为 {FormatDuration(TimeSpan.FromMinutes(_config.UnlockDelayMinutes))}。",
                true);
            RefreshStatus(reloadConfig: false);
        }
        catch (InvalidOperationException exception)
        {
            AppendAudit("UnlockRequestRejected", exception.Message, false);
            MessageBox.Show(exception.Message, "申请解除", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CompleteUnlockButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        try
        {
            ProtectionManager.CompleteUnlock(_config, DateTimeOffset.UtcNow);
            _configStore.Save(_config);
            AppendAudit("ProtectionUnlocked", "冷静期结束，设置已解除锁定；拦截仍保持启用。", true);
            RefreshRules();
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(exception.Message, "完成解除", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DisableProtectionButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        if (_config.ProtectionLocked)
        {
            MessageBox.Show("必须先申请并完成解除。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProtectionManager.DisableProtection(_config);
        _configStore.Save(_config);
        AppendAudit("ProtectionDisabled", "拦截已暂停。", true);
        RefreshStatus(reloadConfig: false);
    }

    private void BrowseExeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要禁止的程序",
            Filter = "可执行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            RuleNameTextBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            RuleTargetComboBox.SelectedIndex = 1;
            RulePatternTextBox.Text = dialog.FileName;
        }
    }

    private void RuleTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RulePatternHelpText is null || BrowseExeButton is null)
        {
            return;
        }

        var selectedTarget = RuleTargetComboBox.SelectedItem as ComboBoxItem;
        _ = Enum.TryParse(selectedTarget?.Tag?.ToString(), out RuleTarget target);
        RulePatternHelpText.Text = target switch
        {
            RuleTarget.FullPath => "匹配内容（完整 EXE 路径，支持 * 和 ?）",
            RuleTarget.Domain => "网站域名（可一行一个或用 ; 分隔；poki.com 会同时屏蔽其子域名）",
            _ => "匹配内容（可一行一个或用 ; 分隔，支持 * 和 ?，自动补 .exe）"
        };
        BrowseExeButton.IsEnabled = target != RuleTarget.Domain;
    }

    private void AddRuleButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        var selectedTarget = RuleTargetComboBox.SelectedItem as ComboBoxItem;
        if (!Enum.TryParse(selectedTarget?.Tag?.ToString(), out RuleTarget target))
        {
            target = RuleTarget.FileName;
        }

        var rule = new BlockRule
        {
            Name = RuleNameTextBox.Text.Trim(),
            Target = target,
            Pattern = RulePatternTextBox.Text,
            Enabled = true
        };

        string? validationError = SafetyPolicy.ValidateRule(rule);
        if (validationError is not null)
        {
            MessageBox.Show(validationError, "规则不安全", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        rule.Pattern = SafetyPolicy.NormalizeRulePattern(target, rule.Pattern);
        bool duplicate = _config.Rules.Any(existing =>
            existing.Target == rule.Target
            && string.Equals(existing.Pattern, rule.Pattern, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            MessageBox.Show("相同匹配规则已经存在。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _config.Rules.Add(rule);
        _configStore.Save(_config);
        AppendAudit("RuleAdded", $"已添加规则“{rule.Name}”：{rule.Pattern}", true);
        RuleNameTextBox.Clear();
        RulePatternTextBox.Clear();
        RefreshRules();
    }

    private void DebugResetButton_Click(object sender, RoutedEventArgs e) => RunDebugReset();

    public void RunDebugReset()
    {
        try
        {
            ReloadConfig();
            ProtectionManager.ResetForDebug(_config);
            _configStore.Save(_config);
            if (File.Exists(_paths.UninstallTokenFile))
            {
                File.Delete(_paths.UninstallTokenFile);
            }

            bool networkReset = GuardServiceInstaller.TryCleanupNetworkPolicies(
                out string networkResetMessage);
            AppendAudit(
                "DebugReset",
                "已执行最高优先级调试复位：暂停拦截、解除锁定、删除自定义规则并恢复未启用的默认规则。"
                    + networkResetMessage,
                networkReset);
            RefreshRules();
            MessageBox.Show(
                "调试复位完成：拦截已暂停，设置已解锁，自定义规则已删除；默认规则已恢复并保持未勾选。\n\n"
                    + networkResetMessage,
                "BlockGame",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                "调试复位失败，无法写入配置：\n\n" + exception.Message,
                "BlockGame",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RuleEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not RuleRow selected)
        {
            return;
        }

        ReloadConfig();
        BlockRule? rule = _config.Rules.FirstOrDefault(candidate => candidate.Id == selected.Id);
        if (rule is null)
        {
            RefreshRules();
            return;
        }

        bool enable = checkBox.IsChecked == true;
        if (!enable && rule.Enabled && _config.ProtectionLocked)
        {
            checkBox.IsChecked = true;
            MessageBox.Show("锁定期间不能停用规则。请先完成解除流程。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (rule.Enabled == enable)
        {
            return;
        }

        rule.Enabled = enable;
        _configStore.Save(_config);
        AppendAudit("RuleToggled", $"规则“{rule.Name}”已{(rule.Enabled ? "启用" : "停用")}。", true);
        RefreshRules();
    }

    private void DeleteRuleButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        if (_config.ProtectionLocked)
        {
            MessageBox.Show("锁定期间不能删除规则。请先完成解除流程。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (RulesDataGrid.SelectedItem is not RuleRow selected)
        {
            MessageBox.Show("请先选择一条规则。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BlockRule? rule = _config.Rules.FirstOrDefault(candidate => candidate.Id == selected.Id);
        if (rule is null)
        {
            return;
        }

        _config.Rules.Remove(rule);
        _configStore.Save(_config);
        AppendAudit("RuleDeleted", $"已删除规则“{rule.Name}”。", true);
        RefreshRules();
    }

    private void RulesDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(RulesDataGrid, source) is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
        else
        {
            RulesDataGrid.SelectedItem = null;
        }
    }

    private void EditRuleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        if (_config.ProtectionLocked)
        {
            MessageBox.Show(
                "锁定期间不能修改规则。请先完成解除流程。",
                "BlockGame",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (RulesDataGrid.SelectedItem is not RuleRow selected)
        {
            MessageBox.Show(
                "请先选择一条规则。",
                "BlockGame",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        BlockRule? rule = _config.Rules.FirstOrDefault(candidate => candidate.Id == selected.Id);
        if (rule is null)
        {
            RefreshRules();
            return;
        }

        var dialog = new RuleEditWindow(rule) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var candidate = new BlockRule
        {
            Name = dialog.RuleName,
            Target = dialog.Target,
            Pattern = dialog.Pattern
        };
        string? validationError = SafetyPolicy.ValidateRule(candidate);
        if (validationError is not null)
        {
            MessageBox.Show(
                validationError,
                "规则不安全",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        bool duplicate = _config.Rules.Any(existing =>
            existing.Id != rule.Id
            && existing.Target == candidate.Target
            && string.Equals(
                existing.Pattern,
                candidate.Pattern,
                StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            MessageBox.Show(
                "相同匹配规则已经存在。",
                "BlockGame",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string previous = $"{rule.Name} / {rule.Target} / {rule.Pattern}";
        rule.Name = candidate.Name;
        rule.Target = candidate.Target;
        rule.Pattern = candidate.Pattern;
        _configStore.Save(_config);
        AppendAudit(
            "RuleModified",
            $"已修改规则：{previous} → {rule.Name} / {rule.Target} / {rule.Pattern}",
            true);
        RefreshRules();
    }

    private void SaveDelayButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        if (!double.TryParse(
                UnlockDelayHoursTextBox.Text,
                NumberStyles.Number,
                CultureInfo.CurrentCulture,
                out double hours)
            || hours < (1d / 60d)
            || hours > 24d * 30d)
        {
            MessageBox.Show("冷静期必须在 1 分钟到 30 天之间。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int newMinutes = Math.Max(1, (int)Math.Round(hours * 60d));
        if (_config.ProtectionLocked && newMinutes < _config.UnlockDelayMinutes)
        {
            MessageBox.Show("锁定期间只能延长冷静期，不能缩短。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _config.UnlockDelayMinutes = newMinutes;
        _configStore.Save(_config);
        AppendAudit("UnlockDelayChanged", $"冷静期已改为 {FormatDuration(TimeSpan.FromMinutes(newMinutes))}。", true);
        RefreshStatus(reloadConfig: false);
    }

    private void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        if (_config.ProtectionLocked)
        {
            MessageBox.Show("必须先完成解除流程，才能修改密码。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (NewPasswordBox.Password != ConfirmNewPasswordBox.Password)
        {
            MessageBox.Show("两次输入的新密码不一致。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _config.Password = PasswordHasher.Create(NewPasswordBox.Password);
            _config.PasswordThrottle = new PasswordThrottle();
            _configStore.Save(_config);
            AppendAudit("PasswordChanged", "管理密码已修改。", true);
            NewPasswordBox.Clear();
            ConfirmNewPasswordBox.Clear();
            MessageBox.Show("管理密码已修改。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "修改密码", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AuthorizeUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        if (_config.ProtectionLocked)
        {
            MessageBox.Show("必须先申请并完成解除，才能生成卸载授权。", "BlockGame", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string token = UninstallAuthorizationService.Create(_config, DateTimeOffset.UtcNow);
        _configStore.Save(_config);
        File.WriteAllText(_paths.UninstallTokenFile, token);
        AppendAudit("UninstallAuthorized", "已生成十分钟有效的一次性卸载授权。", true);
        MessageBox.Show(
            "卸载授权已生成，十分钟内从 Windows“已安装的应用”执行卸载。令牌只能使用一次。",
            "卸载授权",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void RefreshLogsButton_Click(object sender, RoutedEventArgs e) => RefreshLogs(notifyNewBlocks: true);

    private void ReloadConfig() => _config = _configStore.Load();

    private void AppendAudit(string eventType, string message, bool success)
    {
        try
        {
            _auditLog.Append(new AuditEntry
            {
                EventType = eventType,
                Message = message,
                Success = success
            });
        }
        catch (IOException)
        {
            // The settings action already succeeded; do not roll it back only because logging failed.
        }

        RefreshLogs(notifyNewBlocks: true);
    }

    private static string CreateAuditSignature(AuditEntry entry)
        => string.Join(
            "|",
            entry.TimestampUtc.UtcTicks,
            entry.ProcessId,
            entry.RuleId,
            entry.Message);

    private static string GetBlockedApplicationName(AuditEntry entry)
    {
        string? name = entry.ProcessName;
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(entry.ProcessPath))
        {
            name = Path.GetFileName(entry.ProcessPath);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            const string prefix = "已阻止 ";
            int start = entry.Message.IndexOf(prefix, StringComparison.Ordinal);
            int end = entry.Message.IndexOf('，');
            if (start >= 0 && end > start + prefix.Length)
            {
                name = entry.Message[(start + prefix.Length)..end];
            }
        }

        string? withoutExtension = Path.GetFileNameWithoutExtension(name?.Trim());
        return string.IsNullOrWhiteSpace(withoutExtension) ? "该程序" : withoutExtension;
    }

    private void ShowPendingBlockNotifications()
    {
        if (_showingBlockNotification)
        {
            return;
        }

        _showingBlockNotification = true;
        try
        {
            while (_pendingBlockNotifications.TryDequeue(out string? message))
            {
                if (IsVisible)
                {
                    MessageBox.Show(
                        this,
                        message,
                        "BlockGame 拦截提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        message,
                        "BlockGame 拦截提示",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
        }
        finally
        {
            _showingBlockNotification = false;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays} 天 {duration.Hours} 小时 {duration.Minutes} 分钟";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))} 分钟";
    }

    private sealed class RuleRow
    {
        public RuleRow(BlockRule rule)
        {
            Id = rule.Id;
            Name = rule.Name;
            TargetDisplay = rule.Target switch
            {
                RuleTarget.FileName => "程序文件名",
                RuleTarget.FullPath => "完整路径",
                RuleTarget.Domain => "网站域名",
                _ => "未知"
            };
            Pattern = rule.Pattern;
            IsEnabled = rule.Enabled;
            CreatedDisplay = rule.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        public string Id { get; }
        public string Name { get; }
        public string TargetDisplay { get; }
        public string Pattern { get; }
        public bool IsEnabled { get; set; }
        public string CreatedDisplay { get; }
    }

    private sealed class AuditRow
    {
        public AuditRow(AuditEntry entry)
        {
            TimeDisplay = entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            EventType = entry.EventType switch
            {
                "ProcessBlocked" => "拦截事件",
                "WebsiteBlocked" => "网站拦截",
                "WebsiteRulesApplied" => "网站规则同步",
                "DefaultRulesAdded" => "默认规则",
                _ => entry.EventType
            };
            ResultDisplay = entry.Success ? "成功" : "失败";
            Message = entry.Message;
        }

        public string TimeDisplay { get; }
        public string EventType { get; }
        public string ResultDisplay { get; }
        public string Message { get; }
    }
}
