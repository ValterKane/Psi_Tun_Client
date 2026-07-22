using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsiTun.Models;

namespace PsiTun.Services;

/// <summary>
/// Generates sing-box config.json for TUN + DNS + routing.
/// Structure matches V2RayN's working sing-box config (v2rayN_working_config_2.json).
/// sing-box captures traffic via TUN (gvisor), resolves DNS, and forwards to Xray SOCKS.
/// </summary>
public static class SingBoxConfigGenerator
{
    private static readonly Dictionary<string, string[]> DnsHosts = new()
    {
        ["common.dot.dns.yandex.net"] = ["77.88.8.8", "77.88.8.1"],
        ["dns.google"] = ["8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"],
        ["dns.alidns.com"] = ["223.5.5.5", "223.6.6.6", "2400:3200::1", "2400:3200:baba::1"],
        ["one.one.one.one"] = ["1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"],
        ["1dot1dot1dot1.cloudflare-dns.com"] = ["1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"],
        ["cloudflare-dns.com"] = ["104.16.249.249", "104.16.248.249", "2606:4700::6810:f8f9", "2606:4700::6810:f9f9"],
        ["dns.cloudflare.com"] = ["162.159.61.8", "172.64.41.8", "2a06:98c1:52::8", "2803:f800:53::8"],
        ["dot.pub"] = ["1.12.12.12", "120.53.53.53"],
        ["doh.pub"] = ["1.12.12.12", "120.53.53.53"],
        ["dns.quad9.net"] = ["9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9"],
        ["dns.yandex.net"] = ["77.88.8.8", "77.88.8.1", "2a02:6b8::feed:0ff", "2a02:6b8:0:1::feed:0ff"],
        ["dns.sb"] = ["45.11.45.11", "185.222.222.222", "2a09::", "2a11::"],
        ["dns.umbrella.com"] = ["208.67.220.220", "208.67.222.222", "2620:119:35::35", "2620:119:53::53"],
        ["dns.sse.cisco.com"] = ["208.67.220.220", "208.67.222.222", "2620:119:35::35", "2620:119:53::53"],
        ["engage.cloudflareclient.com"] = ["162.159.192.1", "2606:4700:d0::a29f:c001"]
    };

