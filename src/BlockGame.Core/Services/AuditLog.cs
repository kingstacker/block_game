using System.Text.Json;
using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public sealed record AuditSnapshot(
    IReadOnlyList<AuditEntry> Entries,
    int TotalBlockedCount);

public sealed class AuditLog
{
    private const string CrossProcessMutexName = @"Global\BlockGame.AuditLog.Lock";
    private const long DefaultRotationThresholdBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly DataPaths _paths;
    private readonly long _rotationThresholdBytes;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Create();
    private readonly object _sync = new();

    public AuditLog(DataPaths paths)
        : this(paths, DefaultRotationThresholdBytes)
    {
    }

    public AuditLog(DataPaths paths, long rotationThresholdBytes)
    {
        _paths = paths;
        _rotationThresholdBytes = Math.Max(1, rotationThresholdBytes);
    }

    public void Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _paths.EnsureDirectory();
        string line = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;

        lock (_sync)
        {
            using CrossProcessLock auditLock = CrossProcessLock.Acquire(
                CrossProcessMutexName,
                LockTimeout);
            RotateIfNeeded();
            using var stream = new FileStream(
                _paths.AuditFile,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.Write(line);
        }
    }

    /// <summary>
    /// 审计文件的低成本变更标记；未变化时界面可以跳过整轮重读。
    /// </summary>
    public string GetChangeToken()
    {
        try
        {
            return Describe(_paths.AuditFile) + "|" + Describe(_paths.AuditArchiveFile);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // 无法探测时返回唯一值，强制下一次重读。
            return "error:" + DateTimeOffset.UtcNow.UtcTicks;
        }

        static string Describe(string file)
        {
            var info = new FileInfo(file);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "0";
        }
    }

    public IReadOnlyList<AuditEntry> ReadRecent(int maximumCount = 300)
        => ReadSnapshot(maximumCount).Entries;

    public AuditSnapshot ReadSnapshot(int maximumCount = 300)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 5_000);
        var entries = new Queue<AuditEntry>(maximumCount);
        long totalBlockedCount = ReadArchivedBlockedCount();
        foreach (string file in new[] { _paths.AuditArchiveFile, _paths.AuditFile })
        {
            totalBlockedCount += ScanEntries(file, entry =>
            {
                entries.Enqueue(entry);
                if (entries.Count > maximumCount)
                {
                    entries.Dequeue();
                }
            });
        }

        AuditEntry[] recentEntries = entries.Reverse().ToArray();
        return new AuditSnapshot(
            recentEntries,
            (int)Math.Clamp(totalBlockedCount, 0, int.MaxValue));
    }

    private void RotateIfNeeded()
    {
        try
        {
            var current = new FileInfo(_paths.AuditFile);
            if (!current.Exists || current.Length < _rotationThresholdBytes)
            {
                return;
            }

            // 即将覆盖旧归档文件：先把其中的成功拦截数并入持久化基数，
            // 保证界面上的累计拦截数不因日志轮转而回退。
            long archivedBlockedCount = ReadArchivedBlockedCount()
                + ScanEntries(_paths.AuditArchiveFile, _ => { });
            WriteArchivedBlockedCount(archivedBlockedCount);
            File.Move(_paths.AuditFile, _paths.AuditArchiveFile, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // 轮转失败（例如另一进程正在读取）时继续写入原文件，下次再试。
        }
    }

    /// <summary>逐行扫描审计文件，返回其中的成功拦截条数；文件缺失或读取失败按 0 处理。</summary>
    private long ScanEntries(string file, Action<AuditEntry> onEntry)
    {
        if (!File.Exists(file))
        {
            return 0;
        }

        for (int attempt = 0; ; attempt++)
        {
            // 先整体读入再回调，避免读取中途失败重试时向调用方重复投递条目。
            var buffered = new List<AuditEntry>();
            long blockedCount = 0;
            try
            {
                using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    AuditEntry? entry;
                    try
                    {
                        entry = JsonSerializer.Deserialize<AuditEntry>(line, _jsonOptions);
                    }
                    catch (JsonException)
                    {
                        // Keep valid records readable if a partial final line was written.
                        continue;
                    }

                    if (entry is null)
                    {
                        continue;
                    }

                    if (IsSuccessfulBlockEvent(entry))
                    {
                        blockedCount++;
                    }

                    buffered.Add(entry);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 1)
                {
                    return 0;
                }

                // 轮转的 File.Move 瞬间可能撞上读取；稍等重试一次。
                Thread.Sleep(30);
                continue;
            }

            foreach (AuditEntry entry in buffered)
            {
                onEntry(entry);
            }

            return blockedCount;
        }
    }

    private static bool IsSuccessfulBlockEvent(AuditEntry entry)
        => entry.Success && entry.EventType is "ProcessBlocked" or "WebsiteBlocked";

    private long ReadArchivedBlockedCount()
    {
        try
        {
            if (!File.Exists(_paths.AuditStatsFile))
            {
                return 0;
            }

            AuditStats? stats = JsonSerializer.Deserialize<AuditStats>(
                File.ReadAllText(_paths.AuditStatsFile),
                _jsonOptions);
            return Math.Max(0, stats?.ArchivedBlockedCount ?? 0);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    private void WriteArchivedBlockedCount(long archivedBlockedCount)
    {
        string json = JsonSerializer.Serialize(
            new AuditStats { ArchivedBlockedCount = archivedBlockedCount },
            _jsonOptions);
        string temporaryFile = _paths.AuditStatsFile + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporaryFile, json);
        try
        {
            File.Move(temporaryFile, _paths.AuditStatsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private sealed class AuditStats
    {
        public long ArchivedBlockedCount { get; set; }
    }
}
