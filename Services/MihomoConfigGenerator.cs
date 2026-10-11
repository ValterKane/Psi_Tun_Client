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

    /// <summary>
    /// Группа жёсткого DNS-пути. `select` без выбранного узла оставляет резолв
    /// заблокированных без выхода; `fallback` сам берёт живой узел.
    /// </summary>
    public const string DnsProxyGroup = "DNS-PROXY";
    public const string ServersProvider = "psi-servers";

    // Правила разложены по наборам по числу политик: политику несёт строка
    // RULE-SET каркаса, строка внутри classical-набора её не задаёт. Проверено
    // прогоном v1.19.32: `DOMAIN-SUFFIX,x,FORCE` в payload применяется как
    // PROXY набора, `...,DIRECT` и `...,REJECT` — так же.
    public const string RejectRulesProvider = "psi-reject";
    public const string AutoRulesProvider = "psi-auto";
    public const string ForceRulesProvider = "psi-force";
    public const string UserRulesProvider = "psi-user";
    public const string DirectRulesProvider = "psi-direct";

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
            // Резолв идёт через туннель: открытый UDP 53 наружу не уходит, и
            // российских резолверов в отчёте теста на утечку нет. Измерено: на
            // прямом пути запрос уходил на 77.88.8.8 и был виден снаружи, при том
            // что перехвата порта 53 провайдером нет (запросы к адресам TEST-NET
            // истекали по таймауту).
            //
            // Два резолвера — резерв: ядро опрашивает список параллельно и берёт
            // первый ответ, поэтому недоступность Cloudflare из туннеля не
            // оставляет резолв без выхода. Ключ fallback не используем: он
            // включает fallback-filter с geoip-code CN (см. комментарий класса).
            Nameserver =
            [
                $"https://1.1.1.1/dns-query#{DnsProxyGroup}",
                $"https://8.8.8.8/dns-query#{DnsProxyGroup}",
            ],
            NameserverPolicy = new()
            {
                // Прямыми резолверами остаются только российские зоны и приватные
                // адреса: иначе российские сайты получат зарубежные адреса CDN.
                // Отдельной записи для заблокированных не нужно — они и так идут
                // тем же DoH через туннель.
                ["geosite:category-ru"] = ["77.88.8.8"],
                ["geosite:ru-available-only-inside"] = ["77.88.8.8"],
                ["geosite:private"] = ["77.88.8.8"],
            },
            // fallback не задаём: его наличие включает fallback-filter с
            // geoip-code CN и уводит российские домены на зарубежные резолверы.
        },
        Sniffer = new MihomoSniffer
        {
            Enable = s.EnableSniffing,
            // Подмена назначения распознанным доменом: без неё в TUN правило
            // применяется к IP, и заблокированный домен уходит в MATCH,DIRECT.
            OverrideDestination = true,
            Sniff = new()
            {
                ["HTTP"] = new MihomoSniffProtocol { Ports = ["80", "8080-8880"], OverrideDestination = true },
                ["TLS"] = new MihomoSniffProtocol { Ports = ["443", "8443"], OverrideDestination = true },
                ["QUIC"] = new MihomoSniffProtocol { Ports = ["443", "8443"], OverrideDestination = true },
            },
        },
        ProxyProviders = new()
        {
            // Интервал проверки узлов провайдера. Мёртвый узел остаётся выбранным
            // для DNS до следующего тика, поэтому 30 с вместо умолчания 300 с
            // (MihomoHealthCheck.Interval). Ждать активной проверки по отказам
            // нечего: она стартует лишь при `connection refused` либо при пяти
            // отказах внутри окна 5 с (groupbase.go:265-300, умолчания на :95 и
            // :98), а таймаут соединения в такое окно не укладывается.
            [ServersProvider] = new MihomoProxyProvider
            {
                Type = "file",
                Path = "./providers/servers.yaml",
                HealthCheck = new MihomoHealthCheck { Interval = 30 },
            },
        },
        ProxyGroups =
        [
            // Пустой провайдер: умолчание ядра COMPATIBLE — псевдо-узел с прямым
            // выходом (`parser.go:74-77`), то есть трафик уходит мимо туннеля.
            // REJECT — отказ вместо утечки. Ключ несут все три группы: без него
            // пустая подписка молча выпускала бы и DoH, и трафик заблокированных.
            new MihomoProxyGroup
            {
                Name = ProxyGroup,
                Type = "select",
                Use = [ServersProvider],
                EmptyFallback = "REJECT",
            },
            new MihomoProxyGroup
            {
                Name = ForceGroup,
                Type = "select",
                Use = [ServersProvider],
                EmptyFallback = "REJECT",
            },
            new MihomoProxyGroup
            {
                Name = DnsProxyGroup,
                Type = "fallback",
                Use = [ServersProvider],
                EmptyFallback = "REJECT",
            },
        ],
        RuleProviders = new()
        {
            [RejectRulesProvider] = new MihomoRuleProvider { Path = "./rules/reject.yaml" },
            [AutoRulesProvider] = new MihomoRuleProvider { Path = "./rules/auto.yaml" },
            [ForceRulesProvider] = new MihomoRuleProvider { Path = "./rules/force.yaml" },
            [UserRulesProvider] = new MihomoRuleProvider { Path = "./rules/user.yaml" },
            [DirectRulesProvider] = new MihomoRuleProvider { Path = "./rules/direct.yaml" },
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
            // Политику задаёт строка RULE-SET, поэтому наборов столько же,
            // сколько политик. Порядок: блок раньше проксирования, явное
            // указание пользователя раньше выученного автоматически,
            // ru-blocked раньше ru-available-only-inside.
            $"RULE-SET,{RejectRulesProvider},REJECT",
            $"RULE-SET,{AutoRulesProvider},PROXY",
            $"RULE-SET,{ForceRulesProvider},FORCE",
            $"RULE-SET,{UserRulesProvider},PROXY",
            $"RULE-SET,{DirectRulesProvider},DIRECT",
            "GEOIP,private,DIRECT,no-resolve",
            // ru-blocked-all допустим только потому, что набор вычищен
            // GeoSiteSanitizer: сырой файл валит загрузку категории целиком.
            "GEOSITE,ru-blocked,PROXY",
            "GEOSITE,ru-blocked-all,PROXY",
            "GEOSITE,category-ads-all,REJECT",
            "GEOSITE,win-spy,REJECT",
            "MATCH,DIRECT",
        ];
    }
}
