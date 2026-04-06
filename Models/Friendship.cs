using System;

namespace JaeZoo.Server.Models
{
    // Добавили Declined = 2
    public enum FriendshipStatus
    {
        Pending = 0,
        Accepted = 1,
        Declined = 2
    }

    public class Friendship
    {
        public Guid Id { get; set; }

        public Guid RequesterId { get; set; }
        public Guid AddresseeId { get; set; }

        public FriendshipStatus Status { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
