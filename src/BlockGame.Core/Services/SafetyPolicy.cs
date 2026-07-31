using System.Collections.Concurrent;
using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class SafetyPolicy
{
    private const string OwnComponentPrefix = "blockgame.";
    private const int MaximumPatternCacheEntries = 4096;
    private static readonly char[] WildcardCharacters = ['*', '?'];
    private static readonly string? OwnComponentDirectory = DetectOwnComponentDirectory();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> SplitPatternCache =
        new(StringComparer.Ordinal);

    private static readonly HashSet<string> ProtectedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system.exe",
        "registry.exe",
        "idle.exe",
        "smss.exe",
        "csrss.exe",
        "wininit.exe",
        "winlogon.exe",
        "services.exe",
        "lsass.exe",
        "svchost.exe",
        "dwm.exe",
        "fontdrvhost.exe",
        "explorer.exe",
        "sihost.exe",
        "taskhostw.exe",
        "userinit.exe"
    };

    public static bool IsProtectedProcess(ProcessDescriptor process)
    {
        return IsProtectedSystemProcess(process) || IsOwnComponent(process);
    }

    public static bool IsProtectedSystemProcess(ProcessDescriptor process)
    {
        if (process.ProcessId is 0 or 4)
        {
            return true;
        }

        return ProtectedFileNames.Contains(NormalizeFileName(process.FileName));
    }

    private static bool IsOwnComponent(ProcessDescriptor process)
    {
        string fileName = NormalizeFileName(process.FileName);
        if (!fileName.StartsWith(OwnComponentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 文件名可以随意伪造：只有确认文件位于本程序安装目录时才算自身组件；
        // 路径未知时保持保守，由调用方解析完整路径后再复查。
        if (process.FullPath is null)
        {
            return true;
        }

        return IsInOwnComponentDirectory(process.FullPath);
    }

    private static bool IsInOwnComponentDirectory(string fullPath)
    {
        if (OwnComponentDirectory is null)
        {
            return false;
        }

        string? candidateDirectory;
        try
        {
            candidateDirectory = Path.GetDirectoryName(Path.GetFullPath(fullPath.Trim()));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or PathTooLongException
                or NotSupportedException
                or IOException
                or System.Security.SecurityException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(candidateDirectory))
        {
            return false;
        }

        candidateDirectory = Path.TrimEndingDirectorySeparator(candidateDirectory);
        return candidateDirectory.Equals(OwnComponentDirectory, StringComparison.OrdinalIgnoreCase)
            || candidateDirectory.StartsWith(
                OwnComponentDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? DetectOwnComponentDirectory()
    {
        try
        {
            string? directory = Environment.ProcessPath is { } processPath
                ? Path.GetDirectoryName(processPath)
                : null;
            directory = string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
            return string.IsNullOrWhiteSpace(directory)
                ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch
        {
            return null;
        }
    }

    public static string? ValidateRule(BlockRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            return "规则名称不能为空。 ";
        }

        if (string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return "匹配内容不能为空。 ";
        }

        if (rule.Target == RuleTarget.Domain)
        {
            return WebsiteDomainRules.ValidatePattern(rule.Pattern);
        }

        IReadOnlyList<string> patterns = rule.Target == RuleTarget.FileName
            ? SplitFileNamePatterns(rule.Pattern)
            : [rule.Pattern.Trim()];
        if (patterns.Count == 0)
        {
            return "匹配内容不能为空。 ";
        }

        if (patterns.Any(pattern => pattern is "*" or "*.exe"))
        {
            return "规则范围过大，可能导致系统无法正常使用。 ";
        }

        if (patterns.Any(pattern => pattern.Contains("blockgame", StringComparison.OrdinalIgnoreCase)))
        {
            return "不能拦截 BlockGame 自身组件。 ";
        }

        if (rule.Target == RuleTarget.FileName)
        {
            foreach (string pattern in patterns)
            {
                foreach (string protectedName in ProtectedFileNames)
                {
                    if (WildcardMatcher.IsMatch(protectedName, pattern))
                    {
                        return $"该规则会匹配 Windows 关键进程 {protectedName}。 ";
                    }
                }
            }
        }

        if (rule.Target == RuleTarget.FullPath)
        {
            return ValidateFullPathPattern(patterns[0]);
        }

        return null;
    }

    private static string? ValidateFullPathPattern(string pattern)
    {
        string normalized = pattern.Replace('/', '\\').Trim();
        bool hasLiteralDriveRoot = normalized.Length >= 3
            && char.IsAsciiLetter(normalized[0])
            && normalized[1] == ':'
            && normalized[2] == '\\';
        if (!hasLiteralDriveRoot)
        {
            return "完整路径规则必须以盘符开头，例如 C:\\Games\\game.exe。 ";
        }

        // 盘符根目录下的全量通配（C:\*、C:\*.exe、C:\*.* 等）会波及整块磁盘。
        string remainderWithoutWildcards = normalized[3..]
            .Replace("*", string.Empty)
            .Replace("?", string.Empty);
        if (remainderWithoutWildcards.Length == 0
            || remainderWithoutWildcards is "." or ".exe")
        {
            return "规则范围过大，可能导致系统无法正常使用。 ";
        }

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            string windowsRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(windowsDirectory)) + "\\";

            // 通配符（* 匹配任意字符，包括 \）能展开出任意后续目录段，因此比较
            // 通配符之前的字面前缀：字面前缀已进入 Windows 目录，或 Windows 目录
            // 仍在字面前缀的可扩展范围内（如 C:\*.exe、C:\Win*\System32\*.exe），都拒绝。
            int firstWildcard = normalized.IndexOfAny(WildcardCharacters);
            string literalPrefix = firstWildcard < 0 ? normalized : normalized[..firstWildcard];
            bool canReachWindowsDirectory =
                literalPrefix.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase)
                || (firstWildcard >= 0
                    && windowsRoot.StartsWith(literalPrefix, StringComparison.OrdinalIgnoreCase));
            if (canReachWindowsDirectory)
            {
                return "第一版不允许拦截 Windows 系统目录。 ";
            }
        }

        return null;
    }

    public static string NormalizeFileName(string fileName)
    {
        string normalized = Path.GetFileName(fileName.Trim());
        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}.exe";
    }

    public static string NormalizeFileNamePattern(string pattern)
    {
        return string.Join(';', SplitFileNamePatterns(pattern));
    }

    public static string NormalizeRulePattern(RuleTarget target, string pattern)
    {
        return target switch
        {
            RuleTarget.FileName => NormalizeFileNamePattern(pattern),
            RuleTarget.Domain => WebsiteDomainRules.NormalizePattern(pattern),
            _ => pattern.Trim()
        };
    }

    public static IReadOnlyList<string> SplitFileNamePatterns(string patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
        {
            return [];
        }

        // 守护进程每 400ms 会对“每个进程 × 每条规则”调用一次；拆分含
        // 分割、去重和补 .exe，必须按原始字符串缓存结果，避免高频重复解析。
        if (SplitPatternCache.Count > MaximumPatternCacheEntries)
        {
            SplitPatternCache.Clear();
        }

        return SplitPatternCache.GetOrAdd(patterns, static value => value
            .Split(
                [';', '；', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSingleFileNamePattern)
            .Where(pattern => pattern.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public static bool NormalizeFileNameRulePatterns(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool changed = false;
        foreach (BlockRule rule in config.Rules.Where(rule => rule.Target == RuleTarget.FileName))
        {
            string normalized = NormalizeFileNamePattern(rule.Pattern);
            if (!string.Equals(rule.Pattern, normalized, StringComparison.Ordinal))
            {
                rule.Pattern = normalized;
                changed = true;
            }
        }

        return changed;
    }

    public static bool NormalizeDomainRulePatterns(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool changed = false;
        foreach (BlockRule rule in config.Rules.Where(rule => rule.Target == RuleTarget.Domain))
        {
            string normalized = WebsiteDomainRules.NormalizePattern(rule.Pattern);
            if (!string.Equals(rule.Pattern, normalized, StringComparison.Ordinal))
            {
                rule.Pattern = normalized;
                changed = true;
            }
        }

        return changed;
    }

    private static string NormalizeSingleFileNamePattern(string pattern)
    {
        string normalized = pattern.Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}.exe";
    }
}
