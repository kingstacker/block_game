using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class ProtectionManager
{
    public const string UnlockConfirmationText = "我确认申请解除游戏保护，并接受冷静期";

    public static void EnableAndLock(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.ProtectionMode == ProtectionMode.Preview)
        {
            config.ProtectionMode = ProtectionMode.Strict;
        }
        config.ProtectionEnabled = true;
        config.ProtectionLocked = true;
        config.UnlockRequestedAtUtc = null;
        config.UnlockAvailableAtUtc = null;
        ClearUninstallAuthorization(config);
    }

    public static void ChangeMode(AppConfig config, ProtectionMode mode)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (config.ProtectionLocked && config.ProtectionMode != mode)
        {
            throw new InvalidOperationException("当前保护模式已锁定，必须等待冷静期结束并完成解除后才能切换模式。 ");
        }

        ClearTemporaryReleases(config);
        config.ProtectionMode = mode;
        ClearUninstallAuthorization(config);
    }

    public static void EnablePreview(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.ProtectionLocked)
        {
            throw new InvalidOperationException("当前保护模式已锁定，必须先完成解除流程。 ");
        }

        if (config.ProtectionMode != ProtectionMode.Preview)
        {
            throw new InvalidOperationException("当前不是预览屏蔽模式。 ");
        }

        config.ProtectionEnabled = true;
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
        ClearTemporaryReleases(config);
        ClearUninstallAuthorization(config);
    }

    public static DateTimeOffset GrantTemporaryRelease(
        AppConfig config,
        string ruleId,
        int durationMinutes,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.ProtectionMode != ProtectionMode.Negotiation)
        {
            throw new InvalidOperationException("只有协商模式可以临时放行软件。 ");
        }

        if (!config.ProtectionEnabled)
        {
            throw new InvalidOperationException("请先启用协商保护，再临时放行软件。 ");
        }

        if (durationMinutes is < 1 or > TemporaryReleasePolicy.MaximumDurationMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMinutes),
                $"临时放行时长必须在 1 分钟到 {TemporaryReleasePolicy.MaximumDurationMinutes / 60} 小时之间。 ");
        }

        BlockRule rule = config.Rules.FirstOrDefault(candidate => candidate.Id == ruleId)
            ?? throw new InvalidOperationException("找不到要临时放行的规则。 ");
        if (!rule.Enabled)
        {
            throw new InvalidOperationException("只有已启用的规则才能临时放行。 ");
        }

        if (rule.Target is not (RuleTarget.FileName or RuleTarget.FullPath))
        {
            throw new InvalidOperationException("临时放行只适用于软件规则，不适用于网站规则。 ");
        }

        DateTimeOffset allowedUntilUtc = nowUtc.AddMinutes(durationMinutes);
        rule.TemporarilyAllowedUntilUtc = allowedUntilUtc;
        config.NegotiationDefaultReleaseMinutes = durationMinutes;
        ClearUninstallAuthorization(config);
        return allowedUntilUtc;
    }

    public static bool RevokeTemporaryRelease(AppConfig config, string ruleId)
    {
        ArgumentNullException.ThrowIfNull(config);
        BlockRule rule = config.Rules.FirstOrDefault(candidate => candidate.Id == ruleId)
            ?? throw new InvalidOperationException("找不到要收回临时放行的规则。 ");
        if (rule.TemporarilyAllowedUntilUtc is null)
        {
            return false;
        }

        rule.TemporarilyAllowedUntilUtc = null;
        ClearUninstallAuthorization(config);
        return true;
    }

    public static void RestoreDefaults(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.ProtectionLocked)
        {
            throw new InvalidOperationException("设置已锁定，必须先完成解除流程。 ");
        }

        config.ProtectionEnabled = false;
        config.ProtectionLocked = false;
        config.ProtectionMode = ProtectionMode.Strict;
        config.UnlockRequestedAtUtc = null;
        config.UnlockAvailableAtUtc = null;
        config.UnlockDelayMinutes = 24 * 60;
        config.NegotiationDefaultReleaseMinutes = TemporaryReleasePolicy.DefaultDurationMinutes;
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

    private static void ClearTemporaryReleases(AppConfig config)
    {
        foreach (BlockRule rule in config.Rules)
        {
            rule.TemporarilyAllowedUntilUtc = null;
        }
    }
}
