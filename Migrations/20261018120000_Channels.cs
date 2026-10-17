using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Каналы: вид чата, подпись автора, закреплённый пост; баны, пригласительные ссылки, просмотры постов.</summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(AppDbContext))]
    [Migration("20261018120000_Channels")]
    public partial class Channels : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    ALTER TABLE "GroupChats" ADD COLUMN IF NOT EXISTS "Kind" integer NOT NULL DEFAULT 0;
                    ALTER TABLE "GroupChats" ADD COLUMN IF NOT EXISTS "SignPosts" boolean NOT NULL DEFAULT FALSE;
                    ALTER TABLE "GroupChats" ADD COLUMN IF NOT EXISTS "PinnedMessageId" uuid NULL;
                    CREATE INDEX IF NOT EXISTS "IX_GroupChats_Kind_IsPublic" ON "GroupChats" ("Kind", "IsPublic");

                    CREATE TABLE IF NOT EXISTS "GroupBans" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "GroupChatId" uuid NOT NULL,
                        "UserId" uuid NOT NULL,
                        "BannedById" uuid NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "Reason" character varying(200) NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_GroupBans_GroupChatId_UserId" ON "GroupBans" ("GroupChatId", "UserId");

                    CREATE TABLE IF NOT EXISTS "GroupInvites" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "GroupChatId" uuid NOT NULL,
                        "Code" character varying(32) NOT NULL,
                        "CreatedById" uuid NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "ExpiresAt" timestamp with time zone NULL,
                        "MaxUses" integer NULL,
                        "Uses" integer NOT NULL DEFAULT 0,
                        "RevokedAt" timestamp with time zone NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_GroupInvites_Code" ON "GroupInvites" ("Code");
                    CREATE INDEX IF NOT EXISTS "IX_GroupInvites_GroupChatId" ON "GroupInvites" ("GroupChatId");

                    CREATE TABLE IF NOT EXISTS "GroupMessageViews" (
                        "MessageId" uuid NOT NULL,
                        "UserId" uuid NOT NULL,
                        "ViewedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_GroupMessageViews" PRIMARY KEY ("MessageId", "UserId")
                    );
                    """);
                return;
            }

            migrationBuilder.AddColumn<int>(name: "Kind", table: "GroupChats", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<bool>(name: "SignPosts", table: "GroupChats", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<Guid>(name: "PinnedMessageId", table: "GroupChats", nullable: true);
            migrationBuilder.CreateIndex(name: "IX_GroupChats_Kind_IsPublic", table: "GroupChats", columns: new[] { "Kind", "IsPublic" });

            migrationBuilder.CreateTable(
                name: "GroupBans",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    GroupChatId = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    BannedById = table.Column<Guid>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    Reason = table.Column<string>(maxLength: 200, nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_GroupBans", x => x.Id));
            migrationBuilder.CreateIndex(name: "IX_GroupBans_GroupChatId_UserId", table: "GroupBans", columns: new[] { "GroupChatId", "UserId" }, unique: true);

            migrationBuilder.CreateTable(
                name: "GroupInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    GroupChatId = table.Column<Guid>(nullable: false),
                    Code = table.Column<string>(maxLength: 32, nullable: false),
                    CreatedById = table.Column<Guid>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    ExpiresAt = table.Column<DateTime>(nullable: true),
                    MaxUses = table.Column<int>(nullable: true),
                    Uses = table.Column<int>(nullable: false),
                    RevokedAt = table.Column<DateTime>(nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_GroupInvites", x => x.Id));
            migrationBuilder.CreateIndex(name: "IX_GroupInvites_Code", table: "GroupInvites", column: "Code", unique: true);
            migrationBuilder.CreateIndex(name: "IX_GroupInvites_GroupChatId", table: "GroupInvites", column: "GroupChatId");

            migrationBuilder.CreateTable(
                name: "GroupMessageViews",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    ViewedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_GroupMessageViews", x => new { x.MessageId, x.UserId }));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GroupMessageViews");
            migrationBuilder.DropTable(name: "GroupInvites");
            migrationBuilder.DropTable(name: "GroupBans");
            migrationBuilder.DropIndex(name: "IX_GroupChats_Kind_IsPublic", table: "GroupChats");
            migrationBuilder.DropColumn(name: "Kind", table: "GroupChats");
            migrationBuilder.DropColumn(name: "SignPosts", table: "GroupChats");
            migrationBuilder.DropColumn(name: "PinnedMessageId", table: "GroupChats");
        }
    }
}
