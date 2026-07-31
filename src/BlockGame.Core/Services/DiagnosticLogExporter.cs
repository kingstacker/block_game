using System.IO.Compression;
using System.Text;

namespace BlockGame.Core.Services;

public static class DiagnosticLogExporter
{
    public static IReadOnlyList<string> Export(
        DataPaths paths,
        string destinationFile,
        string diagnosticSummary)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(destinationFile))
        {
            throw new ArgumentException("导出文件路径不能为空。", nameof(destinationFile));
        }

        string fullDestination = Path.GetFullPath(destinationFile);
        string? destinationDirectory = Path.GetDirectoryName(fullDestination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("导出文件目录无效。", nameof(destinationFile));
        }

        Directory.CreateDirectory(destinationDirectory);
        string temporaryFile = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var includedFiles = new List<string>();

        try
        {
            using (var output = new FileStream(
                       temporaryFile,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                AddFileIfPresent(archive, paths.AuditFile, "audit.jsonl", includedFiles);
                AddFileIfPresent(archive, paths.AuditArchiveFile, "audit.1.jsonl", includedFiles);
                AddFileIfPresent(archive, paths.AuditStatsFile, "audit-stats.json", includedFiles);
                AddFileIfPresent(archive, paths.HeartbeatFile, "guard-heartbeat.json", includedFiles);

                ZipArchiveEntry summaryEntry = archive.CreateEntry(
                    "diagnostics.txt",
                    CompressionLevel.Optimal);
                using Stream summaryStream = summaryEntry.Open();
                using var writer = new StreamWriter(
                    summaryStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(diagnosticSummary ?? string.Empty);
                includedFiles.Add(summaryEntry.FullName);
            }

            File.Move(temporaryFile, fullDestination, overwrite: true);
            return includedFiles;
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }

    private static void AddFileIfPresent(
        ZipArchive archive,
        string sourceFile,
        string entryName,
        ICollection<string> includedFiles)
    {
        try
        {
            using var source = new FileStream(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using Stream destination = entry.Open();
            source.CopyTo(destination);
            includedFiles.Add(entry.FullName);
        }
        catch (FileNotFoundException)
        {
            // Optional diagnostic files are omitted when they do not exist yet.
        }
        catch (DirectoryNotFoundException)
        {
            // The data directory may not have been created before the first event.
        }
    }
}
