using System.IO;
using PsiTun.Models;
using PsiTun.Services;

namespace PsiTun.Tests;

public class RoutingRuleServiceTests
{
    /// <summary>
    /// Дефолтное правило, исчезнувшее из кода, выпадает из файла: payload-провайдер
    /// mihomo разбирает строки построчно и нераспознанное правило пропускает с
    /// level=warning, теряя покрытие молча. Пользовательское правило остаётся.
    /// </summary>
    [Fact]
    public void Load_DropsRemovedDefaultAndKeepsUserRules()
    {
        var path = Path.Combine(Path.GetTempPath(), $"psi-rules-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
            [
              {"MatchType":6,"Value":"ru-blocked-all","Action":0,"IsDefault":true},
              {"MatchType":6,"Value":"ru-blocked","Action":0,"IsDefault":true},
              {"MatchType":1,"Value":"declared.example","Action":0,"IsDefault":false}
            ]
            """);

            var rules = new RoutingRuleService(path).Load();

            Assert.DoesNotContain(rules, r => r.Value == "ru-blocked-all");
            Assert.Contains(rules, r => r.Value == "ru-blocked");
            Assert.Contains(rules, r => r.Value == "declared.example");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetDefaults_HasNoRemovedLayer()
    {
        Assert.DoesNotContain(RoutingRuleService.GetDefaults(), r => r.Value == "ru-blocked-all");
    }
}
