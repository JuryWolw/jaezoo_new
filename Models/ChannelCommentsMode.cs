namespace JaeZoo.Server.Models;

/// <summary>Комментарии канала (настройка канала): ветки под постами, общая беседа подписчиков, или оба вида.</summary>
[Flags]
public enum ChannelCommentsMode
{
    Off = 0,
    /// <summary>Ветка комментариев под каждым постом (у поста можно выключить отдельно).</summary>
    Threads = 1,
    /// <summary>Беседа канала — общий чат подписчиков, вступление по желанию.</summary>
    Discussion = 2,
    Both = Threads | Discussion,
}
