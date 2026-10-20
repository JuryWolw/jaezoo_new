using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Передача ключа истории между своими устройствами: запросы нового устройства и гранты (MLS Welcome + сообщение).</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261020200000_HistoryDeviceLink")]
    public partial class HistoryDeviceLink : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "HistoryAccessRequests" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "UserId" uuid NOT NULL,
                        "DeviceId" uuid NOT NULL,
                        "KeyPackage" bytea NOT NULL,
                        "Status" integer NOT NULL,
                        "ResolvedByDeviceId" uuid NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "ExpiresAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "FK_HistoryAccessRequests_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS "IX_HistoryAccessRequests_UserId_Status" ON "HistoryAccessRequests" ("UserId", "Status");

                    CREATE TABLE IF NOT EXISTS "HistoryGrants" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "UserId" uuid NOT NULL,
                        "TargetDeviceId" uuid NOT NULL,
                        "FromDeviceId" uuid NOT NULL,
                        "Welcome" bytea NOT NULL,
                        "Ciphertext" bytea NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "FK_HistoryGrants_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS "IX_HistoryGrants_TargetDeviceId" ON "HistoryGrants" ("TargetDeviceId");
                    """);
                return;
            }

            migrationBuilder.CreateTable(
                name: "HistoryAccessRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    DeviceId = table.Column<Guid>(nullable: false),
                    KeyPackage = table.Column<byte[]>(nullable: false),
                    Status = table.Column<int>(nullable: false),
                    ResolvedByDeviceId = table.Column<Guid>(nullable: true),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    ExpiresAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryAccessRequests", x => x.Id);
                    table.ForeignKey("FK_HistoryAccessRequests_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_HistoryAccessRequests_UserId_Status", table: "HistoryAccessRequests", columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateTable(
                name: "HistoryGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    TargetDeviceId = table.Column<Guid>(nullable: false),
                    FromDeviceId = table.Column<Guid>(nullable: false),
                    Welcome = table.Column<byte[]>(nullable: false),
                    Ciphertext = table.Column<byte[]>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryGrants", x => x.Id);
                    table.ForeignKey("FK_HistoryGrants_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_HistoryGrants_TargetDeviceId", table: "HistoryGrants", column: "TargetDeviceId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HistoryGrants");
            migrationBuilder.DropTable(name: "HistoryAccessRequests");
        }
    }
}
