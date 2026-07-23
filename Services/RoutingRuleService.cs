using System.IO;
using System.Text.Json;
using PsiTun.Models;

namespace PsiTun.Services;

public class RoutingRuleService
{
    private readonly string _filePath;

    public RoutingRuleService(string filePath)
    {
        _filePath = filePath;
    }

    public List<RoutingRule> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var rules = JsonSerializer.Deserialize<List<RoutingRule>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return rules ?? GetDefaults();
            }
        }
        catch { }
        return GetDefaults();
    }

    public void Save(List<RoutingRule> rules)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(rules,
            new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true });
        File.WriteAllText(_filePath, json);
    }

    public static List<RoutingRule> GetDefaults()
    {
        return new List<RoutingRule>
        {
            // Force-proxy apps (bypass blocked-only for RTC/UDP)
            new() { Description = "Discord — полный прокси", MatchType = RuleMatchType.ProcessName,
                Value = "Discord.exe", Action = RuleAction.Proxy, ForceProxy = true, IsDefault = true },

            // Torrent clients → direct
            new() { Description = "qBittorrent напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "qbittorrent.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "uTorrent напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "utorrent.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "BitTorrent напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "bittorrent.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Transmission напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "transmission.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Deluge напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "deluge.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Vuze напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "vuze.exe", Action = RuleAction.Direct, IsDefault = true },

            // Game launchers → direct
            new() { Description = "Steam напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "steam.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Steam WebHelper напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "steamwebhelper.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Steam Service напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "steamservice.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Epic Games напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "epicgameslauncher.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "EA Desktop напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "eadesktop.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Origin напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "origin.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Ubisoft Connect напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "ubisoftconnect.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Battle.net напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "battlenet.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "GOG Galaxy напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "goggalaxy.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "RuneScape напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "rsilauncher.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Riot Client напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "riotclientux.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Minecraft Launcher напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "minecraftlauncher.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "TLauncher напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "tlauncher.exe", Action = RuleAction.Direct, IsDefault = true },

            // Games → direct
            new() { Description = "Warframe x64 напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "warframe.x64.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Warframe напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "warframe.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Overwatch напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "overwatch.exe", Action = RuleAction.Direct, IsDefault = true },
            new() { Description = "Destiny 2 напрямую", MatchType = RuleMatchType.ProcessName,
                Value = "destiny2.exe", Action = RuleAction.Direct, IsDefault = true },

            // Protocol-based
            new() { Description = "BitTorrent DPI напрямую", MatchType = RuleMatchType.Protocol,
                Value = "bittorrent", Action = RuleAction.Direct, IsDefault = true },

            // Geosite rules (Xray)
            new() { Description = "Реклама — блок", MatchType = RuleMatchType.Geosite,
                Value = "category-ads-all", Action = RuleAction.Block, IsDefault = true },
            new() { Description = "Шпионы Windows — блок", MatchType = RuleMatchType.Geosite,
                Value = "win-spy", Action = RuleAction.Block, IsDefault = true },
            new() { Description = "Заблокировано в RU — прокси", MatchType = RuleMatchType.Geosite,
                Value = "ru-blocked", Action = RuleAction.Proxy, IsDefault = true },
            new() { Description = "Заблокировано в RU (расш.) — прокси", MatchType = RuleMatchType.Geosite,
                Value = "ru-blocked-all", Action = RuleAction.Proxy, IsDefault = true },
            new() { Description = "Только из RU — напрямую", MatchType = RuleMatchType.Geosite,
                Value = "ru-available-only-inside", Action = RuleAction.Direct, IsDefault = true },
        };
    }
}
