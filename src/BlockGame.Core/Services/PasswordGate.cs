using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public sealed record PasswordVerificationResult(
    bool Success,
    bool RateLimited,
    TimeSpan? RetryAfter,
    string Message);

public static class PasswordGate
{
    public static PasswordVerificationResult Verify(
        AppConfig config,
        string password,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.PasswordThrottle.BlockedUntilUtc is { } blockedUntil && blockedUntil > nowUtc)
        {
            return new PasswordVerificationResult(
                false,
                true,
                blockedUntil - nowUtc,
                "密码尝试过于频繁，请稍后再试。 ");
        }

        if (PasswordHasher.Verify(password, config.Password))
        {
            config.PasswordThrottle.ConsecutiveFailures = 0;
            config.PasswordThrottle.BlockedUntilUtc = null;
            return new PasswordVerificationResult(true, false, null, "密码正确。 ");
        }

        config.PasswordThrottle.ConsecutiveFailures++;
        int failures = config.PasswordThrottle.ConsecutiveFailures;
        if (failures >= 5)
        {
            int delayMinutes = Math.Min(30, 1 << Math.Min(5, failures - 5));
            config.PasswordThrottle.BlockedUntilUtc = nowUtc.AddMinutes(delayMinutes);
            return new PasswordVerificationResult(
                false,
                true,
                TimeSpan.FromMinutes(delayMinutes),
                $"密码错误次数过多，已暂停验证 {delayMinutes} 分钟。 ");
        }

        return new PasswordVerificationResult(
            false,
            false,
            null,
            $"密码错误，还可尝试 {5 - failures} 次后进入等待。 ");
    }
}

