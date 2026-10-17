using System.Security.Cryptography;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Files;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.Chat;

/// <summary>
/// Каналы — группы с видом <see cref="GroupChatKind.Channel"/>: участники, роли, аватарки и (у закрытых) MLS — общие с группами.
/// Здесь то, что есть только у каналов: посты открытых каналов открытым текстом, чтение без вступления, закреплённый пост,
/// просмотры, баны и пригласительные ссылки (ссылки работают и для групп).
/// </summary>
public sealed class ChannelService(AppDbContext db, GroupChatService groups)
{
    public const int MaxPostLength = 8000;
    public const int MaxPostFiles = 10;
    public const string InviteUrlPrefix = "https://jaezoo.ru/join/";
    private const string InviteAlphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<GroupChat> RequireChannelAsync(Guid channelId, CancellationToken ct) =>
        await db.GroupChats.FirstOrDefaultAsync(g => g.Id == channelId && g.Kind == GroupChatKind.Channel, ct)
        ?? throw new KeyNotFoundException("Канал не найден.");

    private Task<GroupChatMember?> FindMemberAsync(Guid chatId, Guid userId, CancellationToken ct) =>
        db.GroupChatMembers.FirstOrDefaultAsync(m => m.GroupChatId == chatId && m.UserId == userId, ct);

    public Task<bool> IsBannedAsync(Guid chatId, Guid userId, CancellationToken ct) =>
        db.GroupBans.AsNoTracking().AnyAsync(b => b.GroupChatId == chatId && b.UserId == userId, ct);

    public async Task<ChannelDetailsDto?> GetDetailsAsync(Guid channelId, Guid me, CancellationToken ct)
    {
        var summary = await groups.GetSummaryAsync(channelId, me, ct, allowPublicPreview: true);
        if (summary is null || summary.Kind != GroupChatKind.Channel)
            return null;
        return new ChannelDetailsDto(summary, summary.IsMember, await IsBannedAsync(channelId, me, ct));
    }

    public async Task<GroupChat> UpdateSettingsAsync(Guid channelId, Guid me, UpdateChannelRequest request, CancellationToken ct)
    {
        // Название и описание проверяет общий код групп (права: владелец или админ). Тип канала не меняется.
        var chat = await groups.UpdateChatAsync(channelId, me, request.Title, request.Description, null, ct);
        if (chat.Kind != GroupChatKind.Channel)
            throw new KeyNotFoundException("Канал не найден.");
        chat.SignPosts = request.SignPosts;
        await db.SaveChangesAsync(ct);
        return chat;
    }

    /// <summary>
    /// Посты: открытый канал видят все (вся история), закрытый — только участники и с момента вступления.
    /// Возвращает null, если доступа нет.
    /// </summary>
    public async Task<ChannelPostsDto?> GetPostsAsync(Guid channelId, Guid me, DateTime? before, Guid? beforeId, int take, CancellationToken ct)
    {
        var chat = await RequireChannelAsync(channelId, ct);
        var member = await db.GroupChatMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GroupChatId == channelId && m.UserId == me, ct);
        if (member is null && !chat.IsPublic)
            return null;
        if (member is null && await IsBannedAsync(channelId, me, ct))
            return null;

        var q = db.GroupMessages.AsNoTracking().Where(m => m.GroupChatId == channelId);
        if (!chat.IsPublic)
        {
            var joinedAt = DirectChatService.EnsureUtc(member!.JoinedAt);
            q = q.Where(m => m.SentAt >= joinedAt);
        }
        if (before.HasValue)
        {
            var bt = DirectChatService.EnsureUtc(before.Value);
            q = beforeId.HasValue
                ? q.Where(m => m.SentAt < bt || (m.SentAt == bt && m.Id.CompareTo(beforeId.Value) < 0))
                : q.Where(m => m.SentAt < bt);
        }

