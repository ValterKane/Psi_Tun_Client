namespace PsiTun.Models;

public enum VpnProtocol
{
    VLess,
    VMess,
    Trojan,
    Shadowsocks
}

public class VpnServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public VpnProtocol Protocol { get; set; }
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string Uuid { get; set; } = "";      // VLESS/VMess
    public string Password { get; set; } = "";   // Trojan/SS
    public string Cipher { get; set; } = "";     // SS method

    // Transport
    public string Network { get; set; } = "tcp";  // tcp, ws, grpc, xhttp
    public string Security { get; set; } = "none"; // tls, reality
    public string Sni { get; set; } = "";
    public string Alpn { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string Flow { get; set; } = "";

    // WebSocket/HTTP
    public string Path { get; set; } = "";
    public string Host { get; set; } = "";

    // gRPC
    public string ServiceName { get; set; } = "";

    // XHTTP
    public string XhttpMode { get; set; } = "";
    public string SpiderX { get; set; } = "";
    public string XpaddingBytes { get; set; } = "";
    public string XmuxConfig { get; set; } = "";

    // Reality extras
    public string Encryption { get; set; } = "";
    public string Mldsa65Verify { get; set; } = "";
    public string Finalmask { get; set; } = "";

    // WS early data
    public int EarlyData { get; set; }

    // Metadata
    public int LatencyMs { get; set; }
    public string RawLink { get; set; } = "";
}
