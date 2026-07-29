namespace BlockGame.Core.Services;

public sealed class DataPaths
{
    public const string ProductDirectoryName = "BlockGame";

    public DataPaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("数据目录不能为空。", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string ConfigFile => Path.Combine(RootDirectory, "config.json");

    public string AuditFile => Path.Combine(RootDirectory, "audit.jsonl");

    public string HeartbeatFile => Path.Combine(RootDirectory, "guard-heartbeat.json");

    public string UninstallTokenFile => Path.Combine(RootDirectory, "uninstall.token");

    public string MaintenanceStopFile => Path.Combine(RootDirectory, "maintenance-stop.request");

    public string NrptDesiredDomainsFile => Path.Combine(RootDirectory, "nrpt-desired-domains.json");

    public string BrowserDnsPolicyBackupFile => Path.Combine(RootDirectory, "browser-dns-policy-backup.json");

    public string BrowserUrlBlockPolicyStateFile => Path.Combine(
        RootDirectory,
        "browser-url-block-policy-state.json");

    public static DataPaths CreateDefault()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("BLOCKGAME_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return new DataPaths(overrideDirectory);
        }

        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new DataPaths(Path.Combine(commonData, ProductDirectoryName));
    }

    public void EnsureDirectory() => Directory.CreateDirectory(RootDirectory);
}
