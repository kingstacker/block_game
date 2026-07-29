using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class SafetyPolicy
{
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
        if (process.ProcessId is 0 or 4)
        {
            return true;
        }

        string fileName = NormalizeFileName(process.FileName);
        return ProtectedFileNames.Contains(fileName)
            || fileName.StartsWith("blockgame.", StringComparison.OrdinalIgnoreCase);
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
            return "网站屏蔽功能已经移除。 ";
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
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string normalizedPattern = patterns[0].Replace('/', '\\');
            if (!string.IsNullOrWhiteSpace(windowsDirectory)
                && normalizedPattern.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase))
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
            _ => pattern.Trim()
        };
    }

    public static int RemoveLegacyWebsiteRules(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Rules.RemoveAll(rule => rule.Target == RuleTarget.Domain);
    }

    public static IReadOnlyList<string> SplitFileNamePatterns(string patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
        {
            return [];
        }

        return patterns
            .Split(
                [';', '；', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSingleFileNamePattern)
            .Where(pattern => pattern.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
