using JaeZoo.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace JaeZoo.Server.Services.Launcher;

/// <summary>
/// Следит за манифестом клиента в бакете и, когда выходит новая версия, сообщает всем онлайн-клиентам
/// через ChatHub событием <c>ClientUpdateAvailable</c> ({ version }). Клиент сам решает, новее ли это его версии.
/// При старте сервера рассылки нет: клиенты проверяют версию сами при подключении.
/// </summary>
public sealed class ClientUpdateNotifierService : BackgroundService
{
    public const string HubEventName = "ClientUpdateAvailable";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(10);

    private readonly ILauncherUpdateService _updates;
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<ClientUpdateNotifierService> _logger;
    private string? _knownVersion;

    public ClientUpdateNotifierService(
        ILauncherUpdateService updates,
        IHubContext<ChatHub> hub,
        ILogger<ClientUpdateNotifierService> logger)
    {
        _updates = updates;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Бакет может быть не настроен (локальная разработка) — не шумим каждые 2 минуты.
                _logger.LogWarning(ex, "Client update check failed; next attempt in {Delay}.", ErrorBackoff);
                delay = ErrorBackoff;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var manifest = await _updates.GetClientManifestAsync(null, cancellationToken);
        var version = manifest.Version?.Trim();
        if (string.IsNullOrWhiteSpace(version))
            return;

        if (_knownVersion == null)
        {
            _knownVersion = version;
            _logger.LogInformation("Client update notifier: current client version {Version}.", version);
            return;
        }

        if (string.Equals(_knownVersion, version, StringComparison.OrdinalIgnoreCase))
            return;

        _knownVersion = version;
        _logger.LogInformation("Client update notifier: new client version {Version}, notifying online users.", version);
        await _hub.Clients.All.SendAsync(HubEventName, new { version }, cancellationToken);
    }
}
