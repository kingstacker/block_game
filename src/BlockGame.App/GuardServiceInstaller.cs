using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BlockGame.Core.Services;

namespace BlockGame.App;

internal sealed record GuardInstallResult(bool Success, bool InstalledNow, string Message);

internal sealed record GuardHealthResult(bool Healthy, bool ActionTaken, string Message);

internal static class GuardServiceInstaller
{
    private const string ServiceName = "BlockGameGuard";
    private const string AutoStartTaskName = "BlockGameAutoStart";
    private const string ServiceWatchdogTaskName = "BlockGameGuardWatchdog";
    private const string ServiceRecoveryTaskName = "BlockGameGuardRecovery";
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceAutoStart = 0x00000002;
    private const int ServiceErrorNormal = 0x00000001;
    private const int ServiceStopped = 0x00000001;
    private const int ServiceStopPending = 0x00000003;
    private const int ServiceRunning = 0x00000004;
    private const int ServiceControlStop = 0x00000001;
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    public static GuardInstallResult EnsureInstalled(DataPaths paths)
    {
        string installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            DataPaths.ProductDirectoryName);
        string targetExecutable = Path.Combine(installDirectory, "BlockGame.Guard.exe");

        ServiceLookup lookup = LookupService();
        if (lookup.ErrorMessage is not null)
        {
            return new GuardInstallResult(false, false, lookup.ErrorMessage);
        }

        if (lookup.Exists)
        {
            if (!ServicePointsToBlockGame())
            {
                return new GuardInstallResult(
                    false,
                    false,
                    "检测到同名的 BlockGameGuard 服务，但它不是当前程序创建的服务；为安全起见未修改它。 ");
            }

            bool updated = false;
            string? updateSourceExecutable = LocateGuardExecutable();
            if (updateSourceExecutable is not null
                && !GuardFilesMatch(Path.GetDirectoryName(updateSourceExecutable)!, installDirectory))
            {
                RemoveServiceWatchdog();
                if (!TryStopService(paths.MaintenanceStopFile, out string stopMessage))
                {
                    return new GuardInstallResult(false, false, "更新后台守护服务失败：" + stopMessage);
                }

                try
                {
                    CopyGuardFiles(Path.GetDirectoryName(updateSourceExecutable)!, installDirectory);
                    updated = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _ = TryStartService(out _);
                    return new GuardInstallResult(
                        false,
                        false,
                        "更新后台守护程序文件失败：" + exception.Message);
                }
            }

            ConfigureRecovery();
            EnsureAutoStart();
            if (!EnsureServiceWatchdog(targetExecutable, out string watchdogMessage))
            {
                return new GuardInstallResult(false, false, watchdogMessage);
            }
            if (!TryStartService(out string startMessage))
            {
                return new GuardInstallResult(false, false, startMessage);
            }

            return new GuardInstallResult(
                true,
                false,
                updated
                    ? "后台守护服务已更新并重新启动。 "
                    : "后台守护服务已经存在并正在运行。 ");
        }

        string? sourceExecutable = LocateGuardExecutable();
        if (sourceExecutable is null)
        {
            return new GuardInstallResult(
                false,
                false,
                "找不到 BlockGame.Guard.exe。请使用完整发布目录运行，或先执行安装脚本。 ");
        }

        try
        {
            Directory.CreateDirectory(installDirectory);
            CopyGuardFiles(Path.GetDirectoryName(sourceExecutable)!, installDirectory);
            paths.EnsureDirectory();
            TryProtectDataDirectory(paths.RootDirectory, out string? aclWarning);

            if (!CreateService(targetExecutable, out string createMessage))
            {
                return new GuardInstallResult(false, false, createMessage);
            }

            ConfigureRecovery();
            EnsureAutoStart();
            if (!EnsureServiceWatchdog(targetExecutable, out string watchdogMessage))
            {
                return new GuardInstallResult(false, true, watchdogMessage);
            }
            if (!TryStartService(out string startMessage))
            {
                return new GuardInstallResult(false, true, startMessage);
            }

            string message = "后台守护服务已自动安装并启动。 ";
            if (aclWarning is not null)
            {
                message += "数据目录权限保护未完全设置：" + aclWarning;
            }

            return new GuardInstallResult(true, true, message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new GuardInstallResult(false, false, "没有权限安装后台服务：" + exception.Message);
        }
        catch (IOException exception)
        {
            return new GuardInstallResult(false, false, "复制守护程序失败：" + exception.Message);
        }
        catch (Exception exception)
        {
            return new GuardInstallResult(false, false, "自动安装后台服务失败：" + exception.Message);
        }
    }

