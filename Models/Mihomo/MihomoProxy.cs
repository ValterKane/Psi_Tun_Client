using YamlDotNet.Serialization;

namespace PsiTun.Models.Mihomo;

/// <summary>
/// Файл proxy-provider: <c>providers/servers.yaml</c>. Список серверов подписки
/// в формате Clash. Смена сервера правит этот файл и обновляет провайдер через
/// REST, ядро не перезапускается.
///
/// Дисциплина та же, что в <see cref="MihomoConfig"/>: YamlDotNet пишет имена
/// членов дословно, поэтому <see cref="YamlMemberAttribute.Alias"/> стоит на
/// каждом члене, а naming convention не используется — он применяется и к
/// алиасам. Все необязательные поля объявлены nullable: null опускается
/// настройкой <c>OmitNull</c>, тогда как значение по умолчанию базового типа
/// было бы записано в файл.
/// </summary>
public sealed class MihomoProxyProviderFile
{
    [YamlMember(Alias = "proxies")] public List<MihomoProxy> Proxies { get; set; } = new();
}

public sealed class MihomoProxy
{
    [YamlMember(Alias = "name")] public string Name { get; set; } = "";
    [YamlMember(Alias = "type")] public string Type { get; set; } = "";
    [YamlMember(Alias = "server")] public string Server { get; set; } = "";
    [YamlMember(Alias = "port")] public int Port { get; set; }

    [YamlMember(Alias = "uuid")] public string? Uuid { get; set; }
    [YamlMember(Alias = "password")] public string? Password { get; set; }
    [YamlMember(Alias = "cipher")] public string? Cipher { get; set; }
    [YamlMember(Alias = "udp")] public bool? Udp { get; set; }
    [YamlMember(Alias = "flow")] public string? Flow { get; set; }
    [YamlMember(Alias = "tls")] public bool? Tls { get; set; }
    [YamlMember(Alias = "servername")] public string? Servername { get; set; }
    [YamlMember(Alias = "alpn")] public List<string>? Alpn { get; set; }
    [YamlMember(Alias = "client-fingerprint")] public string? ClientFingerprint { get; set; }
    [YamlMember(Alias = "skip-cert-verify")] public bool? SkipCertVerify { get; set; }
    [YamlMember(Alias = "encryption")] public string? Encryption { get; set; }
    [YamlMember(Alias = "network")] public string? Network { get; set; }
    [YamlMember(Alias = "reality-opts")] public MihomoRealityOpts? RealityOpts { get; set; }
    [YamlMember(Alias = "xhttp-opts")] public MihomoXhttpOpts? XhttpOpts { get; set; }
    [YamlMember(Alias = "ws-opts")] public MihomoWsOpts? WsOpts { get; set; }
    [YamlMember(Alias = "grpc-opts")] public MihomoGrpcOpts? GrpcOpts { get; set; }
}

/// <summary>
/// <c>ws-opts</c>. Заголовок записывается с заглавной <c>Host</c> — так в
/// документации mihomo; ядро различает регистр ключа заголовка.
/// </summary>
public sealed class MihomoWsOpts
{
    [YamlMember(Alias = "path")] public string? Path { get; set; }
    [YamlMember(Alias = "headers")] public Dictionary<string, string>? Headers { get; set; }
}

public sealed class MihomoGrpcOpts
{
    [YamlMember(Alias = "grpc-service-name")] public string? GrpcServiceName { get; set; }
}

/// <summary>
/// <c>reality-opts</c>. mihomo знает три ключа — <c>public-key</c>,
/// <c>short-id</c> (wiki.metacubex.one/en/config/proxies/vless/) и
/// <c>support-x25519mlkem768</c>. Поля <c>spider-x</c> у mihomo нет, поэтому
/// <c>VpnServer.SpiderX</c> не переносится.
///
/// <c>support-x25519mlkem768</c> обязателен против серверов Xray-core ≥ v26.9.8:
/// REALITY отвергает ClientHello без key-share X25519MLKEM768 и молча уводит
/// рукопожатие на <c>dest</c> (библиотека REALITY, коммит 8cdf7bf от 08.09.2026).
/// У mihomo ключ по умолчанию <c>false</c>, и тогда ядро вырезает группу из
/// ClientHello (<c>BuildRemovedX25519MLKEM768HandshakeState</c>) — рукопожатие
/// не проходит даже с <c>client-fingerprint: chrome</c>. Ключ работает только с
/// профилем chrome: firefox и safari группу не предлагают.
/// </summary>
public sealed class MihomoRealityOpts
{
    [YamlMember(Alias = "public-key")] public string? PublicKey { get; set; }
    [YamlMember(Alias = "short-id")] public string? ShortId { get; set; }
    [YamlMember(Alias = "support-x25519mlkem768")] public bool? SupportX25519MLKEM768 { get; set; }
}

/// <summary>
/// <c>xhttp-opts</c>. Переносятся только ключи, заданные подпиской:
/// <c>path</c>, <c>host</c>, <c>mode</c>, <c>x-padding-bytes</c> и
/// <c>reuse-settings</c>. Остальные ключи mihomo
/// (<c>x-padding-placement</c>, <c>session-*</c>, <c>seq-*</c>,
/// <c>uplink-*</c>, <c>download-settings</c>) Xray не отдаёт — умолчания ядра
/// совпадают с умолчаниями Xray.
/// </summary>
public sealed class MihomoXhttpOpts
{
    [YamlMember(Alias = "path")] public string? Path { get; set; }
    [YamlMember(Alias = "host")] public string? Host { get; set; }
    [YamlMember(Alias = "mode")] public string? Mode { get; set; }
    [YamlMember(Alias = "x-padding-bytes")] public string? XPaddingBytes { get; set; }
    [YamlMember(Alias = "reuse-settings")] public MihomoReuseSettings? ReuseSettings { get; set; }
}

/// <summary>
/// <c>xhttp-opts.reuse-settings</c> — прежний XMUX. Имена ключей у mihomo
/// kebab-case, у Xray camelCase (<c>maxConcurrency</c> → <c>max-concurrency</c>),
/// поэтому перенос дословный невозможен. <c>max-concurrency</c> и
/// <c>max-connections</c> у mihomo конфликтуют — заполняется только первый.
/// </summary>
public sealed class MihomoReuseSettings
{
    [YamlMember(Alias = "c-max-reuse-times")] public string? CMaxReuseTimes { get; set; }
    [YamlMember(Alias = "h-keep-alive-period")] public string? HKeepAlivePeriod { get; set; }
    [YamlMember(Alias = "h-max-request-times")] public string? HMaxRequestTimes { get; set; }
    [YamlMember(Alias = "h-max-reusable-secs")] public string? HMaxReusableSecs { get; set; }
    [YamlMember(Alias = "max-concurrency")] public string? MaxConcurrency { get; set; }
    [YamlMember(Alias = "max-connections")] public string? MaxConnections { get; set; }
}
