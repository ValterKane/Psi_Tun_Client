using System.Text.Json.Serialization;

namespace PsiTun.Models;

public enum RuleMatchType
{
    ProcessName,
    Domain,
    DomainSuffix,
    DomainKeyword,
    DomainRegex,
    IpCidr,
    Geosite,
    Protocol
}

public enum RuleAction
{
    Proxy,
    Direct,
    Block
}

public class RuleCondition
{
    public RuleMatchType Type { get; set; }
    public string Value { get; set; } = "";
}

public class RoutingRule
{
    public string Description { get; set; } = "";
    public RuleMatchType MatchType { get; set; }
    public string Value { get; set; } = "";
    public RuleAction Action { get; set; }
    private string? _network;
    public string? Network { get => _network; set => _network = value; }

    public string? AppProtocol { get; set; }

    private string? _port;
    public string? Port
    {
        get => _port;
        set => _port = value;
    }
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public bool ForceProxy { get; set; }

    [JsonIgnore]
    public bool IsSingBox => MatchType is RuleMatchType.ProcessName or RuleMatchType.Protocol;

    [JsonIgnore]
    public bool IsXray => !IsSingBox;
}
