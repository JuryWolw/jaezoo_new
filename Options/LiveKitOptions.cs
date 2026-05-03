namespace JaeZoo.Server.Options;

public sealed class LiveKitOptions
{
    /// <summary>Public LiveKit websocket URL used by clients, for example wss://sfu.jaezoo.ru.</summary>
    public string Url { get; set; } = "";

    /// <summary>LiveKit API key. Keep it on the backend only.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>LiveKit API secret. Keep it on the backend only.</summary>
    public string ApiSecret { get; set; } = "";

    /// <summary>LiveKit access token lifetime in minutes.</summary>
    public int TokenTtlMinutes { get; set; } = 120;

    /// <summary>How long a voice participant may be silent before being considered disconnected.</summary>
    public int ParticipantStaleSeconds { get; set; } = 90;
}
