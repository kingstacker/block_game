using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class UnlockDelayPolicy
{
    public const int MinutesPerHour = 60;
    public const int MinutesPerDay = 24 * MinutesPerHour;
    public const int MinutesPerMonth = 30 * MinutesPerDay;
    public const int MaximumDelayMinutes = 12 * MinutesPerMonth;

    public static bool TryConvertToMinutes(
        double value,
        UnlockDelayUnit unit,
        out int minutes)
    {
        minutes = 0;
        if (!double.IsFinite(value) || value <= 0)
        {
            return false;
        }

        double calculated = value * GetMinutesPerUnit(unit);
        if (calculated < 1 || calculated > MaximumDelayMinutes)
        {
            return false;
        }

        minutes = (int)Math.Round(calculated, MidpointRounding.AwayFromZero);
        return minutes is >= 1 and <= MaximumDelayMinutes;
    }

    public static double ConvertFromMinutes(int minutes, UnlockDelayUnit unit)
        => Math.Clamp(minutes, 1, MaximumDelayMinutes) / (double)GetMinutesPerUnit(unit);

    public static int GetMinutesPerUnit(UnlockDelayUnit unit)
        => unit switch
        {
            UnlockDelayUnit.Hours => MinutesPerHour,
            UnlockDelayUnit.Days => MinutesPerDay,
            UnlockDelayUnit.Months => MinutesPerMonth,
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
}
