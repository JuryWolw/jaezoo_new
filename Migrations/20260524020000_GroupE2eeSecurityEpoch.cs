using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class GroupE2eeSecurityEpoch : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SecurityEpoch",
                table: "GroupChats",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "SecurityEpochChangedAt",
                table: "GroupChats",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<int>(
                name: "GroupSecurityEpoch",
                table: "GroupMessages",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_GroupChatId_GroupSecurityEpoch_SentAt",
                table: "GroupMessages",
                columns: new[] { "GroupChatId", "GroupSecurityEpoch", "SentAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupMessages_GroupChatId_GroupSecurityEpoch_SentAt",
                table: "GroupMessages");

            migrationBuilder.DropColumn(
                name: "SecurityEpoch",
                table: "GroupChats");

            migrationBuilder.DropColumn(
                name: "SecurityEpochChangedAt",
                table: "GroupChats");

            migrationBuilder.DropColumn(
                name: "GroupSecurityEpoch",
                table: "GroupMessages");
        }
    }
}
