using System.Diagnostics;
using BlockGame.Core.Models;
using BlockGame.Core.Services;

namespace BlockGame.Guard;

internal sealed class GuardWorker
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LegacyHostsCleanupRetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryReapplyInterval = TimeSpan.FromMinutes(5);

    private readonly DataPaths _paths;
    private readonly ConfigStore _configStore;
    private readonly AuditLog _auditLog;
    private readonly HeartbeatStore _heartbeatStore;
    private readonly HostsFileManager _hostsFileManager;
    private readonly NrptPolicyManager _nrptPolicyManager;
    private readonly BrowserDnsPolicyManager _browserDnsPolicyManager;
    private readonly ExecutableMetadataResolver _metadataResolver = new();
    private readonly Dictionary<string, DateTimeOffset> _lastFailureLogs = new();
    private readonly Dictionary<string, DateTimeOffset> _lastProcessBlockToasts = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastWebsiteNotifications = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly object _websiteNotificationLock = new();
    private LocalDnsBlockServer? _dnsBlockServer;
    private string? _websitePolicySignature;
    private DateTimeOffset _nextWebsitePolicyRetryUtc = DateTimeOffset.MinValue;

    public GuardWorker(
        DataPaths paths,
        ConfigStore configStore,
        AuditLog auditLog,
        HeartbeatStore heartbeatStore)
    {
        _paths = paths;
        _configStore = configStore;
        _auditLog = auditLog;
        _heartbeatStore = heartbeatStore;
        _hostsFileManager = HostsFileManager.CreateDefault();
        _nrptPolicyManager = new NrptPolicyManager(paths);
        _browserDnsPolicyManager = new BrowserDnsPolicyManager(paths);
    }

    public async Task RunAsync(string mode, CancellationToken cancellationToken)
    {
        _paths.EnsureDirectory();
        AppConfig lastKnownGoodConfig = LoadConfigOrDefault();
        DateTimeOffset nextHeartbeatUtc = DateTimeOffset.MinValue;
        DateTimeOffset nextLegacyHostsCleanupUtc = DateTimeOffset.MinValue;
        DateTimeOffset nextRecoveryReapplyUtc = DateTimeOffset.MinValue;
        bool legacyHostsCleanupComplete = false;
        bool isWindowsService = string.Equals(mode, "WindowsService", StringComparison.Ordinal);

        TryAudit(new AuditEntry
        {
            EventType = "GuardStarted",
            Message = $"守护程序已启动，模式：{mode}。"
        });

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                lastKnownGoodConfig = LoadConfigAndApplyMigrations();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogFailureWithThrottle("config", "读取配置失败，继续使用最后一份有效配置：" + exception.Message);
            }
            catch (System.Text.Json.JsonException exception)
            {
                LogFailureWithThrottle("config-json", "配置格式损坏，继续使用最后一份有效配置：" + exception.Message);
            }
            catch (InvalidDataException exception)
            {
                // 空配置或更高版本 schema 不能让守护进程崩溃重启，保护必须继续。
                LogFailureWithThrottle("config-invalid", "配置内容无效，继续使用最后一份有效配置：" + exception.Message);
            }

            if (lastKnownGoodConfig.ProtectionEnabled
                && lastKnownGoodConfig.Rules.Any(
                    rule => rule.Enabled && rule.Target is RuleTarget.FileName or RuleTarget.FullPath))
            {
                ScanAndBlock(lastKnownGoodConfig);
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            SynchronizeWebsiteBlocking(lastKnownGoodConfig, nowUtc);
            if (!legacyHostsCleanupComplete && nowUtc >= nextLegacyHostsCleanupUtc)
            {
                legacyHostsCleanupComplete = CleanupLegacyWebsiteRules();
                nextLegacyHostsCleanupUtc = nowUtc.Add(LegacyHostsCleanupRetryInterval);
            }

            if (nowUtc >= nextHeartbeatUtc)
            {
                WriteHeartbeat(mode, nowUtc);
                nextHeartbeatUtc = nowUtc.Add(HeartbeatInterval);
            }

            if (isWindowsService && nowUtc >= nextRecoveryReapplyUtc)
            {
                // 只要服务在运行,就持续守住自己的恢复配置,不被 sc failure 清空。
                ServiceRecoveryGuard.Reapply();
                nextRecoveryReapplyUtc = nowUtc.Add(RecoveryReapplyInterval);
            }

            try
            {
                await Task.Delay(ScanInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        if (_dnsBlockServer is not null)
        {
            await _dnsBlockServer.DisposeAsync().ConfigureAwait(false);
            _dnsBlockServer = null;
        }

        TryAudit(new AuditEntry
        {
            EventType = "GuardStopped",
            Message = "守护程序已停止。"
        });
    }

    private AppConfig LoadConfigOrDefault()
    {
        try
        {
            return LoadConfigAndApplyMigrations();
        }
        catch (Exception exception)
        {
            TryAudit(new AuditEntry
            {
                EventType = "ConfigLoadFailed",
                Message = "首次读取配置失败，保护暂未启用：" + exception.Message,
                Success = false
            });
            return new AppConfig();
        }
    }

    private AppConfig LoadConfigAndApplyMigrations()
    {
        AppConfig config = _configStore.Load();
        bool normalized = SafetyPolicy.NormalizeFileNameRulePatterns(config);
        normalized |= SafetyPolicy.NormalizeDomainRulePatterns(config);
        int addedDefaultRules = DefaultRulePresets.Apply(config);
        if (normalized || addedDefaultRules > 0)
        {
            _configStore.Save(config);
        }
        if (addedDefaultRules > 0)
        {
            TryAudit(new AuditEntry
            {
                EventType = "DefaultRulesUpdated",
                Message = $"已新增或更新 {addedDefaultRules} 条默认规则；新增规则默认为停用，已有规则保留原勾选状态。"
            });
        }

        return config;
    }

    private void SynchronizeWebsiteBlocking(AppConfig config, DateTimeOffset nowUtc)
    {
        IReadOnlyList<WebsiteBlockRegistration> registrations =
            BuildWebsiteRegistrations(config);
        string signature = string.Join(
            '|',
            registrations
                .OrderBy(registration => registration.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(registration => registration.RuleId, StringComparer.Ordinal)
                .Select(registration => $"{registration.Domain}:{registration.RuleId}"));
        if (string.Equals(signature, _websitePolicySignature, StringComparison.Ordinal)
            || nowUtc < _nextWebsitePolicyRetryUtc)
        {
            return;
        }

        IReadOnlyList<string> domains = registrations
            .Select(registration => registration.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        try
        {
            bool browserPoliciesHealthy;
            if (domains.Count > 0)
            {
                if (_dnsBlockServer is null)
                {
                    _dnsBlockServer = new LocalDnsBlockServer(
                        OnWebsiteBlocked,
                        exception => LogFailureWithThrottle(
                            "dns-listener",
                            "本机DNS监听发生错误：" + exception.Message));
                    try
                    {
                        _dnsBlockServer.Start();
                    }
                    catch
                    {
                        _dnsBlockServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        _dnsBlockServer = null;
                        try
                        {
                            _nrptPolicyManager.Synchronize([]);
                            _browserDnsPolicyManager.Restore();
                        }
                        catch
                        {
                            // Preserve the original listener error for the audit log.
                        }

                        throw;
                    }
                }

                _dnsBlockServer.UpdateRegistrations(registrations);
                browserPoliciesHealthy = TryApplyBrowserPolicies(domains);
                _nrptPolicyManager.Synchronize(domains);
            }
            else
            {
                _nrptPolicyManager.Synchronize([]);
                browserPoliciesHealthy = TryRestoreBrowserDnsPolicies();
                _dnsBlockServer?.UpdateRegistrations([]);
            }

            if (!browserPoliciesHealthy)
            {
                // 浏览器策略没有完全生效时不能记成已同步，否则不会再重试；
                // NRPT 和本机DNS已就绪，稍后只需整体重试补齐浏览器策略。
                _websitePolicySignature = null;
                _nextWebsitePolicyRetryUtc = nowUtc.Add(FailureLogInterval);
                return;
            }

            _websitePolicySignature = signature;
            _nextWebsitePolicyRetryUtc = DateTimeOffset.MinValue;
            TryAudit(new AuditEntry
            {
                EventType = "WebsiteRulesApplied",
                Message = domains.Count == 0
                    ? "网站拦截规则已停用；BlockGame NRPT规则已清理，浏览器DNS策略已恢复。"
                    : $"已同步 {domains.Count} 个网站域名；仅命中域名使用本机DNS拦截，未修改网卡DNS。"
            });
        }
        catch (Exception exception)
        {
            _nextWebsitePolicyRetryUtc = nowUtc.Add(FailureLogInterval);
            LogFailureWithThrottle(
                "website-policy",
                "同步网站拦截规则失败：" + exception.Message);
        }
    }

    private bool TryApplyBrowserPolicies(IReadOnlyCollection<string> domains)
    {
        try
        {
            int skippedDomains = _browserDnsPolicyManager.Apply(domains);
            if (skippedDomains > 0)
            {
                LogFailureWithThrottle(
                    "browser-url-blocklist-capacity",
                    $"浏览器网址拦截策略条目已达上限，{skippedDomains} 个域名未写入浏览器策略；"
                        + "这些域名仍由NRPT和本机DNS拦截兜底。");
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or System.Text.Json.JsonException)
        {
            LogFailureWithThrottle(
                "browser-doh-policy",
                "浏览器加密DNS策略同步失败，稍后自动重试；NRPT网站拦截仍会继续尝试：" + exception.Message);
            return false;
        }
    }

    private bool TryRestoreBrowserDnsPolicies()
    {
        try
        {
            _browserDnsPolicyManager.Restore();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or System.Text.Json.JsonException)
        {
            LogFailureWithThrottle(
                "browser-doh-policy-restore",
                "恢复浏览器加密DNS策略失败，稍后自动重试：" + exception.Message);
            return false;
        }
    }

    private static IReadOnlyList<WebsiteBlockRegistration> BuildWebsiteRegistrations(
        AppConfig config)
    {
        if (!config.ProtectionEnabled)
        {
            return [];
        }

        var registrations = new List<WebsiteBlockRegistration>();
        foreach (BlockRule rule in config.Rules.Where(rule =>
                     rule.Enabled && rule.Target == RuleTarget.Domain))
        {
            foreach (string domain in WebsiteDomainRules.SplitAndNormalize(rule.Pattern))
            {
                registrations.Add(new WebsiteBlockRegistration(
                    domain,
                    rule.Id,
                    rule.Name));
            }
        }

        return registrations
            .GroupBy(registration => registration.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private void OnWebsiteBlocked(
        WebsiteBlockRegistration registration,
        string queryDomain)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (_websiteNotificationLock)
        {
            if (_lastWebsiteNotifications.TryGetValue(
                    queryDomain,
                    out DateTimeOffset previous)
                && nowUtc - previous < FailureLogInterval)
            {
                return;
            }

            _lastWebsiteNotifications[queryDomain] = nowUtc;
        }

        bool notificationSent = DesktopNotifier.TryShowWebsiteBlocked(queryDomain);
        TryAudit(new AuditEntry
        {
            EventType = "WebsiteBlocked",
            Message = $"网站 {queryDomain} 已被拦截，命中规则“{registration.RuleName}”。",
            Domain = queryDomain,
            RuleId = registration.RuleId,
            DesktopNotificationSent = notificationSent
        });
    }

    private void ScanAndBlock(AppConfig config)
    {
        DateTimeOffset scanStartedUtc = DateTimeOffset.UtcNow;
        bool hasFileNameRules = config.Rules.Any(
            rule => rule.Enabled && rule.Target == RuleTarget.FileName);
        bool hasFullPathRules = config.Rules.Any(
            rule => rule.Enabled && rule.Target == RuleTarget.FullPath);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception)
        {
            LogFailureWithThrottle("enumeration", "枚举进程失败：" + exception.Message);
            return;
        }

        var activeProcessIds = new HashSet<int>();
        foreach (Process process in processes)
        {
            using (process)
            {
                int processId;
                string processName;
                try
                {
                    processId = process.Id;
                    processName = process.ProcessName;
                }
                catch
                {
                    continue;
                }

                if (processId == Environment.ProcessId)
                {
                    continue;
                }
                activeProcessIds.Add(processId);

                string fileName = SafetyPolicy.NormalizeFileName(processName);
                var descriptor = new ProcessDescriptor(processId, fileName, null);
                RuleMatch? match = RuleMatcher.Match(config, descriptor, scanStartedUtc);
                if (match is null
                    && (hasFileNameRules || hasFullPathRules)
                    && !SafetyPolicy.IsProtectedSystemProcess(descriptor))
                {
                    ExecutableMetadata metadata = _metadataResolver.Read(
                        processId,
                        fileName);
                    descriptor = descriptor with
                    {
                        FullPath = metadata.FullPath,
                        ProductName = metadata.ProductName,
                        FileDescription = metadata.FileDescription
                    };
                    match = RuleMatcher.Match(config, descriptor, scanStartedUtc);
                }

                if (match is null)
                {
                    continue;
                }

                TryTerminate(process, match);
            }
        }

        _metadataResolver.RetainOnly(activeProcessIds);
    }

    private bool CleanupLegacyWebsiteRules()
    {
        try
        {
            if (_hostsFileManager.Synchronize([]))
            {
                TryAudit(new AuditEntry
                {
                    EventType = "LegacyHostsCleanup",
                    Message = "旧版 hosts 托管区块已清理；当前网站拦截不修改 hosts。"
                });
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogFailureWithThrottle(
                "legacy-hosts-cleanup",
                "清理已移除的网站功能留下的 hosts 托管区块失败：" + exception.Message);
            return false;
        }
    }

    private void TryTerminate(Process process, RuleMatch match)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            bool notificationSent;
            if (ShouldShowProcessBlockToast(match.Process.FileName))
            {
                notificationSent = DesktopNotifier.TryShowBlocked(match.Process.FileName);
            }
            else
            {
                // 被拦截的程序反复自启时，30 秒内同名进程只发一次系统通知；
                // 节流窗口内视为“已告知”，管理界面也不必再补弹窗。审计仍逐条记录。
                notificationSent = true;
            }

            TryAudit(new AuditEntry
            {
                EventType = "ProcessBlocked",
                Message = $"已阻止 {match.Process.FileName}，命中规则“{match.Rule.Name}”。",
                ProcessId = match.Process.ProcessId,
                ProcessName = match.Process.FileName,
                ProcessPath = match.Process.FullPath,
                RuleId = match.Rule.Id,
                DesktopNotificationSent = notificationSent
            });
        }
        catch (Exception exception)
        {
            string key = $"kill:{match.Process.ProcessId}:{match.Rule.Id}";
            LogFailureWithThrottle(
                key,
                $"无法终止 {match.Process.FileName}：{exception.Message}",
                match);
        }
    }

    private bool ShouldShowProcessBlockToast(string fileName)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (_lastProcessBlockToasts.TryGetValue(fileName, out DateTimeOffset previous)
            && nowUtc - previous < FailureLogInterval)
        {
            return false;
        }

        _lastProcessBlockToasts[fileName] = nowUtc;
        if (_lastProcessBlockToasts.Count > 256)
        {
            foreach (string staleKey in _lastProcessBlockToasts
                         .Where(pair => nowUtc - pair.Value >= FailureLogInterval)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _lastProcessBlockToasts.Remove(staleKey);
            }
        }

        return true;
    }

    private void WriteHeartbeat(string mode, DateTimeOffset nowUtc)
    {
        try
        {
            _heartbeatStore.Write(new GuardHeartbeat
            {
                TimestampUtc = nowUtc,
                ProcessId = Environment.ProcessId,
                Mode = mode
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogFailureWithThrottle("heartbeat", "写入守护状态失败：" + exception.Message);
        }
    }

    private void LogFailureWithThrottle(string key, string message, RuleMatch? match = null)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (_lastFailureLogs.TryGetValue(key, out DateTimeOffset previous)
            && nowUtc - previous < FailureLogInterval)
        {
            return;
        }

        _lastFailureLogs[key] = nowUtc;
        TryAudit(new AuditEntry
        {
            EventType = "GuardWarning",
            Message = message,
            Success = false,
            ProcessId = match?.Process.ProcessId,
            ProcessPath = match?.Process.FullPath,
            RuleId = match?.Rule.Id
        });
    }

    private void TryAudit(AuditEntry entry)
    {
        try
        {
            _auditLog.Append(entry);
        }
        catch
        {
            // The process blocker should continue even if logging is temporarily unavailable.
        }
    }
}
