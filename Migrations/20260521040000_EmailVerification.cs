using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class EmailVerification : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailVerificationCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: ActiveProvider.Contains("Npgsql") ? "uuid" : "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: ActiveProvider.Contains("Npgsql") ? "uuid" : "TEXT", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    CodeHash = table.Column<string>(type: ActiveProvider.Contains("Npgsql") ? "character varying(128)" : "TEXT", maxLength: 128, nullable: false),
                    Salt = table.Column<string>(type: ActiveProvider.Contains("Npgsql") ? "character varying(64)" : "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: ActiveProvider.Contains("Npgsql") ? "timestamp with time zone" : "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: ActiveProvider.Contains("Npgsql") ? "timestamp with time zone" : "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: ActiveProvider.Contains("Npgsql") ? "timestamp with time zone" : "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastSentAt = table.Column<DateTime>(type: ActiveProvider.Contains("Npgsql") ? "timestamp with time zone" : "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: ActiveProvider.Contains("Npgsql") ? "character varying(64)" : "TEXT", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: ActiveProvider.Contains("Npgsql") ? "character varying(256)" : "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailVerificationCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailVerificationCodes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationCodes_UserId_Purpose_ConsumedAt_ExpiresAt",
                table: "EmailVerificationCodes",
                columns: new[] { "UserId", "Purpose", "ConsumedAt", "ExpiresAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "EmailVerificationCodes");
        }
    }
}
