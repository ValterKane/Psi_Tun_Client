using YamlDotNet.Serialization;

namespace PsiTun.Models.Mihomo;

/// <summary>
/// Каркас mihomo config.yaml. Динамические данные (серверы, правила
/// маршрутизации) в каркасе не живут — они в провайдерах, см. MihomoProviderWriter.
///
/// Ключи: YamlDotNet пишет имена членов дословно, а mihomo требует нижний
/// регистр, поэтому <see cref="YamlMemberAttribute.Alias"/> стоит на КАЖДОМ
/// члене. Naming convention здесь не используется: он применяется и к алиасам и
/// превращает "socks-port" в "socksPort".
/// </summary>
public sealed class MihomoConfig
{
    [YamlMember(Alias = "socks-port")] public int SocksPort { get; set; }
    [YamlMember(Alias = "port")] public int HttpPort { get; set; }
    [YamlMember(Alias = "external-controller")] public string ExternalController { get; set; } = "";
    [YamlMember(Alias = "secret")] public string Secret { get; set; } = "";
    [YamlMember(Alias = "allow-lan")] public bool AllowLan { get; set; }
    [YamlMember(Alias = "mode")] public string Mode { get; set; } = "rule";
    [YamlMember(Alias = "log-level")] public string LogLevel { get; set; } = "info";
    [YamlMember(Alias = "find-process-mode")] public string FindProcessMode { get; set; } = "strict";
    [YamlMember(Alias = "geodata-mode")] public bool GeodataMode { get; set; } = true;
    [YamlMember(Alias = "tun")] public MihomoTun Tun { get; set; } = new();
    [YamlMember(Alias = "dns")] public MihomoDns Dns { get; set; } = new();
    [YamlMember(Alias = "sniffer")] public MihomoSniffer Sniffer { get; set; } = new();
    [YamlMember(Alias = "proxy-providers")] public Dictionary<string, MihomoProxyProvider> ProxyProviders { get; set; } = new();
    [YamlMember(Alias = "proxy-groups")] public List<MihomoProxyGroup> ProxyGroups { get; set; } = new();
    [YamlMember(Alias = "rule-providers")] public Dictionary<string, MihomoRuleProvider> RuleProviders { get; set; } = new();
    [YamlMember(Alias = "rules")] public List<string> Rules { get; set; } = new();
}

public sealed class MihomoTun
{
    [YamlMember(Alias = "enable")] public bool Enable { get; set; } = true;
    [YamlMember(Alias = "device")] public string Device { get; set; } = "sing-tun";
    [YamlMember(Alias = "stack")] public string Stack { get; set; } = "system";
    [YamlMember(Alias = "mtu")] public int Mtu { get; set; } = 1476;
    [YamlMember(Alias = "auto-route")] public bool AutoRoute { get; set; } = true;
    [YamlMember(Alias = "strict-route")] public bool StrictRoute { get; set; } = true;
    [YamlMember(Alias = "dns-hijack")] public List<string> DnsHijack { get; set; } = new();

    /// <summary>
    /// Адрес адаптера. Ключа шлюза и DNS в схеме mihomo нет: следующий узел
    /// выводится из маски подсети, DNS живёт в верхнеуровневой секции <c>dns</c>,
    /// а <c>tun.dns-hijack</c> лишь перенаправляет порт 53 в её модуль
    /// (проверено по wiki.metacubex.one и meta-docs, ядро v1.19.32).
    /// Список: <c>null</c> — ключ не пишется, действует умолчание ядра 198.18.0.1/30.
    /// </summary>
    [YamlMember(Alias = "inet4-address")] public List<string>? Inet4Address { get; set; }
}

