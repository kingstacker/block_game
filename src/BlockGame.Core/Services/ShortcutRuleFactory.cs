using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class ShortcutRuleFactory
{
    public static BlockRule CreateRule(ShortcutTargetInfo shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        string targetPath = NormalizeTargetPath(shortcut);
        if (!string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("快捷方式目标不是 EXE 程序，无法生成软件拦截规则。 ");
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("快捷方式对应的目标程序不存在。 ", targetPath);
        }

        string name = Path.GetFileNameWithoutExtension(shortcut.ShortcutPath).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(targetPath);
        }

        var rule = new BlockRule
        {
            Name = name,
            Target = RuleTarget.FullPath,
            Pattern = targetPath,
            Enabled = true
        };
        string? validationError = SafetyPolicy.ValidateRule(rule);
        if (validationError is not null)
        {
            throw new InvalidDataException(validationError);
        }

        rule.Pattern = SafetyPolicy.NormalizeRulePattern(rule.Target, rule.Pattern);
        return rule;
    }

    private static string NormalizeTargetPath(ShortcutTargetInfo shortcut)
    {
        string targetPath = Environment.ExpandEnvironmentVariables(shortcut.TargetPath)
            .Trim()
            .Trim('"');
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new InvalidDataException("快捷方式没有可读取的目标程序。 ");
        }

        if (!Path.IsPathFullyQualified(targetPath))
        {
            string baseDirectory = Environment.ExpandEnvironmentVariables(shortcut.WorkingDirectory)
                .Trim()
                .Trim('"');
            if (string.IsNullOrWhiteSpace(baseDirectory)
                || !Path.IsPathFullyQualified(baseDirectory))
            {
                baseDirectory = Path.GetDirectoryName(shortcut.ShortcutPath) ?? string.Empty;
            }

            targetPath = Path.Combine(baseDirectory, targetPath);
        }

        return Path.GetFullPath(targetPath);
    }
}
