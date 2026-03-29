namespace JaeZoo.Server.Services.Calls;

public sealed class TurnOptions
{
    public string Secret { get; set; } = string.Empty;
    public int TtlSeconds { get; set; } = 3600;
    public string Realm { get; set; } = "turn.jaezoo.ru";
    public string[] Urls { get; set; } =
    [
        "stun:turn.jaezoo.ru:3478",
        "turn:turn.jaezoo.ru:3478?transport=udp",
        "turn:turn.jaezoo.ru:3478?transport=tcp",
        "turns:turn.jaezoo.ru:5349?transport=tcp"
    ];
}
