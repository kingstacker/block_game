using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class RuleMatcher
{
    public static RuleMatch? Match(AppConfig config, ProcessDescriptor process)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(process);

        if (!config.ProtectionEnabled || SafetyPolicy.IsProtectedProcess(process))
        {
            return null;
        }

        string normalizedFileName = SafetyPolicy.NormalizeFileName(process.FileName);
        foreach (BlockRule rule in config.Rules.Where(candidate => candidate.Enabled))
        {
            string? candidate = rule.Target switch
            {
                RuleTarget.FileName => normalizedFileName,
                RuleTarget.FullPath => process.FullPath,
                _ => null
            };

            if (candidate is null)
            {
                continue;
            }

            bool matched = rule.Target == RuleTarget.FileName
                ? SafetyPolicy.SplitFileNamePatterns(rule.Pattern)
                    .Any(pattern => WildcardMatcher.IsMatch(candidate, pattern))
                : WildcardMatcher.IsMatch(candidate, rule.Pattern);
            if (matched)
            {
                return new RuleMatch(rule, process);
            }
        }

        return null;
    }
}
