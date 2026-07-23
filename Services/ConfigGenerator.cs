using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsiTun.Models;

namespace PsiTun.Services;

/// <summary>
/// Generates Xray config.json — proxy with DNS + routing.
/// Structure matches V2RayN's working Xray config (v2rayN_working_config_example.json).
/// Xray acts as upstream proxy: receives from sing-box via SOCKS, forwards to VPN server.
/// </summary>
public static class ConfigGenerator
{
    private const string ProxyTag = "proxy";
    private const string DirectTag = "direct";
    private const string BlockTag = "block";

    private static readonly Dictionary<string, string[]> DnsHosts = new()
    {
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

    public static string Generate(List<VpnServer> servers,
        int selectedIndex = 0, SettingsService? settings = null,
        List<RoutingRule>? customRules = null)
    {
        settings ??= App.Settings;

        if (selectedIndex >= servers.Count) selectedIndex = 0;
        var server = servers[selectedIndex];

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["dns"] = BuildDnsConfig(server),
            ["inbounds"] = BuildInbounds(settings),
            ["outbounds"] = new JsonArray
            {
                BuildServerOutbound(server),
                new JsonObject { ["tag"] = DirectTag, ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = BlockTag, ["protocol"] = "blackhole" }
            },
            ["routing"] = BuildRoutingConfig(customRules)
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildDnsConfig(VpnServer server)
    {
        // Hosts — predefined DNS provider IPs
        var hosts = new JsonObject();
        foreach (var (domain, ips) in DnsHosts)
        {
            var ipArray = new JsonArray();
            foreach (var ip in ips) ipArray.Add(ip);
            hosts[domain] = ipArray;
        }

        var servers = new JsonArray
        {
            // Direct DNS for server domain resolution
            new JsonObject
            {
                ["address"] = "1.1.1.1",
                ["domains"] = new JsonArray { server.Address },
                ["skipFallback"] = true,
                ["tag"] = "direct-dns-1"
            },
            // Direct DNS for private geosite
            new JsonObject
            {
                ["address"] = "1.1.1.1",
                ["domains"] = new JsonArray { "geosite:private" },
                ["skipFallback"] = true,
                ["tag"] = "direct-dns-2"
            },
            // Fallback DNS servers
            "8.8.8.8",
            "https://dns.google/dns-query",
            "1.1.1.1"
        };

        return new JsonObject
        {
            ["hosts"] = hosts,
            ["servers"] = servers,
            ["tag"] = "dns-module"
        };
    }

    private static JsonArray BuildInbounds(SettingsService s)
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "socks-in",
                ["protocol"] = "socks",
                ["listen"] = "127.0.0.1",
                ["port"] = s.XrayInboundPort,
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray { "http", "tls" },
                    ["routeOnly"] = false
                },
                ["settings"] = new JsonObject
                {
                    ["auth"] = "noauth",
                    ["udp"] = true,
                    ["allowTransparent"] = false
                }
            }
        };
    }

    private static JsonObject BuildRoutingConfig(List<RoutingRule>? customRules = null)
    {
        var rules = new JsonArray
        {
            // 1. Block ads + Win telemetry
            new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = BlockTag,
                ["domain"] = new JsonArray { "geosite:category-ads-all", "geosite:win-spy" }
            },
        };

        // Insert custom domain/ip/geosite rules
        if (customRules is { Count: > 0 })
        {
            foreach (var rule in customRules.Where(r => r.IsEnabled && r.IsXray))
            {
                var obj = ConvertToXrayRule(rule);
                if (obj != null)
                    rules.Add(obj);
            }
        }

        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["outboundTag"] = ProxyTag,
            ["domain"] = new JsonArray { "geosite:ru-blocked" }
        });
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["outboundTag"] = ProxyTag,
            ["domain"] = new JsonArray { "geosite:ru-blocked-all" }
        });
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["outboundTag"] = DirectTag,
            ["ip"] = new JsonArray { "geoip:private" }
        });
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["outboundTag"] = DirectTag,
            ["domain"] = new JsonArray { "geosite:private" }
        });
        // Catch-all → direct (blocked-only mode)
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["port"] = "0-65535",
            ["outboundTag"] = DirectTag
        });
        // DNS direct tags → direct outbound
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["inboundTag"] = new JsonArray { "direct-dns-1", "direct-dns-2" },
            ["outboundTag"] = DirectTag
        });
        // DNS module → proxy
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["inboundTag"] = new JsonArray { "dns-module" },
            ["outboundTag"] = ProxyTag
        });

        return new JsonObject
        {
            ["domainStrategy"] = "AsIs",
            ["rules"] = rules
        };
    }

    private static JsonObject? ConvertToXrayRule(RoutingRule rule)
    {
        var obj = new JsonObject
        {
            ["type"] = "field",
            ["outboundTag"] = rule.Action switch
            {
                RuleAction.Proxy => ProxyTag,
                RuleAction.Direct => DirectTag,
                RuleAction.Block => BlockTag,
                _ => ProxyTag
            }
        };

        var val = new JsonArray { rule.Value };
        switch (rule.MatchType)
        {
            case RuleMatchType.Domain:
            case RuleMatchType.DomainSuffix:
            case RuleMatchType.DomainKeyword:
            case RuleMatchType.DomainRegex:
                obj["domain"] = val;
                break;
            case RuleMatchType.Geosite:
                obj["domain"] = new JsonArray { $"geosite:{rule.Value}" };
                break;
            case RuleMatchType.IpCidr:
                obj["ip"] = val;
                break;
            default:
                return null; // ProcessName/Protocol not supported in Xray
        }

        if (!string.IsNullOrEmpty(rule.Protocol))
            obj["protocol"] = new JsonArray { rule.Protocol.ToLowerInvariant() };
        if (!string.IsNullOrEmpty(rule.Port))
            obj["port"] = rule.Port;

        return obj;
    }

    private static JsonObject BuildServerOutbound(VpnServer s)
    {
        var settings = new JsonObject();
        var streamSettings = new JsonObject();

        switch (s.Protocol)
        {
            case VpnProtocol.VLess:
                var userObj = new JsonObject
                {
                    ["id"] = s.Uuid,
                    ["email"] = "t@t.tt",
                    ["security"] = "auto",
                    ["encryption"] = "none"
                };

                // Add flow only if present
                if (!string.IsNullOrEmpty(s.Flow))
                    userObj["flow"] = s.Flow;

                // Add encryption key if present (ML-KEM hybrid)
                if (!string.IsNullOrEmpty(s.Encryption))
                    userObj["encryption"] = s.Encryption;

                settings["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = s.Address,
                        ["port"] = s.Port,
                        ["users"] = new JsonArray { userObj }
                    }
                };
                break;

            case VpnProtocol.VMess:
                settings["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = s.Address,
                        ["port"] = s.Port,
                        ["users"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = s.Uuid,
                                ["email"] = "t@t.tt",
                                ["security"] = "auto"
                            }
                        }
                    }
                };
                break;

            case VpnProtocol.Trojan:
                settings["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = s.Address,
                        ["port"] = s.Port,
                        ["password"] = s.Password
                    }
                };
                break;

            case VpnProtocol.Shadowsocks:
                settings["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = s.Address,
                        ["port"] = s.Port,
                        ["method"] = s.Cipher,
                        ["password"] = s.Password
                    }
                };
                break;
        }

        // Stream settings — transport FIRST, then security (order matters in Xray)
        streamSettings["network"] = s.Network;

        // Transport settings (MUST come before security)
        switch (s.Network)
        {
            case "ws":
                streamSettings["wsSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                    ["headers"] = string.IsNullOrEmpty(s.Host)
                        ? null
                        : new JsonObject { ["Host"] = s.Host }
                };
                break;

            case "grpc":
                streamSettings["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = string.IsNullOrEmpty(s.ServiceName) ? "" : s.ServiceName
                };
                break;

            case "xhttp":
                var xhttpObj = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                    ["mode"] = string.IsNullOrEmpty(s.XhttpMode) ? "auto" : s.XhttpMode,
                    ["extra"] = new JsonObject
                    {
                        ["mode"] = string.IsNullOrEmpty(s.XhttpMode) ? "auto" : s.XhttpMode,
                        ["xPaddingBytes"] = string.IsNullOrEmpty(s.XpaddingBytes) ? "256-1024" : s.XpaddingBytes,
                        ["xmux"] = BuildXmux(s)
                    }
                };
                streamSettings["xhttpSettings"] = xhttpObj;
                break;
        }

        // Security settings (MUST come after transport)
        if (s.Security == "reality")
        {
            streamSettings["security"] = "reality";

            var realityObj = new JsonObject
            {
                ["serverName"] = !string.IsNullOrEmpty(s.Sni) ? s.Sni :
                                  !string.IsNullOrEmpty(s.Host) ? s.Host : s.Address,
                ["fingerprint"] = string.IsNullOrEmpty(s.Fingerprint) ? "chrome" : s.Fingerprint,
                ["show"] = false,
                ["publicKey"] = s.PublicKey,
                ["shortId"] = s.ShortId,
                ["spiderX"] = string.IsNullOrEmpty(s.SpiderX) ? "/" : s.SpiderX
            };

            if (!string.IsNullOrEmpty(s.Mldsa65Verify))
                realityObj["mldsa65Verify"] = s.Mldsa65Verify;

            streamSettings["realitySettings"] = realityObj;

            // finalmask — QUIC params from subscription (fm=)
            if (!string.IsNullOrEmpty(s.Finalmask))
            {
                var fmNode = JsonNode.Parse(s.Finalmask);
                if (fmNode != null)
                    streamSettings["finalmask"] = fmNode;
            }
        }
        else if (s.Security == "tls")
        {
            streamSettings["security"] = "tls";
            streamSettings["tlsSettings"] = new JsonObject
            {
                ["serverName"] = string.IsNullOrEmpty(s.Host) ? s.Address : s.Host,
                ["allowInsecure"] = false
            };
        }

        var outbound = new JsonObject
        {
            ["tag"] = ProxyTag,
            ["protocol"] = s.Protocol.ToString().ToLowerInvariant(),
            ["settings"] = settings,
            ["streamSettings"] = streamSettings,
            ["mux"] = new JsonObject
            {
                ["enabled"] = false,
                ["concurrency"] = -1
            }
        };

        return outbound;
    }

    /// <summary>
    /// Build xmux JSON node — from subscription's extra.xmux or hardcoded defaults.
    /// Subscription format: {"cMaxReuseTimes":0,"hKeepAlivePeriod":0,...}
    /// </summary>
    private static JsonNode? BuildXmux(VpnServer s)
    {
        if (!string.IsNullOrEmpty(s.XmuxConfig))
        {
            var node = JsonNode.Parse(s.XmuxConfig);
            if (node != null) return node;
        }

        return new JsonObject
        {
            ["cMaxReuseTimes"] = 0,
            ["hKeepAlivePeriod"] = 0,
            ["hMaxRequestTimes"] = "0",
            ["hMaxReusableSecs"] = "0",
            ["maxConcurrency"] = "16-32",
            ["maxConnections"] = 0
        };
    }
}
