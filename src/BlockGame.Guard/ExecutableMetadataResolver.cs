using System.Diagnostics;

namespace BlockGame.Guard;

internal sealed record ExecutableMetadata(
    string? FullPath,
    string? ProductName,
    string? FileDescription);

internal sealed class ExecutableMetadataResolver
{
    private static readonly TimeSpan ProcessCacheLifetime = TimeSpan.FromSeconds(10);
    private readonly Dictionary<int, ProcessCacheEntry> _processCache = new();
    private readonly Dictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public ExecutableMetadata Read(int processId, string fileName)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (_processCache.TryGetValue(processId, out ProcessCacheEntry? cached)
            && string.Equals(
                cached.FileName,
                fileName,
                StringComparison.OrdinalIgnoreCase)
            && nowUtc - cached.CachedAtUtc < ProcessCacheLifetime)
        {
            return cached.Metadata;
        }

        string? path = ProcessPathResolver.TryGetPath(processId);
        ExecutableMetadata metadata = path is null
            ? new ExecutableMetadata(null, null, null)
            : ReadPath(path);
        _processCache[processId] = new ProcessCacheEntry(
            fileName,
            nowUtc,
            metadata);
        return metadata;
    }

    public void RetainOnly(IReadOnlySet<int> activeProcessIds)
    {
        foreach (int processId in _processCache.Keys
                     .Where(processId => !activeProcessIds.Contains(processId))
                     .ToArray())
        {
            _processCache.Remove(processId);
        }
    }

    private ExecutableMetadata ReadPath(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return new ExecutableMetadata(path, null, null);
            }

            if (_cache.TryGetValue(path, out CacheEntry? cached)
                && cached.Length == file.Length
                && cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                return cached.Metadata;
            }

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            var metadata = new ExecutableMetadata(
                path,
                Normalize(version.ProductName),
                Normalize(version.FileDescription));
            _cache[path] = new CacheEntry(
                file.Length,
                file.LastWriteTimeUtc,
                metadata);
            return metadata;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return new ExecutableMetadata(path, null, null);
        }
    }

    private static string? Normalize(string? value)
    {
        string? normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record CacheEntry(
        long Length,
        DateTime LastWriteTimeUtc,
        ExecutableMetadata Metadata);

    private sealed record ProcessCacheEntry(
        string FileName,
        DateTimeOffset CachedAtUtc,
        ExecutableMetadata Metadata);
}
