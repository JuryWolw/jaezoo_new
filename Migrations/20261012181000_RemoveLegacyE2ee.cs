using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>
    /// Removes the legacy E2EE (pre-MLS). Owner decision (2026-10-12): old encrypted history is cleared.
    /// DESTRUCTIVE: take a database backup (pg_dump) before deploying. Down() restores the schema only;
    /// the deleted rows can come back only from that backup.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261012181000_RemoveLegacyE2ee")]
    public partial class RemoveLegacyE2ee : Migration
    {
        // User messages of the old scheme, plus system rows protected by the removed server-side
        // MessageTextProtector ("jzenc1:" prefix) and the old E2EE service notices.
        private const string LegacyDirectMessages =
            """SELECT "Id" FROM "DirectMessages" WHERE "Kind" = 0 OR "Text" LIKE 'jzenc1:%'""";

        private const string LegacyGroupMessages =
            """SELECT "Id" FROM "GroupMessages" WHERE "Kind" = 0 OR "Text" LIKE 'jzenc1:%' OR "SystemKey" IN ('group_history_available', 'group_security_keys_updated')""";

        // Files of removed messages are marked deleted (FileCleanupService removes the blobs). Postgres only:
        // on a fresh database "ChatFiles.DeletedAt" is added by startup schema code after migrations.
        private static readonly string MarkLegacyFilesDeleted = $"""
            UPDATE "ChatFiles" SET "DeletedAt" = CURRENT_TIMESTAMP
                WHERE "DeletedAt" IS NULL AND "Id" IN (
                    SELECT "FileId" FROM "DirectMessageAttachments" WHERE "MessageId" IN ({LegacyDirectMessages})
                    UNION
                    SELECT "FileId" FROM "GroupMessageAttachments" WHERE "MessageId" IN ({LegacyGroupMessages}));
            """;

        private static readonly string ClearLegacyHistory = $"""
            DELETE FROM "DirectMessageAttachments" WHERE "MessageId" IN ({LegacyDirectMessages});
            DELETE FROM "GroupMessageAttachments" WHERE "MessageId" IN ({LegacyGroupMessages});
            DELETE FROM "DirectMessages" WHERE "Id" IN ({LegacyDirectMessages});
            DELETE FROM "GroupMessages" WHERE "Id" IN ({LegacyGroupMessages});
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
                migrationBuilder.Sql(MarkLegacyFilesDeleted);
            migrationBuilder.Sql(ClearLegacyHistory);

            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    DROP TABLE IF EXISTS "GroupHistoryKeyPackages";
                    DROP TABLE IF EXISTS "E2eeDeviceApprovalRequests";
                    DROP TABLE IF EXISTS "E2eeOneTimePreKeys";
                    DROP TABLE IF EXISTS "E2eeSignedPreKeys";
                    DROP TABLE IF EXISTS "E2eeEncryptedBackups";
                    DROP TABLE IF EXISTS "UserE2eeKeys";

                    DROP INDEX IF EXISTS "IX_DirectMessages_E2eeEnvelopeVersion";
                    DROP INDEX IF EXISTS "IX_GroupMessages_E2eeEnvelopeVersion";
                    DROP INDEX IF EXISTS "IX_DirectMessages_ForwardedFromMessageId";
                    DROP INDEX IF EXISTS "IX_GroupMessages_ForwardedFromMessageId";
                    DROP INDEX IF EXISTS "IX_GroupMessages_GroupChatId_GroupSecurityEpoch_SentAt";

                    ALTER TABLE "DirectMessages"
                        DROP COLUMN IF EXISTS "E2eeEnvelopeVersion",
                        DROP COLUMN IF EXISTS "E2eeProtocol",
                        DROP COLUMN IF EXISTS "ForwardedFromMessageId",
                        DROP COLUMN IF EXISTS "ReplyToMessageId";

                    ALTER TABLE "GroupMessages"
                        DROP COLUMN IF EXISTS "E2eeEnvelopeVersion",
                        DROP COLUMN IF EXISTS "E2eeProtocol",
                        DROP COLUMN IF EXISTS "GroupSecurityEpoch",
                        DROP COLUMN IF EXISTS "ForwardedFromMessageId",
                        DROP COLUMN IF EXISTS "ReplyToMessageId";

                    ALTER TABLE "GroupChats"
                        DROP COLUMN IF EXISTS "SecurityEpoch",
                        DROP COLUMN IF EXISTS "SecurityEpochChangedAt",
                        DROP COLUMN IF EXISTS "HistoryPolicy",
                        DROP COLUMN IF EXISTS "HistoryPolicyChangedAt";
                    """);
                return;
            }

            // SQLite (local development only).
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS "GroupHistoryKeyPackages";
                DROP TABLE IF EXISTS "E2eeDeviceApprovalRequests";
                DROP TABLE IF EXISTS "E2eeOneTimePreKeys";
                DROP TABLE IF EXISTS "E2eeSignedPreKeys";
                DROP TABLE IF EXISTS "E2eeEncryptedBackups";
                DROP TABLE IF EXISTS "UserE2eeKeys";
                DROP INDEX IF EXISTS "IX_DirectMessages_E2eeEnvelopeVersion";
                DROP INDEX IF EXISTS "IX_GroupMessages_E2eeEnvelopeVersion";
                DROP INDEX IF EXISTS "IX_DirectMessages_ForwardedFromMessageId";
                DROP INDEX IF EXISTS "IX_GroupMessages_ForwardedFromMessageId";
                DROP INDEX IF EXISTS "IX_GroupMessages_GroupChatId_GroupSecurityEpoch_SentAt";
                """);
            // Legacy columns may or may not exist in a dev SQLite file (some were added by startup code),
            // and SQLite has no DROP COLUMN IF EXISTS: they stay there unmapped (all have defaults).
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    ALTER TABLE "DirectMessages"
                        ADD COLUMN IF NOT EXISTS "E2eeEnvelopeVersion" integer NOT NULL DEFAULT 0,
                        ADD COLUMN IF NOT EXISTS "E2eeProtocol" character varying(64) NULL,
                        ADD COLUMN IF NOT EXISTS "ForwardedFromMessageId" uuid NULL,
                        ADD COLUMN IF NOT EXISTS "ReplyToMessageId" uuid NULL;
                    ALTER TABLE "GroupMessages"
                        ADD COLUMN IF NOT EXISTS "E2eeEnvelopeVersion" integer NOT NULL DEFAULT 0,
                        ADD COLUMN IF NOT EXISTS "E2eeProtocol" character varying(64) NULL,
                        ADD COLUMN IF NOT EXISTS "GroupSecurityEpoch" integer NOT NULL DEFAULT 1,
                        ADD COLUMN IF NOT EXISTS "ForwardedFromMessageId" uuid NULL,
                        ADD COLUMN IF NOT EXISTS "ReplyToMessageId" uuid NULL;
                    ALTER TABLE "GroupChats"
                        ADD COLUMN IF NOT EXISTS "SecurityEpoch" integer NOT NULL DEFAULT 1,
                        ADD COLUMN IF NOT EXISTS "SecurityEpochChangedAt" timestamptz NOT NULL DEFAULT now(),
                        ADD COLUMN IF NOT EXISTS "HistoryPolicy" integer NOT NULL DEFAULT 1,
                        ADD COLUMN IF NOT EXISTS "HistoryPolicyChangedAt" timestamptz NULL;
                    CREATE INDEX IF NOT EXISTS "IX_DirectMessages_ForwardedFromMessageId" ON "DirectMessages" ("ForwardedFromMessageId");
                    CREATE INDEX IF NOT EXISTS "IX_GroupMessages_ForwardedFromMessageId" ON "GroupMessages" ("ForwardedFromMessageId");
                    CREATE INDEX IF NOT EXISTS "IX_GroupMessages_GroupChatId_GroupSecurityEpoch_SentAt" ON "GroupMessages" ("GroupChatId", "GroupSecurityEpoch", "SentAt");

                    CREATE TABLE IF NOT EXISTS "UserE2eeKeys" (
                        "Id" uuid NOT NULL,
                        "UserId" uuid NOT NULL,
                        "DeviceId" character varying(64) NOT NULL DEFAULT 'legacy',
                        "PublicKeyBase64" character varying(8192) NOT NULL,
                        "Algorithm" character varying(64) NOT NULL,
                        "Fingerprint" character varying(128) NOT NULL,
                        "DeviceName" character varying(128) NULL,
                        "IsRevoked" boolean NOT NULL DEFAULT false,
                        "IsTrusted" boolean NOT NULL DEFAULT true,
                        "RevokedAt" timestamp with time zone NULL,
                        "LastSeenAt" timestamp with time zone NULL,
                        "DeviceKeyVersion" integer NOT NULL DEFAULT 2,
                        "TrustState" integer NOT NULL DEFAULT 1,
                        "RequiresUserVerification" boolean NOT NULL DEFAULT false,
                        "UserVerifiedAt" timestamp with time zone NULL,
                        "LastIpAddress" character varying(64) NULL,
                        "Platform" character varying(64) NULL,
                        "ClientVersion" character varying(32) NULL,
                        "SigningPublicKeyBase64" character varying(8192) NULL,
                        "SigningKeyFingerprint" character varying(128) NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_UserE2eeKeys" PRIMARY KEY ("Id"),
                        CONSTRAINT "FK_UserE2eeKeys_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_UserE2eeKeys_UserId_DeviceId" ON "UserE2eeKeys" ("UserId", "DeviceId");

                    CREATE TABLE IF NOT EXISTS "E2eeDeviceApprovalRequests" (
                        "Id" uuid NOT NULL,
                        "UserId" uuid NOT NULL,
                        "DeviceId" character varying(64) NOT NULL,
                        "Fingerprint" character varying(128) NOT NULL,
                        "DeviceName" character varying(128) NULL,
                        "Platform" character varying(64) NULL,
                        "ClientVersion" character varying(32) NULL,
                        "LastIpAddress" character varying(64) NULL,
                        "Status" character varying(32) NOT NULL DEFAULT 'Pending',
                        "ApprovedByDeviceId" character varying(64) NULL,
                        "Reason" character varying(256) NULL,
                        "RequestedAt" timestamp with time zone NOT NULL DEFAULT now(),
                        "ExpiresAt" timestamp with time zone NOT NULL,
                        "ApprovedAt" timestamp with time zone NULL,
                        "RejectedAt" timestamp with time zone NULL,
                        CONSTRAINT "PK_E2eeDeviceApprovalRequests" PRIMARY KEY ("Id"),
                        CONSTRAINT "FK_E2eeDeviceApprovalRequests_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS "E2eeSignedPreKeys" (
                        "Id" uuid NOT NULL,
                        "UserId" uuid NOT NULL,
                        "DeviceId" character varying(64) NOT NULL,
                        "KeyId" character varying(64) NOT NULL,
                        "PublicKeyBase64" character varying(8192) NOT NULL,
                        "SignatureBase64" character varying(8192) NOT NULL,
                        "Algorithm" character varying(96) NOT NULL,
                        "IsRevoked" boolean NOT NULL DEFAULT false,
                        "RevokedAt" timestamp with time zone NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_E2eeSignedPreKeys" PRIMARY KEY ("Id"),
                        CONSTRAINT "FK_E2eeSignedPreKeys_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS "E2eeOneTimePreKeys" (
                        "Id" uuid NOT NULL,
                        "UserId" uuid NOT NULL,
                        "DeviceId" character varying(64) NOT NULL,
                        "KeyId" character varying(64) NOT NULL,
                        "PublicKeyBase64" character varying(8192) NOT NULL,
                        "Algorithm" character varying(64) NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "ClaimedAt" timestamp with time zone NULL,
                        "ClaimedByUserId" uuid NULL,
                        "ClaimedByDeviceId" character varying(64) NULL,
                        CONSTRAINT "PK_E2eeOneTimePreKeys" PRIMARY KEY ("Id"),
                        CONSTRAINT "FK_E2eeOneTimePreKeys_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS "E2eeEncryptedBackups" (
                        "Id" uuid NOT NULL,
                        "UserId" uuid NOT NULL,
                        "DeviceId" character varying(64) NOT NULL,
                        "PublicKeyFingerprint" character varying(128) NULL,
                        "Kdf" character varying(64) NOT NULL,
                        "SaltBase64" character varying(256) NOT NULL,
                        "NonceBase64" character varying(256) NOT NULL,
                        "CiphertextBase64" text NOT NULL,
                        "TagBase64" character varying(256) NOT NULL,
                        "Version" integer NOT NULL DEFAULT 1,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_E2eeEncryptedBackups" PRIMARY KEY ("Id"),
                        CONSTRAINT "FK_E2eeEncryptedBackups_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS "GroupHistoryKeyPackages" (
                        "Id" uuid NOT NULL,
                        "GroupChatId" uuid NOT NULL,
                        "SenderUserId" uuid NOT NULL,
                        "SenderDeviceId" character varying(128) NOT NULL,
                        "SenderKeyId" character varying(128) NOT NULL,
                        "SecurityEpoch" integer NOT NULL DEFAULT 1,
                        "ProviderUserId" uuid NOT NULL,
                        "ProviderDeviceId" character varying(128) NOT NULL,
                        "TargetUserId" uuid NOT NULL,
                        "TargetDeviceId" character varying(128) NOT NULL,
                        "ProviderPublicKeyBase64" character varying(8192) NOT NULL,
                        "TargetPublicKeyBase64" character varying(8192) NOT NULL,
                        "NonceBase64" character varying(256) NOT NULL,
                        "CiphertextBase64" character varying(8192) NOT NULL,
                        "TagBase64" character varying(256) NOT NULL,
                        "Algorithm" character varying(96) NOT NULL,
                        "CreatedAt" timestamptz NOT NULL DEFAULT now(),
                        "DeliveredAt" timestamptz NULL,
                        CONSTRAINT "PK_GroupHistoryKeyPackages" PRIMARY KEY ("Id")
                    );
                    """);
                return;
            }

            // SQLite: Up() leaves legacy columns in place, so there is nothing to restore here.
        }
    }
}
