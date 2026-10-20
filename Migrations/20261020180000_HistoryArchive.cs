using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>История на нескольких устройствах: запечатанное хранилище ключей аккаунта и архив сообщений.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261020180000_HistoryArchive")]
    public partial class HistoryArchive : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "HistoryVaults" (
                        "UserId" uuid NOT NULL PRIMARY KEY,
                        "Version" bigint NOT NULL,
                        "Keyring" bytea NOT NULL,
                        "PasswordWrap" bytea NULL,
                        "UpdatedByDeviceId" uuid NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "FK_HistoryVaults_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS "HistoryRecords" (
                        "UserId" uuid NOT NULL,
                        "MessageId" uuid NOT NULL,
                        "CiphertextHash" character varying(64) NOT NULL,
                        "Blob" bytea NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_HistoryRecords" PRIMARY KEY ("UserId", "MessageId"),
                        CONSTRAINT "FK_HistoryRecords_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS "IX_HistoryRecords_UserId_UpdatedAt" ON "HistoryRecords" ("UserId", "UpdatedAt");
                    CREATE INDEX IF NOT EXISTS "IX_HistoryRecords_MessageId" ON "HistoryRecords" ("MessageId");
                    """);
                return;
            }

            migrationBuilder.CreateTable(
                name: "HistoryVaults",
                columns: table => new
                {
                    UserId = table.Column<Guid>(nullable: false),
                    Version = table.Column<long>(nullable: false),
                    Keyring = table.Column<byte[]>(nullable: false),
                    PasswordWrap = table.Column<byte[]>(nullable: true),
                    UpdatedByDeviceId = table.Column<Guid>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryVaults", x => x.UserId);
                    table.ForeignKey("FK_HistoryVaults_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HistoryRecords",
                columns: table => new
                {
                    UserId = table.Column<Guid>(nullable: false),
                    MessageId = table.Column<Guid>(nullable: false),
                    CiphertextHash = table.Column<string>(maxLength: 64, nullable: false),
                    Blob = table.Column<byte[]>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryRecords", x => new { x.UserId, x.MessageId });
                    table.ForeignKey("FK_HistoryRecords_Users_UserId", x => x.UserId, "Users", "Id", onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_HistoryRecords_UserId_UpdatedAt", table: "HistoryRecords", columns: new[] { "UserId", "UpdatedAt" });
            migrationBuilder.CreateIndex(name: "IX_HistoryRecords_MessageId", table: "HistoryRecords", column: "MessageId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HistoryRecords");
            migrationBuilder.DropTable(name: "HistoryVaults");
        }
    }
}
