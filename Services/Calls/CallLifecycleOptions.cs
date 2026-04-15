namespace JaeZoo.Server.Services.Calls;

public sealed class CallLifecycleOptions
{
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RingTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan AcceptTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectingTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan ConnectedIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan DisconnectGracePeriod { get; set; } = TimeSpan.FromSeconds(35);
}
