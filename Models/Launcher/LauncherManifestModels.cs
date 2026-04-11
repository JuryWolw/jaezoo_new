namespace JaeZoo.Server.Models.Launcher;

public sealed class LauncherManifest
{
    public string Channel { get; set; } = "stable";
    public string Version { get; set; } = string.Empty;
    public string EntryExe { get; set; } = "JaeZoo.Client.Wpf.exe";
    public string? MinLauncherVersion { get; set; }
    public List<LauncherManifestFile> Files { get; set; } = new();
}

public sealed class LauncherManifestFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
