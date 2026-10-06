using System.IO;
using PsiTun.Models.Mihomo;
using YamlDotNet.Serialization;

namespace PsiTun.Services;

/// <summary>
/// Строит каркас mihomo config.yaml: TUN, DNS, группы, провайдеры, статические
/// правила. Смена сервера и правка правил каркас не трогают — они применяются
/// через REST к внешнему контроллеру (MihomoApiClient). Каркас перегенерируется
/// только при изменении DNS/TUN/портов, и тогда ядро перезапускается.
/// </summary>
public static class MihomoConfigGenerator
{
    public const int ControllerPort = 10812;

    public const string ProxyGroup = "PROXY";
    public const string ForceGroup = "FORCE";
    public const string ServersProvider = "psi-servers";
    public const string UserRulesProvider = "psi-user";
    public const string AutoRulesProvider = "psi-auto";

    // Naming convention НЕ используется: он применяется и к алиасам, превращая
    // "socks-port" в "socksPort". Все ключи заданы алиасами в модели.
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static string Generate(SettingsService settings, string controllerSecret)
        => Serializer.Serialize(Build(settings, controllerSecret));

    internal static MihomoConfig Build(SettingsService s, string secret) => new()
    {
        SocksPort = s.SocksPort,
        HttpPort = s.HttpPort,
        ExternalController = $"127.0.0.1:{ControllerPort}",
        Secret = secret,
        AllowLan = false,
        Tun = new MihomoTun
        {
            Enable = s.UseTun,
            Device = s.TunName,
            Stack = s.TunStack,
            Mtu = s.TunMtu,
            AutoRoute = s.AutoRoute,
            StrictRoute = s.StrictRoute,
            DnsHijack = ["any:53"],
        },
        Dns = new MihomoDns
        {
            // Без этого поля ядро держит DNS-модуль выключенным, и при
            // tun.dns-hijack не разрешается ни одно имя.
            Enable = true,
            // fake-ip не используем — как и в прежней связке sing-box.
            EnhancedMode = "redir-host",
            Ipv6 = false, // эквивалент прежнего dns.strategy = prefer_ipv4
            // IP-based, иначе разрешение имён DNS-серверов упирается в курицу-яйцо.
            ProxyServerNameserver = ["1.1.1.1", "77.88.8.8"],
            Nameserver = ["77.88.8.8", "8.8.8.8"],
            NameserverPolicy = new()
            {
                ["geosite:ru-available-only-inside"] = ["77.88.8.8"],
                // DoH через VPN: замена прежнего remote_dns (detour: force-proxy)
                ["geosite:ru-blocked"] = ["https://1.1.1.1/dns-query#PROXY"],
                ["geosite:private"] = ["77.88.8.8"],
            },
            // fallback не задаём: его наличие включает fallback-filter с
            // geoip-code CN и уводит российские домены на зарубежные резолверы.
        },
        ProxyProviders = new()
        {
            [ServersProvider] = new MihomoProxyProvider { Type = "file", Path = "./providers/servers.yaml" },
        },
        ProxyGroups =
        [
            new MihomoProxyGroup { Name = ProxyGroup, Type = "select", Use = [ServersProvider] },
            new MihomoProxyGroup { Name = ForceGroup, Type = "select", Use = [ServersProvider] },
        ],
        RuleProviders = new()
        {
            [UserRulesProvider] = new MihomoRuleProvider { Path = "./rules/user.yaml" },
            [AutoRulesProvider] = new MihomoRuleProvider { Path = "./rules/auto.yaml" },
        },
        Rules = BuildRules(),
    };

    /// <summary>
    /// Каркасный порядок. Пользовательские правила вставлены двумя блоками;
    /// порядок внутри блока — порядок строк в файле-провайдере.
    /// Гео-категории читаются из geosite.dat/geoip.dat при geodata-mode.
    /// </summary>
    private static List<string> BuildRules()
    {
        // Свой процесс — напрямую, иначе трафик ядра уходит в петлю.
        var self = Path.GetFileName(Environment.ProcessPath) ?? "PsiTun.exe";

        return
        [
            $"PROCESS-NAME,{self},DIRECT",
            $"RULE-SET,{AutoRulesProvider},PROXY",
            $"RULE-SET,{UserRulesProvider},PROXY",
            "GEOIP,private,DIRECT,no-resolve",
            // ru-blocked-all отвергнут: mihomo валит категорию целиком на 13 записях
            // с хвостовой точкой (trailing dot is not allowed). См. spec, «Гео-данные».
            "GEOSITE,ru-blocked,PROXY",
            "GEOSITE,category-ads-all,REJECT",
            "GEOSITE,win-spy,REJECT",
            "MATCH,DIRECT",
        ];
    }
}