        var rows = await q.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        rows.Reverse();
        var posts = await groups.BuildMessageDtosAsync(rows, ct);
        return new ChannelPostsDto(posts, await CountViewsAsync(rows.Select(r => r.Id).ToList(), ct));
    }

    /// <summary>Пост открытого канала: открытый текст и обычные (незашифрованные, проверяемые антивирусом) вложения.</summary>
    public async Task<(GroupMessage Message, MessageDto Dto)> CreatePublicPostAsync(Guid channelId, Guid me, PublicPostRequest request, CancellationToken ct)
    {
        var chat = await RequireChannelAsync(channelId, ct);
        if (!chat.IsPublic)
            throw new InvalidOperationException("Посты закрытого канала шифруются (MLS) — отправка открытым текстом запрещена.");
        var member = await FindMemberAsync(channelId, me, ct);
        if (!GroupChatService.CanPost(chat, member))
            throw new UnauthorizedAccessException("Писать в канал могут только админы и модераторы.");

        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length > MaxPostLength)
            throw new InvalidOperationException($"Пост — не больше {MaxPostLength} символов.");

        var ids = (request.FileIds ?? Array.Empty<Guid>()).Where(x => x != Guid.Empty).Distinct().ToList();
        if (ids.Count > MaxPostFiles)
            throw new InvalidOperationException($"Не больше {MaxPostFiles} вложений в посте.");
        if (text.Length == 0 && ids.Count == 0)
            throw new InvalidOperationException("Пост пустой.");

        var files = ids.Count == 0 ? new List<ChatFile>() : await db.ChatFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        if (files.Count != ids.Count)
            throw new InvalidOperationException("Вложение не найдено — загрузите его заново.");
        foreach (var f in files)
        {
            if (f.UploaderId != me) throw new InvalidOperationException("Можно прикрепить только свои файлы.");
            if (f.IsAttached) throw new InvalidOperationException("Файл уже прикреплён к другому сообщению.");
            if (f.ScanStatus == FileScanStatus.ClientEncrypted)
                throw new InvalidOperationException("Вложения открытого канала публичные — их не шифруют.");
            if (f.BlockedAt.HasValue || f.DeletedAt.HasValue) throw new InvalidOperationException("Файл заблокирован или удалён.");
        }

        var now = DateTime.UtcNow;
        var message = new GroupMessage { GroupChatId = channelId, SenderId = me, Text = text, SentAt = now, Kind = DirectMessageKind.User };
        db.GroupMessages.Add(message);
        foreach (var f in files)
        {
            db.GroupMessageAttachments.Add(new GroupMessageAttachment { MessageId = message.Id, FileId = f.Id, CreatedAt = now });
            f.IsAttached = true;
            f.AttachedAt = now;
        }
        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (message, groups.ToMessageDto(message, files.Select(groups.ToAttachmentDto).ToList()));
    }

    public async Task<(GroupMessage Message, MessageDto Dto)> EditPublicPostAsync(Guid messageId, Guid me, EditPublicPostRequest request, CancellationToken ct)
    {
        var message = await db.GroupMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct) ?? throw new KeyNotFoundException("Пост не найден.");
        var chat = await RequireChannelAsync(message.GroupChatId, ct);
        if (!chat.IsPublic) throw new InvalidOperationException("Посты закрытого канала правятся через MLS.");
        if (message.SenderId != me) throw new UnauthorizedAccessException("Править пост может только его автор.");
        if (message.DeletedAt.HasValue || message.Kind != DirectMessageKind.User) throw new InvalidOperationException("Этот пост нельзя изменить.");
        if (!GroupChatService.CanPost(chat, await FindMemberAsync(chat.Id, me, ct)))
            throw new UnauthorizedAccessException("Писать в канал могут только админы и модераторы.");

        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length > MaxPostLength) throw new InvalidOperationException($"Пост — не больше {MaxPostLength} символов.");
        message.Text = text;
        message.EditedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return (message, (await groups.BuildMessageDtosAsync(new[] { message }, ct))[0]);
    }

    /// <summary>Удалить чужой пост может модератор канала (свой — автор, через общий эндпоинт групп).</summary>
    public async Task<bool> CanDeleteAsync(GroupMessage message, Guid me, CancellationToken ct)
    {
        if (message.SenderId == me) return true;
        var chat = await db.GroupChats.AsNoTracking().FirstOrDefaultAsync(g => g.Id == message.GroupChatId, ct);
        return chat is not null && chat.Kind == GroupChatKind.Channel && GroupChatService.CanModerate(chat, await FindMemberAsync(chat.Id, me, ct));
    }

    public async Task<GroupChat> PinAsync(Guid channelId, Guid me, Guid? messageId, CancellationToken ct)
    {
        var chat = await RequireChannelAsync(channelId, ct);
        if (!GroupChatService.CanPost(chat, await FindMemberAsync(channelId, me, ct)))
            throw new UnauthorizedAccessException("Закреплять посты могут админы и модераторы.");
        if (messageId.HasValue && !await db.GroupMessages.AnyAsync(m => m.Id == messageId && m.GroupChatId == channelId && m.DeletedAt == null, ct))
            throw new KeyNotFoundException("Пост не найден.");
        chat.PinnedMessageId = messageId;
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return chat;
    }

    /// <summary>Отметить посты просмотренными (один просмотр на пользователя) и вернуть их счётчики.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> MarkViewedAsync(Guid channelId, Guid me, IReadOnlyList<Guid> messageIds, CancellationToken ct)
    {
        var chat = await RequireChannelAsync(channelId, ct);
        var ids = messageIds.Distinct().Take(100).ToList();
        if (ids.Count == 0) return new Dictionary<Guid, int>();
        var canRead = chat.IsPublic || await db.GroupChatMembers.AnyAsync(m => m.GroupChatId == channelId && m.UserId == me, ct);
        if (!canRead) throw new UnauthorizedAccessException("Нет доступа к каналу.");

        var valid = await db.GroupMessages.AsNoTracking().Where(m => m.GroupChatId == channelId && ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct);
        var seen = await db.GroupMessageViews.AsNoTracking().Where(v => v.UserId == me && valid.Contains(v.MessageId)).Select(v => v.MessageId).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var id in valid.Except(seen))
            db.GroupMessageViews.Add(new GroupMessageView { MessageId = id, UserId = me, ViewedAt = now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Тот же пост отметили параллельно с другого устройства — просмотр уже учтён.
            db.ChangeTracker.Clear();
        }
        return await CountViewsAsync(valid, ct);
    }

    private async Task<IReadOnlyDictionary<Guid, int>> CountViewsAsync(IReadOnlyList<Guid> messageIds, CancellationToken ct) =>
        messageIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.GroupMessageViews.AsNoTracking()
                .Where(v => messageIds.Contains(v.MessageId))
                .GroupBy(v => v.MessageId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    // ---------- Баны ----------

    public async Task BanAsync(Guid chatId, Guid me, Guid userId, string? reason, CancellationToken ct)
    {
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == chatId, ct) ?? throw new KeyNotFoundException("Чат не найден.");
        var actor = await FindMemberAsync(chatId, me, ct);
        if (!GroupChatService.CanModerate(chat, actor)) throw new UnauthorizedAccessException("Банить могут владелец, админы и модераторы.");
        if (userId == me || userId == chat.OwnerId) throw new InvalidOperationException("Этого пользователя нельзя заблокировать.");

        var target = await FindMemberAsync(chatId, userId, ct);
        var actorRole = chat.OwnerId == me ? GroupChatRole.Admin : actor!.Role;
        // Модератор не банит админов и других модераторов; админ — других админов.
        if (target is not null && chat.OwnerId != me && target.Role >= actorRole)
            throw new UnauthorizedAccessException("Нельзя заблокировать участника с такой же или более высокой ролью.");

        if (target is not null)
            db.GroupChatMembers.Remove(target);
        if (!await IsBannedAsync(chatId, userId, ct))
        {
            var normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            db.GroupBans.Add(new GroupBan { GroupChatId = chatId, UserId = userId, BannedById = me, Reason = normalized is { Length: > 200 } ? normalized[..200] : normalized });
        }
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnbanAsync(Guid chatId, Guid me, Guid userId, CancellationToken ct)
    {
        var chat = await db.GroupChats.AsNoTracking().FirstOrDefaultAsync(g => g.Id == chatId, ct) ?? throw new KeyNotFoundException("Чат не найден.");
        if (!GroupChatService.CanModerate(chat, await FindMemberAsync(chatId, me, ct)))
            throw new UnauthorizedAccessException("Снимать бан могут владелец, админы и модераторы.");
        await db.GroupBans.Where(b => b.GroupChatId == chatId && b.UserId == userId).ExecuteDeleteAsync(ct);
    }

    public async Task<List<ChannelBanDto>> ListBansAsync(Guid chatId, Guid me, CancellationToken ct)
    {
        var chat = await db.GroupChats.AsNoTracking().FirstOrDefaultAsync(g => g.Id == chatId, ct) ?? throw new KeyNotFoundException("Чат не найден.");
        if (!GroupChatService.CanModerate(chat, await FindMemberAsync(chatId, me, ct)))
            throw new UnauthorizedAccessException("Список банов видят владелец, админы и модераторы.");
        var rows = await (
            from b in db.GroupBans.AsNoTracking()
            join u in db.Users.AsNoTracking() on b.UserId equals u.Id
            where b.GroupChatId == chatId
            orderby b.CreatedAt descending
            select new { Ban = b, User = u }).ToListAsync(ct);
        return rows.Select(x => new ChannelBanDto(x.User.Id, UserIdentityService.GetPublicName(x.User), UserIdentityService.GetAvatarUrl(x.User), x.Ban.CreatedAt, x.Ban.Reason)).ToList();
    }

    // ---------- Пригласительные ссылки (каналы и группы) ----------

    private async Task RequireCanInviteAsync(Guid chatId, Guid me, CancellationToken ct)
    {
        var chat = await db.GroupChats.AsNoTracking().FirstOrDefaultAsync(g => g.Id == chatId, ct) ?? throw new KeyNotFoundException("Чат не найден.");
        var member = await db.GroupChatMembers.AsNoTracking().FirstOrDefaultAsync(m => m.GroupChatId == chatId && m.UserId == me, ct);
        if (member is null || (chat.OwnerId != me && member.Role != GroupChatRole.Admin))
            throw new UnauthorizedAccessException("Ссылки-приглашения создают владелец и админы.");
    }

    public async Task<InviteDto> CreateInviteAsync(Guid chatId, Guid me, CreateInviteRequest request, CancellationToken ct)
    {
        await RequireCanInviteAsync(chatId, me, ct);
        if (request.MaxUses is <= 0 or > 100_000) throw new InvalidOperationException("Лимит вступлений — от 1 до 100 000.");
        if (request.ExpiresInHours is <= 0 or > 24 * 365) throw new InvalidOperationException("Срок ссылки — от часа до года.");

        var invite = new GroupInvite
        {
            GroupChatId = chatId,
            Code = NewInviteCode(),
            CreatedById = me,
            ExpiresAt = request.ExpiresInHours is { } h ? DateTime.UtcNow.AddHours(h) : null,
            MaxUses = request.MaxUses
        };
        db.GroupInvites.Add(invite);
        await db.SaveChangesAsync(ct);
        return ToInviteDto(invite);
    }

    public async Task<List<InviteDto>> ListInvitesAsync(Guid chatId, Guid me, CancellationToken ct)
    {
        await RequireCanInviteAsync(chatId, me, ct);
        var rows = await db.GroupInvites.AsNoTracking().Where(i => i.GroupChatId == chatId).OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
        return rows.Select(ToInviteDto).ToList();
    }

    public async Task RevokeInviteAsync(Guid chatId, Guid me, Guid inviteId, CancellationToken ct)
    {
        await RequireCanInviteAsync(chatId, me, ct);
        var invite = await db.GroupInvites.FirstOrDefaultAsync(i => i.Id == inviteId && i.GroupChatId == chatId, ct) ?? throw new KeyNotFoundException("Ссылка не найдена.");
        invite.RevokedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<(GroupInvite Invite, GroupChat Chat)> RequireActiveInviteAsync(string code, CancellationToken ct)
    {
        var normalized = (code ?? string.Empty).Trim();
        var invite = await db.GroupInvites.FirstOrDefaultAsync(i => i.Code == normalized, ct);
        if (invite is null || !IsActive(invite))
            throw new KeyNotFoundException("Ссылка недействительна: её отозвали, срок истёк или закончились места.");
        var chat = await db.GroupChats.FirstOrDefaultAsync(g => g.Id == invite.GroupChatId, ct) ?? throw new KeyNotFoundException("Чат не найден.");
        return (invite, chat);
    }

    public async Task<InvitePreviewDto> PreviewInviteAsync(string code, Guid me, CancellationToken ct)
    {
        var (_, chat) = await RequireActiveInviteAsync(code, ct);
        var count = await db.GroupChatMembers.AsNoTracking().CountAsync(m => m.GroupChatId == chat.Id, ct);
        var isMember = await db.GroupChatMembers.AsNoTracking().AnyAsync(m => m.GroupChatId == chat.Id && m.UserId == me, ct);
        return new InvitePreviewDto(chat.Id, chat.Kind, chat.Title, chat.Description, GroupChatService.GetAvatarUrl(chat), count, isMember);
    }

    /// <summary>Вступление по ссылке. Возвращает чат и признак «уже состоял».</summary>
    public async Task<(GroupChat Chat, bool WasMember)> JoinByInviteAsync(string code, Guid me, CancellationToken ct)
    {
        var (invite, chat) = await RequireActiveInviteAsync(code, ct);
        if (await db.GroupChatMembers.AnyAsync(m => m.GroupChatId == chat.Id && m.UserId == me, ct))
            return (chat, true);
        if (await IsBannedAsync(chat.Id, me, ct))
            throw new UnauthorizedAccessException("Вы заблокированы в этом чате.");
        if (await db.GroupChatMembers.CountAsync(m => m.GroupChatId == chat.Id, ct) >= chat.MemberLimit)
            throw new InvalidOperationException($"В чате уже максимум участников ({chat.MemberLimit}).");

        var now = DateTime.UtcNow;
        db.GroupChatMembers.Add(new GroupChatMember { GroupChatId = chat.Id, UserId = me, JoinedAt = now });
        invite.Uses++;
        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (chat, false);
    }

    private static bool IsActive(GroupInvite i) =>
        i.RevokedAt is null && (i.ExpiresAt is null || i.ExpiresAt > DateTime.UtcNow) && (i.MaxUses is null || i.Uses < i.MaxUses);

    private static InviteDto ToInviteDto(GroupInvite i) =>
        new(i.Id, i.Code, InviteUrlPrefix + i.Code, i.CreatedAt, i.ExpiresAt, i.MaxUses, i.Uses, IsActive(i));

    private static string NewInviteCode()
    {
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = InviteAlphabet[RandomNumberGenerator.GetInt32(InviteAlphabet.Length)];
        return new string(chars);
    }
}
