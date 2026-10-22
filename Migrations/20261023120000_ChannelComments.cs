using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Комментарии каналов: ветки под постами и беседа (ThreadRootId), выключение у поста, режим канала, вступление в беседу.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261023120000_ChannelComments")]
    public partial class ChannelComments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    ALTER TABLE "GroupMessages" ADD COLUMN IF NOT EXISTS "ThreadRootId" uuid NULL;
                    ALTER TABLE "GroupMessages" ADD COLUMN IF NOT EXISTS "CommentsClosed" boolean NOT NULL DEFAULT FALSE;
                    ALTER TABLE "GroupMessages" ADD COLUMN IF NOT EXISTS "ReplyToId" uuid NULL;
                    ALTER TABLE "GroupChats" ADD COLUMN IF NOT EXISTS "CommentsMode" integer NOT NULL DEFAULT 1;
                    ALTER TABLE "GroupChatMembers" ADD COLUMN IF NOT EXISTS "InDiscussion" boolean NOT NULL DEFAULT FALSE;
                    CREATE INDEX IF NOT EXISTS "IX_GroupMessages_GroupChatId_ThreadRootId_SentAt" ON "GroupMessages" ("GroupChatId", "ThreadRootId", "SentAt");
                    """);
                return;
            }

            migrationBuilder.AddColumn<Guid>(name: "ThreadRootId", table: "GroupMessages", nullable: true);
            migrationBuilder.AddColumn<bool>(name: "CommentsClosed", table: "GroupMessages", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<Guid>(name: "ReplyToId", table: "GroupMessages", nullable: true);
            migrationBuilder.AddColumn<int>(name: "CommentsMode", table: "GroupChats", nullable: false, defaultValue: 1);
            migrationBuilder.AddColumn<bool>(name: "InDiscussion", table: "GroupChatMembers", nullable: false, defaultValue: false);
            migrationBuilder.CreateIndex(name: "IX_GroupMessages_GroupChatId_ThreadRootId_SentAt", table: "GroupMessages", columns: new[] { "GroupChatId", "ThreadRootId", "SentAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_GroupMessages_GroupChatId_ThreadRootId_SentAt", table: "GroupMessages");
            migrationBuilder.DropColumn(name: "ThreadRootId", table: "GroupMessages");
            migrationBuilder.DropColumn(name: "CommentsClosed", table: "GroupMessages");
            migrationBuilder.DropColumn(name: "ReplyToId", table: "GroupMessages");
            migrationBuilder.DropColumn(name: "CommentsMode", table: "GroupChats");
            migrationBuilder.DropColumn(name: "InDiscussion", table: "GroupChatMembers");
        }
    }
}
