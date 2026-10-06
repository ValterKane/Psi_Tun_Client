using System.IO;
using System.Text.Json.Nodes;
using PsiTun.Models;
using PsiTun.Models.Mihomo;
using YamlDotNet.Serialization;

namespace PsiTun.Services;

/// <summary>
/// Пишет динамические данные mihomo: список серверов в proxy-provider и
/// пользовательские правила в rule-provider. Файлы читаются ядром при старте, а
/// после правки подхватываются через REST (<c>PUT /providers/...</c>) — ядро и
/// TUN-адаптер при этом не пересоздаются.
///
/// Дисциплина та же, что в <see cref="MihomoConfigGenerator"/>: алиасы на каждом
/// члене модели, naming convention не используется.
/// </summary>
public static class MihomoProviderWriter
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Имена прокси для mihomo. Ядро адресует прокси по имени и молча
    /// отбрасывает запись с уже занятым именем, поэтому уникальность обязательна
    /// и проверяется по фактически выданным именам, а не по исходным: сервер,
    /// названный «Node #0», иначе столкнулся бы с именем, порождённым от «Node».
    /// Имя без повторов остаётся как есть — выбранный сервер от этого устойчивее.
    /// Функция детерминирована при неизменном порядке списка.
    /// </summary>
    public static List<string> ProxyNames(IReadOnlyList<VpnServer> servers)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>(servers.Count);

        for (var i = 0; i < servers.Count; i++)
        {
            var baseName = (servers[i].Name ?? "").Trim();
            if (baseName.Length == 0) baseName = $"server-{i}";

            var candidate = baseName;
            for (var suffix = i; !used.Add(candidate); suffix++)
                candidate = $"{baseName} #{suffix}";

            names.Add(candidate);
        }

        return names;
    }

    /// <summary>
    /// Отображение правила в строку Clash. <c>null</c> — правило не переносится:
    /// выключено либо его тип не имеет соответствия (<c>Protocol</c> —
    /// DPI-классификация, аналога у mihomo нет).
    /// </summary>
    public static string? MapRule(RoutingRule rule)
    {
        if (!rule.IsEnabled) return null;
        if (string.IsNullOrWhiteSpace(rule.Value)) return null;

        var head = rule.MatchType switch
        {
            RuleMatchType.Domain => $"DOMAIN,{rule.Value}",
            RuleMatchType.DomainSuffix => $"DOMAIN-SUFFIX,{rule.Value}",
            RuleMatchType.DomainKeyword => $"DOMAIN-KEYWORD,{rule.Value}",
            RuleMatchType.DomainRegex => $"DOMAIN-REGEX,{rule.Value}",
            RuleMatchType.Geosite => $"GEOSITE,{rule.Value}",
            // IP-CIDR6 у mihomo — синоним IP-CIDR с тем же действием.
            RuleMatchType.IpCidr => $"IP-CIDR,{rule.Value}",
            RuleMatchType.ProcessName => $"PROCESS-NAME,{rule.Value}",
            _ => null
        };

        if (head is null) return null;

        // Network/Port перенести нечем. Сочетание условий у Clash выражается
        // логическим правилом, но payload classical-провайдера логических правил
        // не принимает: mihomo v1.19.32 отвечает «payload format error» и
        // отбрасывает строку целиком (проверено живым прогоном; в каркасе
        // rules: тот же синтаксис проходит). Отдать одно основное условие
        // безопаснее потери всего правила, но условие Network/Port теряется —
        // это ограничение mihomo, а не выбор реализации.
        return $"{head},{Policy(rule)}";
    }

    private static string Policy(RoutingRule rule) => rule.Action switch
    {
        RuleAction.Direct => "DIRECT",
        RuleAction.Block => "REJECT",
        _ => rule.ForceProxy ? MihomoConfigGenerator.ForceGroup : MihomoConfigGenerator.ProxyGroup
    };

    public static void WriteServers(string path, IReadOnlyList<VpnServer> servers)
    {
        var names = ProxyNames(servers);
        var file = new MihomoProxyProviderFile
        {
            Proxies = servers.Select((s, i) => ToProxy(s, names[i])).ToList()
        };
        WriteAtomic(path, Serializer.Serialize(file));
    }

    public static void WriteRules(string path, IReadOnlyList<RoutingRule> rules)
    {
        var file = new MihomoClashRule
        {
            Payload = rules.Select(MapRule).Where(line => line is not null).ToList()!
        };
        WriteAtomic(path, Serializer.Serialize(file));
    }

    // Пишем во временный файл и переносим — рабочая копия не бывает половинчатой.
    private static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static MihomoProxy ToProxy(VpnServer s, string name)
    {
        var isTls = s.Security is "reality" or "tls";
        var isXhttp = string.Equals(s.Network, "xhttp", StringComparison.OrdinalIgnoreCase);
        var isWs = string.Equals(s.Network, "ws", StringComparison.OrdinalIgnoreCase);
        var isGrpc = string.Equals(s.Network, "grpc", StringComparison.OrdinalIgnoreCase);

        return new MihomoProxy
        {
            Name = name,
            Type = s.Protocol switch
            {
                VpnProtocol.VMess => "vmess",
                VpnProtocol.Trojan => "trojan",
                VpnProtocol.Shadowsocks => "ss",
                _ => "vless"
            },
            Server = s.Address,
            Port = s.Port,
            Uuid = Null(s.Protocol is VpnProtocol.VLess or VpnProtocol.VMess ? s.Uuid : null),
            Password = Null(s.Protocol is VpnProtocol.Trojan or VpnProtocol.Shadowsocks ? s.Password : null),
            Cipher = Null(s.Protocol == VpnProtocol.Shadowsocks ? s.Cipher : null),
            Udp = true,
            Flow = Null(s.Flow),
            Tls = isTls ? true : null,
            Servername = isTls ? ServerName(s) : null,
            Alpn = AlpnTokens(s.Alpn),
            ClientFingerprint = isTls ? NormalizeFingerprint(s.Fingerprint) : null,
            SkipCertVerify = isTls ? false : null,
            // "none" — умолчание протокола; запись поля ничего не меняет.
            Encryption = string.Equals(s.Encryption, "none", StringComparison.OrdinalIgnoreCase)
                ? null
                : Null(s.Encryption),
            Network = Null(s.Network),
            RealityOpts = s.Security == "reality"
                ? new MihomoRealityOpts
                {
                    PublicKey = Null(s.PublicKey),
                    ShortId = Null(s.ShortId),
                    // Умолчание mihomo — false: ядро вырезает X25519MLKEM768 из
                    // ClientHello, и REALITY сервера Xray ≥ v26.9.8 отвергает
                    // рукопожатие. Ключ стоит всегда: на серверах до v26.9.8 он
                    // лишь сохраняет группу, которую chrome и так предлагает.
                    SupportX25519MLKEM768 = true
                }
                : null,
            XhttpOpts = isXhttp
                ? new MihomoXhttpOpts
                {
                    Path = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                    Host = Null(s.Host),
                    Mode = string.IsNullOrEmpty(s.XhttpMode) ? "auto" : s.XhttpMode,
                    XPaddingBytes = Null(s.XpaddingBytes),
                    ReuseSettings = BuildReuseSettings(s)
                }
                : null,
            WsOpts = isWs
                ? new MihomoWsOpts
                {
                    Path = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                    Headers = string.IsNullOrEmpty(s.Host)
                        ? null
                        : new Dictionary<string, string> { ["Host"] = s.Host }
                }
                : null,
            GrpcOpts = isGrpc
                ? new MihomoGrpcOpts { GrpcServiceName = Null(s.ServiceName) }
                : null
        };
    }

    private static string? Null(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// serverName для рукопожатия — только из того, что несла ссылка: sni, иначе
    /// host. Адрес сюда не попадает никогда: IP не совпадёт с serverNames
    /// сервера, REALITY проксирует цель и отдаёт её настоящий сертификат.
    /// </summary>
    private static string? ServerName(VpnServer s) =>
        !string.IsNullOrEmpty(s.Sni) ? s.Sni :
        !string.IsNullOrEmpty(s.Host) ? s.Host : null;

    /// <summary>ALPN приходит из ссылки списком через запятую, иногда в скобках.</summary>
    private static List<string>? AlpnTokens(string? alpn)
    {
        if (string.IsNullOrWhiteSpace(alpn)) return null;

        var tokens = alpn
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('[', ']', '"', '\'').Trim())
            .Where(t => t.Length > 0)
            .ToList();

        return tokens.Count == 0 ? null : tokens;
    }

    /// <summary>
    /// Только chrome / firefox / safari надёжны с REALITY; остальное сводится к
    /// chrome. «random» и «randomized» дают свежий ClientHello каждый раз и
    /// проходят примерно в половине случаев, ios / android / edge / qq и 360 не
    /// завершают рукопожатие вовсе.
    /// </summary>
    private static string NormalizeFingerprint(string? fp) =>
        fp is not null && (
            fp.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            fp.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            fp.Equals("safari", StringComparison.OrdinalIgnoreCase))
            ? fp
            : "chrome";

    /// <summary>
    /// <c>reuse-settings</c> из XMUX подписки. Ключи Xray — camelCase, mihomo —
    /// kebab-case, переноса «как есть» не существует. При отсутствии
    /// <c>XmuxConfig</c> берутся умолчания Xray.
    /// <c>max-concurrency</c> и <c>max-connections</c> у mihomo конфликтуют,
    /// поэтому заполняется только <c>max-concurrency</c>; <c>max-connections</c>
    /// переносится лишь когда первого нет.
    /// </summary>
    private static MihomoReuseSettings BuildReuseSettings(VpnServer s)
    {
        var src = ParseXmux(s.XmuxConfig);

        string? Get(string camel) =>
            src.TryGetPropertyValue(camel, out var node) && node is not null
                ? node.ToJsonString().Trim('"')
                : null;

        var maxConcurrency = Get("maxConcurrency");

        return new MihomoReuseSettings
        {
            CMaxReuseTimes = Get("cMaxReuseTimes"),
            HKeepAlivePeriod = Get("hKeepAlivePeriod"),
            HMaxRequestTimes = Get("hMaxRequestTimes"),
            HMaxReusableSecs = Get("hMaxReusableSecs"),
            MaxConcurrency = maxConcurrency,
            MaxConnections = maxConcurrency is null ? Get("maxConnections") : null
        };
    }

    private static JsonObject ParseXmux(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject parsed) return parsed;
            }
            catch (System.Text.Json.JsonException)
            {
                // Подписка отдала неразбираемый xmux — работают умолчания.
            }
        }

        return new JsonObject
        {
            ["cMaxReuseTimes"] = 0,
            ["hKeepAlivePeriod"] = 0,
            ["hMaxRequestTimes"] = "0",
            ["hMaxReusableSecs"] = "0",
            ["maxConcurrency"] = "16-32"
        };
    }
}
