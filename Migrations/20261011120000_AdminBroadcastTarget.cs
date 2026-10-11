using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Рассылка конкретному пользователю: получатель и его имя на момент отправки.</summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(AppDbContext))]
    [Migration("20261011120000_AdminBroadcastTarget")]
    public partial class AdminBroadcastTarget : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    ALTER TABLE "AdminBroadcasts" ADD COLUMN IF NOT EXISTS "TargetUserId" uuid NULL;
                    ALTER TABLE "AdminBroadcasts" ADD COLUMN IF NOT EXISTS "TargetName" character varying(64) NULL;
                    CREATE INDEX IF NOT EXISTS "IX_AdminBroadcasts_TargetUserId" ON "AdminBroadcasts" ("TargetUserId");
                    """);
                return;
            }

            migrationBuilder.AddColumn<Guid>(name: "TargetUserId", table: "AdminBroadcasts", nullable: true);
            migrationBuilder.AddColumn<string>(name: "TargetName", table: "AdminBroadcasts", maxLength: 64, nullable: true);
            migrationBuilder.CreateIndex(name: "IX_AdminBroadcasts_TargetUserId", table: "AdminBroadcasts", column: "TargetUserId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_AdminBroadcasts_TargetUserId", table: "AdminBroadcasts");
            migrationBuilder.DropColumn(name: "TargetUserId", table: "AdminBroadcasts");
            migrationBuilder.DropColumn(name: "TargetName", table: "AdminBroadcasts");
        }
    }
}
