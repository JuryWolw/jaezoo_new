using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using JaeZoo.Server.Data;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260521010000_UserIdentityRework")]
    public partial class UserIdentityRework : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Login",
                table: "Users",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoginNormalized",
                table: "Users",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailNormalized",
                table: "Users",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailConfirmed",
                table: "Users",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerifiedAt",
                table: "Users",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "Users",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Users",
                nullable: false,
                defaultValueSql: ActiveProvider.Contains("Npgsql") ? "NOW()" : "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<bool>(
                name: "IsDisabled",
                table: "Users",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DisabledReason",
                table: "Users",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokenVersion",
                table: "Users",
                nullable: false,
                defaultValue: 0);

            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    UPDATE "Users"
                    SET
                        "Login" = COALESCE(NULLIF("Login", ''), "UserName"),
                        "LoginNormalized" = UPPER(COALESCE(NULLIF("Login", ''), "UserName")),
                        "EmailNormalized" = UPPER("Email"),
                        "DisplayName" = COALESCE(NULLIF("DisplayName", ''), 'ZooUser-' || SUBSTRING(REPLACE("Id"::text, '-', ''), 1, 6)),
                        "PublicId" = COALESCE(NULLIF("PublicId", ''), 'JZ-' || UPPER(SUBSTRING(REPLACE("Id"::text, '-', ''), 1, 4)) || '-' || UPPER(SUBSTRING(REPLACE("Id"::text, '-', ''), 5, 6))),
                        "SecurityStamp" = COALESCE(NULLIF("SecurityStamp", ''), md5(random()::text || clock_timestamp()::text)),
                        "UpdatedAt" = COALESCE("UpdatedAt", "CreatedAt");
                    """);
            }
            else
            {
                migrationBuilder.Sql("""
                    UPDATE "Users"
                    SET
                        "Login" = COALESCE(NULLIF("Login", ''), "UserName"),
                        "LoginNormalized" = UPPER(COALESCE(NULLIF("Login", ''), "UserName")),
                        "EmailNormalized" = UPPER("Email"),
                        "DisplayName" = COALESCE(NULLIF("DisplayName", ''), 'ZooUser-' || substr(replace("Id", '-', ''), 1, 6)),
                        "PublicId" = COALESCE(NULLIF("PublicId", ''), 'JZ-' || upper(substr(replace("Id", '-', ''), 1, 4)) || '-' || upper(substr(replace("Id", '-', ''), 5, 6))),
                        "SecurityStamp" = COALESCE(NULLIF("SecurityStamp", ''), lower(hex(randomblob(16)))),
                        "UpdatedAt" = COALESCE("UpdatedAt", "CreatedAt");
                    """);
            }

            migrationBuilder.CreateIndex(
                name: "IX_Users_LoginNormalized",
                table: "Users",
                column: "LoginNormalized",
                unique: true,
                filter: ActiveProvider.Contains("Npgsql") ? "\"LoginNormalized\" IS NOT NULL" : null);

            migrationBuilder.CreateIndex(
                name: "IX_Users_EmailNormalized",
                table: "Users",
                column: "EmailNormalized",
                unique: true,
                filter: ActiveProvider.Contains("Npgsql") ? "\"EmailNormalized\" IS NOT NULL" : null);

            migrationBuilder.CreateIndex(
                name: "IX_Users_PublicId",
                table: "Users",
                column: "PublicId",
                unique: true,
                filter: ActiveProvider.Contains("Npgsql") ? "\"PublicId\" IS NOT NULL" : null);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Users_LoginNormalized", table: "Users");
            migrationBuilder.DropIndex(name: "IX_Users_EmailNormalized", table: "Users");
            migrationBuilder.DropIndex(name: "IX_Users_PublicId", table: "Users");

            migrationBuilder.DropColumn(name: "Login", table: "Users");
            migrationBuilder.DropColumn(name: "LoginNormalized", table: "Users");
            migrationBuilder.DropColumn(name: "EmailNormalized", table: "Users");
            migrationBuilder.DropColumn(name: "EmailConfirmed", table: "Users");
            migrationBuilder.DropColumn(name: "EmailVerifiedAt", table: "Users");
            migrationBuilder.DropColumn(name: "PublicId", table: "Users");
            migrationBuilder.DropColumn(name: "UpdatedAt", table: "Users");
            migrationBuilder.DropColumn(name: "IsDisabled", table: "Users");
            migrationBuilder.DropColumn(name: "DisabledReason", table: "Users");
            migrationBuilder.DropColumn(name: "SecurityStamp", table: "Users");
            migrationBuilder.DropColumn(name: "TokenVersion", table: "Users");
        }
    }
}
