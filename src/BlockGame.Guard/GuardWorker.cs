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

    private readonly DataPaths _paths;
    private readonly ConfigStore _configStore;
    private readonly AuditLog _auditLog;
    private readonly HeartbeatStore _heartbeatStore;
    private readonly HostsFileManager _hostsFileManager;
    private readonly Dictionary<string, DateTimeOffset> _lastFailureLogs = new();

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
    }

    public async Task RunAsync(string mode, CancellationToken cancellationToken)
    {
        _paths.EnsureDirectory();
        AppConfig lastKnownGoodConfig = LoadConfigOrDefault();
        DateTimeOffset nextHeartbeatUtc = DateTimeOffset.MinValue;
        DateTimeOffset nextLegacyHostsCleanupUtc = DateTimeOffset.MinValue;
        bool legacyHostsCleanupComplete = false;

        TryAudit(new AuditEntry
        {
            EventType = "GuardStarted",
            Message = $"守护程序已启动，模式：{mode}。"
        });

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                lastKnownGoodConfig = LoadConfigAndRemoveLegacyWebsiteRules();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogFailureWithThrottle("config", "读取配置失败，继续使用最后一份有效配置：" + exception.Message);
            }
            catch (System.Text.Json.JsonException exception)
            {
                LogFailureWithThrottle("config-json", "配置格式损坏，继续使用最后一份有效配置：" + exception.Message);
            }

            if (lastKnownGoodConfig.ProtectionEnabled
                && lastKnownGoodConfig.Rules.Any(
                    rule => rule.Enabled && rule.Target is RuleTarget.FileName or RuleTarget.FullPath))
            {
                ScanAndBlock(lastKnownGoodConfig);
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
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

            try
            {
                await Task.Delay(ScanInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
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
            return LoadConfigAndRemoveLegacyWebsiteRules();
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

    private AppConfig LoadConfigAndRemoveLegacyWebsiteRules()
    {
        AppConfig config = _configStore.Load();
        int removedWebsiteRules = SafetyPolicy.RemoveLegacyWebsiteRules(config);
        int addedDefaultRules = DefaultRulePresets.Apply(config);
        if (removedWebsiteRules > 0 || addedDefaultRules > 0)
        {
            _configStore.Save(config);
        }
        if (removedWebsiteRules > 0)
        {
            TryAudit(new AuditEntry
            {
                EventType = "WebsiteFeatureRemoved",
                Message = $"网站屏蔽功能已移除，同时删除 {removedWebsiteRules} 条旧网站规则。"
            });
        }
        if (addedDefaultRules > 0)
        {
            TryAudit(new AuditEntry
            {
                EventType = "DefaultRulesAdded",
                Message = $"已添加 {addedDefaultRules} 条默认规则，初始状态为停用。"
            });
        }

        return config;
    }

    private void ScanAndBlock(AppConfig config)
    {
        bool needsFullPath = config.Rules.Any(rule => rule.Enabled && rule.Target == RuleTarget.FullPath);
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

                string fileName = SafetyPolicy.NormalizeFileName(processName);
                string? fullPath = needsFullPath
                    ? ProcessPathResolver.TryGetPath(processId)
                    : null;
                var descriptor = new ProcessDescriptor(processId, fileName, fullPath);
                RuleMatch? match = RuleMatcher.Match(config, descriptor);
                if (match is null)
                {
                    continue;
                }

                TryTerminate(process, match);
            }
        }
    }

    private bool CleanupLegacyWebsiteRules()
    {
        try
        {
            if (_hostsFileManager.Synchronize([]))
            {
                TryAudit(new AuditEntry
                {
                    EventType = "WebsiteFeatureRemoved",
                    Message = "网站屏蔽功能已移除，旧 hosts 托管区块已清理。"
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
            bool notificationSent = DesktopNotifier.TryShowBlocked(match.Process.FileName);
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
