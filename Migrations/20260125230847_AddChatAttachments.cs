using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddChatAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UploaderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoredPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsAttached = table.Column<bool>(type: "boolean", nullable: false),
                    AttachedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DirectMessageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectMessageAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectMessageAttachments_ChatFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "ChatFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectMessageAttachments_DirectMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "DirectMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatFiles_UploaderId_CreatedAt",
                table: "ChatFiles",
                columns: new[] { "UploaderId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessageAttachments_FileId",
                table: "DirectMessageAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectMessageAttachments_MessageId_FileId",
                table: "DirectMessageAttachments",
                columns: new[] { "MessageId", "FileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectMessageAttachments");

            migrationBuilder.DropTable(
                name: "ChatFiles");
        }
    }
}
