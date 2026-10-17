namespace JaeZoo.Server.Models;

/// <summary>Вид чата: обычная группа или канал (пишут только владелец, админы и модераторы).</summary>
public enum GroupChatKind
{
    Group = 0,
    Channel = 1
}
