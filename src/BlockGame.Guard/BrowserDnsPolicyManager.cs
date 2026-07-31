using System.Text.Json;
using BlockGame.Core.Services;
using Microsoft.Win32;

namespace BlockGame.Guard;

internal sealed class BrowserDnsPolicyManager
{
    private const int MaximumUrlBlocklistEntries = 1000;

    private static readonly PolicyTarget[] Targets =
    [
        new(
            @"SOFTWARE\Policies\Google\Chrome",
            "DnsOverHttpsMode",
            RegistryValueKind.String,
            "off"),
        new(
            @"SOFTWARE\Policies\Microsoft\Edge",
            "DnsOverHttpsMode",
            RegistryValueKind.String,
            "off"),
        new(
            @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS",
            "Enabled",
            RegistryValueKind.DWord,
            0),
        new(
            @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS",
            "Locked",
            RegistryValueKind.DWord,
            1)
    ];

    private static readonly string[] UrlBlocklistKeyPaths =
    [
        @"SOFTWARE\Policies\Google\Chrome\URLBlocklist",
        @"SOFTWARE\Policies\Microsoft\Edge\URLBlocklist"
    ];

    private readonly DataPaths _paths;

    public BrowserDnsPolicyManager(DataPaths paths)
    {
        _paths = paths;
    }

    public int Apply(IReadOnlyCollection<string> domains)
    {
        _paths.EnsureDirectory();
        if (!File.Exists(_paths.BrowserDnsPolicyBackupFile))
        {
            var backup = new BrowserDnsPolicyBackup
            {
                Values = Targets.Select(Capture).ToList()
            };
            File.WriteAllText(
                _paths.BrowserDnsPolicyBackupFile,
                JsonSerializer.Serialize(backup, JsonDefaults.Create(indented: true)));
        }

        foreach (PolicyTarget target in Targets)
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(target.KeyPath, writable: true)
                ?? throw new UnauthorizedAccessException(
                    $"无法创建浏览器策略注册表项 {target.KeyPath}。");
            key.SetValue(target.ValueName, target.AppliedValue, target.Kind);
        }

