namespace BlockGame.Core.Models;

public sealed class PasswordCredential
{
    public string SaltBase64 { get; set; } = string.Empty;

    public string HashBase64 { get; set; } = string.Empty;

    public int Iterations { get; set; } = 310_000;
}

