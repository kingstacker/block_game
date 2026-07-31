using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public sealed record UninstallPreparationResult(
    bool Success,
    bool PasswordVerified,
    bool ProtectionLocked,
    string Message,
    string? Token);

public static class PasswordProtectedUninstallService
{
    public static UninstallPreparationResult Prepare(
        AppConfig config,
        string? password,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.ProtectionLocked)
        {
            return new UninstallPreparationResult(
                false,
                false,
                true,
                "保护仍处于锁定状态。请先在 BlockGame 中申请解除并等待冷静期结束。",
                null);
        }

        if (!config.SetupCompleted)
        {
            return new UninstallPreparationResult(
                true,
                false,
                false,
                "首次设置尚未完成，允许移除未配置的安装。",
                UninstallAuthorizationService.Create(config, nowUtc));
        }

        PasswordVerificationResult verification = PasswordGate.Verify(
            config,
            password ?? string.Empty,
            nowUtc);
        if (!verification.Success)
        {
            return new UninstallPreparationResult(
                false,
                false,
                false,
                verification.Message,
                null);
        }

        return new UninstallPreparationResult(
            true,
            true,
            false,
            "管理密码验证成功。",
            UninstallAuthorizationService.Create(config, nowUtc));
    }
}
