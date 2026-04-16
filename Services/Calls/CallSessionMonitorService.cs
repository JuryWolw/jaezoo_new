using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models.Calls;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Calls;

public sealed class CallSessionMonitorService : BackgroundService
{
    private readonly CallSessionService _sessions;
    private readonly CallAuditService _audit;
    private readonly IHubContext<CallsHub> _hub;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CallSessionMonitorService> _logger;
    private readonly CallLifecycleOptions _options;

    public CallSessionMonitorService(
        CallSessionService sessions,
        CallAuditService audit,
        IHubContext<CallsHub> hub,
        IServiceScopeFactory scopeFactory,
        IOptions<CallLifecycleOptions> options,
        ILogger<CallSessionMonitorService> logger)
    {
        _sessions = sessions;
        _audit = audit;
        _hub = hub;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Call session monitor sweep failed.");
            }

            try
            {
                await Task.Delay(_options.CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var session in _sessions.GetAll())
        {
            if (!_sessions.TryGet(session.CallId, out var live) || live is null)
                continue;

            CleanupDecision? decision;
            lock (live)
            {
                decision = Evaluate(live, nowUtc);
                if (decision is null)
                    continue;

                live.State = decision.State;
                live.EndReason = decision.Reason;
                live.EndedAtUtc ??= nowUtc;
                live.LastActivityAtUtc = nowUtc;
            }

            var appliedDecision = decision;
            if (appliedDecision is null)
                continue;

            if (!_sessions.TryRemove(live.CallId, out var removed) || removed is null)
                continue;

            using (var scope = _scopeFactory.CreateScope())
            {
                var history = scope.ServiceProvider.GetRequiredService<CallHistoryService>();
                await history.TryPersistTerminalEventAsync(removed, ct);
            }

            _audit.Warn(removed, appliedDecision.AuditEvent, appliedDecision.Reason);
            var dto = new CallStateChangedDto
            {
                CallId = removed.CallId,
                CallerUserId = removed.CallerUserId,
                CalleeUserId = removed.CalleeUserId,
                DialogId = removed.DialogId,
                Type = removed.Type,
                State = removed.State,
                OccurredAtUtc = nowUtc,
                Reason = removed.EndReason,
                CorrelationId = removed.CorrelationId
            };

            await _hub.Clients.Users(removed.CallerUserId.ToString(), removed.CalleeUserId.ToString())
                .SendAsync(appliedDecision.ClientEvent, dto, ct);
        }
    }

    private CleanupDecision? Evaluate(CallSession session, DateTime nowUtc)
    {
        if (!CallSessionService.IsActiveState(session.State))
            return null;

        var lastSignalUtc = new DateTime?[]
        {
            session.LastActivityAtUtc,
            session.LastOfferAtUtc,
            session.LastAnswerAtUtc,
            session.LastIceCandidateAtUtc,
            session.AcceptedAtUtc,
            session.ConnectedAtUtc,
            session.CreatedAtUtc
        }.Where(x => x.HasValue).Max() ?? session.CreatedAtUtc;

        switch (session.State)
        {
            case CallState.Pending:
            case CallState.Ringing:
                if (nowUtc - session.CreatedAtUtc >= _options.RingTimeout)
                {
                    return new CleanupDecision(CallState.Missed, "missed-timeout", "call.missed.timeout", "call.missed");
                }
                break;

            case CallState.Accepted:
                if (nowUtc - (session.AcceptedAtUtc ?? session.CreatedAtUtc) >= _options.AcceptTimeout)
                {
                    return new CleanupDecision(CallState.Failed, "accept-timeout", "call.accept.timeout", "call.failed");
                }
                break;

            case CallState.Connecting:
                if (nowUtc - lastSignalUtc >= _options.ConnectingTimeout)
                {
                    return new CleanupDecision(CallState.Failed, "signaling-timeout", "call.connecting.timeout", "call.failed");
                }
                break;

            case CallState.Connected:
                var callerOnline = _sessions.IsUserOnline(session.CallerUserId);
                var calleeOnline = _sessions.IsUserOnline(session.CalleeUserId);
                var callerLastSeen = _sessions.GetUserLastSeenUtc(session.CallerUserId) ?? session.ConnectedAtUtc ?? session.CreatedAtUtc;
                var calleeLastSeen = _sessions.GetUserLastSeenUtc(session.CalleeUserId) ?? session.ConnectedAtUtc ?? session.CreatedAtUtc;
                var bothOfflineTooLong = !callerOnline && !calleeOnline
                    && (nowUtc - callerLastSeen >= _options.DisconnectGracePeriod)
                    && (nowUtc - calleeLastSeen >= _options.DisconnectGracePeriod);

                if (bothOfflineTooLong)
                    return new CleanupDecision(CallState.Ended, "disconnect-timeout", "call.disconnect.timeout", "call.ended");

                if (nowUtc - lastSignalUtc >= _options.ConnectedIdleTimeout)
                    return new CleanupDecision(CallState.Ended, "heartbeat-timeout", "call.heartbeat.timeout", "call.ended");
                break;
        }

        return null;
    }

    private sealed record CleanupDecision(CallState State, string Reason, string AuditEvent, string ClientEvent);
}
