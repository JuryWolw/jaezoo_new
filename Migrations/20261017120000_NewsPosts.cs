using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Новости во вкладке «Новости» и отметка «просмотрено до» у пользователя.</summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(AppDbContext))]
    [Migration("20261017120000_NewsPosts")]
    public partial class NewsPosts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "NewsPosts" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "PublishedAt" timestamp with time zone NOT NULL,
                        "EditedAt" timestamp with time zone NULL,
                        "Category" integer NOT NULL DEFAULT 0,
                        "Title" character varying(160) NOT NULL DEFAULT '',
                        "Summary" character varying(400) NOT NULL DEFAULT '',
                        "Body" character varying(20000) NOT NULL DEFAULT '',
                        "CoverKey" character varying(256) NULL,
                        "Version" character varying(32) NULL,
                        "IsPinned" boolean NOT NULL DEFAULT FALSE,
                        "SentToSystemChat" boolean NOT NULL DEFAULT FALSE,
                        "CreatedByUserId" uuid NULL,
                        "CreatedByName" character varying(64) NOT NULL DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS "IX_NewsPosts_PublishedAt" ON "NewsPosts" ("PublishedAt");
                    CREATE INDEX IF NOT EXISTS "IX_NewsPosts_Version" ON "NewsPosts" ("Version");
                    ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "LastSeenNewsAt" timestamp with time zone NULL;
                    """);
                return;
            }

            migrationBuilder.CreateTable(
                name: "NewsPosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    PublishedAt = table.Column<DateTime>(nullable: false),
                    EditedAt = table.Column<DateTime>(nullable: true),
                    Category = table.Column<int>(nullable: false),
                    Title = table.Column<string>(maxLength: 160, nullable: false),
                    Summary = table.Column<string>(maxLength: 400, nullable: false),
                    Body = table.Column<string>(maxLength: 20000, nullable: false),
                    CoverKey = table.Column<string>(maxLength: 256, nullable: true),
                    Version = table.Column<string>(maxLength: 32, nullable: true),
                    IsPinned = table.Column<bool>(nullable: false),
                    SentToSystemChat = table.Column<bool>(nullable: false),
                    CreatedByUserId = table.Column<Guid>(nullable: true),
                    CreatedByName = table.Column<string>(maxLength: 64, nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_NewsPosts", x => x.Id));

            migrationBuilder.CreateIndex(name: "IX_NewsPosts_PublishedAt", table: "NewsPosts", column: "PublishedAt");
            migrationBuilder.CreateIndex(name: "IX_NewsPosts_Version", table: "NewsPosts", column: "Version");
            migrationBuilder.AddColumn<DateTime>(name: "LastSeenNewsAt", table: "Users", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "NewsPosts");
            migrationBuilder.DropColumn(name: "LastSeenNewsAt", table: "Users");
        }
    }
}
