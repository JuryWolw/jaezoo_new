using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class FileStorageRouting : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SafeFileName",
                table: "ChatFiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DetectedContentType",
                table: "ChatFiles",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "application/octet-stream");

            migrationBuilder.AddColumn<string>(
                name: "Bucket",
                table: "ChatFiles",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "jaezoo-files");

            migrationBuilder.AddColumn<string>(
                name: "ObjectKey",
                table: "ChatFiles",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Sha256",
                table: "ChatFiles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "ChatFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ScanStatus",
                table: "ChatFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsPotentiallyDangerous",
                table: "ChatFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RiskNote",
                table: "ChatFiles",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "ChatFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BlockedAt",
                table: "ChatFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "ChatFiles"
                SET "ObjectKey" = "StoredPath"
                WHERE "ObjectKey" = '' AND "StoredPath" <> '';

                UPDATE "ChatFiles"
                SET "SafeFileName" = "OriginalFileName"
                WHERE "SafeFileName" = '';

                UPDATE "ChatFiles"
                SET "DetectedContentType" = "ContentType"
                WHERE "DetectedContentType" = 'application/octet-stream' AND "ContentType" <> '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ChatFiles_Bucket_ObjectKey",
                table: "ChatFiles",
                columns: new[] { "Bucket", "ObjectKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatFiles_Sha256",
                table: "ChatFiles",
                column: "Sha256");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_ChatFiles_Bucket_ObjectKey", table: "ChatFiles");
            migrationBuilder.DropIndex(name: "IX_ChatFiles_Sha256", table: "ChatFiles");

            migrationBuilder.DropColumn(name: "SafeFileName", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "DetectedContentType", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "Bucket", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "ObjectKey", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "Sha256", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "Kind", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "ScanStatus", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "IsPotentiallyDangerous", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "RiskNote", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "DeletedAt", table: "ChatFiles");
            migrationBuilder.DropColumn(name: "BlockedAt", table: "ChatFiles");
        }
    }
}
