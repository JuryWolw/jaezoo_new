using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models
{
    public class Avatar
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid UserId { get; set; }

        [Required]
        public byte[] Data { get; set; } = Array.Empty<byte>();

        [MaxLength(128)]
        public string ContentType { get; set; } = "image/png";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
