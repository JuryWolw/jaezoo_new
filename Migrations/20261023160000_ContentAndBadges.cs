using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Страницы вкладок клиента (правятся из админки) и плашки у ника.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261023160000_ContentAndBadges")]
    public partial class ContentAndBadges : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "SiteContents" (
                        "Key" character varying(32) NOT NULL PRIMARY KEY,
                        "Json" text NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        "UpdatedByName" character varying(64) NULL
                    );
                    CREATE TABLE IF NOT EXISTS "Badges" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "Title" character varying(40) NOT NULL,
                        "ColorHex" character varying(9) NOT NULL,
                        "Icon" character varying(8) NULL,
                        "Description" character varying(200) NULL,
                        "SortOrder" integer NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS "UserBadges" (
                        "UserId" uuid NOT NULL,
                        "BadgeId" uuid NOT NULL,
                        "GrantedById" uuid NULL,
                        "GrantedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_UserBadges" PRIMARY KEY ("UserId", "BadgeId"),
                        CONSTRAINT "FK_UserBadges_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE,
                        CONSTRAINT "FK_UserBadges_Badges_BadgeId" FOREIGN KEY ("BadgeId") REFERENCES "Badges" ("Id") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS "IX_UserBadges_BadgeId" ON "UserBadges" ("BadgeId");
                    """);
                return;
            }

            migrationBuilder.CreateTable(
                name: "SiteContents",
                columns: table => new
                {
                    Key = table.Column<string>(maxLength: 32, nullable: false),
                    Json = table.Column<string>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedByName = table.Column<string>(maxLength: 64, nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_SiteContents", x => x.Key));
            migrationBuilder.CreateTable(
                name: "Badges",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Title = table.Column<string>(maxLength: 40, nullable: false),
                    ColorHex = table.Column<string>(maxLength: 9, nullable: false),
                    Icon = table.Column<string>(maxLength: 8, nullable: true),
                    Description = table.Column<string>(maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_Badges", x => x.Id));
            migrationBuilder.CreateTable(
                name: "UserBadges",
                columns: table => new
                {
                    UserId = table.Column<Guid>(nullable: false),
                    BadgeId = table.Column<Guid>(nullable: false),
                    GrantedById = table.Column<Guid>(nullable: true),
                    GrantedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserBadges", x => new { x.UserId, x.BadgeId });
                    table.ForeignKey("FK_UserBadges_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
                    table.ForeignKey("FK_UserBadges_Badges_BadgeId", x => x.BadgeId, "Badges", "Id", onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_UserBadges_BadgeId", table: "UserBadges", column: "BadgeId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UserBadges");
            migrationBuilder.DropTable(name: "Badges");
            migrationBuilder.DropTable(name: "SiteContents");
        }
    }
}
