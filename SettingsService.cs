using System.IO;
using System.Text.Json;

namespace PsiTun;

public class SettingsService
{
    public string SubscriptionUrl { get; set; } = "";
    public int LastServerIndex { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool AutoStart { get; set; } = true;
    public bool UseTun { get; set; } = true;
    public bool UsePac { get; set; } = true;
    public bool AutoProxyEnabled { get; set; } = true;
    public bool AutoScroll { get; set; } = true;

    // Хосты-заглушки провайдеров: редирект на такой хост — признак блокировки.
    // lawfilter.ertelecom.ru подтверждён тремя независимыми свидетельствами
    // (см. план, Фаза 10). Остальные кандидаты проверку не прошли и сюда не внесены.
    public List<string> StubHosts { get; set; } = ["lawfilter.ertelecom.ru"];

    // TUN settings. Ни адреса, ни шлюза, ни DNS здесь нет: ключа `tun.inet4-address`
    // в разборе конфига mihomo v1.19.32 не существует (поле закомментировано,
    // config/config.go:284), адрес адаптера ядро выводит из `dns.fake-ip-range`
    // с маской /30; DNS живёт в секции dns каркаса.
    public string TunName { get; set; } = "sing-tun";
    public int TunMtu { get; set; } = 1476;
    public bool AutoRoute { get; set; } = true;
    public bool StrictRoute { get; set; } = true;
    public string TunStack { get; set; } = "system";
    public bool EnableSniffing { get; set; } = true;

    // Proxy ports
    public int HttpPort { get; set; } = 10809;
    public int SocksPort { get; set; } = 10808;

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }

    public static SettingsService Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<SettingsService>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        return new();
    }
}
