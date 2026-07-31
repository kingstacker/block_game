using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BlockGame.App;

internal static class ElevatedFileDropSupport
{
    private const uint MessageFilterAllow = 1;
    private const uint WmCopyGlobalData = 0x0049;
    private const uint WmCopyData = 0x004A;
    private const uint WmDropFiles = 0x0233;

    public static bool TryAllowExplorerFileDrops(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return false;
        }

        // BlockGame 的管理界面以管理员权限运行。仅放行 Windows 文件拖放所需的
        // 三类消息，让普通权限的 Explorer 可以把 .lnk 投递到窗口；规则内容仍会
        // 经过扩展名、目标路径、系统目录和自身组件安全校验。
        bool dropFilesAllowed = ChangeWindowMessageFilterEx(
            handle,
            WmDropFiles,
            MessageFilterAllow,
            0);
        bool copyDataAllowed = ChangeWindowMessageFilterEx(
            handle,
            WmCopyData,
            MessageFilterAllow,
            0);
        bool copyGlobalDataAllowed = ChangeWindowMessageFilterEx(
            handle,
            WmCopyGlobalData,
            MessageFilterAllow,
            0);
        return dropFilesAllowed && copyDataAllowed && copyGlobalDataAllowed;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(
        nint windowHandle,
        uint message,
        uint action,
        nint changeFilterStatus);
}
