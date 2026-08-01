namespace BlockGame.Core.Models;

public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool SetupCompleted { get; set; }

    public PasswordCredential Password { get; set; } = new();

    public PasswordThrottle PasswordThrottle { get; set; } = new();

    public ProtectionMode ProtectionMode { get; set; } = ProtectionMode.Strict;

    public bool ProtectionEnabled { get; set; }

    public bool ProtectionLocked { get; set; }

    public int UnlockDelayMinutes { get; set; } = 24 * 60;

    public int NegotiationDefaultReleaseMinutes { get; set; } = 30;

    public DateTimeOffset? UnlockRequestedAtUtc { get; set; }

    public DateTimeOffset? UnlockAvailableAtUtc { get; set; }

    public string? UninstallTokenHashBase64 { get; set; }

    public DateTimeOffset? UninstallAuthorizedUntilUtc { get; set; }

    public List<BlockRule> Rules { get; set; } = [];

    public int DefaultRulePresetVersion { get; set; }
}
