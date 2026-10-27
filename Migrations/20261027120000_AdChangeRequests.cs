using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Заявки рекламного менеджера на изменение рекламы (проверка в Control Room).</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261027120000_AdChangeRequests")]
    public partial class AdChangeRequests : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "AdChangeRequests" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "CreatedById" uuid NOT NULL,
                        "CreatedByName" character varying(64) NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "Comment" character varying(500) NULL,
                        "ItemsJson" text NOT NULL,
                        "Status" integer NOT NULL,
                        "ReviewedById" uuid NULL,
                        "ReviewedByName" character varying(64) NULL,
                        "ReviewedAt" timestamp with time zone NULL,
                        "RejectReason" character varying(500) NULL
                    );
                    CREATE INDEX IF NOT EXISTS "IX_AdChangeRequests_Status_CreatedAt" ON "AdChangeRequests" ("Status", "CreatedAt");
                    """);
                return;
            }

            migrationBuilder.CreateTable(
                name: "AdChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    CreatedById = table.Column<Guid>(nullable: false),
                    CreatedByName = table.Column<string>(maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    Comment = table.Column<string>(maxLength: 500, nullable: true),
                    ItemsJson = table.Column<string>(nullable: false),
                    Status = table.Column<int>(nullable: false),
                    ReviewedById = table.Column<Guid>(nullable: true),
                    ReviewedByName = table.Column<string>(maxLength: 64, nullable: true),
                    ReviewedAt = table.Column<DateTime>(nullable: true),
                    RejectReason = table.Column<string>(maxLength: 500, nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_AdChangeRequests", x => x.Id));
            migrationBuilder.CreateIndex(name: "IX_AdChangeRequests_Status_CreatedAt", table: "AdChangeRequests", columns: new[] { "Status", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AdChangeRequests");
        }
    }
}
