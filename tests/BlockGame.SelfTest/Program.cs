using BlockGame.Core.Models;
using BlockGame.Core.Services;

namespace BlockGame.SelfTest;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("密码哈希", PasswordHashRoundTrip),
            ("密码输错限速", PasswordRateLimit),
            ("通配符和规则匹配", RuleMatching),
            ("默认游戏平台和影音规则", DefaultRules),
            ("网站域名规则", WebsiteDomainRulesTest),
            ("DNS 拦截响应", DnsBlockedResponse),
            ("旧 hosts 托管区清理", LegacyHostsCleanup),
            ("关键系统进程安全名单", SafetyList),
            ("最高优先级调试复位", DebugReset),
            ("解除冷静期", UnlockCooldown),
            ("一次性卸载令牌", UninstallToken),
            ("配置和审计持久化", Persistence)
        };

        int failed = 0;
        foreach ((string name, Action run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"[FAIL] {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"完成：{tests.Length - failed} 通过，{failed} 失败。 ");
        return failed == 0 ? 0 : 1;
    }

    private static void PasswordHashRoundTrip()
    {
        PasswordCredential credential = PasswordHasher.Create("correct-horse", 100_000);
        Assert(PasswordHasher.Verify("correct-horse", credential), "正确密码未通过。 ");
        Assert(!PasswordHasher.Verify("wrong-password", credential), "错误密码通过了。 ");
    }

    private static void PasswordRateLimit()
    {
        var config = new AppConfig
        {
            Password = PasswordHasher.Create("correct-horse", 100_000)
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PasswordVerificationResult result = default!;
        for (int index = 0; index < 5; index++)
        {
            result = PasswordGate.Verify(config, "wrong-password", now);
        }

        Assert(result.RateLimited, "第五次错误后没有触发等待。 ");
        PasswordVerificationResult blocked = PasswordGate.Verify(config, "correct-horse", now.AddSeconds(10));
        Assert(!blocked.Success && blocked.RateLimited, "等待期间仍可验证。 ");
        PasswordVerificationResult recovered = PasswordGate.Verify(config, "correct-horse", now.AddMinutes(2));
        Assert(recovered.Success, "等待结束后正确密码未通过。 ");
    }

    private static void RuleMatching()
    {
        Assert(WildcardMatcher.IsMatch("MyGame.exe", "my*.exe"), "通配符大小写匹配失败。 ");
        var config = new AppConfig
        {
            ProtectionEnabled = true,
            Rules =
            [
                new BlockRule
                {
                    Name = "示例游戏",
                    Target = RuleTarget.FileName,
                    Pattern = "game*.exe"
                }
            ]
        };

        RuleMatch? match = RuleMatcher.Match(config, new ProcessDescriptor(1234, "GameClient", null));
        Assert(match is not null, "文件名规则未命中。 ");
        Assert(RuleMatcher.Match(config, new ProcessDescriptor(1235, "Editor", null)) is null, "无关程序被误匹配。 ");

        config.Rules[0].Pattern = "qq";
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1236, "QQ", null)) is not null,
            "未自动为 qq 规则补全 .exe。 ");
        config.Rules[0].Pattern = "qq*";
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1237, "QQApp", null)) is not null,
            "无扩展名的 QQ 通配规则未命中。 ");
        Assert(SafetyPolicy.NormalizeFileNameRulePatterns(config), "旧规则未触发规范化迁移。 ");
        Assert(config.Rules[0].Pattern == "qq*.exe", "旧规则未保存为明确的 .exe 通配规则。 ");
        Assert(SafetyPolicy.NormalizeFileNamePattern(" ") == string.Empty, "空规则被错误补全为 .exe。 ");

        config.Rules[0].Pattern = " qq ; WeChat*；game.exe; ";
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1238, "QQ", null)) is not null,
            "多程序规则未匹配 QQ。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1239, "WeChatApp", null)) is not null,
            "多程序规则中的通配符未生效。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1240, "game.exe", null)) is not null,
            "多程序规则未匹配已有扩展名的程序。 ");
        Assert(
            SafetyPolicy.NormalizeFileNamePattern(" qq ; WeChat*；game.exe; ")
                == "qq.exe;WeChat*.exe;game.exe",
            "多程序规则未正确规范化。 ");
        Assert(
            SafetyPolicy.NormalizeFileNamePattern("qq\nWeChat*\r\ngame.exe")
                == "qq.exe;WeChat*.exe;game.exe",
            "换行分隔的多程序规则未正确规范化。 ");
    }

    private static void SafetyList()
    {
        var config = new AppConfig
        {
            ProtectionEnabled = true,
            Rules =
            [
                new BlockRule
                {
                    Name = "危险规则",
                    Target = RuleTarget.FileName,
                    Pattern = "explorer.exe"
                }
            ]
        };

        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(100, "explorer.exe", null)) is null,
            "关键系统进程未受到运行时安全名单保护。 ");
        Assert(SafetyPolicy.ValidateRule(config.Rules[0]) is not null, "危险规则通过了保存前验证。 ");
    }

    private static void DefaultRules()
    {
        var config = new AppConfig
        {
            ProtectionEnabled = true,
            Rules =
            [
                new BlockRule
                {
                    Name = "用户规则",
                    Target = RuleTarget.FileName,
                    Pattern = "custom.exe"
                }
            ]
        };

        Assert(DefaultRulePresets.Apply(config) == 4, "首次迁移未添加四条默认规则。 ");
        Assert(config.Rules.Count == 5, "添加默认规则时破坏了用户原有规则。 ");

        BlockRule gameRule = config.Rules.Single(rule => rule.Name == "默认：常见游戏平台");
        BlockRule mediaRule = config.Rules.Single(rule => rule.Name == "默认：常见影音平台");
        BlockRule gameWebsiteRule = config.Rules.Single(rule => rule.Name == "默认：常见游戏网站");
        BlockRule mediaWebsiteRule = config.Rules.Single(rule => rule.Name == "默认：常见影音网站");
        Assert(
            !gameRule.Enabled
                && !mediaRule.Enabled
                && !gameWebsiteRule.Enabled
                && !mediaWebsiteRule.Enabled,
            "默认规则不应在升级后自动启用。 ");
        Assert(SafetyPolicy.ValidateRule(gameRule) is null, "游戏平台默认规则未通过安全校验。 ");
        Assert(SafetyPolicy.ValidateRule(mediaRule) is null, "影音平台默认规则未通过安全校验。 ");
        Assert(SafetyPolicy.ValidateRule(gameWebsiteRule) is null, "游戏网站默认规则未通过安全校验。 ");
        Assert(SafetyPolicy.ValidateRule(mediaWebsiteRule) is null, "影音网站默认规则未通过安全校验。 ");
        Assert(
            WebsiteDomainRules.SplitAndNormalize(gameWebsiteRule.Pattern)
                .Contains("wegame.com.cn", StringComparer.OrdinalIgnoreCase)
                && WebsiteDomainRules.SplitAndNormalize(gameWebsiteRule.Pattern)
                    .Contains("steampowered.com", StringComparer.OrdinalIgnoreCase)
                && WebsiteDomainRules.SplitAndNormalize(gameWebsiteRule.Pattern)
                    .Contains("4399.com", StringComparer.OrdinalIgnoreCase),
            "游戏网站默认规则缺少常见平台。 ");
        Assert(
            WebsiteDomainRules.SplitAndNormalize(mediaWebsiteRule.Pattern)
                .Contains("iqiyi.com", StringComparer.OrdinalIgnoreCase)
                && WebsiteDomainRules.SplitAndNormalize(mediaWebsiteRule.Pattern)
                    .Contains("v.qq.com", StringComparer.OrdinalIgnoreCase)
                && WebsiteDomainRules.SplitAndNormalize(mediaWebsiteRule.Pattern)
                    .Contains("bilibili.com", StringComparer.OrdinalIgnoreCase),
            "影音网站默认规则缺少常见平台。 ");

        gameRule.Enabled = true;
        mediaRule.Enabled = true;
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(2001, "WeGame", null)) is not null,
            "游戏平台规则未匹配 WeGame。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(2002, "steam", null)) is not null,
            "游戏平台规则未匹配 Steam。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(2003, "QQLive", null)) is not null,
            "影音平台规则未匹配腾讯视频。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(2004, "cloudmusic", null)) is not null,
            "影音平台规则未匹配网易云音乐。 ");

        Assert(DefaultRulePresets.Apply(config) == 0, "默认规则被重复添加。 ");
        Assert(config.Rules.Count == 5, "重复迁移改变了规则数量。 ");

        var versionOneConfig = new AppConfig
        {
            DefaultRulePresetVersion = 1
        };
        Assert(
            DefaultRulePresets.Apply(versionOneConfig) == 2,
            "从版本1升级时未补充两条网站默认规则。 ");
        Assert(
            versionOneConfig.Rules.Count == 2
                && versionOneConfig.Rules.All(rule => rule.Target == RuleTarget.Domain),
            "从版本1升级时错误地重新添加了旧的程序默认规则。 ");
    }

    private static void WebsiteDomainRulesTest()
    {
        Assert(
            WebsiteDomainRules.NormalizePattern(
                "https://poki.com/zh; *.POKI.com；例子.测试")
                == "poki.com;xn--fsqu00a.xn--0zwm56d",
            "网址、通配符或国际化域名未正确规范化。 ");
        Assert(
            WebsiteDomainRules.IsMatch("www.poki.com", "poki.com"),
            "域名规则未覆盖子域名。 ");
        Assert(
            !WebsiteDomainRules.IsMatch("notpoki.com", "poki.com"),
            "域名边界匹配错误。 ");

        var rule = new BlockRule
        {
            Name = "Poki",
            Target = RuleTarget.Domain,
            Pattern = "https://poki.com/zh"
        };
        Assert(
            SafetyPolicy.ValidateRule(rule) is null,
            "有效网站规则未通过验证。 ");
        rule.Pattern = "*game*";
        Assert(
            SafetyPolicy.ValidateRule(rule) is not null,
            "无法由选择性NRPT实现的任意通配规则通过了验证。 ");
    }

    private static void LegacyHostsCleanup()
    {
        const string originalHosts = "127.0.0.1 custom.local\r\n# user comment\r\n";
        string rendered = HostsFileRenderer.Render(
            originalHosts,
            ["example.com", "www.example.com"]);
        Assert(rendered.Contains("127.0.0.1 custom.local", StringComparison.Ordinal), "用户原有 hosts 内容被删除。 ");
        Assert(rendered.Contains("0.0.0.0 example.com", StringComparison.Ordinal), "IPv4 网站屏蔽项未写入。 ");
        Assert(rendered.Contains(":: www.example.com", StringComparison.Ordinal), "IPv6 网站屏蔽项未写入。 ");
        string removed = HostsFileRenderer.Render(rendered, []);
        Assert(!removed.Contains(HostsFileRenderer.BeginMarker, StringComparison.Ordinal), "旧 hosts 托管区块未移除。 ");
        Assert(removed.Contains("# user comment", StringComparison.Ordinal), "移除托管区块时破坏了用户 hosts 内容。 ");
    }

    private static void DnsBlockedResponse()
    {
        byte[] query =
        [
            0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x0E, (byte)'b', (byte)'l', (byte)'o', (byte)'c', (byte)'k',
            (byte)'g', (byte)'a', (byte)'m', (byte)'e', (byte)'-', (byte)'t',
            (byte)'e', (byte)'s', (byte)'t',
            0x07, (byte)'i', (byte)'n', (byte)'v', (byte)'a', (byte)'l',
            (byte)'i', (byte)'d',
            0x00, 0x00, 0x01, 0x00, 0x01
        ];
        Assert(
            DnsMessageResponder.TryCreateNameErrorResponse(
                query,
                out string domain,
                out byte[] response),
            "有效DNS查询未生成拦截响应。 ");
        Assert(domain == "blockgame-test.invalid", "DNS查询域名解析错误。 ");
        Assert(response[0] == 0x12 && response[1] == 0x34, "DNS事务ID未保留。 ");
        Assert((response[2] & 0x80) != 0, "DNS响应标志未设置。 ");
        Assert((response[3] & 0x0F) == 3, "DNS响应不是NXDOMAIN。 ");
        Assert(response[6] == 0 && response[7] == 0, "DNS拦截响应错误地包含答案。 ");
    }

    private static void UnlockCooldown()
    {
        var config = new AppConfig { UnlockDelayMinutes = 60 };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProtectionManager.EnableAndLock(config);
        ProtectionManager.RequestUnlock(config, ProtectionManager.UnlockConfirmationText, now);

        bool earlyUnlockRejected = false;
        try
        {
            ProtectionManager.CompleteUnlock(config, now.AddMinutes(59));
        }
        catch (InvalidOperationException)
        {
            earlyUnlockRejected = true;
        }

        Assert(earlyUnlockRejected, "冷静期结束前可以解除。 ");
        ProtectionManager.CompleteUnlock(config, now.AddMinutes(60));
        Assert(!config.ProtectionLocked, "冷静期结束后无法完成解除。 ");
        Assert(config.ProtectionEnabled, "完成解除不应自动停止拦截。 ");
    }

    private static void DebugReset()
    {
        var config = new AppConfig
        {
            ProtectionEnabled = true,
            ProtectionLocked = true,
            UnlockRequestedAtUtc = DateTimeOffset.UtcNow,
            UnlockAvailableAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            UninstallTokenHashBase64 = "token",
            UninstallAuthorizedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            PasswordThrottle = new PasswordThrottle
            {
                ConsecutiveFailures = 5,
                BlockedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1)
            },
            Rules = [new BlockRule { Name = "QQ", Pattern = "qq" }]
        };

        ProtectionManager.ResetForDebug(config);

        Assert(!config.ProtectionEnabled, "调试复位后仍在拦截。 ");
        Assert(!config.ProtectionLocked, "调试复位后仍处于锁定。 ");
        Assert(config.UnlockRequestedAtUtc is null && config.UnlockAvailableAtUtc is null, "调试复位未清除解除申请。 ");
        Assert(config.UninstallTokenHashBase64 is null && config.UninstallAuthorizedUntilUtc is null, "调试复位未清除卸载授权。 ");
        Assert(config.PasswordThrottle.ConsecutiveFailures == 0, "调试复位未清除密码限流。 ");
        Assert(config.Rules.Count == 0, "调试复位未删除规则。 ");
    }

    private static void UninstallToken()
    {
        var config = new AppConfig { ProtectionLocked = false };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string token = UninstallAuthorizationService.Create(config, now, TimeSpan.FromMinutes(10));
        Assert(
            UninstallAuthorizationService.ValidateAndConsume(config, token, now.AddMinutes(1)),
            "有效令牌未通过。 ");
        Assert(
            !UninstallAuthorizationService.ValidateAndConsume(config, token, now.AddMinutes(2)),
            "一次性令牌被重复使用。 ");
    }

    private static void Persistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "BlockGameSelfTest", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        try
        {
            var store = new ConfigStore(paths);
            var audit = new AuditLog(paths);
            var config = new AppConfig
            {
                SetupCompleted = true,
                UnlockDelayMinutes = 90,
                Rules = [new BlockRule { Name = "Test", Pattern = "test.exe" }]
            };
            store.Save(config);

            AppConfig loaded = store.Load();
            Assert(loaded.SetupCompleted, "配置布尔值丢失。 ");
            Assert(loaded.UnlockDelayMinutes == 90, "配置数值丢失。 ");
            Assert(loaded.Rules.Single().Pattern == "test.exe", "规则未正确还原。 ");

            audit.Append(new AuditEntry
            {
                EventType = "ProcessBlocked",
                Message = "已阻止 Test.exe，命中规则“Test”。",
                ProcessName = "Test.exe",
                DesktopNotificationSent = true
            });
            AuditEntry persistedEntry = audit.ReadRecent().Single();
            Assert(persistedEntry.EventType == "ProcessBlocked", "审计记录未正确还原。 ");
            Assert(persistedEntry.ProcessName == "Test.exe", "拦截程序名未写入审计记录。 ");
            Assert(persistedEntry.DesktopNotificationSent == true, "桌面通知投递状态未写入审计记录。 ");
        }
        finally
        {
            string fullRoot = Path.GetFullPath(root);
            string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BlockGameSelfTest"));
            if (fullRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
