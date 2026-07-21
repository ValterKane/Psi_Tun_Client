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

    // TUN settings
    public string TunName { get; set; } = "sing-tun";
    public string TunAddress { get; set; } = "172.18.0.1/30";
    public string TunGateway { get; set; } = "172.18.0.2";
    public int TunMtu { get; set; } = 1500;
    public string TunDns { get; set; } = "172.18.0.2";
    public bool AutoRoute { get; set; } = true;
    public string TunStack { get; set; } = "system";
    public bool EnableSniffing { get; set; } = true;

    // Proxy ports
    public int HttpPort { get; set; } = 10809;
    public int SocksPort { get; set; } = 10808;
    public int XrayInboundPort { get; set; } = 10810;

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
