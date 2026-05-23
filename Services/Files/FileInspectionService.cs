using System.Security.Cryptography;
using JaeZoo.Server.Models.Files;

namespace JaeZoo.Server.Services.Files;

public sealed class FileInspectionService(ILogger<FileInspectionService> log)
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".scr", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".msi",
        ".com", ".pif", ".jar", ".reg", ".hta", ".lnk", ".iso", ".apk"
    };

    public async Task<(string TempPath, FileInspectionResult Result)> CopyToTempAndInspectAsync(
        IFormFile file,
        string tempRoot,
        CancellationToken ct)
    {
        Directory.CreateDirectory(tempRoot);

        var safeName = SanitizeFileName(file.FileName);
        var extension = Path.GetExtension(safeName);
        if (extension.Length > 32) extension = extension[..32];

        var tempPath = Path.Combine(tempRoot, $"upload-{Guid.NewGuid():N}.tmp");
        var firstBytes = new byte[512];
        var firstCount = 0;

        await using (var input = file.OpenReadStream())
        await using (var output = File.Create(tempPath))
        using (var sha = SHA256.Create())
        {
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                if (firstCount < firstBytes.Length)
                {
                    var toCopy = Math.Min(read, firstBytes.Length - firstCount);
                    Buffer.BlockCopy(buffer, 0, firstBytes, firstCount, toCopy);
                    firstCount += toCopy;
                }

                sha.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var detected = DetectContentType(firstBytes.AsSpan(0, firstCount), extension, file.ContentType);
            var kind = Classify(detected, extension);
            var isDangerous = ExecutableExtensions.Contains(extension);
            var riskNote = isDangerous
                ? "Executable/script-like file. Upload is allowed, but file is marked as potentially dangerous."
                : null;

            if (isDangerous)
                log.LogInformation("Potentially dangerous file extension detected. Name={FileName} Ext={Ext}", safeName, extension);

            return (tempPath, new FileInspectionResult(
                kind,
                detected,
                safeName,
                extension,
                Convert.ToHexString(sha.Hash!).ToLowerInvariant(),
                kind == StoredFileKind.Photo || detected.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
                kind == StoredFileKind.Video || detected.StartsWith("video/", StringComparison.OrdinalIgnoreCase),
                kind == StoredFileKind.Music || detected.StartsWith("audio/", StringComparison.OrdinalIgnoreCase),
                isDangerous,
                riskNote,
                isDangerous ? FileScanStatus.Suspicious : FileScanStatus.MetadataChecked
            ));
        }
    }

    public static string SanitizeFileName(string? name)
    {
        name ??= "file";
        name = name.Trim();
        if (name.Length == 0) name = "file";
        if (name.Length > 200) name = name[..200];
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static StoredFileKind Classify(string detectedContentType, string extension)
    {
        if (detectedContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return StoredFileKind.Photo;
        if (detectedContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return StoredFileKind.Video;
        if (detectedContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return StoredFileKind.Music;

        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".heif" or ".avif" => StoredFileKind.Photo,
            ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v" => StoredFileKind.Video,
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac" => StoredFileKind.Music,
            _ => StoredFileKind.File
        };
    }

    private static string DetectContentType(ReadOnlySpan<byte> b, string extension, string? browserContentType)
    {
        if (StartsWith(b, new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (StartsWith(b, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (StartsWithAscii(b, "GIF87a") || StartsWithAscii(b, "GIF89a")) return "image/gif";
        if (b.Length >= 12 && StartsWithAscii(b, "RIFF") && EncodingAt(b, 8, "WEBP")) return "image/webp";
        if (StartsWithAscii(b, "%PDF")) return "application/pdf";
        if (b.Length >= 12 && EncodingAt(b, 4, "ftyp")) return DetectIsoBaseMedia(b, extension, browserContentType);
        if (StartsWithAscii(b, "ID3") || LooksLikeMp3Frame(b)) return "audio/mpeg";
        if (b.Length >= 12 && StartsWithAscii(b, "RIFF") && EncodingAt(b, 8, "WAVE")) return "audio/wav";
        if (StartsWithAscii(b, "OggS")) return "application/ogg";
        if (StartsWithAscii(b, "fLaC")) return "audio/flac";
        if (StartsWith(b, new byte[] { 0x50, 0x4B, 0x03, 0x04 }) || StartsWith(b, new byte[] { 0x50, 0x4B, 0x05, 0x06 }) || StartsWith(b, new byte[] { 0x50, 0x4B, 0x07, 0x08 })) return "application/zip";
        if (StartsWith(b, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 })) return "application/vnd.rar";
        if (StartsWith(b, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return "application/x-7z-compressed";
        if (StartsWith(b, new byte[] { 0x1F, 0x8B })) return "application/gzip";
        if (StartsWith(b, new byte[] { 0x4D, 0x5A })) return "application/vnd.microsoft.portable-executable";

        return FromExtensionOrBrowser(extension, browserContentType);
    }

    private static string DetectIsoBaseMedia(ReadOnlySpan<byte> b, string extension, string? browserContentType)
    {
        var ext = extension.ToLowerInvariant();
        if (ext is ".m4a" or ".aac") return "audio/mp4";
        return "video/mp4";
    }

    private static string FromExtensionOrBrowser(string extension, string? browserContentType)
    {
        var ext = extension.ToLowerInvariant();
        var byExt = ext switch
        {
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg" => "application/ogg",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".zip" => "application/zip",
            ".rar" => "application/vnd.rar",
            ".7z" => "application/x-7z-compressed",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(byExt)) return byExt;
        if (!string.IsNullOrWhiteSpace(browserContentType)) return browserContentType.Trim();
        return "application/octet-stream";
    }

    private static bool StartsWith(ReadOnlySpan<byte> b, ReadOnlySpan<byte> sig) => b.Length >= sig.Length && b[..sig.Length].SequenceEqual(sig);
    private static bool StartsWithAscii(ReadOnlySpan<byte> b, string s) => EncodingAt(b, 0, s);
    private static bool EncodingAt(ReadOnlySpan<byte> b, int offset, string s)
    {
        if (b.Length < offset + s.Length) return false;
        for (var i = 0; i < s.Length; i++)
            if (b[offset + i] != (byte)s[i]) return false;
        return true;
    }

    private static bool LooksLikeMp3Frame(ReadOnlySpan<byte> b) => b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0;
}
