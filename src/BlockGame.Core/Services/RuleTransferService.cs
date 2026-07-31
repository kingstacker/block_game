using System.Text.Json;
using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class RuleTransferService
{
    public const int CurrentFormatVersion = 1;
    public const int MaximumRuleCount = 5_000;

    public static string Export(IEnumerable<BlockRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var document = new RuleTransferDocument
        {
            FormatVersion = CurrentFormatVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Rules = rules.Select(rule => new RuleTransferItem
            {
                Name = rule.Name,
                Target = rule.Target,
                Pattern = rule.Pattern,
                Enabled = rule.Enabled,
                CreatedAtUtc = rule.CreatedAtUtc
            }).ToList()
        };

        return JsonSerializer.Serialize(document, JsonDefaults.Create(indented: true));
    }

    public static IReadOnlyList<BlockRule> Import(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("规则文件内容为空。 ");
        }

        RuleTransferDocument document;
        try
        {
            document = JsonSerializer.Deserialize<RuleTransferDocument>(
                    json,
                    JsonDefaults.Create())
                ?? throw new InvalidDataException("规则文件内容为空。 ");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("规则文件不是有效的 BlockGame JSON 文件。 ", exception);
        }

        if (document.FormatVersion is < 1 or > CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"不支持规则文件版本 {document.FormatVersion}。 ");
        }

        document.Rules ??= [];
        if (document.Rules.Count > MaximumRuleCount)
        {
            throw new InvalidDataException(
                $"规则数量超过上限 {MaximumRuleCount}。 ");
        }

        var imported = new List<BlockRule>(document.Rules.Count);
        for (int index = 0; index < document.Rules.Count; index++)
        {
            RuleTransferItem item = document.Rules[index];
            var rule = new BlockRule
            {
                Name = item.Name?.Trim() ?? string.Empty,
                Target = item.Target,
                Pattern = item.Pattern ?? string.Empty,
                Enabled = item.Enabled,
                CreatedAtUtc = item.CreatedAtUtc ?? DateTimeOffset.UtcNow
            };

            string? validationError = SafetyPolicy.ValidateRule(rule);
            if (validationError is not null)
            {
                throw new InvalidDataException(
                    $"第 {index + 1} 条规则无效：{validationError.Trim()}");
            }

            rule.Pattern = SafetyPolicy.NormalizeRulePattern(rule.Target, rule.Pattern);
            imported.Add(rule);
        }

        return imported;
    }

    private sealed class RuleTransferDocument
    {
        public int FormatVersion { get; set; }

        public DateTimeOffset ExportedAtUtc { get; set; }

        public List<RuleTransferItem>? Rules { get; set; }
    }

    private sealed class RuleTransferItem
    {
        public string? Name { get; set; }

        public RuleTarget Target { get; set; }

        public string? Pattern { get; set; }

        public bool Enabled { get; set; } = true;

        public DateTimeOffset? CreatedAtUtc { get; set; }
    }
}
