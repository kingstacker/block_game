namespace BlockGame.Core.Models;

public sealed class AuditEntry
{
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    public string EventType { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public bool Success { get; set; } = true;

    public int? ProcessId { get; set; }

    public string? ProcessName { get; set; }

    public string? ProcessPath { get; set; }

    public string? RuleId { get; set; }

    public bool? DesktopNotificationSent { get; set; }
}
