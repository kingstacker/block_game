using BlockGame.Core.Models;

namespace BlockGame.Core.Services;

public static class DefaultRulePresets
{
    public const int CurrentVersion = 1;

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

    public static int Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.DefaultRulePresetVersion >= CurrentVersion)
        {
            return 0;
        }

        int added = 0;
        added += AddIfMissing(
            config,
            "默认：常见游戏平台",
            CommonGamePlatformsPattern);
        added += AddIfMissing(
            config,
            "默认：常见影音平台",
            CommonMediaPlatformsPattern);
        config.DefaultRulePresetVersion = CurrentVersion;
        return added;
    }

    private static int AddIfMissing(AppConfig config, string name, string pattern)
    {
        string normalizedPattern = SafetyPolicy.NormalizeFileNamePattern(pattern);
        bool exists = config.Rules.Any(rule =>
            rule.Target == RuleTarget.FileName
            && (string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    SafetyPolicy.NormalizeFileNamePattern(rule.Pattern),
                    normalizedPattern,
                    StringComparison.OrdinalIgnoreCase)));
        if (exists)
        {
            return 0;
        }

        config.Rules.Add(new BlockRule
        {
            Name = name,
            Target = RuleTarget.FileName,
            Pattern = normalizedPattern,
            Enabled = false
        });
        return 1;
    }
}
