using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDialogSentAtIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DirectMessages_DialogId_SentAt",
                table: "DirectMessages");

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_DialogId_SentAt_Id",
                table: "DirectMessages",
                columns: new[] { "DialogId", "SentAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DirectMessages_DialogId_SentAt_Id",
                table: "DirectMessages");

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessages_DialogId_SentAt",
                table: "DirectMessages",
                columns: new[] { "DialogId", "SentAt" });
        }
    }
}
