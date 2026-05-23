namespace JaeZoo.Server.Options;

public sealed class FileAntivirusOptions
{
    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = "Basic"; // Basic | ClamAv
    public int PollSeconds { get; set; } = 3;
    public int BatchSize { get; set; } = 4;
    public int ScanTimeoutSeconds { get; set; } = 45;
    public string ClamAvHost { get; set; } = "127.0.0.1";
    public int ClamAvPort { get; set; } = 3310;
    public int MaxBytesToScanInBasicMode { get; set; } = 1024 * 1024;
}
