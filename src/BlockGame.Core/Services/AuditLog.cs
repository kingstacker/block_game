using System.Text.Json;
using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public sealed class AuditLog
{
    private readonly DataPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Create();
    private readonly object _sync = new();

    public AuditLog(DataPaths paths)
    {
        _paths = paths;
    }

    public void Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _paths.EnsureDirectory();
        string line = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;

        lock (_sync)
        {
            using var stream = new FileStream(
                _paths.AuditFile,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.Write(line);
        }
    }

    public IReadOnlyList<AuditEntry> ReadRecent(int maximumCount = 300)
    {
        if (!File.Exists(_paths.AuditFile))
        {
            return [];
        }

        maximumCount = Math.Clamp(maximumCount, 1, 5_000);
        string[] lines;
        try
        {
            using var stream = new FileStream(
                _paths.AuditFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var allLines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                allLines.Add(line);
            }

            lines = allLines.TakeLast(maximumCount).ToArray();
        }
        catch (IOException)
        {
            return [];
        }

        var entries = new List<AuditEntry>();
        foreach (string line in lines)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line, _jsonOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Keep valid records readable if a partial final line was written.
            }
        }

        entries.Reverse();
        return entries;
    }
}

