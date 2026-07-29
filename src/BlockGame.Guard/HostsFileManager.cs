using System.Runtime.InteropServices;
using System.Text;
using BlockGame.Core.Services;

namespace BlockGame.Guard;

internal sealed class HostsFileManager
{
    private readonly string _hostsFile;

    public HostsFileManager(string hostsFile)
    {
        _hostsFile = Path.GetFullPath(hostsFile);
    }

    public static HostsFileManager CreateDefault()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return new HostsFileManager(Path.Combine(systemDirectory, "drivers", "etc", "hosts"));
    }

    public bool Synchronize(IEnumerable<string> domains)
    {
        string existingContent = File.Exists(_hostsFile)
            ? File.ReadAllText(_hostsFile)
            : string.Empty;
        string updatedContent = HostsFileRenderer.Render(existingContent, domains);
        if (string.Equals(existingContent, updatedContent, StringComparison.Ordinal))
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(_hostsFile);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            _hostsFile,
            updatedContent,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _ = DnsFlushResolverCache();
        return true;
    }

    [DllImport("dnsapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
}
