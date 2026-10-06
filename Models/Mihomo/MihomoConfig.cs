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
/// Защиту от подмены несёт плечо <c>nameserver-policy</c> для
/// <c>geosite:ru-blocked</c> (DoH через PROXY).
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
