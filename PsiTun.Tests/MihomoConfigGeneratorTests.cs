using PsiTun.Services;

namespace PsiTun.Tests;

public class MihomoConfigGeneratorTests
{
    [Fact]
    public void Generate_ContainsTunAndControllerAndProviders()
    {
        var yaml = MihomoConfigGenerator.Generate(TestSettings.Create(), "s3cr3t");

        Assert.Contains("external-controller: 127.0.0.1:10812", yaml);
        Assert.Contains("enable: true", yaml);          // tun
        Assert.Contains("psi-servers", yaml);           // proxy-provider
        Assert.Contains("rule-providers:", yaml);
        Assert.Contains("MATCH,DIRECT", yaml);
    }

    /// <summary>
    /// ru-blocked-all валит загрузку mihomo: категория отвергается целиком на 13
    /// записях с хвостовой точкой (trailing dot is not allowed). В каркасе —
    /// только ru-blocked, и в rules, и в nameserver-policy.
    /// </summary>
    [Fact]
    public void Generate_UsesRuBlocked_NotRuBlockedAll()
    {
        var yaml = MihomoConfigGenerator.Generate(TestSettings.Create(), "s3cr3t");

        Assert.DoesNotContain("ru-blocked-all", yaml);
        Assert.Contains("GEOSITE,ru-blocked,PROXY", yaml);
        Assert.Contains("geosite:ru-blocked", yaml);

        var iBlocked = yaml.IndexOf("GEOSITE,ru-blocked,", StringComparison.Ordinal);
        var iAds = yaml.IndexOf("category-ads-all", StringComparison.Ordinal);
        var iMatch = yaml.IndexOf("MATCH,DIRECT", StringComparison.Ordinal);

        Assert.True(iBlocked > 0 && iAds > iBlocked && iMatch > iAds,
            $"Порядок правил нарушен: blocked={iBlocked}, ads={iAds}, match={iMatch}");
    }

    /// <summary>
    /// DNS-модуль выключен без <c>dns.enable: true</c>: при <c>tun.dns-hijack</c>
    /// весь трафик порта 53 уходит в модуль, которого нет, и ни одно имя не
    /// разрешается (измерено: `DNS section is disabled`). <c>fallback</c> не
    /// задаём — он включает fallback-filter с geoip-code CN.
    /// </summary>
    [Fact]
    public void Generate_EnablesDnsModuleAndDropsFallback()
    {
        // YamlDotNet пишет CRLF: приводим к LF, иначе поиск секции не сходится.
        var yaml = MihomoConfigGenerator.Generate(TestSettings.Create(), "s3cr3t")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = yaml.IndexOf("\ndns:\n", StringComparison.Ordinal);
        Assert.True(start > 0, "секция dns не найдена");

        var rest = yaml[(start + 1)..];
        var nextTop = rest.IndexOf("\nproxy-providers:", StringComparison.Ordinal);
        var dnsBlock = nextTop > 0 ? rest[..nextTop] : rest;

        Assert.Contains("enable: true", dnsBlock);
        Assert.DoesNotContain("fallback", dnsBlock);
        Assert.Contains("nameserver-policy:", dnsBlock);
        Assert.Contains("geosite:ru-blocked", dnsBlock);
    }

    /// <summary>
    /// mihomo принимает только нижний регистр в ключах. Тест ловит регресс
    /// сериализации: YamlDotNet пишет имена членов дословно, поэтому каждый
    /// член модели обязан нести YamlMember(Alias).
    /// </summary>
    [Fact]
    public void Generate_KeysHaveNoUppercaseLetters()
    {
        var yaml = MihomoConfigGenerator.Generate(TestSettings.Create(), "s3cr3t");

        var offenders = new List<string>();
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] == '-' || line[0] == '#') continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon];
            if (key.Any(char.IsUpper)) offenders.Add(key);
        }

        Assert.True(offenders.Count == 0,
            $"Ключи с заглавными буквами: {string.Join(", ", offenders)}");
    }
}
