using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models.Mls;
using Microsoft.AspNetCore.SignalR;

namespace JaeZoo.Server.Services.Mls;

/// <summary>
/// Revokes devices that have not been seen for <see cref="MlsDeliveryService.InactiveDeviceLifetime"/>
/// and tells the members of their groups, whose clients then commit the Remove.
/// </summary>
public sealed class MlsDeviceCleanupService(IServiceScopeFactory scopes, IHubContext<ChatHub> hub, ILogger<MlsDeviceCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var mls = scope.ServiceProvider.GetRequiredService<MlsDeliveryService>();
                var revoked = await mls.RevokeInactiveDevicesAsync(stoppingToken);
                foreach (var (device, notify) in revoked)
                {
                    var targets = notify.Append(device.UserId).Distinct().Select(u => u.ToString()).ToList();
                    await hub.Clients.Users(targets).SendAsync("MlsDeviceRevoked", new MlsDeviceRevokedDto(device.UserId, device.Id), stoppingToken);
                }
                if (revoked.Count > 0)
                    log.LogInformation("Revoked {Count} inactive devices.", revoked.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Inactive device cleanup failed.");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
