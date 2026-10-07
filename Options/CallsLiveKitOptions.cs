namespace JaeZoo.Server.Options;

/// <summary>
/// LiveKit-сервер для личных звонков (1:1). Отдельно от <see cref="LiveKitOptions"/>,
/// чтобы групповые звонки можно было держать на другом сервере.
/// </summary>
public sealed class CallsLiveKitOptions
{
    /// <summary>Публичный WebSocket URL, например wss://sfu.jaezoo.ru.</summary>
    public string Url { get; set; } = "";

    /// <summary>API key. Только на сервере, из переменных окружения.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>API secret. Только на сервере, из переменных окружения.</summary>
    public string ApiSecret { get; set; } = "";

    /// <summary>
    /// Срок жизни токена для входа в комнату. Нужен только на момент подключения:
    /// подключённому клиенту LiveKit продлевает токен сам.
    /// </summary>
    public int TokenTtlMinutes { get; set; } = 10;
}
