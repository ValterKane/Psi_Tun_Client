namespace PsiTun.Models;

public record ProbeResult(string Host, bool DirectOk, long DirectMs, bool ProxyOk, long ProxyMs);
