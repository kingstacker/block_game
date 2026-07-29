using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class DefaultRulePresets
{
    public const int CurrentVersion = 2;

    public const string CommonGamePlatformsPattern =
        "WeGame.exe;WeGameClient.exe;tgp_daemon.exe;"
        + "steam.exe;steamwebhelper.exe;steamchina.exe;"
        + "EpicGamesLauncher.exe;Battle.net.exe;"
        + "EADesktop.exe;EALauncher.exe;UbisoftConnect.exe;"
        + "RiotClientServices.exe;RiotClientUx.exe;"
        + "GalaxyClient.exe;GOGGalaxy.exe;XboxPcApp.exe;"
        + "MuMuPlayer.exe;NemuPlayer.exe;dnplayer.exe;LDPlayer.exe;AndroidEmulatorEx.exe";

    public const string CommonMediaPlatformsPattern =
        "QQLive.exe;QyClient.exe;IQIYI Video.exe;"
        + "Youku.exe;YoukuClient.exe;"
        + "bilibili.exe;哔哩哔哩.exe;Douyin.exe;"
        + "QQMusic.exe;cloudmusic.exe;KuGou.exe;KwMusic.exe;"
        + "MiguMusic.exe;SodaMusic.exe;Spotify.exe";

    public const string CommonGameWebsitesPattern =
        "wegame.com.cn;steampowered.com;steamcommunity.com;steamchina.com;"
        + "epicgames.com;battle.net;xbox.com;playstation.com;nintendo.com.hk;"
        + "taptap.cn;4399.com;7k7k.com;biligame.com;"
        + "game.qq.com;game.163.com;miyoushe.com;wanmei.com;37.com";

    public const string CommonMediaWebsitesPattern =
        "iqiyi.com;v.qq.com;youku.com;bilibili.com;mgtv.com;"
        + "douyin.com;kuaishou.com;ixigua.com;haokan.baidu.com;"
        + "huya.com;douyu.com;acfun.cn;le.com;pptv.com;tv.sohu.com;"
        + "music.163.com;y.qq.com;qqmusic.qq.com;kugou.com;kuwo.cn;"
        + "music.migu.cn;ximalaya.com;qingting.fm;spotify.com";

    public static int Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.DefaultRulePresetVersion >= CurrentVersion)
        {
            return 0;
        }

        int added = 0;
        if (config.DefaultRulePresetVersion < 1)
        {
            added += AddIfMissing(
                config,
                "默认：常见游戏平台",
                RuleTarget.FileName,
                CommonGamePlatformsPattern);
            added += AddIfMissing(
                config,
                "默认：常见影音平台",
                RuleTarget.FileName,
                CommonMediaPlatformsPattern);
        }

        if (config.DefaultRulePresetVersion < 2)
        {
            added += AddIfMissing(
                config,
                "默认：常见游戏网站",
                RuleTarget.Domain,
                CommonGameWebsitesPattern);
            added += AddIfMissing(
                config,
                "默认：常见影音网站",
                RuleTarget.Domain,
                CommonMediaWebsitesPattern);
        }

        config.DefaultRulePresetVersion = CurrentVersion;
        return added;
    }

    private static int AddIfMissing(
        AppConfig config,
        string name,
        RuleTarget target,
        string pattern)
    {
        string normalizedPattern = NormalizePattern(target, pattern);
        bool exists = config.Rules.Any(rule =>
            rule.Target == target
            && (string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    NormalizePattern(target, rule.Pattern),
                    normalizedPattern,
                    StringComparison.OrdinalIgnoreCase)));
        if (exists)
        {
            return 0;
        }

        config.Rules.Add(new BlockRule
        {
            Name = name,
            Target = target,
            Pattern = normalizedPattern,
            Enabled = false
        });
        return 1;
    }

    private static string NormalizePattern(RuleTarget target, string pattern)
        => target switch
        {
            RuleTarget.Domain => WebsiteDomainRules.NormalizePattern(pattern),
            RuleTarget.FileName => SafetyPolicy.NormalizeFileNamePattern(pattern),
            _ => pattern.Trim()
        };
}
