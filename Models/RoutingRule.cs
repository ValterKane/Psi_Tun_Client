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

public class RoutingRule
{
    public string Description { get; set; } = "";
    public RuleMatchType MatchType { get; set; }
    public string Value { get; set; } = "";
    public RuleAction Action { get; set; }
    public string? Protocol { get; set; }
    public string? Port { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }

    // Auto-detect target core
    [JsonIgnore]
    public bool IsSingBox => MatchType is RuleMatchType.ProcessName or RuleMatchType.Protocol;

    [JsonIgnore]
    public bool IsXray => !IsSingBox;
}
