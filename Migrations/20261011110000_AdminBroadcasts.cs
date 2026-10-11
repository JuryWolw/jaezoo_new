using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Рассылки из админки (уведомления и системный чат JaeZoo).</summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(AppDbContext))]
    [Migration("20261011110000_AdminBroadcasts")]
    public partial class AdminBroadcasts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "AdminBroadcasts" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "CreatedByUserId" uuid NOT NULL,
                        "CreatedByName" character varying(64) NOT NULL DEFAULT '',
                        "Title" character varying(120) NOT NULL DEFAULT '',
                        "Text" character varying(4000) NOT NULL DEFAULT '',
                        "Channels" integer NOT NULL DEFAULT 0,
                        "Audience" integer NOT NULL DEFAULT 0,
                        "DeliverOffline" boolean NOT NULL DEFAULT FALSE,
                        "ExpiresAt" timestamp with time zone NOT NULL,
                        "RecipientsCount" integer NOT NULL DEFAULT 0,
                        "FileIdsCsv" character varying(1024) NULL
                    );
                    CREATE INDEX IF NOT EXISTS "IX_AdminBroadcasts_CreatedAt" ON "AdminBroadcasts" ("CreatedAt");
                    """);
                return;
            }

            migrationBuilder.CreateTable(
                name: "AdminBroadcasts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    CreatedByUserId = table.Column<Guid>(nullable: false),
                    CreatedByName = table.Column<string>(maxLength: 64, nullable: false),
                    Title = table.Column<string>(maxLength: 120, nullable: false),
                    Text = table.Column<string>(maxLength: 4000, nullable: false),
                    Channels = table.Column<int>(nullable: false),
                    Audience = table.Column<int>(nullable: false),
                    DeliverOffline = table.Column<bool>(nullable: false),
                    ExpiresAt = table.Column<DateTime>(nullable: false),
                    RecipientsCount = table.Column<int>(nullable: false),
                    FileIdsCsv = table.Column<string>(maxLength: 1024, nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_AdminBroadcasts", x => x.Id));

            migrationBuilder.CreateIndex(name: "IX_AdminBroadcasts_CreatedAt", table: "AdminBroadcasts", column: "CreatedAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AdminBroadcasts");
        }
    }
}
