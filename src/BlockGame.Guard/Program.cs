using BlockGame.Core.Models;
using BlockGame.Core.Services;

namespace BlockGame.Guard;

internal static class Program
{
    private static int Main(string[] args)
    {
        var paths = DataPaths.CreateDefault();
        var configStore = new ConfigStore(paths);
        var auditLog = new AuditLog(paths);
        var heartbeatStore = new HeartbeatStore(paths);

        try
        {
            if (args.Contains("--watch-service", StringComparer.OrdinalIgnoreCase))
            {
                ServiceWatchdog.RunContinuouslyAsync().GetAwaiter().GetResult();
                return 0;
            }

            if (args.Contains("--ensure-service-running", StringComparer.OrdinalIgnoreCase))
            {
                return ServiceWatchdog.EnsureServiceRunning();
            }

            if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
            {
                WindowsServiceHost.Run(
                    cancellationToken => new GuardWorker(
                        paths,
                        configStore,
                        auditLog,
                        heartbeatStore).RunAsync("WindowsService", cancellationToken),
                    paths.MaintenanceStopFile);
                return 0;
            }

            if (args.Contains("--cleanup-network-policies", StringComparer.OrdinalIgnoreCase))
            {
                return CleanupNetworkPolicies(paths, auditLog);
            }

            if (args.Contains("--verify-unconfigured-uninstall", StringComparer.OrdinalIgnoreCase))
            {
                return VerifyUnconfiguredUninstall(paths, configStore, auditLog);
            }

            int tokenArgument = Array.FindIndex(
                args,
                argument => string.Equals(argument, "--verify-uninstall-token", StringComparison.OrdinalIgnoreCase));
            if (tokenArgument >= 0)
            {
                if (tokenArgument + 1 >= args.Length)
                {
                    Console.Error.WriteLine("缺少卸载授权令牌。 ");
                    return 2;
                }

                return VerifyUninstallToken(
                    args[tokenArgument + 1],
                    paths,
                    configStore,
                    auditLog);
            }

            return RunConsole(paths, configStore, auditLog, heartbeatStore);
        }
        catch (Exception exception)
        {
            TryAppendAudit(
                auditLog,
                new AuditEntry
                {
                    EventType = "GuardFatalError",
                    Message = exception.Message,
                    Success = false
                });
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RunConsole(
        DataPaths paths,
        ConfigStore configStore,
        AuditLog auditLog,
        HeartbeatStore heartbeatStore)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.WriteLine("BlockGame Guard 正在以控制台模式运行。按 Ctrl+C 停止。 ");
        var worker = new GuardWorker(paths, configStore, auditLog, heartbeatStore);
        worker.RunAsync("Console", cancellation.Token).GetAwaiter().GetResult();
        return 0;
    }

    private static int VerifyUninstallToken(
        string token,
        DataPaths paths,
        ConfigStore configStore,
        AuditLog auditLog)
    {
        bool valid = false;
        _ = configStore.Update(config =>
        {
            valid = UninstallAuthorizationService.ValidateAndConsume(
                config,
                token,
                DateTimeOffset.UtcNow);
            if (!valid)
            {
                return false;
            }

            config.ProtectionEnabled = false;
            config.ProtectionLocked = false;
            config.UnlockRequestedAtUtc = null;
            config.UnlockAvailableAtUtc = null;
            return true;
        });

        TryAppendAudit(
            auditLog,
            new AuditEntry
            {
                EventType = "UninstallTokenValidation",
                Message = valid ? "卸载授权令牌验证成功。" : "卸载授权令牌无效或已过期。",
                Success = valid
            });

        if (!valid)
        {
            return 3;
        }

        return FinishUninstallVerification(paths, auditLog);
    }

    private static int VerifyUnconfiguredUninstall(
        DataPaths paths,
        ConfigStore configStore,
        AuditLog auditLog)
    {
        bool valid = false;
        bool cleanupRequired = false;
        _ = configStore.Update(config =>
        {
            valid = !config.SetupCompleted && !config.ProtectionLocked;
            if (!valid)
            {
                return false;
            }

            cleanupRequired = config.ProtectionEnabled;
            config.ProtectionEnabled = false;
            config.UnlockRequestedAtUtc = null;
            config.UnlockAvailableAtUtc = null;
            ProtectionManager.ClearUninstallAuthorization(config);
            return true;
        });

        TryAppendAudit(
            auditLog,
            new AuditEntry
            {
                EventType = "UnconfiguredUninstallValidation",
                Message = valid
                    ? "首次设置尚未完成，允许移除未配置的安装。"
                    : "未配置安装卸载校验失败：首次设置已完成或保护仍处于锁定状态。",
                Success = valid
            });

        if (!valid)
        {
            return 3;
        }

        if (!cleanupRequired)
        {
            if (File.Exists(paths.UninstallTokenFile))
            {
                File.Delete(paths.UninstallTokenFile);
            }

            return 0;
        }

        return FinishUninstallVerification(paths, auditLog);
    }

    private static int FinishUninstallVerification(DataPaths paths, AuditLog auditLog)
    {
        if (File.Exists(paths.UninstallTokenFile))
        {
            File.Delete(paths.UninstallTokenFile);
        }

        try
        {
            new NrptPolicyManager(paths).Synchronize([]);
            new BrowserDnsPolicyManager(paths).Restore();
        }
        catch (Exception exception)
        {
            TryAppendAudit(
                auditLog,
                new AuditEntry
                {
                    EventType = "NetworkPolicyCleanupFailed",
                    Message = "卸载前清理网站拦截策略失败：" + exception.Message,
                    Success = false
                });
            Console.Error.WriteLine("卸载前清理网站拦截策略失败：" + exception.Message);
            return 4;
        }

        return 0;
    }

    private static int CleanupNetworkPolicies(DataPaths paths, AuditLog auditLog)
    {
        try
        {
            new NrptPolicyManager(paths).Synchronize([]);
            new BrowserDnsPolicyManager(paths).Restore();
            TryAppendAudit(
                auditLog,
                new AuditEntry
                {
                    EventType = "NetworkPoliciesReset",
                    Message = "BlockGame网站NRPT和浏览器DNS策略已清理。"
                });
            return 0;
        }
        catch (Exception exception)
        {
            TryAppendAudit(
                auditLog,
                new AuditEntry
                {
                    EventType = "NetworkPolicyCleanupFailed",
                    Message = "清理网站拦截策略失败：" + exception.Message,
                    Success = false
                });
            Console.Error.WriteLine(exception.Message);
            return 4;
        }
    }

    private static void TryAppendAudit(AuditLog auditLog, AuditEntry entry)
    {
        try
        {
            auditLog.Append(entry);
        }
        catch
        {
            // A guard failure must still reach stderr/SCM if the audit file is unavailable.
        }
    }
}
