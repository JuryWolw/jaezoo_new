using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddUserShowOnline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowOnline",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowOnline",
                table: "Users");
        }
    }
}
