using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class PublicPrivateGroups : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "GroupChats",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_GroupChats_IsPublic",
                table: "GroupChats",
                column: "IsPublic");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupChats_IsPublic",
                table: "GroupChats");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "GroupChats");
        }
    }
}
