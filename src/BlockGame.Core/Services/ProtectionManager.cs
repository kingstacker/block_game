using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class ProtectionManager
{
    public const string UnlockConfirmationText = "我确认申请解除游戏保护，并接受冷静期";

    public static void EnableAndLock(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.ProtectionEnabled = true;
        config.ProtectionLocked = true;
        config.UnlockRequestedAtUtc = null;
        config.UnlockAvailableAtUtc = null;
        ClearUninstallAuthorization(config);
    }

    public static void RequestUnlock(AppConfig config, string confirmationText, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.ProtectionLocked)
        {
            throw new InvalidOperationException("当前管理设置未锁定。 ");
        }

        if (!string.Equals(confirmationText?.Trim(), UnlockConfirmationText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("确认文本不正确。 ");
        }

        if (config.UnlockAvailableAtUtc is not null)
        {
            throw new InvalidOperationException("已经存在解除申请。 ");
        }

        int delayMinutes = Math.Clamp(
            config.UnlockDelayMinutes,
            1,
            UnlockDelayPolicy.MaximumDelayMinutes);
        config.UnlockRequestedAtUtc = nowUtc;
        config.UnlockAvailableAtUtc = nowUtc.AddMinutes(delayMinutes);
    }

    public static TimeSpan GetRemainingUnlockDelay(AppConfig config, DateTimeOffset nowUtc)
    {
        if (config.UnlockAvailableAtUtc is not { } availableAt || availableAt <= nowUtc)
        {
            return TimeSpan.Zero;
        }

        return availableAt - nowUtc;
    }

    public static bool ChangeUnlockDelay(AppConfig config, int newDelayMinutes, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);
        int clampedMinutes = Math.Clamp(newDelayMinutes, 1, UnlockDelayPolicy.MaximumDelayMinutes);
        if (config.ProtectionLocked && clampedMinutes < config.UnlockDelayMinutes)
        {
            throw new InvalidOperationException("锁定期间只能延长冷静期，不能缩短。 ");
        }

        config.UnlockDelayMinutes = clampedMinutes;
        if (config.UnlockAvailableAtUtc is not { } currentDeadline)
        {
            return false;
        }

        // 已提交解除申请时，延长冷静期必须同步顺延当前截止时间，否则延长不生效。
        // 旧配置可能没有记录申请时间，此时以当前时刻为锚点重新计时。
        DateTimeOffset anchorUtc = config.UnlockRequestedAtUtc ?? nowUtc;
        DateTimeOffset newDeadline = anchorUtc.AddMinutes(clampedMinutes);
        if (newDeadline <= currentDeadline)
        {
            return false;
        }

        config.UnlockAvailableAtUtc = newDeadline;
        return true;
    }

    public static void CompleteUnlock(AppConfig config, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.ProtectionLocked)
        {
            return;
        }

        if (config.UnlockAvailableAtUtc is not { } availableAt)
        {
            throw new InvalidOperationException("尚未申请解除。 ");
        }

        if (availableAt > nowUtc)
        {
            throw new InvalidOperationException("冷静期尚未结束。 ");
        }

        config.ProtectionLocked = false;
        config.UnlockRequestedAtUtc = null;
        config.UnlockAvailableAtUtc = null;
    }

    public static void DisableProtection(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.ProtectionLocked)
        {
            throw new InvalidOperationException("必须先完成解除流程。 ");
        }

        config.ProtectionEnabled = false;
        ClearUninstallAuthorization(config);
    }

    public static void ResetForDebug(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.ProtectionEnabled = false;
        config.ProtectionLocked = false;
        config.UnlockRequestedAtUtc = null;
        config.UnlockAvailableAtUtc = null;
        config.PasswordThrottle = new PasswordThrottle();
        config.Rules.Clear();
        config.DefaultRulePresetVersion = 0;
        DefaultRulePresets.Apply(config);
        foreach (BlockRule rule in config.Rules)
        {
            rule.Enabled = false;
        }
        ClearUninstallAuthorization(config);
    }

    public static void ClearUninstallAuthorization(AppConfig config)
    {
        config.UninstallTokenHashBase64 = null;
        config.UninstallAuthorizedUntilUtc = null;
    }
}