    public static string Generate(SettingsService settings,
        List<VpnServer> servers, int selectedIndex)
    {
        var server = selectedIndex < servers.Count ? servers[selectedIndex] : null;
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var xrayPath = Path.Combine(baseDir, "xray", "xray.exe");
        var cachePath = Path.Combine(baseDir, "cache.db");

        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "warn",
                ["timestamp"] = true
            },
            ["dns"] = BuildDnsConfig(server),
            ["inbounds"] = BuildInbounds(settings),
            ["outbounds"] = BuildOutbounds(settings),
            ["endpoints"] = new JsonArray(),
            ["route"] = BuildRouteConfig(xrayPath, cachePath, baseDir),
            ["experimental"] = new JsonObject
            {
                ["cache_file"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["path"] = cachePath,
                    ["store_fakeip"] = false
                }
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildDnsConfig(VpnServer? server)
    {
        // Predefined hosts — same as V2RayN
        var predefinedHosts = new JsonObject();
        foreach (var (domain, ips) in DnsHosts)
        {
            var ipArray = new JsonArray();
            foreach (var ip in ips) ipArray.Add(ip);
            predefinedHosts[domain] = ipArray;
        }

        var servers = new JsonArray
        {
            // local_local — bootstrap DNS (IP-based, no circular dep)
            new JsonObject
            {
                ["server"] = "1.1.1.1",
                ["type"] = "udp",
                ["tag"] = "local_local"
            },
            new JsonObject
            {
                ["server"] = "77.88.8.8",
                ["type"] = "udp",
                ["tag"] = "local_local"
            },
            new JsonObject
            {
                ["server"] = "8.8.8.8",
                ["type"] = "udp",
                ["tag"] = "local_local"
            },
            // yandex_dns — Яндекс UDP для RU-only сайтов (напрямую)
            new JsonObject
            {
                ["server"] = "77.88.8.8",
                ["type"] = "udp",
                ["tag"] = "yandex_dns"
            },
            // yandex_doh — Яндекс DoH, fallback если UDP заблокирован
            new JsonObject
            {
                ["server"] = "https://common.dot.dns.yandex.net/dns-query",
                ["domain_resolver"] = "local_local",
                ["type"] = "https",
                ["tag"] = "yandex_doh"
            },
            // direct_dns — пул прямых UDP DNS (fallback между серверами)
            new JsonObject
            {
                ["server"] = "1.1.1.1",
                ["domain_resolver"] = "local_local",
                ["type"] = "udp",
                ["tag"] = "direct_dns"
            },
            new JsonObject
            {
                ["server"] = "8.8.8.8",
                ["domain_resolver"] = "local_local",
                ["type"] = "udp",
                ["tag"] = "direct_dns"
            },
            new JsonObject
            {
                ["server"] = "77.88.8.8",
                ["domain_resolver"] = "local_local",
                ["type"] = "udp",
                ["tag"] = "direct_dns"
            },
            // direct_doh — Cloudflare DoH, fallback если UDP заблокирован
            new JsonObject
            {
                ["server"] = "https://cloudflare-dns.com/dns-query",
                ["domain_resolver"] = "local_local",
                ["type"] = "https",
                ["tag"] = "direct_doh"
            },
            // remote_dns — 8.8.8.8 через прокси (для заблокированных)
            new JsonObject
            {
                ["server"] = "8.8.8.8",
                ["domain_resolver"] = "local_local",
                ["type"] = "udp",
                ["tag"] = "remote_dns",
                ["detour"] = "proxy"
            },
            // hosts_dns — предзаполненные hostname→IP
            new JsonObject
            {
                ["predefined"] = predefinedHosts,
                ["type"] = "hosts",
                ["tag"] = "hosts_dns"
            }
        };

        var rules = new JsonArray
        {
            // Hosts first (ip_accept_any)
            new JsonObject
            {
                ["server"] = "hosts_dns",
                ["ip_accept_any"] = true
            }
        };

        // Resolve VPN server domain via direct_dns (77.88.8.8)
        if (server != null && !string.IsNullOrEmpty(server.Address))
        {
            var serverDomains = new JsonArray { server.Address };
            rules.Add(new JsonObject
            {
                ["server"] = "direct_dns",
                ["domain"] = serverDomains
            });
        }

        // RU-only сайты → Яндекс DNS (напрямую, быстро)
        rules.Add(new JsonObject
        {
            ["server"] = "yandex_dns",
            ["rule_set"] = new JsonArray { "geosite-ru-available-only-inside" }
        });

        // Реклама + Win-шпионы → NXDOMAIN (блок на уровне DNS)
        rules.Add(new JsonObject
        {
            ["rule_set"] = new JsonArray { "geosite-category-ads-all", "geosite-win-spy" },
            ["action"] = "predefined",
            ["rcode"] = "NXDOMAIN"
        });

        // Приватные домены → напрямую (direct_dns)
        rules.Add(new JsonObject
        {
            ["server"] = "direct_dns",
            ["rule_set"] = new JsonArray { "geosite-private" }
        });

        // Query type 64/65 → NOERROR (DNS rebinding prevention)
        rules.Add(new JsonObject
        {
            ["action"] = "predefined",
            ["rcode"] = "NOERROR",
            ["query_type"] = new JsonArray { 64, 65 }
        });

        return new JsonObject
        {
            ["servers"] = servers,
            ["rules"] = rules,
            ["final"] = "direct_dns",
            ["independent_cache"] = true,
            ["strategy"] = "prefer_ipv4"
        };
    }

    private static bool IsWiFiActive()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(ni => ni.OperationalStatus == OperationalStatus.Up &&
                           ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }
        catch { return false; }
    }

