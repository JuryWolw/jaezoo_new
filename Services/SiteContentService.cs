using System.Text.Json;
using JaeZoo.Server.Data;
using JaeZoo.Server.Hubs;
using JaeZoo.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Services;

/// <summary>
/// Страницы вкладок клиента: «Поддержка» (support), «Сотрудничество» (collaboration), FAQ (faq). Пока страницу не правили —
/// отдаётся встроенная по умолчанию (та же, что была зашита в клиент). Сохраняется только проверенный документ.
/// </summary>
public sealed class SiteContentService(AppDbContext db, IHubContext<ChatHub> hub)
{
    public const string Support = "support";
    public const string Collaboration = "collaboration";
    public const string Faq = "faq";
    public const string HubEventName = "ContentChanged";

    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal) { Support, Collaboration, Faq };
    public static readonly IReadOnlySet<string> ContactKinds = new HashSet<string>(StringComparer.Ordinal) { "telegram", "vk", "max", "mail", "link" };

    // Кириллица — как есть, а не \uXXXX: документ читают люди (админка).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    public async Task<SiteContentDto> GetAsync(string key, CancellationToken ct)
    {
        RequireKey(key);
        var row = await db.SiteContents.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct);
        return row is null
            ? new SiteContentDto(key, DefaultJson(key), null, null)
            : new SiteContentDto(key, row.Json, row.UpdatedAt, row.UpdatedByName);
    }

    public async Task<SiteContentDto> SaveAsync(string key, string json, string editorName, CancellationToken ct)
    {
        RequireKey(key);
        var normalized = Normalize(key, json);
        var row = await db.SiteContents.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (row is null)
        {
            row = new SiteContent { Key = key };
            db.SiteContents.Add(row);
        }
        row.Json = normalized;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByName = editorName.Length > 64 ? editorName[..64] : editorName;
        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync(HubEventName, new SiteContentChangedDto(key), ct);
        return new SiteContentDto(key, row.Json, row.UpdatedAt, row.UpdatedByName);
    }

    private static void RequireKey(string key)
    {
        if (!Keys.Contains(key))
            throw new KeyNotFoundException("Такой страницы нет.");
    }

    /// <summary>Проверка и приведение: лимиты на число и длину, разрешённые виды контактов и ссылки (http, https, mailto).</summary>
    public static string Normalize(string key, string json)
    {
        try
        {
            if (key == Faq)
            {
                var page = JsonSerializer.Deserialize<FaqPageDto>(json, Json) ?? throw new InvalidOperationException("Пустая страница.");
                var items = (page.Items ?? new()).Where(i => i is not null).ToList();
                if (items.Count > 80) throw new InvalidOperationException("Не больше 80 вопросов.");
                var clean = items.Select(i => new FaqEntryDto(Text(i.Question, 200, "Вопрос"), Text(i.Answer, 2000, "Ответ"))).ToList();
                return JsonSerializer.Serialize(new FaqPageDto(clean), Json);
            }

            var contacts = JsonSerializer.Deserialize<ContactPageDto>(json, Json) ?? throw new InvalidOperationException("Пустая страница.");
            var sections = (contacts.Sections ?? new()).Where(s => s is not null).ToList();
            if (sections.Count > 12) throw new InvalidOperationException("Не больше 12 разделов.");
            var cleanSections = sections.Select(s =>
            {
                var items = (s.Items ?? new()).Where(i => i is not null).ToList();
                if (items.Count > 16) throw new InvalidOperationException($"В разделе «{s.Title}» больше 16 контактов.");
                return new ContactSectionDto(
                    Text(s.Title, 120, "Название раздела"),
                    Optional(s.Description, 600),
                    items.Select(i =>
                    {
                        var kind = (i.Kind ?? string.Empty).Trim().ToLowerInvariant();
                        if (!ContactKinds.Contains(kind)) throw new InvalidOperationException($"Неизвестный вид контакта «{i.Kind}».");
                        var url = Optional(i.Url, 500);
                        if (url != null && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "mailto")))
                            throw new InvalidOperationException($"Ссылка «{url}»: допустимы только http(s) и mailto.");
                        return new ContactItemDto(kind, Text(i.Title, 60, "Название контакта"), Text(i.Text, 200, "Текст контакта"), url, Optional(i.CopyText, 200), i.Soon);
                    }).ToList());
            }).ToList();
            return JsonSerializer.Serialize(new ContactPageDto(cleanSections), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Документ страницы повреждён: " + ex.Message);
        }
    }

    private static string Text(string? value, int max, string what)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0) throw new InvalidOperationException($"{what}: пусто.");
        if (v.Length > max) throw new InvalidOperationException($"{what}: не больше {max} символов.");
        return v;
    }

    private static string? Optional(string? value, int max)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return v.Length > max ? throw new InvalidOperationException($"Слишком длинно (больше {max} символов): «{v[..30]}…».") : v;
    }

    /// <summary>То, что было зашито в клиент до появления редактора.</summary>
    public static string DefaultJson(string key) => key switch
    {
        Support => JsonSerializer.Serialize(new ContactPageDto(new()
        {
            new("Сообщество", "Новости, обсуждения и общение с другими пользователями JaeZoo.", new()
            {
                new("telegram", "Telegram", "t.me/jaezoo", "https://t.me/jaezoo"),
                new("vk", "ВКонтакте", "Сообщество скоро появится", null, null, true),
            }),
            new("Нашли баг?", "Напишите, что случилось и как это повторить — по возможности приложите скриншот.", new()
            {
                new("telegram", "Telegram", "t.me/jrwolw", "https://t.me/jrwolw"),
                new("max", "Max", "Написать в Max", "https://max.ru/u/f9LHodD0cOKE9V_In3AFUHdh57PGwC0fd7ALIm0XnUXMZ39jDZTAlDqwsF0"),
                new("vk", "ВКонтакте", "Скоро", null, null, true),
                new("mail", "Почта", "Скоро", null, null, true),
            }),
        }), Json),
        Collaboration => JsonSerializer.Serialize(new ContactPageDto(new()
        {
            new("Реклама и партнёрство", "Рекламные интеграции, баннеры в JaeZoo и совместные проекты. Опишите идею, формат и сроки — ответим на почту.", new()
            {
                new("mail", "Почта", "yurywolw@yandex.ru", "mailto:yurywolw@yandex.ru", "yurywolw@yandex.ru"),
            }),
        }), Json),
        Faq => JsonSerializer.Serialize(new FaqPageDto(new()
        {
            new("Как отправить сообщение?", "Напиши текст в поле ввода и нажми Enter. Для переноса строки нажми Shift+Enter."),
            new("Как отправить файл?", "Нажми плюс рядом с полем ввода или перетащи файл в область чата. Перед отправкой файл появится в предпросмотре."),
            new("Как посмотреть все вложения в чате?", "Открой карточку друга или группы и нажми «Вложения». Фото и видео отображаются сеткой, музыка, файлы и ссылки отображаются списком."),
            new("Как работает музыка в JaeZoo?", "Запусти аудиофайл из чата или из раздела вложений. Трек продолжит играть при переключении между чатами."),
            new("Как открыть профиль пользователя?", "Нажми на пользователя в поиске или в списке участников группы. В профиле доступны статус, активность, описание и добавление в друзья."),
            new("Как посмотреть аватарки пользователя?", "Нажми на аватарку в карточке друга или в предпросмотре профиля. Откроется публичная галерея аватарок."),
            new("Почему статус последнего входа не точный?", "Пользователь выбирает приватность сам. Он может показывать точное время, примерное время или скрывать последний вход."),
            new("Почему не видно активность в игре?", "Активность определяется по запущенным процессам на ПК. Если игра неизвестна или пользователь отключил показ активности, JaeZoo ничего не покажет."),
            new("Как работают уведомления?", "Панель показывает только активные данные: заявки, загрузки и системные сообщения. Пустые блоки скрываются."),
            new("Как свернуть JaeZoo в трей?", "Нажми крестик в главном окне. JaeZoo останется работать в трее, звонки и уведомления продолжат работать."),
            new("Как полностью закрыть JaeZoo?", "Нажми правой кнопкой по иконке JaeZoo в трее и выбери «Закрыть JaeZoo»."),
            new("Что делать, если что-то не работает?", "Открой раздел «Поддержка» и отправь баг в Telegram, VK, Max или на почту."),
        }), Json),
        _ => "{}",
    };
}
