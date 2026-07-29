using System.Text.Json;
using BlockGame.Core.Services;
using Microsoft.Win32;

namespace BlockGame.Guard;

internal sealed class BrowserDnsPolicyManager
{
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

    private readonly DataPaths _paths;

    public BrowserDnsPolicyManager(DataPaths paths)
    {
        _paths = paths;
    }

    public void Apply()
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
    }

    public void Restore()
    {
        if (!File.Exists(_paths.BrowserDnsPolicyBackupFile))
        {
            return;
        }

        BrowserDnsPolicyBackup backup = JsonSerializer.Deserialize<BrowserDnsPolicyBackup>(
                File.ReadAllText(_paths.BrowserDnsPolicyBackupFile),
                JsonDefaults.Create())
            ?? new BrowserDnsPolicyBackup();

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
