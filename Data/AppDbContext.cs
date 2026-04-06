using JaeZoo.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<DirectDialog> DirectDialogs => Set<DirectDialog>();
    public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();
    public DbSet<Avatar> Avatars => Set<Avatar>();
    public DbSet<ChatFile> ChatFiles => Set<ChatFile>();
    public DbSet<DirectMessageAttachment> DirectMessageAttachments => Set<DirectMessageAttachment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<User>().HasIndex(u => u.UserName).IsUnique();
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();

        b.Entity<Friendship>()
            .HasIndex(f => new { f.RequesterId, f.AddresseeId })
            .IsUnique();

        b.Entity<DirectDialog>()
            .HasIndex(d => new { d.User1Id, d.User2Id })
            .IsUnique();

        b.Entity<DirectMessage>()
            .HasIndex(m => new { m.DialogId, m.SentAt, m.Id });

        b.Entity<DirectMessage>()
            .Property(m => m.Text)
            .HasMaxLength(4000);

        b.Entity<DirectMessage>()
            .Property(m => m.SystemKey)
            .HasMaxLength(64);

        b.Entity<DirectMessage>()
            .Property(m => m.Kind)
            .HasConversion<int>();

        b.Entity<DirectMessage>()
            .HasIndex(m => new { m.DialogId, m.DeletedAt, m.SentAt, m.Id });

        b.Entity<DirectMessage>()
            .HasIndex(m => m.ForwardedFromMessageId);

        b.Entity<Avatar>()
            .HasIndex(a => a.UserId);

        b.Entity<ChatFile>()
            .HasIndex(f => new { f.UploaderId, f.CreatedAt });

        b.Entity<ChatFile>()
            .Property(f => f.OriginalFileName)
            .HasMaxLength(256);

        b.Entity<ChatFile>()
            .Property(f => f.ContentType)
            .HasMaxLength(128);

        b.Entity<ChatFile>()
            .Property(f => f.StoredPath)
            .HasMaxLength(512);

        b.Entity<DirectMessageAttachment>()
            .HasIndex(a => new { a.MessageId, a.FileId })
            .IsUnique();

        b.Entity<DirectMessageAttachment>()
            .HasOne<DirectMessage>()
            .WithMany(m => m.Attachments)
            .HasForeignKey(a => a.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<DirectMessageAttachment>()
            .HasOne<ChatFile>()
            .WithMany()
            .HasForeignKey(a => a.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
