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
            ("规则导入导出", RuleTransferRoundTrip),
            ("默认游戏平台和影音规则", DefaultRules),
            ("网站域名规则", WebsiteDomainRulesTest),
            ("DNS 拦截响应", DnsBlockedResponse),
            ("旧 hosts 托管区清理", LegacyHostsCleanup),
            ("关键系统进程安全名单", SafetyList),
            ("自身组件路径校验", OwnComponentPathVerification),
            ("完整路径规则通配校验", FullPathRuleValidation),
            ("最高优先级调试复位", DebugReset),
            ("冷静期单位换算", UnlockDelayUnitConversion),
            ("解除冷静期", UnlockCooldown),
            ("锁定期间延长冷静期", UnlockDelayExtension),
            ("一次性卸载令牌", UninstallToken),
            ("密码保护卸载授权", PasswordProtectedUninstall),
            ("配置和审计持久化", Persistence),
            ("审计日志轮转", AuditRotation)
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

        config.Rules[0].Pattern = "steam";
        Assert(
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(
                    1236,
                    "renamed-client",
                    @"C:\Games\renamed-client.exe",
                    ProductName: "Steam",
                    FileDescription: "Steam Client Bootstrapper")) is not null,
            "修改文件名后未通过内部产品名命中规则。 ");
        config.Rules[0].Pattern = "steam client bootstrapper";
        Assert(
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(
                    1237,
                    "another-name",
                    @"C:\Games\another-name.exe",
                    ProductName: "Valve Client",
                    FileDescription: "Steam Client Bootstrapper")) is not null,
            "修改文件名后未通过文件描述命中规则。 ");
        Assert(
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(
                    1238,
                    "unrelated",
                    @"C:\Tools\unrelated.exe",
                    ProductName: "Unrelated Product",
                    FileDescription: "Unrelated Tool")) is null,
            "无关内部产品信息被误匹配。 ");

        config.Rules[0].Pattern = "qq";
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1239, "QQ", null)) is not null,
            "未自动为 qq 规则补全 .exe。 ");
        config.Rules[0].Pattern = "qq*";
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1240, "QQApp", null)) is not null,
            "无扩展名的 QQ 通配规则未命中。 ");
        Assert(SafetyPolicy.NormalizeFileNameRulePatterns(config), "旧规则未触发规范化迁移。 ");
        Assert(config.Rules[0].Pattern == "qq*.exe", "旧规则未保存为明确的 .exe 通配规则。 ");
        Assert(SafetyPolicy.NormalizeFileNamePattern(" ") == string.Empty, "空规则被错误补全为 .exe。 ");

        config.Rules[0].Pattern = " qq ; WeChat*；game.exe; ";
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1241, "QQ", null)) is not null,
            "多程序规则未匹配 QQ。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1242, "WeChatApp", null)) is not null,
            "多程序规则中的通配符未生效。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(1243, "game.exe", null)) is not null,
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

    private static void OwnComponentPathVerification()
    {
        string ownDirectory = Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
        Assert(
            SafetyPolicy.IsProtectedProcess(new ProcessDescriptor(
                600,
                "BlockGame.Guard.exe",
                Path.Combine(ownDirectory, "BlockGame.Guard.exe"))),
            "安装目录中的自身组件未受保护。 ");
        Assert(
            SafetyPolicy.IsProtectedProcess(
                new ProcessDescriptor(601, "BlockGame.Guard.exe", null)),
            "路径未知时应保守地视为自身组件，待解析路径后复查。 ");
        Assert(
            !SafetyPolicy.IsProtectedProcess(new ProcessDescriptor(
                602,
                "BlockGame.steam.exe",
                @"C:\Games\BlockGame.steam.exe")),
            "改名为 BlockGame.* 的外部程序不应获得自身组件保护。 ");
        Assert(
            SafetyPolicy.IsProtectedSystemProcess(
                new ProcessDescriptor(603, "svchost.exe", null)),
            "系统进程未列入无需路径解析的安全名单。 ");
        Assert(
            !SafetyPolicy.IsProtectedSystemProcess(
                new ProcessDescriptor(604, "BlockGame.App.exe", null)),
            "BlockGame 组件不应跳过路径解析（否则改名程序永远不会被识破）。 ");

        var config = new AppConfig
        {
            ProtectionEnabled = true,
            Rules =
            [
                new BlockRule
                {
                    Name = "Steam",
                    Target = RuleTarget.FileName,
                    Pattern = "steam"
                }
            ]
        };
        Assert(
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(
                    605,
                    "BlockGame.steam.exe",
                    @"C:\Games\BlockGame.steam.exe",
                    ProductName: "Steam")) is not null,
            "改名为 BlockGame.* 后仍应通过内部产品名命中规则。 ");
        config.Rules[0] = new BlockRule
        {
            Name = "游戏目录",
            Target = RuleTarget.FullPath,
            Pattern = @"C:\Games\*.exe"
        };
        Assert(
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(
                    606,
                    "BlockGame.steam.exe",
                    @"C:\Games\BlockGame.steam.exe")) is not null,
            "改名为 BlockGame.* 后仍应命中完整路径规则。 ");
        config.Rules[0] = new BlockRule
        {
            Name = "误杀测试",
            Target = RuleTarget.FullPath,
            Pattern = Path.Combine(ownDirectory, "*.exe")
        };
        string ownComponentPath = Path.Combine(ownDirectory, "BlockGame.Guard.exe");
        Assert(
            WildcardMatcher.IsMatch(ownComponentPath, config.Rules[0].Pattern),
            "误杀测试的路径规则没有覆盖到自身组件路径，测试无效。 ");
        Assert(
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(607, "BlockGame.Guard.exe", ownComponentPath)) is null,
            "安装目录中的自身组件被规则误杀。 ");
    }

    private static void FullPathRuleValidation()
    {
        var rule = new BlockRule
        {
            Name = "路径规则",
            Target = RuleTarget.FullPath,
            Pattern = @"C:\Games\MyGame\game.exe"
        };
        Assert(SafetyPolicy.ValidateRule(rule) is null, "正常完整路径规则未通过校验。 ");

        rule.Pattern = @"C:\Games\*.exe";
        Assert(SafetyPolicy.ValidateRule(rule) is null, "游戏目录通配规则未通过校验。 ");

        rule.Pattern = @"C:\Users\*\AppData\Local\MyGame\game.exe";
        Assert(SafetyPolicy.ValidateRule(rule) is null, "跨用户目录的通配规则未通过校验。 ");

        rule.Pattern = @"C:\Windows Games\game.exe";
        Assert(
            SafetyPolicy.ValidateRule(rule) is null,
            "与 Windows 目录同前缀的普通目录被误拒。 ");

        rule.Pattern = @"C:\*.exe";
        Assert(SafetyPolicy.ValidateRule(rule) is not null, "全盘 *.exe 通配规则通过了校验。 ");

        rule.Pattern = @"C:\*";
        Assert(SafetyPolicy.ValidateRule(rule) is not null, "全盘通配规则通过了校验。 ");

        rule.Pattern = @"C:\Win*\System32\*.exe";
        Assert(
            SafetyPolicy.ValidateRule(rule) is not null,
            "可展开到 Windows 目录的通配规则通过了校验。 ");

        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        rule.Pattern = Path.Combine(windowsDirectory, "System32", "cmd.exe");
        Assert(
            SafetyPolicy.ValidateRule(rule) is not null,
            "Windows 系统目录内的完整路径规则通过了校验。 ");

        rule.Pattern = windowsDirectory + @"\*";
        Assert(
            SafetyPolicy.ValidateRule(rule) is not null,
            "Windows 目录通配规则通过了校验。 ");

        rule.Pattern = @"game.exe";
        Assert(
            SafetyPolicy.ValidateRule(rule) is not null,
            "缺少盘符的完整路径规则通过了校验。 ");

        rule.Pattern = @"*\Games\game.exe";
        Assert(
            SafetyPolicy.ValidateRule(rule) is not null,
            "以通配符开头的完整路径规则通过了校验。 ");
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
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(2005, "WeGameMiniLoader.std.7.06.27.1446", null)) is not null,
            "游戏平台规则未匹配带版本号的 WeGameMiniLoader。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(2006, "GameLoader", null)) is not null,
            "游戏平台规则未匹配当前DNF加载器。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(2007, "DNF", null)) is not null,
            "游戏平台规则未匹配DNF。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(2008, "crossfire", null)) is not null,
            "游戏平台规则未匹配CF。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(2009, "LeagueClientUx", null)) is not null,
            "游戏平台规则未匹配英雄联盟。 ");
        Assert(
            RuleMatcher.Match(config, new ProcessDescriptor(2010, "YuanShen", null)) is not null,
            "游戏平台规则未匹配原神。 ");
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

        var resetByOldVersion = new AppConfig
        {
            DefaultRulePresetVersion = 2
        };
        Assert(
            DefaultRulePresets.Apply(resetByOldVersion) == 4,
            "未修复旧调试复位删除的默认规则。 ");
        Assert(
            resetByOldVersion.Rules.Count == 4
                && resetByOldVersion.Rules.All(rule => !rule.Enabled),
            "修复旧调试复位后，默认规则数量或勾选状态不正确。 ");

        var versionThreeConfig = new AppConfig
        {
            DefaultRulePresetVersion = 3,
            Rules =
            [
                new BlockRule
                {
                    Name = "默认：常见游戏平台",
                    Target = RuleTarget.FileName,
                    Pattern = "WeGame.exe;WeGameClient.exe;tgp_daemon.exe",
                    Enabled = true
                }
            ]
        };
        Assert(
            DefaultRulePresets.Apply(versionThreeConfig) == 2,
            "未升级现有WeGame默认规则。 ");
        Assert(
            versionThreeConfig.Rules[0].Pattern.Contains(
                "WeGame*.exe",
                StringComparison.OrdinalIgnoreCase),
            "现有WeGame默认规则未补充通配模式。 ");
        Assert(versionThreeConfig.Rules[0].Enabled, "升级默认规则时改变了原勾选状态。 ");

        var versionFourConfig = new AppConfig
        {
            DefaultRulePresetVersion = 4,
            Rules =
            [
                new BlockRule
                {
                    Name = "默认：常见游戏平台",
                    Target = RuleTarget.FileName,
                    Pattern = "WeGame*.exe;steam.exe",
                    Enabled = true
                }
            ]
        };
        Assert(
            DefaultRulePresets.Apply(versionFourConfig) == 1,
            "未给现有默认规则补充常见游戏进程。 ");
        Assert(
            SafetyPolicy.SplitFileNamePatterns(versionFourConfig.Rules[0].Pattern)
                .Contains("GameLoader.exe", StringComparer.OrdinalIgnoreCase)
                && SafetyPolicy.SplitFileNamePatterns(versionFourConfig.Rules[0].Pattern)
                    .Contains("crossfire.exe", StringComparer.OrdinalIgnoreCase),
            "现有默认规则未补充DNF或CF进程。 ");
        Assert(versionFourConfig.Rules[0].Enabled, "扩展常见游戏规则时改变了原勾选状态。 ");
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

    private static void UnlockDelayExtension()
    {
        var config = new AppConfig { UnlockDelayMinutes = 60 };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProtectionManager.EnableAndLock(config);
        ProtectionManager.RequestUnlock(config, ProtectionManager.UnlockConfirmationText, now);
        DateTimeOffset originalDeadline = config.UnlockAvailableAtUtc!.Value;

        bool shortenRejected = false;
        try
        {
            ProtectionManager.ChangeUnlockDelay(config, 30, now.AddMinutes(5));
        }
        catch (InvalidOperationException)
        {
            shortenRejected = true;
        }

        Assert(shortenRejected, "锁定期间缩短冷静期未被拒绝。 ");
        Assert(
            config.UnlockDelayMinutes == 60 && config.UnlockAvailableAtUtc == originalDeadline,
            "被拒绝的缩短操作仍然修改了配置。 ");

        Assert(
            ProtectionManager.ChangeUnlockDelay(config, 120, now.AddMinutes(5)),
            "已申请解除时延长冷静期未报告顺延截止时间。 ");
        Assert(config.UnlockDelayMinutes == 120, "延长后的冷静期时长未保存。 ");
        Assert(
            config.UnlockAvailableAtUtc == now.AddMinutes(120),
            "延长冷静期未按申请时间同步顺延解除截止时间。 ");

        bool stillBlocked = false;
        try
        {
            ProtectionManager.CompleteUnlock(config, now.AddMinutes(90));
        }
        catch (InvalidOperationException)
        {
            stillBlocked = true;
        }

        Assert(stillBlocked, "顺延后的截止时间没有真正生效。 ");

        var idle = new AppConfig { UnlockDelayMinutes = 60 };
        Assert(
            !ProtectionManager.ChangeUnlockDelay(idle, 30, now),
            "没有解除申请时不应报告顺延。 ");
        Assert(idle.UnlockDelayMinutes == 30, "未锁定时缩短冷静期应被允许。 ");
    }

    private static void UnlockDelayUnitConversion()
    {
        Assert(
            UnlockDelayPolicy.TryConvertToMinutes(
                2,
                UnlockDelayUnit.Hours,
                out int hours)
            && hours == 120,
            "小时未正确换算为分钟。 ");
        Assert(
            UnlockDelayPolicy.TryConvertToMinutes(
                2,
                UnlockDelayUnit.Days,
                out int days)
            && days == 2 * 24 * 60,
            "天未正确换算为分钟。 ");
        Assert(
            UnlockDelayPolicy.TryConvertToMinutes(
                1.5,
                UnlockDelayUnit.Months,
                out int months)
            && months == 45 * 24 * 60,
            "月未按 30 天正确换算。 ");
        Assert(
            UnlockDelayPolicy.TryConvertToMinutes(
                12,
                UnlockDelayUnit.Months,
                out int maximum)
            && maximum == UnlockDelayPolicy.MaximumDelayMinutes,
            "12 个月上限未通过。 ");
        Assert(
            !UnlockDelayPolicy.TryConvertToMinutes(
                12.01,
                UnlockDelayUnit.Months,
                out _),
            "超过 12 个月的冷静期未被拒绝。 ");
        Assert(
            Math.Abs(
                UnlockDelayPolicy.ConvertFromMinutes(
                    45 * 24 * 60,
                    UnlockDelayUnit.Months)
                - 1.5) < 0.000001,
            "分钟未正确换算回月。 ");
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
        Assert(config.Rules.Count == 4, "调试复位未恢复四条默认规则。 ");
        Assert(config.Rules.All(rule => !rule.Enabled), "调试复位后的默认规则仍处于勾选状态。 ");
        Assert(config.Rules.All(rule => rule.Name.StartsWith("默认：", StringComparison.Ordinal)), "调试复位未删除自定义规则。 ");
        Assert(
            config.DefaultRulePresetVersion == DefaultRulePresets.CurrentVersion,
            "调试复位后的默认规则版本不正确。 ");
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

    private static void PasswordProtectedUninstall()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var configured = new AppConfig
        {
            SetupCompleted = true,
            Password = PasswordHasher.Create("correct-horse", 100_000)
        };

        UninstallPreparationResult wrongPassword =
            PasswordProtectedUninstallService.Prepare(configured, "wrong-password", now);
        Assert(!wrongPassword.Success && wrongPassword.Token is null, "错误密码生成了卸载授权。");

        UninstallPreparationResult correctPassword =
            PasswordProtectedUninstallService.Prepare(configured, "correct-horse", now);
        Assert(
            correctPassword.Success
                && correctPassword.PasswordVerified
                && !string.IsNullOrWhiteSpace(correctPassword.Token),
            "正确密码未生成卸载授权。");

        var locked = new AppConfig
        {
            SetupCompleted = true,
            ProtectionLocked = true,
            Password = PasswordHasher.Create("correct-horse", 100_000)
        };
        UninstallPreparationResult lockedResult =
            PasswordProtectedUninstallService.Prepare(locked, "correct-horse", now);
        Assert(
            !lockedResult.Success && lockedResult.ProtectionLocked && lockedResult.Token is null,
            "锁定状态下生成了卸载授权。");

        var notConfigured = new AppConfig { SetupCompleted = false };
        UninstallPreparationResult notConfiguredResult =
            PasswordProtectedUninstallService.Prepare(notConfigured, null, now);
        Assert(
            notConfiguredResult.Success
                && !notConfiguredResult.PasswordVerified
                && !string.IsNullOrWhiteSpace(notConfiguredResult.Token),
            "尚未首次设置的安装无法移除。");
    }

    private static void RuleTransferRoundTrip()
    {
        var sourceRules = new[]
        {
            new BlockRule
            {
                Name = "Steam",
                Target = RuleTarget.FileName,
                Pattern = "steam",
                Enabled = true
            },
            new BlockRule
            {
                Name = "Poki",
                Target = RuleTarget.Domain,
                Pattern = "https://poki.com/zh",
                Enabled = false
            }
        };

        string json = RuleTransferService.Export(sourceRules);
        IReadOnlyList<BlockRule> imported = RuleTransferService.Import(json);
        Assert(imported.Count == 2, "导入后的规则数量不正确。 ");
        Assert(
            imported[0].Pattern == "steam.exe"
                && imported[0].Target == RuleTarget.FileName
                && imported[0].Enabled,
            "程序规则未正确导入或规范化。 ");
        Assert(
            imported[1].Pattern == "poki.com"
                && imported[1].Target == RuleTarget.Domain
                && !imported[1].Enabled,
            "网站规则未正确导入或保留启用状态。 ");
        Assert(
            imported[0].Id != sourceRules[0].Id,
            "导入规则复用了原规则 ID。 ");

        bool invalidRejected = false;
        try
        {
            RuleTransferService.Import(
                """
                {
                  "formatVersion": 1,
                  "rules": [
                    {
                      "name": "危险规则",
                      "target": "FileName",
                      "pattern": "*",
                      "enabled": true
                    }
                  ]
                }
                """);
        }
        catch (InvalidDataException)
        {
            invalidRejected = true;
        }

        Assert(invalidRejected, "危险的导入规则未被拒绝。 ");
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

            audit.Append(new AuditEntry
            {
                EventType = "WebsiteBlocked",
                Message = "网站 example.com 已被拦截。",
                Domain = "example.com"
            });
            audit.Append(new AuditEntry
            {
                EventType = "ProcessBlocked",
                Message = "拦截失败记录不应计入累计数。",
                Success = false
            });
            AuditSnapshot snapshot = audit.ReadSnapshot(maximumCount: 2);
            Assert(snapshot.TotalBlockedCount == 2, "累计成功拦截数统计错误。 ");
            Assert(snapshot.Entries.Count == 2, "审计快照未遵守最近记录数量。 ");
            Assert(
                snapshot.Entries[0].Success == false
                    && snapshot.Entries[1].EventType == "WebsiteBlocked",
                "审计快照的最近记录顺序错误。 ");
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

    private static void AuditRotation()
    {
        string root = Path.Combine(Path.GetTempPath(), "BlockGameSelfTest", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        try
        {
            var audit = new AuditLog(paths, rotationThresholdBytes: 512);
            const int totalBlocked = 40;
            for (int index = 0; index < totalBlocked; index++)
            {
                audit.Append(new AuditEntry
                {
                    EventType = "ProcessBlocked",
                    Message = $"已阻止 test-{index}.exe。",
                    ProcessName = $"test-{index}.exe"
                });
            }

            Assert(File.Exists(paths.AuditArchiveFile), "超过阈值后审计日志未轮转出归档文件。 ");
            Assert(
                new FileInfo(paths.AuditFile).Length < 4 * 1024,
                "轮转后当前审计文件仍在无限增长。 ");

            AuditSnapshot snapshot = audit.ReadSnapshot();
            Assert(
                snapshot.TotalBlockedCount == totalBlocked,
                $"轮转后累计拦截数丢失：期望 {totalBlocked}，实际 {snapshot.TotalBlockedCount}。 ");
            Assert(snapshot.Entries.Count > 0, "轮转后读不到最近审计记录。 ");
            Assert(
                snapshot.Entries[0].Message.Contains($"test-{totalBlocked - 1}", StringComparison.Ordinal),
                "轮转后最近记录顺序错误。 ");

            string tokenBefore = audit.GetChangeToken();
            Assert(
                string.Equals(tokenBefore, audit.GetChangeToken(), StringComparison.Ordinal),
                "文件未变化时变更标记不稳定。 ");
            audit.Append(new AuditEntry
            {
                EventType = "GuardWarning",
                Message = "变更标记测试。",
                Success = false
            });
            Assert(
                !string.Equals(tokenBefore, audit.GetChangeToken(), StringComparison.Ordinal),
                "追加记录后变更标记未变化。 ");

            AuditSnapshot finalSnapshot = audit.ReadSnapshot();
            Assert(
                finalSnapshot.TotalBlockedCount == totalBlocked,
                "失败记录不应改变累计拦截数。 ");
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
