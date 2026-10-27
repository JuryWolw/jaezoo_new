using JaeZoo.Server.Models;
using JaeZoo.Server.Models.Files;
using JaeZoo.Server.Models.Moderation;
using JaeZoo.Server.Models.Mls;
using JaeZoo.Server.Models.Security;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<DirectDialog> DirectDialogs => Set<DirectDialog>();
    public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();
    public DbSet<Avatar> Avatars => Set<Avatar>();
    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();
    public DbSet<ChatFile> ChatFiles => Set<ChatFile>();
    public DbSet<DirectMessageAttachment> DirectMessageAttachments => Set<DirectMessageAttachment>();
    public DbSet<GroupChat> GroupChats => Set<GroupChat>();
    public DbSet<GroupChatMember> GroupChatMembers => Set<GroupChatMember>();
    public DbSet<GroupMessage> GroupMessages => Set<GroupMessage>();
    public DbSet<GroupBan> GroupBans => Set<GroupBan>();
    public DbSet<GroupInvite> GroupInvites => Set<GroupInvite>();
    public DbSet<GroupMessageView> GroupMessageViews => Set<GroupMessageView>();
    public DbSet<GroupMessageReaction> GroupMessageReactions => Set<GroupMessageReaction>();
    public DbSet<GroupMessageAttachment> GroupMessageAttachments => Set<GroupMessageAttachment>();
    public DbSet<GroupAvatar> GroupAvatars => Set<GroupAvatar>();
    public DbSet<GroupBanner> GroupBanners => Set<GroupBanner>();
    public DbSet<SiteContent> SiteContents => Set<SiteContent>();
    public DbSet<Badge> Badges => Set<Badge>();
    public DbSet<JaeZoo.Server.Models.Ads.AdChangeRequest> AdChangeRequests => Set<JaeZoo.Server.Models.Ads.AdChangeRequest>();
    public DbSet<UserBadge> UserBadges => Set<UserBadge>();
    public DbSet<GroupVoiceSession> GroupVoiceSessions => Set<GroupVoiceSession>();
    public DbSet<GroupVoiceParticipant> GroupVoiceParticipants => Set<GroupVoiceParticipant>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();
    public DbSet<AdminBroadcast> AdminBroadcasts => Set<AdminBroadcast>();
    public DbSet<NewsPost> NewsPosts => Set<NewsPost>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<EmailVerificationCode> EmailVerificationCodes => Set<EmailVerificationCode>();
    public DbSet<ModerationBan> ModerationBans => Set<ModerationBan>();
    public DbSet<ModerationReport> ModerationReports => Set<ModerationReport>();
    public DbSet<ModerationWarning> ModerationWarnings => Set<ModerationWarning>();
    public DbSet<FileScanAllowList> FileScanAllowList => Set<FileScanAllowList>();
    public DbSet<TwoFactorRecoveryCode> TwoFactorRecoveryCodes => Set<TwoFactorRecoveryCode>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<MlsKeyPackage> MlsKeyPackages => Set<MlsKeyPackage>();
    public DbSet<MlsGroupState> MlsGroups => Set<MlsGroupState>();
    public DbSet<MlsGroupMember> MlsGroupMembers => Set<MlsGroupMember>();
    public DbSet<MlsGroupEvent> MlsGroupEvents => Set<MlsGroupEvent>();
    public DbSet<MlsWelcome> MlsWelcomes => Set<MlsWelcome>();
    public DbSet<MlsGroupInfo> MlsGroupInfos => Set<MlsGroupInfo>();
    public DbSet<HistoryVault> HistoryVaults => Set<HistoryVault>();
    public DbSet<HistoryRecord> HistoryRecords => Set<HistoryRecord>();
    public DbSet<HistoryAccessRequest> HistoryAccessRequests => Set<HistoryAccessRequest>();
    public DbSet<HistoryGrant> HistoryGrants => Set<HistoryGrant>();
    public DbSet<TwoFactorLoginChallenge> TwoFactorLoginChallenges => Set<TwoFactorLoginChallenge>();
    public DbSet<LoginAlertToken> LoginAlertTokens => Set<LoginAlertToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<User>().Property(u => u.UserName).HasMaxLength(64);
        b.Entity<User>().Property(u => u.Login).HasMaxLength(64);
        b.Entity<User>().Property(u => u.LoginNormalized).HasMaxLength(64);
        b.Entity<User>().Property(u => u.Email).HasMaxLength(128);
        b.Entity<User>().Property(u => u.EmailNormalized).HasMaxLength(128);
        b.Entity<User>().Property(u => u.LoginHash).HasMaxLength(128);
        b.Entity<User>().Property(u => u.LoginEncrypted).HasMaxLength(1024);
        b.Entity<User>().Property(u => u.EmailHash).HasMaxLength(128);
        b.Entity<User>().Property(u => u.EmailEncrypted).HasMaxLength(1024);
        b.Entity<User>().Property(u => u.PublicId).HasMaxLength(32);
        b.Entity<User>().Property(u => u.SecurityStamp).HasMaxLength(64);
        b.Entity<User>().Property(u => u.ProfileBannerUrl).HasMaxLength(512);
        b.Entity<User>().Property(u => u.ProfileTextTheme).HasMaxLength(16);
        b.Entity<User>().Property(u => u.CurrentActivityName).HasMaxLength(96);
        b.Entity<User>().Property(u => u.TwoFactorSecretEncrypted).HasMaxLength(1024);
        b.Entity<User>().Property(u => u.TwoFactorPendingSecretEncrypted).HasMaxLength(1024);
        b.Entity<User>().Property(u => u.LastSeenVisibility).HasConversion<int>();
        b.Entity<User>().Property(u => u.DisabledReason).HasMaxLength(256);

        // Старые индексы оставляем, чтобы не ломать существующую схему и код.
        // Новые проверки уникальности идут по нормализованным полям.
        b.Entity<User>().HasIndex(u => u.UserName).IsUnique();
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<User>().HasIndex(u => u.LoginNormalized).IsUnique();
        b.Entity<User>().HasIndex(u => u.EmailNormalized).IsUnique();
        b.Entity<User>().HasIndex(u => u.LoginHash).IsUnique();
        b.Entity<User>().HasIndex(u => u.EmailHash).IsUnique();
        b.Entity<User>().HasIndex(u => u.PublicId).IsUnique();


        b.Entity<GroupChat>()
            .Property(g => g.IsPublic)
            .HasDefaultValue(false);

        b.Entity<GroupChat>()
            .HasIndex(g => g.IsPublic);

        // ---------- Каналы ----------
        b.Entity<GroupChat>().Property(g => g.Kind).HasConversion<int>();
        b.Entity<GroupChat>().HasIndex(g => new { g.Kind, g.IsPublic });
        b.Entity<GroupBan>().HasIndex(x => new { x.GroupChatId, x.UserId }).IsUnique();
        b.Entity<GroupInvite>().HasIndex(x => x.Code).IsUnique();
        b.Entity<GroupInvite>().HasIndex(x => x.GroupChatId);
        b.Entity<GroupMessageView>().HasKey(x => new { x.MessageId, x.UserId });
        b.Entity<GroupMessageReaction>().HasKey(x => new { x.MessageId, x.UserId, x.Kind });
        b.Entity<GroupMessageReaction>().Property(x => x.Kind).HasMaxLength(16);


        // ---------- MLS delivery service ----------
        b.Entity<UserDevice>().HasKey(d => d.Id);
        b.Entity<UserDevice>().Property(d => d.Id).ValueGeneratedNever();
        b.Entity<UserDevice>().HasIndex(d => new { d.UserId, d.RevokedAt });
        b.Entity<UserDevice>().HasIndex(d => d.SessionId);
        b.Entity<UserDevice>().HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<MlsKeyPackage>().HasIndex(k => new { k.DeviceId, k.IsLastResort, k.CreatedAt });
        b.Entity<MlsKeyPackage>().HasOne<UserDevice>().WithMany().HasForeignKey(k => k.DeviceId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<MlsGroupState>().HasKey(g => g.Id);
        b.Entity<MlsGroupState>().Property(g => g.Id).ValueGeneratedNever();
        b.Entity<MlsGroupState>().Property(g => g.Kind).HasConversion<int>();
        b.Entity<MlsGroupState>().HasIndex(g => g.DialogId).IsUnique();
        b.Entity<MlsGroupState>().HasIndex(g => g.GroupChatId).IsUnique();

        b.Entity<MlsGroupMember>().HasKey(m => new { m.GroupId, m.DeviceId });
        b.Entity<MlsGroupMember>().HasIndex(m => m.DeviceId);
        b.Entity<MlsGroupMember>().HasOne<MlsGroupState>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<MlsGroupEvent>().HasKey(e => e.Id);
        b.Entity<MlsGroupEvent>().Property(e => e.Id).ValueGeneratedOnAdd();
        b.Entity<MlsGroupEvent>().Property(e => e.Kind).HasConversion<int>();
        b.Entity<MlsGroupEvent>().HasIndex(e => new { e.GroupId, e.Id });
        b.Entity<MlsGroupEvent>().HasOne<MlsGroupState>().WithMany().HasForeignKey(e => e.GroupId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<MlsWelcome>().HasIndex(w => new { w.TargetDeviceId, w.CreatedAt });
        b.Entity<MlsWelcome>().HasOne<MlsGroupState>().WithMany().HasForeignKey(w => w.GroupId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<MlsGroupInfo>().HasKey(i => i.GroupId);
        b.Entity<MlsGroupInfo>().HasOne<MlsGroupState>().WithMany().HasForeignKey(i => i.GroupId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<HistoryVault>().HasKey(v => v.UserId);
        b.Entity<HistoryVault>().HasOne<User>().WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HistoryRecord>().HasKey(r => new { r.UserId, r.MessageId });
        b.Entity<HistoryRecord>().Property(r => r.CiphertextHash).HasMaxLength(64);
        b.Entity<HistoryRecord>().HasIndex(r => new { r.UserId, r.UpdatedAt });
        b.Entity<HistoryRecord>().HasIndex(r => r.MessageId);
        b.Entity<HistoryRecord>().HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HistoryAccessRequest>().HasKey(r => r.Id);
        b.Entity<HistoryAccessRequest>().Property(r => r.Status).HasConversion<int>();
        b.Entity<HistoryAccessRequest>().HasIndex(r => new { r.UserId, r.Status });
        b.Entity<HistoryAccessRequest>().HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HistoryGrant>().HasKey(g => g.Id);
        b.Entity<HistoryGrant>().HasIndex(g => g.TargetDeviceId);
        b.Entity<HistoryGrant>().HasOne<User>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<TwoFactorRecoveryCode>()
            .Property(c => c.CodeHash)
            .HasMaxLength(128);

        b.Entity<TwoFactorRecoveryCode>()
            .Property(c => c.UsedIpAddress)
            .HasMaxLength(64);

        b.Entity<TwoFactorRecoveryCode>()
            .HasIndex(c => new { c.UserId, c.UsedAt });

        b.Entity<TwoFactorRecoveryCode>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<TwoFactorLoginChallenge>()
            .Property(c => c.ChallengeTokenHash)
            .HasMaxLength(128);

        b.Entity<TwoFactorLoginChallenge>()
            .Property(c => c.DeviceName)
            .HasMaxLength(128);

        b.Entity<TwoFactorLoginChallenge>()
            .Property(c => c.Platform)
            .HasMaxLength(64);

        b.Entity<TwoFactorLoginChallenge>()
            .Property(c => c.ClientVersion)
            .HasMaxLength(32);

        b.Entity<TwoFactorLoginChallenge>()
            .Property(c => c.IpAddress)
            .HasMaxLength(64);

        b.Entity<TwoFactorLoginChallenge>()
            .Property(c => c.UserAgent)
            .HasMaxLength(256);

        b.Entity<TwoFactorLoginChallenge>()
            .HasIndex(c => c.ChallengeTokenHash)
            .IsUnique();

        b.Entity<TwoFactorLoginChallenge>()
            .HasIndex(c => new { c.UserId, c.ExpiresAt, c.UsedAt });

        b.Entity<TwoFactorLoginChallenge>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);


        b.Entity<LoginAlertToken>()
            .Property(a => a.TokenHash)
            .HasMaxLength(128);

        b.Entity<LoginAlertToken>()
            .Property(a => a.IpAddress)
            .HasMaxLength(64);

        b.Entity<LoginAlertToken>()
            .Property(a => a.DeviceName)
            .HasMaxLength(128);

        b.Entity<LoginAlertToken>()
            .Property(a => a.Platform)
            .HasMaxLength(64);

        b.Entity<LoginAlertToken>()
            .Property(a => a.ClientVersion)
            .HasMaxLength(32);

        b.Entity<LoginAlertToken>()
            .Property(a => a.UsedIpAddress)
            .HasMaxLength(64);

        b.Entity<LoginAlertToken>()
            .HasIndex(a => a.TokenHash)
            .IsUnique();

        b.Entity<LoginAlertToken>()
            .HasIndex(a => new { a.UserId, a.ExpiresAt, a.UsedAt });

        b.Entity<LoginAlertToken>()
            .HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

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

        b.Entity<AdminBroadcast>().HasIndex(x => x.CreatedAt);
        b.Entity<AdminBroadcast>().HasIndex(x => x.TargetUserId);
        b.Entity<NewsPost>().HasIndex(x => x.PublishedAt);
        b.Entity<NewsPost>().HasIndex(x => x.Version);

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

        b.Entity<UserSession>()
            .Property(s => s.RefreshTokenHash)
            .HasMaxLength(128);

        b.Entity<UserSession>()
            .Property(s => s.IpAddress)
            .HasMaxLength(64);

        b.Entity<UserSession>()
            .Property(s => s.UserAgent)
            .HasMaxLength(256);

        b.Entity<UserSession>()
            .Property(s => s.DeviceName)
            .HasMaxLength(128);

        b.Entity<UserSession>()
            .Property(s => s.Platform)
            .HasMaxLength(64);

        b.Entity<UserSession>()
            .Property(s => s.ClientVersion)
            .HasMaxLength(32);

        b.Entity<UserSession>()
            .Property(s => s.FingerprintHash)
            .HasMaxLength(128);

        b.Entity<UserSession>()
            .HasIndex(s => s.RefreshTokenHash)
            .IsUnique();

        b.Entity<UserSession>()
            .HasIndex(s => new { s.UserId, s.RevokedAt, s.ExpiresAt });

        b.Entity<UserSession>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<EmailVerificationCode>()
            .Property(c => c.Purpose)
            .HasConversion<int>();

        b.Entity<EmailVerificationCode>()
            .Property(c => c.CodeHash)
            .HasMaxLength(128);

        b.Entity<EmailVerificationCode>()
            .Property(c => c.Salt)
            .HasMaxLength(64);

        b.Entity<EmailVerificationCode>()
            .Property(c => c.IpAddress)
            .HasMaxLength(64);

        b.Entity<EmailVerificationCode>()
            .Property(c => c.UserAgent)
            .HasMaxLength(256);

        b.Entity<EmailVerificationCode>()
            .HasIndex(c => new { c.UserId, c.Purpose, c.ConsumedAt, c.ExpiresAt });

        b.Entity<EmailVerificationCode>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);


        b.Entity<ModerationBan>()
            .Property(ban => ban.Type)
            .HasMaxLength(64);

        b.Entity<ModerationBan>()
            .Property(ban => ban.Reason)
            .HasMaxLength(512);

        b.Entity<ModerationBan>()
            .Property(ban => ban.RevokeReason)
            .HasMaxLength(512);

        b.Entity<ModerationBan>()
            .HasIndex(ban => new { ban.UserId, ban.RevokedAt, ban.ExpiresAt });

        b.Entity<ModerationBan>()
            .HasIndex(ban => ban.CreatedAt);

        b.Entity<ModerationReport>()
            .Property(r => r.TargetType)
            .HasMaxLength(32);

        b.Entity<ModerationReport>()
            .Property(r => r.TargetId)
            .HasMaxLength(128);

        b.Entity<ModerationReport>()
            .Property(r => r.Reason)
            .HasMaxLength(128);

        b.Entity<ModerationReport>()
            .Property(r => r.Details)
            .HasMaxLength(2000);

        b.Entity<ModerationReport>()
            .Property(r => r.Status)
            .HasMaxLength(32);

        b.Entity<ModerationReport>()
            .Property(r => r.ModerationNote)
            .HasMaxLength(2000);

        b.Entity<ModerationReport>()
            .HasIndex(r => new { r.Status, r.CreatedAt });

        b.Entity<ModerationReport>()
            .HasIndex(r => r.TargetUserId);

        b.Entity<ModerationReport>()
            .HasIndex(r => r.TargetMessageId);

        b.Entity<ModerationReport>()
            .HasIndex(r => r.TargetGroupId);

        b.Entity<ModerationWarning>()
            .Property(w => w.Reason)
            .HasMaxLength(512);

        b.Entity<ModerationWarning>()
            .Property(w => w.EmailSubject)
            .HasMaxLength(160);

        b.Entity<ModerationWarning>()
            .Property(w => w.EmailBody)
            .HasMaxLength(4000);

        b.Entity<ModerationWarning>()
            .HasIndex(w => new { w.UserId, w.CreatedAt });




        b.Entity<FileScanAllowList>()
            .Property(a => a.Sha256)
            .HasMaxLength(64);

        b.Entity<FileScanAllowList>()
            .Property(a => a.Reason)
            .HasMaxLength(512);

        b.Entity<FileScanAllowList>()
            .HasIndex(a => a.Sha256)
            .IsUnique();

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
            .HasColumnType("text");

        b.Entity<DirectMessage>()
            .Property(m => m.SystemKey)
            .HasMaxLength(64);

        b.Entity<DirectMessage>()
            .Property(m => m.Kind)
            .HasConversion<int>();

        b.Entity<DirectMessage>()
            .HasIndex(m => new { m.DialogId, m.DeletedAt, m.SentAt, m.Id });

        b.Entity<Avatar>()
            .HasIndex(a => a.UserId);

        b.Entity<UserAvatar>()
            .Property(a => a.Bucket)
            .HasMaxLength(128);

        b.Entity<UserAvatar>()
            .Property(a => a.ObjectKey)
            .HasMaxLength(512);

        b.Entity<UserAvatar>()
            .Property(a => a.Url)
            .HasMaxLength(512);

        b.Entity<UserAvatar>()
            .Property(a => a.ContentType)
            .HasMaxLength(128);

        b.Entity<UserAvatar>()
            .HasIndex(a => new { a.UserId, a.DeletedAt, a.CreatedAt });

        b.Entity<UserAvatar>()
            .HasIndex(a => new { a.UserId, a.IsCurrent });

        b.Entity<UserAvatar>()
            .HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

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

        b.Entity<ChatFile>()
            .Property(f => f.SafeFileName)
            .HasMaxLength(256);

        b.Entity<ChatFile>()
            .Property(f => f.DetectedContentType)
            .HasMaxLength(128);

        b.Entity<ChatFile>()
            .Property(f => f.Bucket)
            .HasMaxLength(128);

        b.Entity<ChatFile>()
            .Property(f => f.ObjectKey)
            .HasMaxLength(512);

        b.Entity<ChatFile>()
            .Property(f => f.Sha256)
            .HasMaxLength(64);

        b.Entity<ChatFile>()
            .Property(f => f.Kind)
            .HasConversion<int>();

        b.Entity<ChatFile>()
            .Property(f => f.ScanStatus)
            .HasConversion<int>();

        b.Entity<ChatFile>()
            .Property(f => f.RiskNote)
            .HasMaxLength(512);

        b.Entity<ChatFile>()
            .HasIndex(f => new { f.Bucket, f.ObjectKey });

        b.Entity<ChatFile>()
            .HasIndex(f => f.Sha256);

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
            .HasColumnType("text");

        b.Entity<GroupMessage>()
            .Property(m => m.SystemKey)
            .HasMaxLength(64);

        b.Entity<GroupMessage>()
            .Property(m => m.Kind)
            .HasConversion<int>();

        b.Entity<GroupMessage>()
            .HasIndex(m => new { m.GroupChatId, m.DeletedAt, m.SentAt, m.Id });

        // Ветки комментариев и беседа канала: выборка по корню.
        b.Entity<GroupMessage>()
            .HasIndex(m => new { m.GroupChatId, m.ThreadRootId, m.SentAt });

        b.Entity<GroupChat>().Property(g => g.CommentsMode).HasConversion<int>();

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

        b.Entity<GroupBanner>()
            .HasIndex(a => a.GroupChatId);
        b.Entity<UserBadge>().HasKey(x => new { x.UserId, x.BadgeId });
        b.Entity<UserBadge>().HasIndex(x => x.BadgeId);
        b.Entity<UserBadge>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserBadge>().HasOne<Badge>().WithMany().HasForeignKey(x => x.BadgeId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<JaeZoo.Server.Models.Ads.AdChangeRequest>().HasIndex(x => new { x.Status, x.CreatedAt });
        b.Entity<GroupBanner>()
            .HasOne<GroupChat>()
            .WithMany()
            .HasForeignKey(a => a.GroupChatId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<GroupChat>().Property(g => g.BannerUrl).HasMaxLength(512);

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

