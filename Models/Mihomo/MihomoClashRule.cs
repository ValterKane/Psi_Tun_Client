using YamlDotNet.Serialization;

namespace PsiTun.Models.Mihomo;

/// <summary>
/// Корень файла rule-provider с <c>behavior: classical</c> — <c>{"payload": [...]}</c>.
/// Каждой строкой списка служит правило синтаксиса основного <c>rules:</c>
/// с хвостом-политикой, например <c>DOMAIN-SUFFIX,example.com,PROXY</c>.
/// </summary>
public sealed class MihomoClashRule
{
    [YamlMember(Alias = "payload")] public List<string> Payload { get; set; } = new();
}
