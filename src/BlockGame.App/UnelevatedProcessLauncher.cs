using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace BlockGame.App;

internal static class UnelevatedProcessLauncher
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint LogonWithProfile = 0x00000001;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    public static int Start(string fileName, string arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!IsAdministrator())
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("无法启动快捷方式拖放组件。");
            return process.Id;
        }

        nint shellWindow = GetShellWindow();
        if (shellWindow == 0)
        {
            throw new InvalidOperationException("找不到当前 Windows 桌面资源管理器。");
        }

        _ = GetWindowThreadProcessId(shellWindow, out uint shellProcessId);
        nint shellProcess = OpenProcess(ProcessQueryLimitedInformation, false, shellProcessId);
        if (shellProcess == 0)
        {
            throw CreateWin32Exception("无法访问 Windows 桌面资源管理器进程");
        }

        nint shellToken = 0;
        nint primaryToken = 0;
        nint environment = 0;
        try
        {
            if (!OpenProcessToken(
                    shellProcess,
                    TokenAssignPrimary | TokenDuplicate | TokenQuery,
                    out shellToken))
            {
                throw CreateWin32Exception("无法读取 Windows 桌面资源管理器令牌");
            }

            if (!DuplicateTokenEx(
                    shellToken,
                    MaximumAllowed,
                    0,
                    SecurityImpersonation,
                    TokenPrimary,
                    out primaryToken))
            {
                throw CreateWin32Exception("无法创建普通权限进程令牌");
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                throw CreateWin32Exception("无法创建普通权限进程环境");
            }

            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default"
            };
            string commandLine = QuoteArgument(fileName)
                + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments);
            var mutableCommandLine = new StringBuilder(commandLine);
            if (!CreateProcessWithTokenW(
                    primaryToken,
                    LogonWithProfile,
                    fileName,
                    mutableCommandLine,
                    CreateUnicodeEnvironment,
                    environment,
                    workingDirectory,
                    ref startupInfo,
                    out ProcessInformation processInformation))
            {
                throw CreateWin32Exception("无法以普通权限启动快捷方式拖放组件");
            }

            try
            {
                return checked((int)processInformation.ProcessId);
            }
            finally
            {
                CloseHandle(processInformation.ThreadHandle);
                CloseHandle(processInformation.ProcessHandle);
            }
        }
        finally
        {
            if (environment != 0)
            {
                _ = DestroyEnvironmentBlock(environment);
            }
            if (primaryToken != 0)
            {
                CloseHandle(primaryToken);
            }
            if (shellToken != 0)
            {
                CloseHandle(shellToken);
            }
            CloseHandle(shellProcess);
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string QuoteArgument(string value)
    {
        if (value.Contains('"'))
        {
            throw new ArgumentException("进程路径包含无效的双引号。", nameof(value));
        }

        return '"' + value + '"';
    }

    private static Win32Exception CreateWin32Exception(string action)
    {
        int error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{action}失败：{new Win32Exception(error).Message}");
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        nint existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out nint newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out nint environment,
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        nint tokenHandle,
        uint logonFlags,
        string? applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Count;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint ProcessHandle;
        public nint ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }
}