    /// <summary>
    /// 看门狗定时调用:确认后台守护服务仍然存在、运行,并守住恢复配置。
    /// 被删除则重装,被停止则重启,恢复动作被清空则补刷。
    /// </summary>
    public static GuardHealthResult VerifyAndRepair(DataPaths paths)
    {
        ServiceLookup lookup = LookupService();
        if (lookup.ErrorMessage is not null)
        {
            return new GuardHealthResult(false, false, lookup.ErrorMessage);
        }

        if (!lookup.Exists)
        {
            GuardInstallResult install = EnsureInstalled(paths);
            return new GuardHealthResult(
                install.Success,
                true,
                install.Success
                    ? "检测到后台守护服务被移除，已自动重新安装并启动。 "
                    : "自动修复后台守护服务失败：" + install.Message);
        }

        if (!ServicePointsToBlockGame())
        {
            return new GuardHealthResult(
                false,
                false,
                "检测到同名的 BlockGameGuard 服务，但它不是当前程序创建的服务；为安全起见未修改它。 ");
        }

        // 无论服务是否在跑，都补刷一次恢复配置，防止它被 sc failure 清空。
        ConfigureRecovery();
        EnsureAutoStart();
        string watchdogExecutable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            DataPaths.ProductDirectoryName,
            "BlockGame.Guard.exe");
        if (!EnsureServiceWatchdog(watchdogExecutable, out string watchdogMessage))
        {
            return new GuardHealthResult(false, true, watchdogMessage);
        }

        if (TryQueryServiceState(out int state) && state == ServiceRunning)
        {
            return new GuardHealthResult(true, false, "后台守护服务运行正常。 ");
        }

