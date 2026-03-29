using JaeZoo.Server.Models.Calls;

namespace JaeZoo.Server.Services.Calls;

public sealed class CallAuditService
{
    private readonly ILogger<CallAuditService> _logger;

    public CallAuditService(ILogger<CallAuditService> logger)
    {
        _logger = logger;
    }

    public void Info(CallSession session, string eventName, string? reason = null, object? extra = null)
    {
        _logger.LogInformation(
            "CallEvent={EventName} CallId={CallId} CorrelationId={CorrelationId} CallerUserId={CallerUserId} CalleeUserId={CalleeUserId} DialogId={DialogId} State={State} Reason={Reason} Extra={@Extra}",
            eventName,
            session.CallId,
            session.CorrelationId,
            session.CallerUserId,
            session.CalleeUserId,
            session.DialogId,
            session.State,
            reason,
            extra);
    }

    public void Warn(CallSession session, string eventName, string? reason = null, object? extra = null)
    {
        _logger.LogWarning(
            "CallEvent={EventName} CallId={CallId} CorrelationId={CorrelationId} CallerUserId={CallerUserId} CalleeUserId={CalleeUserId} DialogId={DialogId} State={State} Reason={Reason} Extra={@Extra}",
            eventName,
            session.CallId,
            session.CorrelationId,
            session.CallerUserId,
            session.CalleeUserId,
            session.DialogId,
            session.State,
            reason,
            extra);
    }

    public void Error(CallSession session, Exception ex, string eventName, string? reason = null, object? extra = null)
    {
        _logger.LogError(
            ex,
            "CallEvent={EventName} CallId={CallId} CorrelationId={CorrelationId} CallerUserId={CallerUserId} CalleeUserId={CalleeUserId} DialogId={DialogId} State={State} Reason={Reason} Extra={@Extra}",
            eventName,
            session.CallId,
            session.CorrelationId,
            session.CallerUserId,
            session.CalleeUserId,
            session.DialogId,
            session.State,
            reason,
            extra);
    }
}
