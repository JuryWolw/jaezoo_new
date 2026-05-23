using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class ProfileMedia : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProfileBannerUrl",
                table: "Users",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileTextTheme",
                table: "Users",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserAvatars",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    Bucket = table.Column<string>(maxLength: 128, nullable: false),
                    ObjectKey = table.Column<string>(maxLength: 512, nullable: false),
                    Url = table.Column<string>(maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(nullable: false),
                    IsCurrent = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    DeletedAt = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAvatars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAvatars_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserAvatars_UserId_DeletedAt_CreatedAt",
                table: "UserAvatars",
                columns: new[] { "UserId", "DeletedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAvatars_UserId_IsCurrent",
                table: "UserAvatars",
                columns: new[] { "UserId", "IsCurrent" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UserAvatars");
            migrationBuilder.DropColumn(name: "ProfileBannerUrl", table: "Users");
            migrationBuilder.DropColumn(name: "ProfileTextTheme", table: "Users");
        }
    }
}
