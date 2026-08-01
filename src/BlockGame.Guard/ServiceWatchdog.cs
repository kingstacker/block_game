using System.Runtime.InteropServices;
using BlockGame.Core.Services;

namespace BlockGame.Guard;

/// <summary>
/// 由独立的 SYSTEM 计划任务调用。常驻任务每两秒检查，分钟任务负责兜底；
/// 即使管理程序和服务进程都已退出，仍可从服务控制管理器重新启动守护服务。
/// </summary>
internal static class ServiceWatchdog
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const int ServiceStopped = 0x00000001;
    private const int ServiceStartPending = 0x00000002;
    private const int ServiceRunning = 0x00000004;
    private const int ErrorServiceAlreadyRunning = 1056;

    public static async Task RunContinuouslyAsync()
    {
        while (true)
        {
            _ = EnsureServiceRunning();
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }

    public static int EnsureServiceRunning()
    {
        var paths = DataPaths.CreateDefault();
        if (File.Exists(paths.MaintenanceStopFile))
        {
            return 0;
        }

        nint manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == nint.Zero)
        {
            return Marshal.GetLastWin32Error();
        }

        try
        {
            nint service = OpenService(
                manager,
                WindowsServiceHost.ServiceName,
                ServiceQueryStatus | ServiceStart);
            if (service == nint.Zero)
            {
                return Marshal.GetLastWin32Error();
            }

            try
            {
                if (!QueryServiceStatus(service, out ServiceStatus status))
                {
                    return Marshal.GetLastWin32Error();
                }

                if (status.CurrentState is ServiceRunning or ServiceStartPending)
                {
                    return 0;
                }

                if (status.CurrentState != ServiceStopped)
                {
                    return 0;
                }

                if (StartService(service, 0, nint.Zero))
                {
                    return 0;
                }

                int error = Marshal.GetLastWin32Error();
                return error == ErrorServiceAlreadyRunning ? 0 : error;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
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
    private static extern nint OpenService(
        nint serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(
        nint service,
        out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(
        nint service,
        int argumentCount,
        nint arguments);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
