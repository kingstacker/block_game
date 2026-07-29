namespace BlockGame.Core.Models;

public sealed class GuardHeartbeat
{
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    public int ProcessId { get; set; }

    public string Mode { get; set; } = "ProcessWatcher";
}

