using System.Reflection;
using System.Runtime.InteropServices;

namespace BlockGame.Core.Services;

public sealed record ShortcutTargetInfo(
    string ShortcutPath,
    string TargetPath,
    string Arguments,
    string WorkingDirectory);

public static class ShortcutTargetResolver
{
    public static ShortcutTargetInfo Resolve(string shortcutPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows 快捷方式只能在 Windows 上读取。 ");
        }

        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            throw new ArgumentException("快捷方式路径不能为空。 ", nameof(shortcutPath));
        }

        string fullShortcutPath = Path.GetFullPath(shortcutPath.Trim());
        if (!string.Equals(Path.GetExtension(fullShortcutPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("仅支持 Windows .lnk 快捷方式。 ");
        }

        if (!File.Exists(fullShortcutPath))
        {
            throw new FileNotFoundException("快捷方式不存在。 ", fullShortcutPath);
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("当前 Windows 无法创建快捷方式解析器。 ");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("当前 Windows 无法启动快捷方式解析器。 ");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [fullShortcutPath])
                ?? throw new InvalidDataException("无法读取快捷方式。 ");

            Type shortcutType = shortcut.GetType();
            string targetPath = ReadStringProperty(shortcutType, shortcut, "TargetPath");
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new InvalidDataException("快捷方式没有可读取的目标程序。 ");
            }

            return new ShortcutTargetInfo(
                fullShortcutPath,
                targetPath,
                ReadStringProperty(shortcutType, shortcut, "Arguments"),
                ReadStringProperty(shortcutType, shortcut, "WorkingDirectory"));
        }
        catch (Exception exception) when (
            exception is COMException
                or TargetInvocationException
                or MissingMethodException
                or InvalidCastException)
        {
            throw new InvalidDataException("读取快捷方式目标失败。 ", exception);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static string ReadStringProperty(Type type, object instance, string propertyName)
        => Convert.ToString(
                type.InvokeMember(
                    propertyName,
                    BindingFlags.GetProperty,
                    binder: null,
                    target: instance,
                    args: null),
                System.Globalization.CultureInfo.InvariantCulture)
            ?.Trim()
            ?? string.Empty;

    private static void ReleaseComObject(object? instance)
    {
        if (OperatingSystem.IsWindows()
            && instance is not null
            && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }
}
