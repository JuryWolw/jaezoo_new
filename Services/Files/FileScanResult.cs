namespace JaeZoo.Server.Services.Files;

public sealed record FileScanResult(
    bool IsDangerous,
    bool IsClean,
    string Engine,
    string? Reason = null)
{
    public static FileScanResult Clean(string engine) => new(false, true, engine, null);
    public static FileScanResult Dangerous(string engine, string reason) => new(true, false, engine, reason);
    public static FileScanResult Failed(string engine, string reason) => new(false, false, engine, reason);
}
