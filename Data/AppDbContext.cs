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
    public DbSet<GroupChat> GroupChats => Set<GroupChat>();
    public DbSet<GroupChatMember> GroupChatMembers => Set<GroupChatMember>();
    public DbSet<GroupMessage> GroupMessages => Set<GroupMessage>();
    public DbSet<GroupMessageAttachment> GroupMessageAttachments => Set<GroupMessageAttachment>();
    public DbSet<GroupAvatar> GroupAvatars => Set<GroupAvatar>();
    public DbSet<GroupVoiceSession> GroupVoiceSessions => Set<GroupVoiceSession>();
    public DbSet<GroupVoiceParticipant> GroupVoiceParticipants => Set<GroupVoiceParticipant>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<User>().Property(u => u.UserName).HasMaxLength(64);
        b.Entity<User>().Property(u => u.Login).HasMaxLength(64);
        b.Entity<User>().Property(u => u.LoginNormalized).HasMaxLength(64);
        b.Entity<User>().Property(u => u.Email).HasMaxLength(128);
        b.Entity<User>().Property(u => u.EmailNormalized).HasMaxLength(128);
        b.Entity<User>().Property(u => u.PublicId).HasMaxLength(32);
        b.Entity<User>().Property(u => u.SecurityStamp).HasMaxLength(64);
        b.Entity<User>().Property(u => u.DisabledReason).HasMaxLength(256);

        // Старые индексы оставляем, чтобы не ломать существующую схему и код.
        // Новые проверки уникальности идут по нормализованным полям.
        b.Entity<User>().HasIndex(u => u.UserName).IsUnique();
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<User>().HasIndex(u => u.LoginNormalized).IsUnique();
        b.Entity<User>().HasIndex(u => u.EmailNormalized).IsUnique();
        b.Entity<User>().HasIndex(u => u.PublicId).IsUnique();


        b.Entity<UserRole>()
            .Property(r => r.Role)
            .HasConversion<int>();

        b.Entity<UserRole>()
            .Property(r => r.Reason)
            .HasMaxLength(256);

        b.Entity<UserRole>()
            .Property(r => r.RevokeReason)
            .HasMaxLength(256);

        b.Entity<UserRole>()
            .HasIndex(r => new { r.UserId, r.Role, r.RevokedAt });

        b.Entity<UserRole>()
            .HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<AdminAuditLog>()
            .Property(a => a.ActorPublicId)
            .HasMaxLength(64);

        b.Entity<AdminAuditLog>()
            .Property(a => a.ActorDisplayName)
            .HasMaxLength(64);

        b.Entity<AdminAuditLog>()
            .Property(a => a.Action)
            .HasMaxLength(80);

        b.Entity<AdminAuditLog>()
            .Property(a => a.TargetType)
            .HasMaxLength(64);

        b.Entity<AdminAuditLog>()
            .Property(a => a.TargetId)
            .HasMaxLength(128);

        b.Entity<AdminAuditLog>()
            .Property(a => a.Summary)
            .HasMaxLength(512);

        b.Entity<AdminAuditLog>()
            .Property(a => a.IpAddress)
            .HasMaxLength(64);

        b.Entity<AdminAuditLog>()
            .Property(a => a.UserAgent)
            .HasMaxLength(256);

        b.Entity<AdminAuditLog>()
            .HasIndex(a => new { a.CreatedAt, a.Action });

        b.Entity<AdminAuditLog>()
            .HasIndex(a => a.ActorUserId);

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

        b.Entity<GroupChat>()
            .Property(g => g.Title)
            .HasMaxLength(120);

        b.Entity<GroupChat>()
            .HasIndex(g => new { g.OwnerId, g.CreatedAt });

        b.Entity<GroupChat>()
            .Property(g => g.Description)
            .HasMaxLength(1000);

        b.Entity<GroupChat>()
            .Property(g => g.AvatarUrl)
            .HasMaxLength(512);

        b.Entity<GroupChatMember>()
            .Property(m => m.Role)
            .HasConversion<int>();

        b.Entity<GroupChatMember>()
            .HasIndex(m => new { m.GroupChatId, m.UserId })
            .IsUnique();

        b.Entity<GroupChatMember>()
            .HasIndex(m => new { m.UserId, m.JoinedAt });

        b.Entity<GroupChatMember>()
            .HasOne<GroupChat>()
            .WithMany(g => g.Members)
            .HasForeignKey(m => m.GroupChatId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<GroupMessage>()
            .HasIndex(m => new { m.GroupChatId, m.SentAt, m.Id });

        b.Entity<GroupMessage>()
            .Property(m => m.Text)
            .HasMaxLength(4000);

        b.Entity<GroupMessage>()
            .Property(m => m.SystemKey)
            .HasMaxLength(64);

        b.Entity<GroupMessage>()
            .Property(m => m.Kind)
            .HasConversion<int>();

        b.Entity<GroupMessage>()
            .HasIndex(m => new { m.GroupChatId, m.DeletedAt, m.SentAt, m.Id });

        b.Entity<GroupMessage>()
            .HasIndex(m => m.ForwardedFromMessageId);

        b.Entity<GroupMessage>()
            .HasOne<GroupChat>()
            .WithMany()
            .HasForeignKey(m => m.GroupChatId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<GroupMessageAttachment>()
            .HasIndex(a => new { a.MessageId, a.FileId })
            .IsUnique();

        b.Entity<GroupMessageAttachment>()
            .HasOne<GroupMessage>()
            .WithMany(m => m.Attachments)
            .HasForeignKey(a => a.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<GroupMessageAttachment>()
            .HasOne<ChatFile>()
            .WithMany()
            .HasForeignKey(a => a.FileId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<GroupAvatar>()
            .HasIndex(a => a.GroupChatId);

        b.Entity<GroupAvatar>()
            .HasOne<GroupChat>()
            .WithMany()
            .HasForeignKey(a => a.GroupChatId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<GroupVoiceSession>()
            .Property(s => s.RoomName)
            .HasMaxLength(160);

        b.Entity<GroupVoiceSession>()
            .Property(s => s.State)
            .HasConversion<int>();

        b.Entity<GroupVoiceSession>()
            .HasIndex(s => new { s.GroupChatId, s.State, s.StartedAt });

        b.Entity<GroupVoiceSession>()
            .HasOne<GroupChat>()
            .WithMany()
            .HasForeignKey(s => s.GroupChatId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<GroupVoiceParticipant>()
            .Property(p => p.ClientInfo)
            .HasMaxLength(256);

        b.Entity<GroupVoiceParticipant>()
            .HasIndex(p => new { p.SessionId, p.UserId })
            .IsUnique();

        b.Entity<GroupVoiceParticipant>()
            .HasIndex(p => new { p.GroupChatId, p.IsActive, p.LastSeenAt });

        b.Entity<GroupVoiceParticipant>()
            .HasOne<GroupVoiceSession>()
            .WithMany(s => s.Participants)
            .HasForeignKey(p => p.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<GroupVoiceParticipant>()
            .HasOne<GroupChat>()
            .WithMany()
            .HasForeignKey(p => p.GroupChatId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

