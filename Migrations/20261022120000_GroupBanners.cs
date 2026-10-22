using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Шапки групп и каналов: таблица картинок и ссылка с версией в GroupChats.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261022120000_GroupBanners")]
    public partial class GroupBanners : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    ALTER TABLE "GroupChats" ADD COLUMN IF NOT EXISTS "BannerUrl" character varying(512) NULL;
                    CREATE TABLE IF NOT EXISTS "GroupBanners" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "GroupChatId" uuid NOT NULL,
                        "Data" bytea NOT NULL,
                        "ContentType" text NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "FK_GroupBanners_GroupChats_GroupChatId" FOREIGN KEY ("GroupChatId") REFERENCES "GroupChats" ("Id") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS "IX_GroupBanners_GroupChatId" ON "GroupBanners" ("GroupChatId");
                    """);
                return;
            }

            migrationBuilder.AddColumn<string>(name: "BannerUrl", table: "GroupChats", maxLength: 512, nullable: true);
            migrationBuilder.CreateTable(
                name: "GroupBanners",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    GroupChatId = table.Column<Guid>(nullable: false),
                    Data = table.Column<byte[]>(nullable: false),
                    ContentType = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBanners", x => x.Id);
                    table.ForeignKey("FK_GroupBanners_GroupChats_GroupChatId", x => x.GroupChatId, "GroupChats", "Id", onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_GroupBanners_GroupChatId", table: "GroupBanners", column: "GroupChatId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GroupBanners");
            migrationBuilder.DropColumn(name: "BannerUrl", table: "GroupChats");
        }
    }
}
