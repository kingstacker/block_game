namespace BlockGame.Core.Models;

public sealed class PasswordThrottle
{
    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset? BlockedUntilUtc { get; set; }
}

