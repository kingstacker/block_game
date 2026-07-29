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
        AppConfig config = configStore.Load();
        bool valid = UninstallAuthorizationService.ValidateAndConsume(
            config,
            token,
            DateTimeOffset.UtcNow);

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

        configStore.Save(config);
        if (File.Exists(paths.UninstallTokenFile))
        {
            File.Delete(paths.UninstallTokenFile);
        }

        return 0;
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
