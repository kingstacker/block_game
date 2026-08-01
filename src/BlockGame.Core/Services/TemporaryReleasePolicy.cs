using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class TemporaryReleasePolicy
{
    public const int DefaultDurationMinutes = 30;
    public const int MaximumDurationMinutes = 24 * 60;

    public static bool TryConvertToMinutes(
        double value,
        TemporaryReleaseUnit unit,
        out int minutes)
    {
        minutes = 0;
        if (!double.IsFinite(value) || value <= 0)
        {
            return false;
        }

        double calculated = value * GetMinutesPerUnit(unit);
        if (calculated < 1 || calculated > MaximumDurationMinutes)
        {
            return false;
        }

        minutes = (int)Math.Round(calculated, MidpointRounding.AwayFromZero);
        return minutes is >= 1 and <= MaximumDurationMinutes;
    }

    public static double ConvertFromMinutes(int minutes, TemporaryReleaseUnit unit)
        => NormalizeDurationMinutes(minutes) / (double)GetMinutesPerUnit(unit);

    public static int NormalizeDurationMinutes(int minutes)
        => minutes is >= 1 and <= MaximumDurationMinutes
            ? minutes
            : DefaultDurationMinutes;

    public static bool IsRuleTemporarilyAllowed(
        AppConfig config,
        BlockRule rule,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rule);
        return config.ProtectionMode == ProtectionMode.Negotiation
            && rule.Target is RuleTarget.FileName or RuleTarget.FullPath
            && rule.TemporarilyAllowedUntilUtc is { } allowedUntil
            && allowedUntil > nowUtc;
    }

    public static int GetMinutesPerUnit(TemporaryReleaseUnit unit)
        => unit switch
        {
            TemporaryReleaseUnit.Minutes => 1,
            TemporaryReleaseUnit.Hours => 60,
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
}
