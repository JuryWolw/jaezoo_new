using JaeZoo.Server.Controllers.Admin;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using JaeZoo.Server.Services.Chat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services.News;

public sealed record NewsItemDto(
    Guid Id,
    int Category,
    string Title,
    string Summary,
    string Body,
    string? CoverUrl,
    string? Version,
    bool IsPinned,
    DateTime PublishedAt,
    DateTime? EditedAt);

public sealed record NewsFeedDto(IReadOnlyList<NewsItemDto> Items, DateTime? LastSeenAt);

/// <summary>Событие хаба: новость опубликована/изменена (Item) или удалена (DeletedId).</summary>
public sealed record NewsChangedDto(NewsItemDto? Item, Guid? DeletedId);

/// <summary>Публикация новостей: сохранение, событие всем онлайн-клиентам, дубль в личку от JaeZoo.</summary>
public sealed class NewsService(AppDbContext db, IHubContext<ChatHub> hub, IServiceScopeFactory scopeFactory, ILogger<NewsService> logger)
{
    public const string HubEventName = "NewsChanged";
    public const string CoverPrefix = "news/covers/";
    public const string CoverRoute = "/api/news/covers/";

    public static NewsItemDto ToDto(NewsPost p) => new(
        p.Id, p.Category, p.Title, p.Summary, p.Body, CoverUrl(p.CoverKey), p.Version, p.IsPinned, p.PublishedAt, p.EditedAt);

    public static string? CoverUrl(string? key) =>
        string.IsNullOrWhiteSpace(key) || !key.StartsWith(CoverPrefix, StringComparison.Ordinal) ? null : CoverRoute + key[CoverPrefix.Length..];

    /// <summary>Техработы уходят в личку всегда, остальное — по галочке.</summary>
    public static bool ShouldSendToSystemChat(int category, bool requested) => requested || category == NewsPost.CategoryMaintenance;

    public async Task<NewsPost> PublishAsync(NewsPost post, bool sendToSystemChat, CancellationToken ct)
    {
        post.SentToSystemChat = ShouldSendToSystemChat(post.Category, sendToSystemChat);
        db.NewsPosts.Add(post);
        await db.SaveChangesAsync(ct);

        await hub.Clients.All.SendAsync(HubEventName, new NewsChangedDto(ToDto(post), null), ct);

        if (post.SentToSystemChat)
        {
            var recipients = await AdminBroadcastsController.ResolveAudienceAsync(db, AdminBroadcast.AudienceAll, ct);
            var text = SystemChatText(post);
            _ = Task.Run(() => AdminBroadcastsController.DeliverToSystemChatAsync(scopeFactory, logger, recipients, text, new List<Guid>()));
        }

        return post;
    }

    public Task NotifyChangedAsync(NewsPost post, CancellationToken ct) =>
        hub.Clients.All.SendAsync(HubEventName, new NewsChangedDto(ToDto(post), null), ct);

    public Task NotifyDeletedAsync(Guid id, CancellationToken ct) =>
        hub.Clients.All.SendAsync(HubEventName, new NewsChangedDto(null, id), ct);

    /// <summary>Новость «Обновление x.y.z» из списка изменений релиза; повторно для той же версии не создаётся.</summary>
    public async Task<NewsPost?> EnsureReleasePostAsync(string version, string? releaseNotes, CancellationToken ct)
    {
        var notes = (releaseNotes ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(version) || notes.Length == 0)
            return null;
        if (await db.NewsPosts.AnyAsync(p => p.Version == version, ct))
            return null;

        var post = new NewsPost
        {
            Category = NewsPost.CategoryUpdate,
            Title = $"Обновление {version}",
            Body = notes.Length > 20000 ? notes[..20000] : notes,
            Version = version,
            CreatedByName = "ReleaseTool"
        };
        logger.LogInformation("News: release post for client {Version}.", version);
        return await PublishAsync(post, sendToSystemChat: false, ct);
    }

    internal static string SystemChatText(NewsPost post)
    {
        var icon = post.Category == NewsPost.CategoryMaintenance ? "🛠" : "📰";
        var lead = !string.IsNullOrWhiteSpace(post.Summary) ? post.Summary.Trim() : Excerpt(post.Body, 400);
        return string.IsNullOrWhiteSpace(lead)
            ? $"{icon} {post.Title}\n\nПодробнее — во вкладке «Новости»."
            : $"{icon} {post.Title}\n\n{lead}\n\nПодробнее — во вкладке «Новости».";
    }

    /// <summary>Начало текста без разметки.</summary>
    internal static string Excerpt(string body, int max)
    {
        var plain = string.Join(' ', (body ?? string.Empty)
            .Split('\n')
            .Where(l => !l.TrimStart().StartsWith("![", StringComparison.Ordinal))
            .Select(l => l.Trim().TrimStart('#', '-', ' ').Replace("**", string.Empty))
            .Where(l => l.Length > 0));
        return plain.Length <= max ? plain : plain[..max].TrimEnd() + "…";
    }
}
