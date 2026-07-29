using System.Security.Cryptography;
using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class PasswordHasher
{
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int DefaultIterations = 310_000;

    public static PasswordCredential Create(string password, int iterations = DefaultIterations)
    {
        ValidateNewPassword(password);
        if (iterations < 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "PBKDF2 迭代次数过低。 ");
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashLength);

        return new PasswordCredential
        {
            SaltBase64 = Convert.ToBase64String(salt),
            HashBase64 = Convert.ToBase64String(hash),
            Iterations = iterations
        };
    }

    public static bool Verify(string password, PasswordCredential credential)
    {
        if (string.IsNullOrEmpty(password)
            || string.IsNullOrWhiteSpace(credential.SaltBase64)
            || string.IsNullOrWhiteSpace(credential.HashBase64)
            || credential.Iterations < 100_000)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(credential.SaltBase64);
            byte[] expectedHash = Convert.FromBase64String(credential.HashBase64);
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                credential.Iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new ArgumentException("管理密码至少需要 8 个字符。", nameof(password));
        }

        if (password.Length > 256)
        {
            throw new ArgumentException("管理密码不能超过 256 个字符。", nameof(password));
        }
    }
}

