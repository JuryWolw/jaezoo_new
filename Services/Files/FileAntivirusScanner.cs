using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using JaeZoo.Server.Models;
using JaeZoo.Server.Options;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Files;

public sealed class FileAntivirusScanner(
    IOptions<FileAntivirusOptions> options,
    ILogger<FileAntivirusScanner> log) : IFileAntivirusScanner
{
    private static readonly byte[] EicarBytes = Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
    private readonly FileAntivirusOptions _options = options.Value;

    public async Task<FileScanResult> ScanAsync(ChatFile file, Stream content, CancellationToken ct)
    {
        var mode = (_options.Mode ?? "Basic").Trim();
        if (mode.Equals("ClamAv", StringComparison.OrdinalIgnoreCase))
            return await ScanWithClamAvAsync(file, content, ct);

        return await ScanBasicAsync(file, content, ct);
    }

    private async Task<FileScanResult> ScanBasicAsync(ChatFile file, Stream content, CancellationToken ct)
    {
        // Режим без ложных срабатываний: блокируем только официальный EICAR-тест.
        // Реальные подозрительные расширения уже помечаются метаданными, но не блокируются.
        var max = Math.Max(16 * 1024, _options.MaxBytesToScanInBasicMode);
        var buffer = new byte[Math.Min(64 * 1024, max)];
        var window = new List<byte>(EicarBytes.Length + buffer.Length);
        var total = 0;

        while (total < max)
        {
            var toRead = Math.Min(buffer.Length, max - total);
            var read = await content.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read <= 0) break;
            total += read;

            window.AddRange(buffer.AsSpan(0, read).ToArray());
            if (ContainsSequence(window, EicarBytes))
                return FileScanResult.Dangerous("Basic/EICAR", "Обнаружен EICAR test signature.");

            var keep = EicarBytes.Length - 1;
            if (window.Count > keep)
                window.RemoveRange(0, window.Count - keep);
        }

        return FileScanResult.Clean("Basic/EICAR");
    }

    private async Task<FileScanResult> ScanWithClamAvAsync(ChatFile file, Stream content, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.ScanTimeoutSeconds)));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_options.ClamAvHost, _options.ClamAvPort, timeoutCts.Token);
            await using var ns = client.GetStream();

            var command = Encoding.ASCII.GetBytes("zINSTREAM\0");
            await ns.WriteAsync(command.AsMemory(0, command.Length), timeoutCts.Token);

            var buffer = new byte[1024 * 1024];
            while (true)
            {
                var read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token);
                if (read <= 0) break;

                var len = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(len, read);
                await ns.WriteAsync(len, timeoutCts.Token);
                await ns.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);
            }

            await ns.WriteAsync(new byte[4], timeoutCts.Token);
            await ns.FlushAsync(timeoutCts.Token);

            var responseBuffer = new byte[4096];
            var responseRead = await ns.ReadAsync(responseBuffer.AsMemory(0, responseBuffer.Length), timeoutCts.Token);
            var response = Encoding.UTF8.GetString(responseBuffer, 0, Math.Max(0, responseRead)).Trim('\0', '\r', '\n', ' ');

            if (response.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
                return FileScanResult.Dangerous("ClamAV", response);

            if (response.Contains("OK", StringComparison.OrdinalIgnoreCase))
                return FileScanResult.Clean("ClamAV");

            return FileScanResult.Failed("ClamAV", string.IsNullOrWhiteSpace(response) ? "Empty ClamAV response." : response);
        }
        catch (OperationCanceledException ex)
        {
            log.LogWarning(ex, "ClamAV scan timed out for FileId={FileId}", file.Id);
            return FileScanResult.Failed("ClamAV", "ClamAV scan timeout.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ClamAV scan failed for FileId={FileId}", file.Id);
            return FileScanResult.Failed("ClamAV", ex.Message);
        }
    }

    private static bool ContainsSequence(List<byte> source, byte[] pattern)
    {
        if (source.Count < pattern.Length) return false;
        for (var i = 0; i <= source.Count - pattern.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j]) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }
}
