namespace JaeZoo.Server.Models.Launcher;

public sealed class LauncherManifest
{
    public string Channel { get; set; } = "stable";
    public string Version { get; set; } = string.Empty;
    public string EntryExe { get; set; } = "JaeZoo.Client.Wpf.exe";
    public string? MinLauncherVersion { get; set; }

    /// <summary>
    /// Ключ package.zip в бакете (версионный, например stable/client/packages/0.5.8/package.zip).
    /// Пусто у старых манифестов — тогда используется {channel}/{area}/package.zip.
    /// </summary>
    public string? PackageKey { get; set; }

    /// <summary>Список изменений релиза (ReleaseTool). Сервер публикует из него новость «Обновление x.y.z».</summary>
    public string? ReleaseNotes { get; set; }
    // Подпись выпуска (ReleaseTool). Сервер передаёт её лаунчеру как есть — подделать её он не может.
    public long? PackageSize { get; set; }
    public string? PackageSha256 { get; set; }
    public string? Signature { get; set; }
    public string? SignatureAlgorithm { get; set; }

    public List<LauncherManifestFile> Files { get; set; } = new();
}

public sealed class LauncherManifestFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
