using System.IO;
using PsiTun.Models;
using PsiTun.Services;

namespace PsiTun.Tests;

public class MihomoProviderWriterTests
{
    // Сервер подписки PsiTun: VLESS + XHTTP + REALITY.
    private static VpnServer XhttpReality() => new()
    {
        Name = "VLESS XHTTP Reality 443",
        Protocol = VpnProtocol.VLess,
        Address = "192.0.2.10",
        Port = 443,
        Uuid = "11111111-2222-3333-4444-555555555555",
        Network = "xhttp",
        Security = "reality",
        Sni = "microsoft.com",
        Fingerprint = "chrome",
        PublicKey = "PUBKEY",
        ShortId = "229f5b7281b0bf70",
        Path = "/api/v1/data",
        XhttpMode = "auto",
        XpaddingBytes = "100-1000",
        Encryption = "none"
    };

    [Theory]
    [InlineData(RuleMatchType.ProcessName, "Discord.exe", RuleAction.Proxy, false, "PROCESS-NAME,Discord.exe,PROXY")]
    [InlineData(RuleMatchType.DomainSuffix, "example.com", RuleAction.Direct, false, "DOMAIN-SUFFIX,example.com,DIRECT")]
    [InlineData(RuleMatchType.IpCidr, "10.0.0.0/8", RuleAction.Block, false, "IP-CIDR,10.0.0.0/8,REJECT")]
    [InlineData(RuleMatchType.ProcessName, "game.exe", RuleAction.Proxy, true, "PROCESS-NAME,game.exe,FORCE")]
    public void MapRule_ProducesClashLine(
        RuleMatchType matchType, string value, RuleAction action, bool force, string expected)
    {
        var rule = new RoutingRule
        {
            MatchType = matchType, Value = value, Action = action, ForceProxy = force, IsEnabled = true
        };

        Assert.Equal(expected, MihomoProviderWriter.MapRule(rule));
    }

    [Fact]
    public void MapRule_SkipsDisabledAndProtocol()
    {
        var disabled = new RoutingRule { MatchType = RuleMatchType.Domain, Value = "a.com", IsEnabled = false };
        var protocol = new RoutingRule { MatchType = RuleMatchType.Protocol, Value = "bittorrent" };

        Assert.Null(MihomoProviderWriter.MapRule(disabled));
        Assert.Null(MihomoProviderWriter.MapRule(protocol));
    }

    /// <summary>
    /// mihomo отвергает логические правила в payload classical-провайдера
    /// («payload format error», проверено живым прогоном v1.19.32), поэтому
    /// сочетание с Network/Port невыразимо: переносится основное условие.
    /// </summary>
    [Fact]
    public void MapRule_DropsNetworkAndPortBecauseLogicalRulesAreRejected()
    {
        var rule = new RoutingRule
        {
            MatchType = RuleMatchType.DomainSuffix, Value = "game.example", Network = "UDP", Port = "27015"
        };

        Assert.Equal("DOMAIN-SUFFIX,game.example,PROXY", MihomoProviderWriter.MapRule(rule));
    }

    [Fact]
    public void MapRule_SkipsRulesWithoutValue()
    {
        Assert.Null(MihomoProviderWriter.MapRule(new RoutingRule { MatchType = RuleMatchType.Domain, Value = "" }));
        Assert.Null(MihomoProviderWriter.MapRule(new RoutingRule { MatchType = RuleMatchType.Domain, Value = null! }));
    }

    [Fact]
    public void WriteServers_EmitsXhttpAndRealityFields()
    {
        var yaml = WriteAndReadServers(new[] { XhttpReality() });

        Assert.Contains("type: vless", yaml);
        Assert.Contains("server: 192.0.2.10", yaml);
        Assert.Contains("tls: true", yaml);
        Assert.Contains("servername: microsoft.com", yaml);
        Assert.Contains("client-fingerprint: chrome", yaml);
        Assert.Contains("network: xhttp", yaml);

        Assert.Contains("reality-opts:", yaml);
        Assert.Contains("public-key: PUBKEY", yaml);
        Assert.Contains("short-id: 229f5b7281b0bf70", yaml);
        // Без этого ключа REALITY сервера Xray ≥ v26.9.8 отвергает рукопожатие.
        Assert.Contains("support-x25519mlkem768: true", yaml);

        Assert.Contains("xhttp-opts:", yaml);
        Assert.Contains("path: /api/v1/data", yaml);
        Assert.Contains("mode: auto", yaml);
        Assert.Contains("x-padding-bytes: 100-1000", yaml);

        // XMUX переносится в kebab-case mihomo, а не дословно из Xray.
        Assert.Contains("reuse-settings:", yaml);
        Assert.Contains("max-concurrency: 16-32", yaml);
        Assert.DoesNotContain("max-connections", yaml);
        Assert.DoesNotContain("maxConcurrency", yaml);

        // encryption: none — умолчание протокола, поле не пишется.
        Assert.DoesNotContain("encryption:", yaml);
    }

