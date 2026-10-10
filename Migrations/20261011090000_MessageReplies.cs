using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Ответы на сообщения: ссылка на исходное сообщение (без текста — E2EE не затрагивается).</summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(AppDbContext))]
    [Migration("20261011090000_MessageReplies")]
    public partial class MessageReplies : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    ALTER TABLE "DirectMessages" ADD COLUMN IF NOT EXISTS "ReplyToMessageId" uuid NULL;
                    ALTER TABLE "GroupMessages" ADD COLUMN IF NOT EXISTS "ReplyToMessageId" uuid NULL;
                    """);
                return;
            }

            migrationBuilder.AddColumn<Guid>(name: "ReplyToMessageId", table: "DirectMessages", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "ReplyToMessageId", table: "GroupMessages", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ReplyToMessageId", table: "DirectMessages");
            migrationBuilder.DropColumn(name: "ReplyToMessageId", table: "GroupMessages");
        }
    }
}
