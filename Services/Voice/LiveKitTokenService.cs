using JaeZoo.Server.Models;
using JaeZoo.Server.Services;
using JaeZoo.Server.Options;
using Microsoft.Extensions.Options;

namespace JaeZoo.Server.Services.Voice;

public sealed class LiveKitTokenService(IOptions<LiveKitOptions> options)
{
    private readonly LiveKitOptions _options = options.Value;

    public string Url => LiveKitJwt.NormalizeUrl(_options.Url);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Url) &&
        !string.IsNullOrWhiteSpace(_options.ApiKey) &&
        !string.IsNullOrWhiteSpace(_options.ApiSecret) &&
        !LiveKitJwt.LooksLikePlaceholder(_options.ApiKey) &&
        !LiveKitJwt.LooksLikePlaceholder(_options.ApiSecret);

    public string CreateJoinToken(User user, Guid groupId, Guid sessionId, string roomName)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("LiveKit is not configured. Set LiveKit__Url, LiveKit__ApiKey and LiveKit__ApiSecret.");

        var now = DateTimeOffset.UtcNow;
        var ttl = TimeSpan.FromMinutes(Math.Clamp(_options.TokenTtlMinutes, 5, 24 * 60));

        var payload = new Dictionary<string, object?>
        {
            ["iss"] = _options.ApiKey,
            // Keep LiveKit identity stable and simple.
            // Composite identities were introduced during debugging, but they changed the
            // room participant model and made reconnect/recovery much harder to reason
            // about. The old working group-call engine expects identity == JaeZoo UserId.
            ["sub"] = user.Id.ToString(),
            ["name"] = UserIdentityService.GetPublicName(user),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-10).ToUnixTimeSeconds(),
            ["exp"] = now.Add(ttl).ToUnixTimeSeconds(),
            ["metadata"] = LiveKitJwt.SerializeMetadata(new
            {
                userId = user.Id,
                userName = UserIdentityService.GetPublicName(user),
                publicId = user.PublicId,
                groupId,
                sessionId
            }),
            ["video"] = new Dictionary<string, object?>
            {
                ["roomJoin"] = true,
                ["room"] = roomName,
                ["canPublish"] = true,
                ["canSubscribe"] = true,
                ["canPublishData"] = true,
                ["canUpdateOwnMetadata"] = true
            }
        };

        return LiveKitJwt.Sign(_options.ApiSecret, payload);
    }

    public static string BuildGroupRoomName(Guid groupId) => $"group-{groupId:N}-voice";
}