    [Fact]
    public void WriteServers_KeysHaveNoUppercaseLetters()
    {
        var offenders = WriteAndReadServers(new[] { XhttpReality() })
            .Split('\n')
            .Select(raw => raw.TrimStart())
            .Where(line => line.Length > 0 && line[0] != '-' && line[0] != '#')
            .Select(line => (line, colon: line.IndexOf(':')))
            .Where(x => x.colon > 0)
            .Select(x => x.line[..x.colon])
            .Where(key => key.Any(char.IsUpper))
            .ToList();

        Assert.True(offenders.Count == 0, $"Ключи с заглавными буквами: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void WriteRules_OmitsUnmappedRulesAndKeepsOrder()
    {
        RoutingRule[] rules =
        [
            new() { MatchType = RuleMatchType.Protocol, Value = "bittorrent" },
            new() { MatchType = RuleMatchType.Geosite, Value = "category-ads-all", Action = RuleAction.Block },
            new() { MatchType = RuleMatchType.Domain, Value = "skip.me", IsEnabled = false },
            new() { MatchType = RuleMatchType.DomainSuffix, Value = "example.com", ForceProxy = true }
        ];

        var yaml = WriteAndReadRules(rules);

        Assert.Contains("payload:", yaml);
        Assert.Contains("GEOSITE,category-ads-all,REJECT", yaml);
        Assert.Contains("DOMAIN-SUFFIX,example.com,FORCE", yaml);
        Assert.DoesNotContain("bittorrent", yaml);
        Assert.DoesNotContain("skip.me", yaml);

        var ads = yaml.IndexOf("category-ads-all", StringComparison.Ordinal);
        var suffix = yaml.IndexOf("DOMAIN-SUFFIX,example.com", StringComparison.Ordinal);
        Assert.True(ads > 0 && suffix > ads, "Порядок правил не сохранён");
    }

    /// <summary>
    /// Ядро адресует прокси по имени и молча отбрасывает запись с занятым
    /// именем, поэтому столкновение заголовком «Node #0» недопустимо.
    /// </summary>
    [Fact]
    public void ProxyNames_AreUniqueEvenWhenGeneratedNamesCollide()
    {
        VpnServer[] servers =
        [
            new() { Name = "Node" },
            new() { Name = "Node" },
            new() { Name = "Node #0" },
            new() { Name = "" },
            new() { Name = null! }
        ];

        var names = MihomoProviderWriter.ProxyNames(servers);

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Node", names[0]);
        Assert.Equal("Node #1", names[1]);
        Assert.Equal("Node #0", names[2]);
        Assert.Equal("server-3", names[3]);
        Assert.Equal("server-4", names[4]);
    }

    [Fact]
    public void WriteServers_SurvivesNullName()
    {
        var yaml = WriteAndReadServers(new[] { new VpnServer { Name = null!, Address = "192.0.2.1", Port = 443 } });

        Assert.Contains("name: server-0", yaml);
    }

    [Fact]
    public void WriteServers_EmitsWsAndGrpcOpts()
    {
        VpnServer[] servers =
        [
            new()
            {
                Name = "WS", Protocol = VpnProtocol.VLess, Address = "192.0.2.11", Port = 443, Uuid = "u",
                Network = "ws", Security = "tls", Sni = "example.com", Path = "/ws", Host = "cdn.example.com"
            },
            new()
            {
                Name = "GRPC", Protocol = VpnProtocol.VLess, Address = "192.0.2.12", Port = 443, Uuid = "u",
                Network = "grpc", Security = "tls", Sni = "example.com", ServiceName = "svc"
            }
        ];

        var yaml = WriteAndReadServers(servers);

        Assert.Contains("ws-opts:", yaml);
        Assert.Contains("path: /ws", yaml);
        Assert.Contains("Host: cdn.example.com", yaml);
        Assert.Contains("grpc-opts:", yaml);
        Assert.Contains("grpc-service-name: svc", yaml);
    }

    private static string WriteAndReadServers(IReadOnlyList<VpnServer> servers)
    {
        var path = TempFile();
        try
        {
            MihomoProviderWriter.WriteServers(path, servers);
            return File.ReadAllText(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteAndReadRules(IReadOnlyList<RoutingRule> rules)
    {
        var path = TempFile();
        try
        {
            MihomoProviderWriter.WriteRules(path, rules);
            return File.ReadAllText(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"psi-provider-{Guid.NewGuid():N}.yaml");
}
