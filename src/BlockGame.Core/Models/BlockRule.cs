namespace BlockGame.Core.Models;

public sealed class BlockRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public RuleTarget Target { get; set; } = RuleTarget.FileName;

    public string Pattern { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

