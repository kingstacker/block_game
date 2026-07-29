using System.Security.Cryptography;
using System.Text;
using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class UninstallAuthorizationService
{
    public static string Create(AppConfig config, DateTimeOffset nowUtc, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.ProtectionLocked)
        {
            throw new InvalidOperationException("必须先完成解除流程。 ");
        }

        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
        string token = Convert.ToBase64String(tokenBytes);
        config.UninstallTokenHashBase64 = HashToken(token);
        config.UninstallAuthorizedUntilUtc = nowUtc.Add(lifetime ?? TimeSpan.FromMinutes(10));
        return token;
    }

    public static bool ValidateAndConsume(AppConfig config, string token, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.ProtectionLocked
            || string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(config.UninstallTokenHashBase64)
            || config.UninstallAuthorizedUntilUtc is not { } expiresAt
            || expiresAt < nowUtc)
        {
            return false;
        }

        byte[] expected;
        byte[] actual;
        try
        {
            expected = Convert.FromBase64String(config.UninstallTokenHashBase64);
            actual = Convert.FromBase64String(HashToken(token));
        }
        catch (FormatException)
        {
            return false;
        }

        bool valid = CryptographicOperations.FixedTimeEquals(expected, actual);
        if (valid)
        {
            ProtectionManager.ClearUninstallAuthorization(config);
        }

        return valid;
    }

    private static string HashToken(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(hash);
    }
}

