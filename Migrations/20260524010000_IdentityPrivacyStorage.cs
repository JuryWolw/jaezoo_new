using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class IdentityPrivacyStorage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LoginHash",
                table: "Users",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LoginEncrypted",
                table: "Users",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EmailHash",
                table: "Users",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EmailEncrypted",
                table: "Users",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "IdentityPrivacyVersion",
                table: "Users",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Users_LoginHash",
                table: "Users",
                column: "LoginHash",
                unique: true,
                filter: "\"LoginHash\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Users_EmailHash",
                table: "Users",
                column: "EmailHash",
                unique: true,
                filter: "\"EmailHash\" <> ''");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Users_LoginHash", table: "Users");
            migrationBuilder.DropIndex(name: "IX_Users_EmailHash", table: "Users");
            migrationBuilder.DropColumn(name: "LoginHash", table: "Users");
            migrationBuilder.DropColumn(name: "LoginEncrypted", table: "Users");
            migrationBuilder.DropColumn(name: "EmailHash", table: "Users");
            migrationBuilder.DropColumn(name: "EmailEncrypted", table: "Users");
            migrationBuilder.DropColumn(name: "IdentityPrivacyVersion", table: "Users");
        }
    }
}