        bool started = TryStartService(out string startMessage);
        return new GuardHealthResult(
            started,
            true,
            started
                ? "检测到后台守护服务已停止，已重新启动。 "
                : "重新启动后台守护服务失败：" + startMessage);
    }

    public static bool TryCleanupNetworkPolicies(out string message)
    {
        string? guardExecutable = LocateGuardExecutable();
        if (guardExecutable is null)
        {
            message = "找不到 BlockGame.Guard.exe，后台服务启动后会继续清理网站策略。";
            return false;
        }

        try
        {
            CommandResult result = RunProcess(
                guardExecutable,
                "--cleanup-network-policies");
            if (result.ExitCode == 0)
            {
                message = "网站NRPT和浏览器DNS策略已立即清理。";
                return true;
            }

            message = "立即清理网站策略失败：" + result.Output.Trim();
            return false;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            message = "立即清理网站策略失败：" + exception.Message;
            return false;
        }
    }

    private static string? LocateGuardExecutable()
    {
        string baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "BlockGame.Guard.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "guard", "BlockGame.Guard.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "BlockGame.Guard", "bin", "Release", "net9.0-windows", "BlockGame.Guard.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "BlockGame.Guard", "bin", "Debug", "net9.0-windows", "BlockGame.Guard.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "BlockGame.Guard", "bin", "Release", "net9.0-windows", "BlockGame.Guard.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "BlockGame.Guard", "bin", "Debug", "net9.0-windows", "BlockGame.Guard.exe")),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                DataPaths.ProductDirectoryName,
                "BlockGame.Guard.exe")
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    private static void CopyGuardFiles(string sourceDirectory, string targetDirectory)
    {
        if (string.Equals(
                Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var files = Directory.EnumerateFiles(sourceDirectory)
            .Where(IsGuardFile);

        foreach (string file in files)
        {
            string destination = Path.Combine(targetDirectory, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool GuardFilesMatch(string sourceDirectory, string targetDirectory)
    {
        string normalizedSource = Path.GetFullPath(sourceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        string normalizedTarget = Path.GetFullPath(targetDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory).Where(IsGuardFile))
            {
                string targetFile = Path.Combine(targetDirectory, Path.GetFileName(sourceFile));
                if (!File.Exists(targetFile) || !FilesAreEqual(sourceFile, targetFile))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsGuardFile(string file)
    {
        string name = Path.GetFileName(file);
        return name.StartsWith("BlockGame.Guard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "BlockGame.Core.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool FilesAreEqual(string first, string second)
    {
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        using FileStream firstStream = File.OpenRead(first);
        using FileStream secondStream = File.OpenRead(second);
        return SHA256.HashData(firstStream).SequenceEqual(SHA256.HashData(secondStream));
    }

    private static ServiceLookup LookupService()
    {
        CommandResult result = RunSc("query", ServiceName);
        if (result.ExitCode == 0)
        {
            return new ServiceLookup(true, null);
        }

        if (result.ExitCode == 1060 || result.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceLookup(false, null);
        }

        return new ServiceLookup(false, "查询后台服务失败：" + result.Output.Trim());
    }

    private static bool ServicePointsToBlockGame()
    {
        CommandResult result = RunSc("qc", ServiceName);
        return result.ExitCode == 0
            && result.Output.Contains("BlockGame.Guard.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CreateService(string targetExecutable, out string message)
    {
        nint manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
        if (manager == nint.Zero)
        {
            message = "打开 Windows 服务管理器失败：" + Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            string binaryPath = $"\"{targetExecutable}\" --service";
            nint service = CreateService(
                manager,
                ServiceName,
                "BlockGame Guard Service",
                ServiceQueryStatus | ServiceStart,
                ServiceWin32OwnProcess,
                ServiceAutoStart,
                ServiceErrorNormal,
                binaryPath,
                null,
                nint.Zero,
                null,
                null,
                null);

            if (service == nint.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceExists)
                {
                    message = "服务已经存在。 ";
                    return true;
                }

                message = "创建后台服务失败：" + error;
                return false;
            }

            CloseServiceHandle(service);
            message = "服务创建成功。 ";
            return true;
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static bool TryStartService(out string message)
    {        nint manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == nint.Zero)
        {
            message = "打开服务管理器失败：" + Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            nint service = OpenService(manager, ServiceName, ServiceQueryStatus | ServiceStart);
            if (service == nint.Zero)
            {
                message = "打开后台服务失败：" + Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                if (QueryServiceStatus(service, out NativeServiceStatus status)
                    && status.CurrentState == ServiceRunning)
                {
                    message = "后台服务正在运行。 ";
                    return true;
                }

                if (!StartService(service, 0, nint.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceAlreadyRunning)
                    {
                        message = "启动后台服务失败：" + error;
                        return false;
                    }
                }

                message = "后台服务已启动。 ";
                return true;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static bool TryQueryServiceState(out int state)
    {
        state = 0;
        nint manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == nint.Zero)
        {
            return false;
        }

        try
        {
            nint service = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (service == nint.Zero)
            {
                return false;
            }

            try
            {
                if (QueryServiceStatus(service, out NativeServiceStatus status))
                {
                    state = status.CurrentState;
                    return true;
                }

                return false;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static bool TryStopService(string maintenanceStopFile, out string message)
    {
        nint manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == nint.Zero)
        {
            message = "打开服务管理器失败：" + Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            nint service = OpenService(manager, ServiceName, ServiceQueryStatus | ServiceStop);
            if (service == nint.Zero)
            {
                message = "打开后台服务失败：" + Marshal.GetLastWin32Error();
                return false;
            }

            bool maintenanceStopRequested = false;
            try
            {
                if (!QueryServiceStatus(service, out NativeServiceStatus status))
                {
                    message = "查询后台服务状态失败：" + Marshal.GetLastWin32Error();
                    return false;
                }

                if (status.CurrentState == ServiceStopped)
                {
                    message = "后台服务已经停止。 ";
                    return true;
                }

                try
                {
                    string? markerDirectory = Path.GetDirectoryName(maintenanceStopFile);
                    if (!string.IsNullOrWhiteSpace(markerDirectory))
                    {
                        Directory.CreateDirectory(markerDirectory);
                    }

                    File.WriteAllText(
                        maintenanceStopFile,
                        $"{Environment.ProcessId}:{Guid.NewGuid():N}");
                    maintenanceStopRequested = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    message = "创建守护服务维护停止标记失败：" + exception.Message;
                    return false;
                }

                if (status.CurrentState != ServiceStopPending
                    && !ControlService(service, ServiceControlStop, out _))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceNotActive)
                    {
                        message = "停止后台服务失败：" + error;
                        return false;
                    }
                }

                for (int attempt = 0; attempt < 100; attempt++)
                {
                    Thread.Sleep(100);
                    if (QueryServiceStatus(service, out status)
                        && status.CurrentState == ServiceStopped)
                    {
                        message = "后台服务已停止。 ";
                        return true;
                    }
                }

                message = "等待后台服务停止超时。 ";
                return false;
            }
            finally
            {
                if (maintenanceStopRequested)
                {
                    try
                    {
                        File.Delete(maintenanceStopFile);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // The service normally consumes the marker.
                    }
                }

                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static void ConfigureRecovery()
    {
        _ = RunSc(
            "failure",
            ServiceName,
            "actions=",
            "restart/500/restart/1000/restart/3000",
            "reset=",
            "86400");
        _ = RunSc("failureflag", ServiceName, "1");
    }

    /// <summary>
    /// 用“登录时触发、以最高权限运行”的计划任务实现开机自启。控制面板要求管理员
    /// 权限,若用注册表 Run 键会在每次登录弹 UAC;计划任务则静默提权启动托盘看门狗。
    /// </summary>
    private static void EnsureAutoStart()
    {
        try
        {
            string? appExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(appExecutable) || !File.Exists(appExecutable))
            {
                return;
            }

            // /TR 必须把映像路径和静默启动参数作为一个完整命令传入；/F 覆盖同名
            // 任务，以便升级后刷新程序路径和启动模式。
            string taskCommand = StartupArguments.BuildAutoStartCommand(appExecutable);
            _ = RunProcess(
                Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                "/Create",
                "/TN",
                AutoStartTaskName,
                "/TR",
                taskCommand,
                "/SC",
                "ONLOGON",
                "/RL",
                "HIGHEST",
                "/F");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            // 自启注册失败不应阻断服务安装;下次启动会再次尝试。
        }
    }

    private static bool EnsureServiceWatchdog(
        string guardExecutable,
        out string message)
    {
        string scheduledTaskTool = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        CommandResult watchdogQuery = RunProcess(
            scheduledTaskTool,
            "/Query",
            "/TN",
            ServiceWatchdogTaskName);
        if (watchdogQuery.ExitCode != 0)
        {
            string watchdogCommand = $"\"{guardExecutable}\" --watch-service";
            CommandResult watchdogCreate = RunProcess(
                scheduledTaskTool,
                "/Create",
                "/TN",
                ServiceWatchdogTaskName,
                "/TR",
                watchdogCommand,
                "/SC",
                "MINUTE",
                "/MO",
                "1",
                "/RU",
                "SYSTEM",
                "/RL",
                "HIGHEST",
                "/F");
            if (watchdogCreate.ExitCode != 0)
            {
                message = "注册常驻守护服务看门狗失败：" + watchdogCreate.Output.Trim();
                return false;
            }

            _ = RunProcess(
                scheduledTaskTool,
                "/Run",
                "/TN",
                ServiceWatchdogTaskName);
        }

        CommandResult recoveryQuery = RunProcess(
            scheduledTaskTool,
            "/Query",
            "/TN",
            ServiceRecoveryTaskName);
        if (recoveryQuery.ExitCode != 0)
        {
            string recoveryCommand = $"\"{guardExecutable}\" --ensure-service-running";
            CommandResult recoveryCreate = RunProcess(
                scheduledTaskTool,
                "/Create",
                "/TN",
                ServiceRecoveryTaskName,
                "/TR",
                recoveryCommand,
                "/SC",
                "MINUTE",
                "/MO",
                "1",
                "/RU",
                "SYSTEM",
                "/RL",
                "HIGHEST",
                "/F");
            if (recoveryCreate.ExitCode != 0)
            {
                message = "注册守护服务分钟恢复任务失败：" + recoveryCreate.Output.Trim();
                return false;
            }
        }

        message = "独立守护服务看门狗已注册。 ";
        return true;
    }

    private static void RemoveServiceWatchdog()
    {
        string scheduledTaskTool = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        foreach (string taskName in new[] { ServiceWatchdogTaskName, ServiceRecoveryTaskName })
        {
            _ = RunProcess(scheduledTaskTool, "/End", "/TN", taskName);
            _ = RunProcess(scheduledTaskTool, "/Delete", "/TN", taskName, "/F");
        }
    }

    private static void TryProtectDataDirectory(string directory, out string? warning)
    {
        warning = null;
        CommandResult result = RunProcess(
            Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            directory,
            "/inheritance:r",
            "/grant:r",
            "*S-1-5-18:(OI)(CI)F",
            "/grant:r",
            "*S-1-5-32-544:(OI)(CI)F");
        if (result.ExitCode != 0)
        {
            warning = result.Output.Trim();
        }
    }

    private static CommandResult RunSc(params string[] arguments)
        => RunProcess(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments);

    private static CommandResult RunProcess(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动系统服务工具。 ");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return new CommandResult(-1, "系统工具运行超时。");
        }

        string output = standardOutput.GetAwaiter().GetResult();
        output += standardError.GetAwaiter().GetResult();
        return new CommandResult(process.ExitCode, output);
    }

    private sealed record ServiceLookup(bool Exists, string? ErrorMessage);

    private sealed record CommandResult(int ExitCode, string Output);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateService(
        nint serviceManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        int serviceType,
        int startType,
        int errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        nint tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(
        nint serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(nint service, uint argumentCount, nint arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(nint service, out NativeServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        nint service,
        int control,
        out NativeServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
