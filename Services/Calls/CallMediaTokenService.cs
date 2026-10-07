using JaeZoo.Server.Models;
using JaeZoo.Server.Options;
using JaeZoo.Server.Services.Voice;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Calls;

/// <summary>Выдаёт токены LiveKit для медиа личного звонка: одна комната на звонок, только его участникам.</summary>
public sealed class CallMediaTokenService(IOptions<CallsLiveKitOptions> options)
{
    private readonly CallsLiveKitOptions _options = options.Value;

    public string Url => LiveKitJwt.NormalizeUrl(_options.Url);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Url) &&
        !string.IsNullOrWhiteSpace(_options.ApiKey) &&
        !string.IsNullOrWhiteSpace(_options.ApiSecret) &&
        !LiveKitJwt.LooksLikePlaceholder(_options.ApiKey) &&
        !LiveKitJwt.LooksLikePlaceholder(_options.ApiSecret);

    public static string BuildRoomName(Guid callId) => $"call-{callId:N}";

    public (string Token, DateTime ExpiresAtUtc) CreateJoinToken(User user, Guid callId)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Calls LiveKit is not configured. Set CallsLiveKit__Url, CallsLiveKit__ApiKey and CallsLiveKit__ApiSecret.");

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(Math.Clamp(_options.TokenTtlMinutes, 2, 60));

        var payload = new Dictionary<string, object?>
        {
            ["iss"] = _options.ApiKey,
            ["sub"] = user.Id.ToString(),
            ["name"] = UserIdentityService.GetPublicName(user),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-10).ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["metadata"] = LiveKitJwt.SerializeMetadata(new { userId = user.Id, callId }),
            ["video"] = new Dictionary<string, object?>
            {
                ["roomJoin"] = true,
                ["room"] = BuildRoomName(callId),
                ["canPublish"] = true,
                ["canSubscribe"] = true,
                ["canPublishData"] = true,
                ["canUpdateOwnMetadata"] = false,
                ["canPublishSources"] = new[] { "microphone", "camera", "screen_share", "screen_share_audio" }
            }
        };

        return (LiveKitJwt.Sign(_options.ApiSecret, payload), expiresAt.UtcDateTime);
    }
}
