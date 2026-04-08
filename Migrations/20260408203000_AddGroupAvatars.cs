using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class AddGroupAvatars : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                table: "GroupChats",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroupAvatars",
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
                    table.PrimaryKey("PK_GroupAvatars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupAvatars_GroupChats_GroupChatId",
                        column: x => x.GroupChatId,
                        principalTable: "GroupChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAvatars_GroupChatId",
                table: "GroupAvatars",
                column: "GroupChatId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GroupAvatars");
            migrationBuilder.DropColumn(name: "AvatarUrl", table: "GroupChats");
        }
    }
}
