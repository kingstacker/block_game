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

        IReadOnlyList<string> fileNameCandidates = BuildFileNameCandidates(process);
        foreach (BlockRule rule in config.Rules.Where(candidate => candidate.Enabled))
        {
            bool matched = rule.Target switch
            {
                RuleTarget.FileName => SafetyPolicy.SplitFileNamePatterns(rule.Pattern)
                    .Any(pattern => fileNameCandidates.Any(
                        candidate => WildcardMatcher.IsMatch(candidate, pattern))),
                RuleTarget.FullPath when process.FullPath is not null =>
                    WildcardMatcher.IsMatch(process.FullPath, rule.Pattern),
                _ => false
            };

            if (matched)
            {
                return new RuleMatch(rule, process);
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildFileNameCandidates(ProcessDescriptor process)
    {
        var candidates = new List<string>
        {
            SafetyPolicy.NormalizeFileName(process.FileName)
        };
        AddDisplayNameCandidate(candidates, process.ProductName);
        AddDisplayNameCandidate(candidates, process.FileDescription);
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddDisplayNameCandidate(List<string> candidates, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            candidates.Add(SafetyPolicy.NormalizeFileName(displayName));
        }
    }
}
