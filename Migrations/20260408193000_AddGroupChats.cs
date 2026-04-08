using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class AddGroupChats : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupChats",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Title = table.Column<string>(maxLength: 120, nullable: false),
                    OwnerId = table.Column<Guid>(nullable: false),
                    MemberLimit = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupChats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupChatMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    GroupChatId = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    JoinedAt = table.Column<DateTime>(nullable: false),
                    LastReadAt = table.Column<DateTime>(nullable: false),
                    LastReadMessageId = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupChatMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupChatMembers_GroupChats_GroupChatId",
                        column: x => x.GroupChatId,
                        principalTable: "GroupChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    GroupChatId = table.Column<Guid>(nullable: false),
                    SenderId = table.Column<Guid>(nullable: false),
                    Text = table.Column<string>(maxLength: 4000, nullable: false),
                    SentAt = table.Column<DateTime>(nullable: false),
                    Kind = table.Column<int>(nullable: false),
                    SystemKey = table.Column<string>(maxLength: 64, nullable: true),
                    ForwardedFromMessageId = table.Column<Guid>(nullable: true),
                    EditedAt = table.Column<DateTime>(nullable: true),
                    DeletedAt = table.Column<DateTime>(nullable: true),
                    DeletedById = table.Column<Guid>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupMessages_GroupChats_GroupChatId",
                        column: x => x.GroupChatId,
                        principalTable: "GroupChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupMessageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    MessageId = table.Column<Guid>(nullable: false),
                    FileId = table.Column<Guid>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMessageAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupMessageAttachments_ChatFiles_FileId",
                        column: x => x.FileId,
                        principalTable: "ChatFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMessageAttachments_GroupMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "GroupMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupChats_OwnerId_CreatedAt",
                table: "GroupChats",
                columns: new[] { "OwnerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupChatMembers_GroupChatId_UserId",
                table: "GroupChatMembers",
                columns: new[] { "GroupChatId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupChatMembers_UserId_JoinedAt",
                table: "GroupChatMembers",
                columns: new[] { "UserId", "JoinedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_GroupChatId_SentAt_Id",
                table: "GroupMessages",
                columns: new[] { "GroupChatId", "SentAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_GroupChatId_DeletedAt_SentAt_Id",
                table: "GroupMessages",
                columns: new[] { "GroupChatId", "DeletedAt", "SentAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_ForwardedFromMessageId",
                table: "GroupMessages",
                column: "ForwardedFromMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessageAttachments_MessageId_FileId",
                table: "GroupMessageAttachments",
                columns: new[] { "MessageId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessageAttachments_FileId",
                table: "GroupMessageAttachments",
                column: "FileId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GroupChatMembers");
            migrationBuilder.DropTable(name: "GroupMessageAttachments");
            migrationBuilder.DropTable(name: "GroupMessages");
            migrationBuilder.DropTable(name: "GroupChats");
        }
    }
}