    private static JsonArray BuildInbounds(SettingsService s)
    {
        var inbounds = new JsonArray();
        var isWiFi = IsWiFiActive();

        // TUN inbound — gvisor stack (no admin needed), matches V2RayN
        if (s.UseTun)
        {
            var address = new JsonArray { s.TunAddress };
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun",
                ["interface_name"] = "singbox_tun",
                ["address"] = address,
                ["mtu"] = 9000,
                ["auto_route"] = true,
                ["strict_route"] = !isWiFi,
                ["stack"] = "mixed"
            });
        }

        // SOCKS inbound — user-facing proxy port
        inbounds.Add(new JsonObject
        {
            ["type"] = "socks",
            ["tag"] = "socks-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = s.SocksPort
        });

        // HTTP inbound — user-facing proxy port
        inbounds.Add(new JsonObject
        {
            ["type"] = "http",
            ["tag"] = "http-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = s.HttpPort
        });

        return inbounds;
    }

    private static JsonArray BuildOutbounds(SettingsService s)
    {
        return new JsonArray
        {
            // Proxy → Xray SOCKS (version 5)
            new JsonObject
            {
                ["server"] = "127.0.0.1",
                ["server_port"] = s.XrayInboundPort,
                ["version"] = "5",
                ["type"] = "socks",
                ["tag"] = "proxy"
            },
            // Direct
            new JsonObject
            {
                ["type"] = "direct",
                ["tag"] = "direct"
            }
        };
    }

    private static JsonObject BuildRouteConfig(string xrayPath, string cachePath, string baseDir)
    {
        var xrayExePath = xrayPath; // JsonSerializer handles escaping

        var rules = new JsonArray
        {
            // Xray's own DNS → direct (not hijacked, breaks the circular loop)
            new JsonObject
            {
                ["port"] = new JsonArray { 53 },
                ["process_path"] = new JsonArray { xrayExePath },
                ["outbound"] = "direct"
            },
            // Xray process traffic goes direct (no loop)
            new JsonObject
            {
                ["outbound"] = "direct",
                ["process_path"] = new JsonArray { xrayExePath }
            },
            // Sniff for protocol detection
            new JsonObject { ["action"] = "sniff" },
            // DNS hijack — logical OR of port 53 + protocol dns
            new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "or",
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["port"] = new JsonArray { 53 } },
                    new JsonObject { ["protocol"] = new JsonArray { "dns" } }
                },
                ["action"] = "hijack-dns"
            },
            // Реклама + Win-шпионы → reject (на уровне TUN)
            new JsonObject
            {
                ["rule_set"] = new JsonArray { "geosite-category-ads-all", "geosite-win-spy" },
                ["action"] = "reject"
            },
            // Discord → proxy always (before ip_is_private)
            new JsonObject
            {
                ["outbound"] = "proxy",
                ["process_name"] = new JsonArray { "discord.exe" }
            },
            // BitTorrent protocol → direct (DPI, catches all clients)
            new JsonObject
            {
                ["outbound"] = "direct",
                ["protocol"] = new JsonArray { "bittorrent" }
            },
            // P2P/Torrent/Steam/Launchers → direct (high-volume, no proxy needed)
            new JsonObject
            {
                ["outbound"] = "direct",
                ["process_name"] = new JsonArray
                {
                    "qbittorrent.exe", "utorrent.exe", "bittorrent.exe",
                    "transmission.exe", "deluge.exe", "vuze.exe",
                    "steam.exe", "steamwebhelper.exe", "steamservice.exe",
                    "epicgameslauncher.exe", "eadesktop.exe", "origin.exe",
                    "ubisoftconnect.exe", "battlenet.exe", "goggalaxy.exe",
                    "rsilauncher.exe", "riotclientux.exe",
                    "minecraftlauncher.exe", "tlauncher.exe",
                    "warframe.x64.exe", "warframe.exe",
                    "overwatch.exe", "destiny2.exe"
                }
            },
            // Private IPs → direct
            new JsonObject
            {
                ["outbound"] = "direct",
                ["ip_is_private"] = true
            },
            // Приватные домены → direct (rule_set)
            new JsonObject
            {
                ["outbound"] = "direct",
                ["rule_set"] = new JsonArray { "geosite-private" }
            },
            // RU-only сайты → direct (не идут в VPN)
            new JsonObject
            {
                ["outbound"] = "direct",
                ["rule_set"] = new JsonArray { "geosite-ru-available-only-inside" }
            },
            // TCP/UDP catch-all → proxy (всё остальное в xray)
            new JsonObject
            {
                ["outbound"] = "proxy",
                ["port_range"] = new JsonArray { "0:65535" }
            }
        };

        var route = new JsonObject
        {
            ["default_domain_resolver"] = new JsonObject { ["server"] = "direct_dns" },
            ["auto_detect_interface"] = true,
            ["rules"] = rules,
            ["final"] = "proxy",
            ["rule_set"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "local",
                    ["tag"] = "geosite-ru-available-only-inside",
                    ["path"] = Path.Combine(baseDir, "sing-box", "rules", "rule-set-geosite",
                        "geosite-ru-available-only-inside.srs"),
                    ["format"] = "binary"
                },
                new JsonObject
                {
                    ["type"] = "local",
                    ["tag"] = "geosite-category-ads-all",
                    ["path"] = Path.Combine(baseDir, "sing-box", "rules", "rule-set-geosite",
                        "geosite-category-ads-all.srs"),
                    ["format"] = "binary"
                },
                new JsonObject
                {
                    ["type"] = "local",
                    ["tag"] = "geosite-win-spy",
                    ["path"] = Path.Combine(baseDir, "sing-box", "rules", "rule-set-geosite",
                        "geosite-win-spy.srs"),
                    ["format"] = "binary"
                },
                new JsonObject
                {
                    ["type"] = "local",
                    ["tag"] = "geosite-private",
                    ["path"] = Path.Combine(baseDir, "sing-box", "rules", "rule-set-geosite",
                        "geosite-private.srs"),
                    ["format"] = "binary"
                }
            }
        };

        return route;
    }
}
