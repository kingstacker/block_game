namespace BlockGame.Core.Models;

public sealed record ProcessDescriptor(
    int ProcessId,
    string FileName,
    string? FullPath,
    string? ProductName = null,
    string? FileDescription = null);

