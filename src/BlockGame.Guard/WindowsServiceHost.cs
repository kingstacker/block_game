using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BlockGame.Guard;

internal static class WindowsServiceHost
{
    public const string ServiceName = "BlockGameGuard";

    private const int ServiceStopped = 0x00000001;
    private const int ServiceStartPending = 0x00000002;
    private const int ServiceStopPending = 0x00000003;
    private const int ServiceRunning = 0x00000004;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceAcceptShutdown = 0x00000004;
    private const int ServiceControlStop = 0x00000001;
    private const int ServiceControlShutdown = 0x00000005;
    private const int ServiceWin32OwnProcess = 0x00000010;

    private static Func<CancellationToken, Task>? _workerFactory;
    private static string? _maintenanceStopFile;
    private static CancellationTokenSource? _cancellation;
    private static nint _statusHandle;
    private static ServiceMainDelegate? _serviceMainDelegate;
    private static HandlerExDelegate? _handlerDelegate;

    public static void Run(
        Func<CancellationToken, Task> workerFactory,
        string maintenanceStopFile)
    {
        _workerFactory = workerFactory ?? throw new ArgumentNullException(nameof(workerFactory));
        _maintenanceStopFile = string.IsNullOrWhiteSpace(maintenanceStopFile)
            ? throw new ArgumentException("Maintenance stop file cannot be empty.", nameof(maintenanceStopFile))
            : Path.GetFullPath(maintenanceStopFile);
        _serviceMainDelegate = ServiceMain;

        var serviceTable = new[]
        {
            new ServiceTableEntry
            {
                ServiceName = ServiceName,
                ServiceMain = _serviceMainDelegate
            },
            new ServiceTableEntry()
        };

        if (!StartServiceCtrlDispatcher(serviceTable))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void ServiceMain(int argumentCount, nint arguments)
    {
        _handlerDelegate = Handler;
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _handlerDelegate, nint.Zero);
        if (_statusHandle == nint.Zero)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        ReportStatus(ServiceStartPending, 10_000);

        try
        {
            ReportStatus(ServiceRunning);
            _workerFactory!(_cancellation.Token).GetAwaiter().GetResult();
        }
        finally
        {
            ReportStatus(ServiceStopped);
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private static int Handler(int control, int eventType, nint eventData, nint context)
    {
        if (control == ServiceControlStop && !TryConsumeMaintenanceStopRequest())
        {
            Environment.FailFast(
                "BlockGameGuard received an unauthorized stop request; Windows service recovery will restart it.");
        }

        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            ReportStatus(ServiceStopPending, 10_000);
            _cancellation?.Cancel();
        }

        return 0;
    }

    private static bool TryConsumeMaintenanceStopRequest()
    {
        string? marker = _maintenanceStopFile;
        if (string.IsNullOrWhiteSpace(marker) || !File.Exists(marker))
        {
            return false;
        }

        try
        {
            DateTime lastWriteUtc = File.GetLastWriteTimeUtc(marker);
            TimeSpan age = DateTime.UtcNow - lastWriteUtc;
            if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(30))
            {
                File.Delete(marker);
                return false;
            }

            File.Delete(marker);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReportStatus(int state, int waitHint = 0)
    {
        if (_statusHandle == nint.Zero)
        {
            return;
        }

        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == ServiceRunning
                ? ServiceAcceptStop | ServiceAcceptShutdown
                : 0,
            Win32ExitCode = 0,
            ServiceSpecificExitCode = 0,
            CheckPoint = 0,
            WaitHint = waitHint
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainDelegate(int argumentCount, nint arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerExDelegate(int control, int eventType, nint eventData, nint context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServiceName;

        public ServiceMainDelegate? ServiceMain;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher(
        [In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint RegisterServiceCtrlHandlerEx(
        string serviceName,
        HandlerExDelegate handler,
        nint context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(nint statusHandle, ref ServiceStatus serviceStatus);
}