/// <summary>
/// <c>dns</c>. Поле <see cref="Enable"/> обязательно: без него mihomo v1.19.32
/// держит DNS-модуль выключенным (`DNS section is disabled`) и не открывает
/// слушатель. При <c>tun.dns-hijack</c> весь трафик порта 53 уходит в модуль,
/// которого нет, — имена не разрешаются ни для одного домена. Умолчание ядра —
/// выключено.
///
/// <c>fallback</c> не задаётся намеренно: его наличие автоматически включает
/// <c>fallback-filter</c> с <c>geoip-code: CN</c>, и любой ответ вне Китая
/// считается подменённым — российские домены уходят на зарубежные резолверы.
/// Резолв по умолчанию идёт DoH через группу жёсткого пути; прямыми остаются
/// только российские зоны и приватные адреса — их задаёт <c>nameserver-policy</c>.
/// </summary>
public sealed class MihomoDns
{
    [YamlMember(Alias = "enable")] public bool Enable { get; set; }
    [YamlMember(Alias = "enhanced-mode")] public string EnhancedMode { get; set; } = "redir-host";
    [YamlMember(Alias = "ipv6")] public bool Ipv6 { get; set; }
    [YamlMember(Alias = "proxy-server-nameserver")] public List<string> ProxyServerNameserver { get; set; } = new();
    [YamlMember(Alias = "nameserver")] public List<string> Nameserver { get; set; } = new();
    [YamlMember(Alias = "nameserver-policy")] public Dictionary<string, List<string>> NameserverPolicy { get; set; } = new();
}

public sealed class MihomoProxyGroup
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";
    [YamlMember(Alias = "type")] public string Type { get; set; } = "select";
    [YamlMember(Alias = "use")] public List<string> Use { get; set; } = new();
}

public sealed class MihomoProxyProvider
{
    [YamlMember(Alias = "type")] public string Type { get; set; } = "file";
    [YamlMember(Alias = "path")] public string Path { get; set; } = "";
    [YamlMember(Alias = "health-check")] public MihomoHealthCheck HealthCheck { get; set; } = new();
}

public sealed class MihomoHealthCheck
{
    [YamlMember(Alias = "enable")] public bool Enable { get; set; } = true;
    [YamlMember(Alias = "url")] public string Url { get; set; } = "https://www.gstatic.com/generate_204";
    [YamlMember(Alias = "interval")] public int Interval { get; set; } = 300;
}

public sealed class MihomoRuleProvider
{
    [YamlMember(Alias = "type")] public string Type { get; set; } = "file";
    [YamlMember(Alias = "behavior")] public string Behavior { get; set; } = "classical";
    [YamlMember(Alias = "path")] public string Path { get; set; } = "";
    [YamlMember(Alias = "format")] public string Format { get; set; } = "yaml";
}

/// <summary>
/// <c>sniffer</c>. Умолчание ядра — выключен; пустой <c>sniff</c> не вынюхивает
/// ничего, поэтому порты задаются явно. Имена протоколов записаны заглавными
/// буквами намеренно: это идентификаторы протоколов, а не члены модели, и
/// YamlDotNet пишет их дословно (wiki.metacubex.one/en/config/sniff).
/// </summary>
public sealed class MihomoSniffer
{
    [YamlMember(Alias = "enable")] public bool Enable { get; set; }

    /// <summary>
    /// Подменять назначение распознанным доменом. Умолчание ядра — false; без
    /// подмены в TUN-режиме правило применяется к IP, а не к домену.
    /// <c>null</c> — ключ не пишется.
    /// </summary>
    [YamlMember(Alias = "override-destination")] public bool? OverrideDestination { get; set; }

    [YamlMember(Alias = "sniff")] public Dictionary<string, MihomoSniffProtocol> Sniff { get; set; } = new();
}

public sealed class MihomoSniffProtocol
{
    /// <summary>Порты строками: запись допускает диапазон (<c>8080-8880</c>).</summary>
    [YamlMember(Alias = "ports")] public List<string> Ports { get; set; } = new();

    /// <summary>Перекрывает глобальное <c>override-destination</c>.</summary>
    [YamlMember(Alias = "override-destination")] public bool? OverrideDestination { get; set; }
}
