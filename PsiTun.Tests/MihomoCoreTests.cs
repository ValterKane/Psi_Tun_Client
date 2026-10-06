using System.IO;
using PsiTun.Services;

namespace PsiTun.Tests;

public class MihomoCoreTests
{
    /// <summary>
    /// mihomo -t с несуществующим путём не падает, а создаёт по нему шаблон и
    /// валидирует его (измерено). Проверка существования файла обязана сработать
    /// раньше запуска ядра, иначе предвалидация подтверждает конфиг, которого нет.
    /// Имя ядра фиктивно: до запуска процесса дело не доходит.
    /// </summary>
    [Fact]
    public void ValidateConfig_IsFalseForMissingFileWithoutStartingCore()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"psi-missing-{Guid.NewGuid():N}.yaml");

        Assert.False(MihomoCore.ValidateConfig("no-such-mihomo.exe", Path.GetTempPath(), missing));
        Assert.False(File.Exists(missing));
    }

    /// <summary>
    /// Готовность TUN определяется по строке ядра. Образец строки снят живым
    /// прогоном (Task 6.3); строка о повторной попытке создания адаптера идёт
    /// **до** готовности и признаком служить не должна.
    /// </summary>
    [Fact]
    public void IsTunReadyLine_AcceptsAdapterLineOnly()
    {
        const string ready = "level=info msg=\"Tun adapter listening at: sing-tun([198.18.0.1/30]), mtu: 1476, ip stack: System\"";
        const string retry = "level=warning msg=\"Start Tun interface timeout: The device is not ready for use. [retrying 1/3]\"";
        const string noise = "level=info msg=\"[TCP] 198.18.0.1:60172(exe) --> mtalk.google.com:5228 using DIRECT\"";

        Assert.True(MihomoCore.IsTunReadyLine(ready));
        Assert.False(MihomoCore.IsTunReadyLine(retry));
        Assert.False(MihomoCore.IsTunReadyLine(noise));
        Assert.False(MihomoCore.IsTunReadyLine(""));
    }

    [Fact]
    public void NewSecret_IsUrlSafeAndNonRepeating()
    {
        var a = MihomoCore.NewSecret();
        var b = MihomoCore.NewSecret();

        Assert.NotEqual(a, b);
        Assert.DoesNotContain("+", a);
        Assert.DoesNotContain("/", a);
        Assert.DoesNotContain("=", a);
        Assert.True(a.Length >= 32, $"Секрет короткий: {a.Length}");
    }

    [Fact]
    public void NewSecret_HasNoCharactersNeedingEscapingInUrlOrYaml()
    {
        var secret = MihomoCore.NewSecret();

        Assert.All(secret, c => Assert.True(char.IsLetterOrDigit(c) || c is '-' or '_',
            $"Недопустимый символ в секрете: '{c}'"));
        Assert.Equal(secret, Uri.EscapeDataString(secret));
    }
}
