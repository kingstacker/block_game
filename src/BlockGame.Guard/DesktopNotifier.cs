using System.Runtime.InteropServices;
using System.Text;

namespace BlockGame.Guard;

internal static class DesktopNotifier
{
    private const uint InvalidSessionId = 0xFFFFFFFF;
    private const int MbOk = 0x00000000;
    private const int MbIconInformation = 0x00000040;
    private const int MbSetForeground = 0x00010000;

    public static bool TryShowBlocked(string processFileName)
    {
        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == InvalidSessionId)
            {
                return false;
            }

            string applicationName = Path.GetFileNameWithoutExtension(processFileName);
            if (string.IsNullOrWhiteSpace(applicationName))
            {
                applicationName = "该程序";
            }

            const string title = "BlockGame 拦截提示";
            string message = $"{applicationName} 软件已被拦截。";
            return WTSSendMessage(
                nint.Zero,
                sessionId,
                title,
                Encoding.Unicode.GetByteCount(title),
                message,
                Encoding.Unicode.GetByteCount(message),
                MbOk | MbIconInformation | MbSetForeground,
                0,
                out _,
                wait: false);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryShowWebsiteBlocked(string domain)
        => TryShowMessage($"{domain} 网站已被拦截。");

    private static bool TryShowMessage(string message)
    {
        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == InvalidSessionId)
            {
                return false;
            }

            const string title = "BlockGame 拦截提示";
            return WTSSendMessage(
                nint.Zero,
                sessionId,
                title,
                Encoding.Unicode.GetByteCount(title),
                message,
                Encoding.Unicode.GetByteCount(message),
                MbOk | MbIconInformation | MbSetForeground,
                0,
                out _,
                wait: false);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSSendMessage(
        nint server,
        uint sessionId,
        string title,
        int titleLength,
        string message,
        int messageLength,
        int style,
        int timeout,
        out int response,
        [MarshalAs(UnmanagedType.Bool)] bool wait);
}
