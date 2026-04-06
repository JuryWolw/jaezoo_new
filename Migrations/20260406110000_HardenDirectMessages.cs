using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class HardenDirectMessages : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "DirectMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SystemKey",
                table: "DirectMessages",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ForwardedFromMessageId",
                table: "DirectMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EditedAt",
                table: "DirectMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "DirectMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedById",
                table: "DirectMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_DialogId_DeletedAt_SentAt_Id",
                table: "DirectMessages",
                columns: new[] { "DialogId", "DeletedAt", "SentAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_ForwardedFromMessageId",
                table: "DirectMessages",
                column: "ForwardedFromMessageId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DirectMessages_DialogId_DeletedAt_SentAt_Id",
                table: "DirectMessages");

            migrationBuilder.DropIndex(
                name: "IX_DirectMessages_ForwardedFromMessageId",
                table: "DirectMessages");

            migrationBuilder.DropColumn(name: "Kind", table: "DirectMessages");
            migrationBuilder.DropColumn(name: "SystemKey", table: "DirectMessages");
            migrationBuilder.DropColumn(name: "ForwardedFromMessageId", table: "DirectMessages");
            migrationBuilder.DropColumn(name: "EditedAt", table: "DirectMessages");
            migrationBuilder.DropColumn(name: "DeletedAt", table: "DirectMessages");
            migrationBuilder.DropColumn(name: "DeletedById", table: "DirectMessages");
        }
    }
}
