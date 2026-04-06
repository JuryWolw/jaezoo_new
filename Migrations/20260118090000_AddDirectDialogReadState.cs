using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectDialogReadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastReadAtUser1",
                table: "DirectDialogs",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<Guid>(
                name: "LastReadMessageIdUser1",
                table: "DirectDialogs",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReadAtUser2",
                table: "DirectDialogs",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<Guid>(
                name: "LastReadMessageIdUser2",
                table: "DirectDialogs",
                nullable: false,
                defaultValue: Guid.Empty);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "LastReadAtUser1", table: "DirectDialogs");
            migrationBuilder.DropColumn(name: "LastReadMessageIdUser1", table: "DirectDialogs");
            migrationBuilder.DropColumn(name: "LastReadAtUser2", table: "DirectDialogs");
            migrationBuilder.DropColumn(name: "LastReadMessageIdUser2", table: "DirectDialogs");
        }
    }
}
