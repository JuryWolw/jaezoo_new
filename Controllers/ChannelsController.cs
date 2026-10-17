using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Security;
using JaeZoo.Server.Services.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

/// <summary>
/// Каналы. Общее с группами (участники, роли, аватарка, выход, удаление и посты закрытого канала через MLS)
/// идёт через api/chat/groups/*; здесь — только своё у каналов, а также баны и пригласительные ссылки.
/// </summary>
[ApiController]
[Authorize]
[Route("api/channels")]
public sealed class ChannelsController(
    AppDbContext db,
    GroupChatService groups,
    ChannelService channels,
    IHubContext<ChatHub> hub,
    ILogger<ChannelsController> log) : ControllerBase
{
    private Guid MeId
    {
        get
        {
            var s = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(s, out var id) ? id : throw new UnauthorizedAccessException("No user id claim.");
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GroupChatSummaryDto>>> Mine(CancellationToken ct) =>
        await groups.ListForUserAsync(MeId, ct, GroupChatKind.Channel);

    [HttpGet("search")]
    [EnableRateLimiting("search")]
    public async Task<ActionResult<IReadOnlyList<PublicGroupSearchDto>>> Search([FromQuery] string? q, [FromQuery] int take = 30, CancellationToken ct = default) =>
        await groups.SearchPublicAsync(MeId, q, take, ct, GroupChatKind.Channel);

    [HttpPost]
    [RequireVerifiedEmail]
    public Task<ActionResult<ChannelDetailsDto>> Create([FromBody] CreateChannelRequest body, CancellationToken ct) => Run(async () =>
    {
        var chat = await groups.CreateChatAsync(MeId, body.Title, body.Description, null, body.IsPublic, ct, GroupChatKind.Channel, body.SignPosts);
        return (await channels.GetDetailsAsync(chat.Id, MeId, ct))!;
    });

    [HttpGet("{id:guid}")]
    public Task<ActionResult<ChannelDetailsDto>> Get(Guid id, CancellationToken ct) => Run(async () =>
        await channels.GetDetailsAsync(id, MeId, ct) ?? throw new KeyNotFoundException("Канал не найден."));

    [HttpPut("{id:guid}")]
    [RequireVerifiedEmail]
    public Task<ActionResult<ChannelDetailsDto>> Update(Guid id, [FromBody] UpdateChannelRequest body, CancellationToken ct) => Run(async () =>
    {
        await channels.UpdateSettingsAsync(id, MeId, body, ct);
        await BroadcastUpdatedAsync(id, ct);
        return (await channels.GetDetailsAsync(id, MeId, ct))!;
    });

    [HttpPost("{id:guid}/join")]
    [RequireVerifiedEmail]
    [EnableRateLimiting("security-sensitive")]
    public Task<ActionResult<ChannelDetailsDto>> Join(Guid id, CancellationToken ct) => Run(async () =>
    {
        await channels.RequireChannelAsync(id, ct);
        await groups.JoinPublicAsync(id, MeId, ct);
        await BroadcastMembersChangedAsync(id, ct);
        return (await channels.GetDetailsAsync(id, MeId, ct))!;
    });

    [HttpGet("{id:guid}/posts")]
    public Task<ActionResult<ChannelPostsDto>> Posts(Guid id, [FromQuery] DateTime? before, [FromQuery] Guid? beforeId, [FromQuery] int take = 30, CancellationToken ct = default) => Run(async () =>
        await channels.GetPostsAsync(id, MeId, before, beforeId, take, ct) ?? throw new UnauthorizedAccessException("Канал закрытый — посты видят только участники."));

    /// <summary>Пост открытого канала (открытый текст). Посты закрытого — api/chat/groups/{id}/messages/send (MLS).</summary>
    [HttpPost("{id:guid}/posts")]
    [EnableRateLimiting("chat-write")]
    [RequireVerifiedEmail]
    public Task<ActionResult<MessageDto>> CreatePost(Guid id, [FromBody] PublicPostRequest body, CancellationToken ct) => Run(async () =>
    {
        var (message, dto) = await channels.CreatePublicPostAsync(id, MeId, body, ct);
        await BroadcastPostCreatedAsync(id, message.SenderId, dto, ct);
        return dto;
    });

    [HttpPost("posts/{messageId:guid}/edit")]
    [RequireVerifiedEmail]
    public Task<ActionResult<MessageDto>> EditPost(Guid messageId, [FromBody] EditPublicPostRequest body, CancellationToken ct) => Run(async () =>
    {
        var (message, dto) = await channels.EditPublicPostAsync(messageId, MeId, body, ct);
        await SendToMembersAsync(message.GroupChatId, "GroupChatMessageUpdated", new GroupChatMessageUpdatedDto(message.GroupChatId, dto), ct);
        return dto;
    });

    [HttpPost("{id:guid}/pin")]
    public Task<ActionResult<ChannelDetailsDto>> Pin(Guid id, [FromBody] PinPostRequest body, CancellationToken ct) => Run(async () =>
    {
        await channels.PinAsync(id, MeId, body.MessageId, ct);
        await BroadcastUpdatedAsync(id, ct);
        return (await channels.GetDetailsAsync(id, MeId, ct))!;
    });

    [HttpPost("{id:guid}/views")]
    public Task<ActionResult<IReadOnlyDictionary<Guid, int>>> Views(Guid id, [FromBody] MarkPostsViewedRequest body, CancellationToken ct) => Run(async () =>
        await channels.MarkViewedAsync(id, MeId, body.MessageIds ?? Array.Empty<Guid>(), ct));

    // ---------- Баны (каналы и группы) ----------

    [HttpGet("{id:guid}/bans")]
    public Task<ActionResult<List<ChannelBanDto>>> Bans(Guid id, CancellationToken ct) => Run(() => channels.ListBansAsync(id, MeId, ct));

    [HttpPost("{id:guid}/bans/{userId:guid}")]
    public Task<ActionResult<object>> Ban(Guid id, Guid userId, [FromBody] BanMemberRequest? body, CancellationToken ct) => Run<object>(async () =>
    {
        await channels.BanAsync(id, MeId, userId, body?.Reason, ct);
        await BroadcastMembersChangedAsync(id, ct, userId);
        return new { ok = true };
    });

    [HttpDelete("{id:guid}/bans/{userId:guid}")]
    public Task<ActionResult<object>> Unban(Guid id, Guid userId, CancellationToken ct) => Run<object>(async () =>
    {
        await channels.UnbanAsync(id, MeId, userId, ct);
        return new { ok = true };
    });

    // ---------- Пригласительные ссылки (каналы и группы) ----------

    [HttpGet("{id:guid}/invites")]
    public Task<ActionResult<List<InviteDto>>> Invites(Guid id, CancellationToken ct) => Run(() => channels.ListInvitesAsync(id, MeId, ct));

    [HttpPost("{id:guid}/invites")]
    [RequireVerifiedEmail]
    public Task<ActionResult<InviteDto>> CreateInvite(Guid id, [FromBody] CreateInviteRequest body, CancellationToken ct) =>
        Run(() => channels.CreateInviteAsync(id, MeId, body, ct));

    [HttpDelete("{id:guid}/invites/{inviteId:guid}")]
    public Task<ActionResult<object>> RevokeInvite(Guid id, Guid inviteId, CancellationToken ct) => Run<object>(async () =>
    {
        await channels.RevokeInviteAsync(id, MeId, inviteId, ct);
        return new { ok = true };
    });

    [HttpGet("/api/invites/{code}")]
    [EnableRateLimiting("search")]
    public Task<ActionResult<InvitePreviewDto>> PreviewInvite(string code, CancellationToken ct) => Run(() => channels.PreviewInviteAsync(code, MeId, ct));

    [HttpPost("/api/invites/{code}/join")]
    [RequireVerifiedEmail]
    [EnableRateLimiting("security-sensitive")]
    public Task<ActionResult<InvitePreviewDto>> JoinByInvite(string code, CancellationToken ct) => Run(async () =>
    {
        var (chat, wasMember) = await channels.JoinByInviteAsync(code, MeId, ct);
        if (!wasMember)
            await BroadcastMembersChangedAsync(chat.Id, ct);
        var count = await db.GroupChatMembers.AsNoTracking().CountAsync(m => m.GroupChatId == chat.Id, ct);
        return new InvitePreviewDto(chat.Id, chat.Kind, chat.Title, chat.Description, GroupChatService.GetAvatarUrl(chat), count, true);
    });

    // ---------- общее ----------

    /// <summary>Ошибки домена → коды HTTP с понятным текстом (как у остальных чатов: { error }).</summary>
    private async Task<ActionResult<T>> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Channels: request failed for {User}.", MeId);
            return Problem("Не удалось выполнить действие с каналом.", statusCode: 500);
        }
    }

    private async Task<List<Guid>> MemberIdsAsync(Guid chatId, CancellationToken ct) =>
        await db.GroupChatMembers.AsNoTracking().Where(m => m.GroupChatId == chatId).Select(m => m.UserId).ToListAsync(ct);

    private async Task SendToMembersAsync(Guid chatId, string method, object payload, CancellationToken ct)
    {
        var ids = await MemberIdsAsync(chatId, ct);
        await hub.Clients.Users(ids.Select(x => x.ToString()).ToList()).SendAsync(method, payload, ct);
    }

    private async Task BroadcastPostCreatedAsync(Guid chatId, Guid senderId, MessageDto dto, CancellationToken ct)
    {
        var ids = await MemberIdsAsync(chatId, ct);
        await hub.Clients.Users(ids.Select(x => x.ToString()).ToList()).SendAsync("GroupChatMessageCreated", new GroupChatRealtimeMessageDto(chatId, dto), ct);
        foreach (var memberId in ids.Where(x => x != senderId))
        {
            var unread = await groups.GetUnreadForUserAsync(chatId, memberId, ct);
            await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatUnreadChanged", new GroupChatUnreadChangedDto(chatId, unread.count, unread.firstId, unread.firstAt), ct);
        }
    }

    private async Task BroadcastUpdatedAsync(Guid chatId, CancellationToken ct)
    {
        foreach (var memberId in await MemberIdsAsync(chatId, ct))
        {
            var summary = await groups.GetSummaryAsync(chatId, memberId, ct);
            if (summary is not null)
                await hub.Clients.User(memberId.ToString()).SendAsync("GroupChatUpdated", new GroupChatUpdatedDto(summary), ct);
        }
    }

    /// <summary>Состав изменился: участникам (закрытый канал сверяет MLS-группу) и, при бане, самому заблокированному.</summary>
    private async Task BroadcastMembersChangedAsync(Guid chatId, CancellationToken ct, Guid? alsoNotify = null)
    {
        var members = await groups.GetMemberDtosAsync(chatId, ct);
        var targets = members.Select(m => m.UserId).Concat(alsoNotify is { } extra ? new[] { extra } : Array.Empty<Guid>()).Distinct().Select(x => x.ToString()).ToList();
        await hub.Clients.Users(targets).SendAsync("GroupChatMembersChanged", new GroupChatMembersChangedDto(chatId, members), ct);
        await BroadcastUpdatedAsync(chatId, ct);
    }
}