        return ApplyUrlBlocklists(domains);
    }

    public void Restore()
    {
        RestoreUrlBlocklists();

        if (!File.Exists(_paths.BrowserDnsPolicyBackupFile))
        {
            return;
        }

        BrowserDnsPolicyBackup backup;
        try
        {
            backup = JsonSerializer.Deserialize<BrowserDnsPolicyBackup>(
                    File.ReadAllText(_paths.BrowserDnsPolicyBackupFile),
                    JsonDefaults.Create())
                ?? new BrowserDnsPolicyBackup();
        }
        catch (JsonException)
        {
            // 备份文件损坏时无法得知原始值；删除备份避免每次恢复都在同一处失败。
            File.Delete(_paths.BrowserDnsPolicyBackupFile);
            return;
        }

        foreach (PolicyValueBackup saved in backup.Values)
        {
            PolicyTarget? target = Targets.FirstOrDefault(candidate =>
                candidate.KeyPath.Equals(saved.KeyPath, StringComparison.OrdinalIgnoreCase)
                && candidate.ValueName.Equals(saved.ValueName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                continue;
            }

            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                saved.KeyPath,
                writable: true);
            if (key is null || !CurrentValueIsApplied(key, target))
            {
                continue;
            }

            if (!saved.Existed)
            {
                key.DeleteValue(saved.ValueName, throwOnMissingValue: false);
            }
            else if (saved.Kind == RegistryValueKind.DWord)
            {
                key.SetValue(saved.ValueName, saved.DwordValue ?? 0, RegistryValueKind.DWord);
            }
            else
            {
                key.SetValue(
                    saved.ValueName,
                    saved.StringValue ?? string.Empty,
                    RegistryValueKind.String);
            }
        }

        File.Delete(_paths.BrowserDnsPolicyBackupFile);
    }

    private int ApplyUrlBlocklists(IReadOnlyCollection<string> domains)
    {
        RestoreUrlBlocklists();

        string[] filters = domains
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (filters.Length == 0)
        {
            return 0;
        }

        var state = new BrowserUrlBlockPolicyState();
        var skippedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string keyPath in UrlBlocklistKeyPaths)
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true)
                ?? throw new UnauthorizedAccessException(
                    $"无法创建浏览器网址拦截策略注册表项 {keyPath}。");
            var occupiedNames = key.GetValueNames()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int nextValueNumber = 1;
            for (int index = 0; index < filters.Length; index++)
            {
                string filter = filters[index];
                while (occupiedNames.Contains(nextValueNumber.ToString()))
                {
                    nextValueNumber++;
                }

                if (nextValueNumber > MaximumUrlBlocklistEntries)
                {
                    // 浏览器策略名额用完不能让整个网站同步失败：剩余域名仍由
                    // NRPT 和本机DNS拦截兜底，这里只记录被跳过的数量。
                    for (int skipped = index; skipped < filters.Length; skipped++)
                    {
                        skippedDomains.Add(filters[skipped]);
                    }

                    break;
                }

                string valueName = nextValueNumber.ToString();
                occupiedNames.Add(valueName);
                state.Entries.Add(new BrowserUrlBlockPolicyEntry
                {
                    KeyPath = keyPath,
                    ValueName = valueName,
                    AppliedValue = filter
                });
                nextValueNumber++;
            }
        }

        File.WriteAllText(
            _paths.BrowserUrlBlockPolicyStateFile,
            JsonSerializer.Serialize(state, JsonDefaults.Create(indented: true)));

        foreach (BrowserUrlBlockPolicyEntry entry in state.Entries)
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(
                    entry.KeyPath,
                    writable: true)
                ?? throw new UnauthorizedAccessException(
                    $"无法创建浏览器网址拦截策略注册表项 {entry.KeyPath}。");
            key.SetValue(entry.ValueName, entry.AppliedValue, RegistryValueKind.String);
        }

        return skippedDomains.Count;
    }

    private void RestoreUrlBlocklists()
    {
        if (!File.Exists(_paths.BrowserUrlBlockPolicyStateFile))
        {
            return;
        }

        BrowserUrlBlockPolicyState state;
        try
        {
            state = JsonSerializer.Deserialize<BrowserUrlBlockPolicyState>(
                    File.ReadAllText(_paths.BrowserUrlBlockPolicyStateFile),
                    JsonDefaults.Create())
                ?? new BrowserUrlBlockPolicyState();
        }
        catch (JsonException)
        {
            // 状态文件损坏时无法得知此前写入的条目；删除文件避免每次同步都卡在这里。
            File.Delete(_paths.BrowserUrlBlockPolicyStateFile);
            return;
        }

        foreach (BrowserUrlBlockPolicyEntry entry in state.Entries)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                entry.KeyPath,
                writable: true);
            object? current = key?.GetValue(
                entry.ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (key is not null
                && current is string currentValue
                && currentValue.Equals(entry.AppliedValue, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            }
        }

        File.Delete(_paths.BrowserUrlBlockPolicyStateFile);
    }

    private static PolicyValueBackup Capture(PolicyTarget target)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(target.KeyPath);
        object? value = key?.GetValue(target.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        RegistryValueKind? kind = value is null
            ? null
            : key?.GetValueKind(target.ValueName);
        return new PolicyValueBackup
        {
            KeyPath = target.KeyPath,
            ValueName = target.ValueName,
            Existed = value is not null,
            Kind = kind ?? target.Kind,
            StringValue = value as string,
            DwordValue = value is int number ? number : null
        };
    }

    private static bool CurrentValueIsApplied(RegistryKey key, PolicyTarget target)
    {
        object? current = key.GetValue(
            target.ValueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return current is not null && Equals(current, target.AppliedValue);
    }

    private sealed record PolicyTarget(
        string KeyPath,
        string ValueName,
        RegistryValueKind Kind,
        object AppliedValue);

    private sealed class BrowserDnsPolicyBackup
    {
        public List<PolicyValueBackup> Values { get; set; } = [];
    }

    private sealed class BrowserUrlBlockPolicyState
    {
        public List<BrowserUrlBlockPolicyEntry> Entries { get; set; } = [];
    }

    private sealed class BrowserUrlBlockPolicyEntry
    {
        public string KeyPath { get; set; } = string.Empty;
        public string ValueName { get; set; } = string.Empty;
        public string AppliedValue { get; set; } = string.Empty;
    }

    private sealed class PolicyValueBackup
    {
        public string KeyPath { get; set; } = string.Empty;
        public string ValueName { get; set; } = string.Empty;
        public bool Existed { get; set; }
        public RegistryValueKind Kind { get; set; }
        public string? StringValue { get; set; }
        public int? DwordValue { get; set; }
    }
}
