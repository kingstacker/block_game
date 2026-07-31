using System.Text.Json;
using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public sealed class ConfigStore
{
    private const string CrossProcessMutexName = @"Global\BlockGame.ConfigStore.Lock";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly DataPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Create(indented: true);

    public ConfigStore(DataPaths paths)
    {
        _paths = paths;
    }

    public AppConfig Load()
    {
        using CrossProcessLock configLock = CrossProcessLock.Acquire(
            CrossProcessMutexName,
            LockTimeout);
        _paths.EnsureDirectory();
        if (!File.Exists(_paths.ConfigFile))
        {
            return new AppConfig();
        }

        string json = ReadWithRetry(_paths.ConfigFile);
        var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions)
            ?? throw new InvalidDataException("配置文件内容为空。 ");

        if (config.SchemaVersion > AppConfig.CurrentSchemaVersion)
        {
            throw new InvalidDataException("配置文件来自更高版本，当前程序无法安全读取。 ");
        }

        config.Rules ??= [];
        config.Password ??= new PasswordCredential();
        config.PasswordThrottle ??= new PasswordThrottle();
        if (!Enum.IsDefined(config.ProtectionMode) || config.ProtectionLocked)
        {
            // 旧配置没有模式字段时枚举默认值即为 Strict；锁定状态也必须始终归属严格模式。
            config.ProtectionMode = ProtectionMode.Strict;
        }
        return config;
    }

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        using CrossProcessLock configLock = CrossProcessLock.Acquire(
            CrossProcessMutexName,
            LockTimeout);
        _paths.EnsureDirectory();
        config.SchemaVersion = AppConfig.CurrentSchemaVersion;

        string json = JsonSerializer.Serialize(config, _jsonOptions);
        string temporaryFile = Path.Combine(
            _paths.RootDirectory,
            $"config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(temporaryFile, json);
        try
        {
            File.Move(temporaryFile, _paths.ConfigFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private static string ReadWithRetry(string file)
    {
        IOException? lastError = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return File.ReadAllText(file);
            }
            catch (IOException exception)
            {
                lastError = exception;
                Thread.Sleep(25 * (attempt + 1));
            }
        }

        throw lastError ?? new IOException("读取配置文件失败。 ");
    }
}
