using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BlockGame.Core.Services;

namespace BlockGame.Guard;

internal sealed class NrptPolicyManager
{
    private const string SynchronizeScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try {
            $managedComment = 'BlockGame managed website blocking'
            $changed = $false
            $desiredDomains = @()
            if (Test-Path -LiteralPath $env:BLOCKGAME_NRPT_FILE) {
                $json = Get-Content -Raw -LiteralPath $env:BLOCKGAME_NRPT_FILE
                if (-not [string]::IsNullOrWhiteSpace($json)) {
                    $parsedDomains = ConvertFrom-Json -InputObject $json
                    $desiredDomains = @(
                        $parsedDomains |
                            ForEach-Object { ([string]$_).Trim().ToLowerInvariant() } |
                            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                            Sort-Object -Unique
                    )
                }
            }
            $desiredNamespaces = @()
            foreach ($domain in $desiredDomains) {
                $desiredNamespaces += $domain
                $desiredNamespaces += '.' + $domain
            }
            $desiredNamespaces = @(
                $desiredNamespaces |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    Sort-Object -Unique
            )
            $allRules = @(Get-DnsClientNrptRule -ErrorAction Stop)
            $managedRules = @($allRules | Where-Object { $_.Comment -eq $managedComment })
            foreach ($namespace in $desiredNamespaces) {
                $conflict = @($allRules | Where-Object {
                    $_.Comment -ne $managedComment -and
                    @($_.Namespace) -contains $namespace
                })
                if ($conflict.Count -gt 0) {
                    throw "域名 $namespace 已存在其他 NRPT 规则，BlockGame 未覆盖该规则。"
                }
            }
            foreach ($rule in $managedRules) {
                $ruleNamespaces = @($rule.Namespace)
                $keep = $ruleNamespaces.Count -eq 1 -and
                    $desiredNamespaces -contains [string]$ruleNamespaces[0] -and
                    @($rule.NameServers) -contains '127.0.0.1' -and
                    @($rule.NameServers) -contains '::1'
                if (-not $keep) {
                    Remove-DnsClientNrptRule -Name $rule.Name -Confirm:$false -ErrorAction Stop
                    $changed = $true
                }
            }
            $managedRules = @(Get-DnsClientNrptRule -ErrorAction Stop |
                Where-Object { $_.Comment -eq $managedComment })
            foreach ($namespace in $desiredNamespaces) {
                $exists = @($managedRules | Where-Object {
                    @($_.Namespace) -contains $namespace
                }).Count -gt 0
                if (-not $exists) {
                    Add-DnsClientNrptRule `
                        -Namespace $namespace `
                        -NameServers @('127.0.0.1', '::1') `
                        -DisplayName ('BlockGame: ' + $namespace) `
                        -Comment $managedComment `
                        -ErrorAction Stop
                    $changed = $true
                }
            }
            if ($changed) {
                Clear-DnsClientCache
            }
            Write-Output ('BlockGame NRPT synchronized: ' + $desiredDomains.Count)
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;

    private readonly DataPaths _paths;

    public NrptPolicyManager(DataPaths paths)
    {
        _paths = paths;
    }

    public void Synchronize(IReadOnlyCollection<string> domains)
    {
        _paths.EnsureDirectory();
        string json = JsonSerializer.Serialize(
            domains.OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase),
            JsonDefaults.Create());
        File.WriteAllText(_paths.NrptDesiredDomainsFile, json);

        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        string encodedScript = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(SynchronizeScript));
        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);
        startInfo.Environment["BLOCKGAME_NRPT_FILE"] = _paths.NrptDesiredDomainsFile;

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Windows DNS 策略工具。 ");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("同步 Windows NRPT 网站规则超时。 ");
        }

        string standardOutput = standardOutputTask.GetAwaiter().GetResult();
        string standardError = standardErrorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(standardError)
                ? standardOutput
                : standardError;
            throw new InvalidOperationException(
                "同步 Windows NRPT 网站规则失败：" + detail.Trim());
        }
    }
}
