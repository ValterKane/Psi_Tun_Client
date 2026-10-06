namespace PsiTun.Tests;

internal static class TestSettings
{
    public static SettingsService Create() => new()
    {
        SocksPort = 10808,
        HttpPort = 10809,
        UseTun = true,
        TunName = "sing-tun",
        TunAddress = "172.18.0.1/30",
        TunMtu = 1476,
        TunStack = "system",
        AutoRoute = true,
        StrictRoute = true,
        EnableSniffing = true,
    };
}
