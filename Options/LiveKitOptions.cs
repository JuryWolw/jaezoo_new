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

    /// <summary>Public TURN domain for the LiveKit/SFU machine, for example turn-sfu.jaezoo.ru.</summary>
    public string TurnDomain { get; set; } = "";

    /// <summary>Browser ICE policy for group calls. Use "all" in production, "relay" for strict diagnostics.</summary>
    public string IceTransportPolicy { get; set; } = "all";

    /// <summary>When true the client tries TCP/TLS TURN endpoints before UDP TURN endpoints.</summary>
    public bool PreferTcpTurn { get; set; } = false;
}
