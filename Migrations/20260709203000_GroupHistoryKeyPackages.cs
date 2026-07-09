using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(AppDbContext))]
    [Migration("20260709203000_GroupHistoryKeyPackages")]
    public partial class GroupHistoryKeyPackages : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    ALTER TABLE "GroupChats"
                        ADD COLUMN IF NOT EXISTS "HistoryPolicy" integer NOT NULL DEFAULT 1,
                        ADD COLUMN IF NOT EXISTS "HistoryPolicyChangedAt" timestamptz NULL;

                    ALTER TABLE "GroupChats" ALTER COLUMN "HistoryPolicy" SET DEFAULT 1;

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

                    CREATE INDEX IF NOT EXISTS "IX_GroupHistoryKeyPackages_Target"
                        ON "GroupHistoryKeyPackages" ("GroupChatId", "TargetUserId", "TargetDeviceId");

                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_GroupHistoryKeyPackages_UniquePackage"
                        ON "GroupHistoryKeyPackages" ("GroupChatId", "SecurityEpoch", "TargetUserId", "TargetDeviceId", "SenderUserId", "SenderDeviceId", "SenderKeyId");
                    """);
                return;
            }

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "GroupHistoryKeyPackages" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_GroupHistoryKeyPackages" PRIMARY KEY,
                    "GroupChatId" TEXT NOT NULL,
                    "SenderUserId" TEXT NOT NULL,
                    "SenderDeviceId" TEXT NOT NULL,
                    "SenderKeyId" TEXT NOT NULL,
                    "SecurityEpoch" INTEGER NOT NULL DEFAULT 1,
                    "ProviderUserId" TEXT NOT NULL,
                    "ProviderDeviceId" TEXT NOT NULL,
                    "TargetUserId" TEXT NOT NULL,
                    "TargetDeviceId" TEXT NOT NULL,
                    "ProviderPublicKeyBase64" TEXT NOT NULL,
                    "TargetPublicKeyBase64" TEXT NOT NULL,
                    "NonceBase64" TEXT NOT NULL,
                    "CiphertextBase64" TEXT NOT NULL,
                    "TagBase64" TEXT NOT NULL,
                    "Algorithm" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "DeliveredAt" TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS "IX_GroupHistoryKeyPackages_Target"
                    ON "GroupHistoryKeyPackages" ("GroupChatId", "TargetUserId", "TargetDeviceId");

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_GroupHistoryKeyPackages_UniquePackage"
                    ON "GroupHistoryKeyPackages" ("GroupChatId", "SecurityEpoch", "TargetUserId", "TargetDeviceId", "SenderUserId", "SenderDeviceId", "SenderKeyId");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GroupHistoryKeyPackages");
        }
    }
}
