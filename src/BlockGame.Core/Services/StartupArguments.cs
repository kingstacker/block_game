namespace BlockGame.Core.Services;

public static class StartupArguments
{
    public const string AutoStart = "--autostart";

    public static bool IsAutoStart(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument =>
            string.Equals(argument, AutoStart, StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildAutoStartCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("程序路径不能为空。", nameof(executablePath));
        }

        return $"\"{Path.GetFullPath(executablePath)}\" {AutoStart}";
    }
}
