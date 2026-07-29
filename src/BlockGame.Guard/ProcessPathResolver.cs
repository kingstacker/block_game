using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BlockGame.Guard;

internal static class ProcessPathResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static string? TryGetPath(int processId)
    {
        nint processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0)
        {
            return null;
        }

        try
        {
            int capacity = 1024;
            var builder = new StringBuilder(capacity);
            if (!QueryFullProcessImageName(processHandle, 0, builder, ref capacity))
            {
                return null;
            }

            return builder.ToString();
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        nint processHandle,
        int flags,
        StringBuilder executablePath,
        ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

