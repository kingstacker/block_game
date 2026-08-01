using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
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
            ("快捷方式生成规则", ShortcutRuleFromLnk),
            ("开机静默启动参数", AutoStartArguments),
            ("预览与严格模式状态机", ProtectionModes),
            ("协商模式临时放行", NegotiationTemporaryRelease),
            ("旧配置默认严格模式", LegacyConfigDefaultsToStrict),
            ("恢复默认设置", RestoreDefaults),
            ("冷静期单位换算", UnlockDelayUnitConversion),
            ("解除冷静期", UnlockCooldown),
            ("锁定期间延长冷静期", UnlockDelayExtension),
            ("一次性卸载令牌", UninstallToken),
            ("密码保护卸载授权", PasswordProtectedUninstall),
            ("配置和审计持久化", Persistence),
            ("诊断日志导出", DiagnosticLogExport),
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

    private static void ShortcutRuleFromLnk()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "ShortcutRuleSelfTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string targetPath = Path.Combine(root, "Demo Game.exe");
            string shortcutPath = Path.Combine(root, "演示游戏.lnk");
            File.WriteAllBytes(targetPath, [0x4D, 0x5A]);
            CreateWindowsShortcut(shortcutPath, targetPath, "--profile preview", root);

            ShortcutTargetInfo target = ShortcutTargetResolver.Resolve(shortcutPath);
            Assert(
                string.Equals(
                    Path.GetFullPath(target.TargetPath),
                    Path.GetFullPath(targetPath),
                    StringComparison.OrdinalIgnoreCase),
                "快捷方式目标 EXE 解析错误。 ");
            Assert(
                target.Arguments.Contains("--profile preview", StringComparison.Ordinal),
                "快捷方式启动参数未读取。 ");

            BlockRule rule = ShortcutRuleFactory.CreateRule(target);
            Assert(rule.Name == "演示游戏", "生成的规则名称没有使用快捷方式名称。 ");
            Assert(rule.Target == RuleTarget.FullPath, "快捷方式没有生成完整路径规则。 ");
            Assert(rule.Enabled, "快捷方式生成的规则未默认启用。 ");
            Assert(
                string.Equals(rule.Pattern, targetPath, StringComparison.OrdinalIgnoreCase),
                "快捷方式生成的规则路径错误。 ");

            string textTarget = Path.Combine(root, "readme.txt");
            File.WriteAllText(textTarget, "test");
            bool nonExecutableRejected = false;
            try
            {
                ShortcutRuleFactory.CreateRule(
                    new ShortcutTargetInfo(shortcutPath, textTarget, string.Empty, root));
            }
            catch (InvalidDataException)
            {
                nonExecutableRejected = true;
            }

            Assert(nonExecutableRejected, "非 EXE 快捷方式目标仍生成了软件规则。 ");
        }
        finally
        {
            string fullRoot = Path.GetFullPath(root);
            string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ShortcutRuleSelfTest"));
            if (fullRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
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

    private static void AutoStartArguments()
    {
        Assert(
            StartupArguments.IsAutoStart(["--autostart"]),
            "未识别标准静默启动参数。 ");
        Assert(
            StartupArguments.IsAutoStart(["--AUTOSTART"]),
            "静默启动参数未忽略大小写。 ");
        Assert(
            !StartupArguments.IsAutoStart(["--other"]),
            "无关参数被误识别为静默启动。 ");
        string executable = Path.Combine(Path.GetTempPath(), "Block Game", "BlockGame.App.exe");
        string expected = $"\"{Path.GetFullPath(executable)}\" --autostart";
        Assert(
            string.Equals(
                StartupArguments.BuildAutoStartCommand(executable),
                expected,
                StringComparison.Ordinal),
            "计划任务的静默启动命令格式错误。 ");
    }

    private static void ProtectionModes()
    {
        var config = new AppConfig
        {
            UnlockDelayMinutes = 60,
            Rules = [new BlockRule { Name = "Test", Pattern = "test.exe", Enabled = true }]
        };
        Assert(config.ProtectionMode == ProtectionMode.Strict, "新配置没有默认使用严格模式。 ");

        ProtectionManager.ChangeMode(config, ProtectionMode.Preview);
        ProtectionManager.EnablePreview(config);
        Assert(
            config.ProtectionMode == ProtectionMode.Preview
                && config.ProtectionEnabled
                && !config.ProtectionLocked,
            "预览屏蔽未能在不锁定的情况下启用。 ");

        ProtectionManager.DisableProtection(config);
        Assert(!config.ProtectionEnabled, "预览屏蔽无法立即暂停。 ");
        ProtectionManager.EnablePreview(config);

        ProtectionManager.ChangeMode(config, ProtectionMode.Strict);
        Assert(
            config.ProtectionMode == ProtectionMode.Strict
                && config.ProtectionEnabled
                && !config.ProtectionLocked,
            "从预览切回严格模式时不应未经确认就自动锁定或暂停。 ");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProtectionManager.EnableAndLock(config);
        bool lockedModeChangeRejected = false;
        try
        {
            ProtectionManager.ChangeMode(config, ProtectionMode.Preview);
        }
        catch (InvalidOperationException)
        {
            lockedModeChangeRejected = true;
        }

        Assert(lockedModeChangeRejected, "严格模式锁定后仍能切换到预览模式。 ");
        Assert(
            config.ProtectionMode == ProtectionMode.Strict && config.ProtectionLocked,
            "被拒绝的模式切换仍修改了严格锁定状态。 ");

        ProtectionManager.RequestUnlock(config, ProtectionManager.UnlockConfirmationText, now);
        ProtectionManager.CompleteUnlock(config, now.AddMinutes(60));
        ProtectionManager.ChangeMode(config, ProtectionMode.Preview);
        Assert(
            config.ProtectionMode == ProtectionMode.Preview && !config.ProtectionLocked,
            "冷静期结束并完成解除后仍无法切换到预览模式。 ");
    }

    private static void NegotiationTemporaryRelease()
    {
        Assert(
            TemporaryReleasePolicy.TryConvertToMinutes(
                1.5,
                TemporaryReleaseUnit.Hours,
                out int convertedMinutes)
            && convertedMinutes == 90,
            "协商模式放行时长单位换算不正确。 ");
        Assert(
            !TemporaryReleasePolicy.TryConvertToMinutes(
                25,
                TemporaryReleaseUnit.Hours,
                out _),
            "协商模式允许了超过 24 小时的放行时长。 ");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var rule = new BlockRule
        {
            Name = "Test Game",
            Target = RuleTarget.FileName,
            Pattern = "game.exe",
            Enabled = true
        };
        var config = new AppConfig
        {
            ProtectionMode = ProtectionMode.Negotiation,
            Rules = [rule]
        };

        ProtectionManager.EnableAndLock(config);
        Assert(
            config.ProtectionMode == ProtectionMode.Negotiation
                && config.ProtectionEnabled
                && config.ProtectionLocked,
            "协商模式启用锁定时被错误切回严格模式。 ");

        DateTimeOffset allowedUntil = ProtectionManager.GrantTemporaryRelease(
            config,
            rule.Id,
            45,
            now);
        Assert(allowedUntil == now.AddMinutes(45), "临时放行截止时间不正确。 ");
        Assert(config.NegotiationDefaultReleaseMinutes == 45, "临时放行时长没有记住。 ");
        Assert(
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(2001, "game.exe", null),
                now.AddMinutes(44)) is null,
            "临时放行期间软件仍被规则命中。 ");
        Assert(
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(2002, "game.exe", null),
                now.AddMinutes(46)) is not null,
            "临时放行到期后软件仍被放行。 ");

        Assert(
            ProtectionManager.RevokeTemporaryRelease(config, rule.Id),
            "无法主动收回临时放行。 ");
        Assert(
            RuleMatcher.Match(
                config,
                new ProcessDescriptor(2003, "game.exe", null),
                now.AddMinutes(1)) is not null,
            "主动收回临时放行后规则没有恢复。 ");

        var strictConfig = new AppConfig
        {
            ProtectionMode = ProtectionMode.Strict,
            ProtectionEnabled = true,
            Rules = [new BlockRule { Name = "Strict", Pattern = "strict.exe" }]
        };
        bool strictRejected = false;
        try
        {
            ProtectionManager.GrantTemporaryRelease(
                strictConfig,
                strictConfig.Rules[0].Id,
                30,
                now);
        }
        catch (InvalidOperationException)
        {
            strictRejected = true;
        }
        Assert(strictRejected, "严格模式错误地允许临时放行。 ");

        var domainRule = new BlockRule
        {
            Name = "Website",
            Target = RuleTarget.Domain,
            Pattern = "example.com"
        };
        var domainConfig = new AppConfig
        {
            ProtectionMode = ProtectionMode.Negotiation,
            ProtectionEnabled = true,
            Rules = [domainRule]
        };
        bool domainRejected = false;
        try
        {
            ProtectionManager.GrantTemporaryRelease(
                domainConfig,
                domainRule.Id,
                30,
                now);
        }
        catch (InvalidOperationException)
        {
            domainRejected = true;
        }
        Assert(domainRejected, "网站规则错误地允许临时放行。 ");

        config.ProtectionLocked = false;
        ProtectionManager.GrantTemporaryRelease(config, rule.Id, 30, now);
        ProtectionManager.ChangeMode(config, ProtectionMode.Strict);
        Assert(
            rule.TemporarilyAllowedUntilUtc is null,
            "离开协商模式后没有清除临时放行。 ");
    }

    private static void LegacyConfigDefaultsToStrict()
    {
        string root = Path.Combine(Path.GetTempPath(), "BlockGameSelfTest", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        try
        {
            paths.EnsureDirectory();
            File.WriteAllText(
                paths.ConfigFile,
                """
                {
                  "SchemaVersion": 1,
                  "ProtectionEnabled": false,
                  "ProtectionLocked": false,
                  "Rules": []
                }
                """);
            AppConfig legacy = new ConfigStore(paths).Load();
            Assert(
                legacy.ProtectionMode == ProtectionMode.Strict,
                "缺少模式字段的旧配置没有迁移为严格模式。 ");

            File.WriteAllText(
                paths.ConfigFile,
                """
                {
                  "SchemaVersion": 1,
                  "ProtectionMode": "Preview",
                  "ProtectionEnabled": true,
                  "ProtectionLocked": true,
                  "Rules": []
                }
                """);
            AppConfig inconsistent = new ConfigStore(paths).Load();
            Assert(
                inconsistent.ProtectionMode == ProtectionMode.Strict,
                "锁定的预览配置没有被规范为严格模式。 ");

            File.WriteAllText(
                paths.ConfigFile,
                """
                {
                  "SchemaVersion": 1,
                  "ProtectionMode": "Negotiation",
                  "ProtectionEnabled": true,
                  "ProtectionLocked": true,
                  "Rules": []
                }
                """);
            AppConfig negotiation = new ConfigStore(paths).Load();
            Assert(
                negotiation.ProtectionMode == ProtectionMode.Negotiation,
                "锁定的协商模式被错误迁移为严格模式。 ");
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

    private static void RestoreDefaults()
    {
        PasswordCredential password = PasswordHasher.Create("keep-password", 100_000);
        var config = new AppConfig
        {
            SetupCompleted = true,
            Password = password,
            ProtectionMode = ProtectionMode.Preview,
            ProtectionEnabled = true,
            ProtectionLocked = false,
            UnlockDelayMinutes = 60,
            NegotiationDefaultReleaseMinutes = 90,
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

        ProtectionManager.RestoreDefaults(config);

        Assert(!config.ProtectionEnabled, "恢复默认设置后仍在拦截。 ");
        Assert(!config.ProtectionLocked, "恢复默认设置后仍处于锁定。 ");
        Assert(config.ProtectionMode == ProtectionMode.Strict, "恢复默认设置未切回严格模式。 ");
        Assert(config.UnlockDelayMinutes == 24 * 60, "恢复默认设置未还原 24 小时冷静期。 ");
        Assert(
            config.NegotiationDefaultReleaseMinutes == TemporaryReleasePolicy.DefaultDurationMinutes,
            "恢复默认设置未还原协商模式默认放行时长。 ");
        Assert(config.UnlockRequestedAtUtc is null && config.UnlockAvailableAtUtc is null, "恢复默认设置未清除解除申请。 ");
        Assert(config.UninstallTokenHashBase64 is null && config.UninstallAuthorizedUntilUtc is null, "恢复默认设置未清除卸载授权。 ");
        Assert(config.PasswordThrottle.ConsecutiveFailures == 0, "恢复默认设置未清除密码限流。 ");
        Assert(config.Rules.Count == 4, "恢复默认设置未恢复四条默认规则。 ");
        Assert(config.Rules.All(rule => !rule.Enabled), "恢复默认设置后的默认规则仍处于勾选状态。 ");
        Assert(config.Rules.All(rule => rule.Name.StartsWith("默认：", StringComparison.Ordinal)), "恢复默认设置未删除自定义规则。 ");
        Assert(config.SetupCompleted, "恢复默认设置错误地清除了首次配置状态。 ");
        Assert(ReferenceEquals(config.Password, password), "恢复默认设置错误地替换了管理密码。 ");
        Assert(
            config.DefaultRulePresetVersion == DefaultRulePresets.CurrentVersion,
            "恢复默认设置后的默认规则版本不正确。 ");

        var lockedConfig = new AppConfig
        {
            ProtectionEnabled = true,
            ProtectionLocked = true,
            Rules = [new BlockRule { Name = "Locked", Pattern = "locked.exe" }]
        };
        bool lockedRejected = false;
        try
        {
            ProtectionManager.RestoreDefaults(lockedConfig);
        }
        catch (InvalidOperationException)
        {
            lockedRejected = true;
        }

        Assert(lockedRejected, "锁定期间仍可恢复默认设置。 ");
        Assert(
            lockedConfig.ProtectionEnabled
                && lockedConfig.ProtectionLocked
                && lockedConfig.Rules.Count == 1,
            "拒绝锁定状态的恢复操作时仍修改了配置。 ");
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

    private static void DiagnosticLogExport()
    {
        string root = Path.Combine(Path.GetTempPath(), "BlockGameSelfTest", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        try
        {
            var audit = new AuditLog(paths);
            audit.Append(new AuditEntry
            {
                EventType = "DiagnosticTest",
                Message = "诊断导出测试记录。",
                Success = true
            });
            new HeartbeatStore(paths).Write(new GuardHeartbeat
            {
                ProcessId = 1234,
                Mode = "SelfTest"
            });

            string exportFile = Path.Combine(root, "diagnostics.zip");
            IReadOnlyList<string> included = DiagnosticLogExporter.Export(
                paths,
                exportFile,
                "SummaryValue=诊断测试");

            Assert(File.Exists(exportFile), "诊断日志 ZIP 未生成。 ");
            Assert(included.Contains("audit.jsonl"), "诊断日志未包含当前审计文件。 ");
            Assert(included.Contains("guard-heartbeat.json"), "诊断日志未包含守护心跳。 ");
            Assert(included.Contains("diagnostics.txt"), "诊断日志未包含摘要。 ");

            using ZipArchive archive = ZipFile.OpenRead(exportFile);
            ZipArchiveEntry? auditEntry = archive.GetEntry("audit.jsonl");
            ZipArchiveEntry? summaryEntry = archive.GetEntry("diagnostics.txt");
            Assert(auditEntry is not null, "ZIP 中找不到审计日志。 ");
            Assert(summaryEntry is not null, "ZIP 中找不到诊断摘要。 ");
            using var auditReader = new StreamReader(auditEntry!.Open());
            using var summaryReader = new StreamReader(summaryEntry!.Open());
            Assert(
                auditReader.ReadToEnd().Contains("DiagnosticTest", StringComparison.Ordinal),
                "导出的审计日志内容不正确。 ");
            Assert(
                summaryReader.ReadToEnd().Contains("SummaryValue=诊断测试", StringComparison.Ordinal),
                "导出的诊断摘要内容不正确。 ");
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

    private static void CreateWindowsShortcut(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("测试环境无法创建 WScript.Shell。 ");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("测试环境无法启动 WScript.Shell。 ");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath])
                ?? throw new InvalidOperationException("测试快捷方式创建失败。 ");
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.SetProperty,
                binder: null,
                target: shortcut,
                args: [targetPath]);
            shortcutType.InvokeMember(
                "Arguments",
                BindingFlags.SetProperty,
                binder: null,
                target: shortcut,
                args: [arguments]);
            shortcutType.InvokeMember(
                "WorkingDirectory",
                BindingFlags.SetProperty,
                binder: null,
                target: shortcut,
                args: [workingDirectory]);
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shortcut,
                args: null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
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
