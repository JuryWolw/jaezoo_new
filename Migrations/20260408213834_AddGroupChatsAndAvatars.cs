using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupChatsAndAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupChats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberLimit = table.Column<int>(type: "integer", nullable: false),
                    AvatarUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupChats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupAvatars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupChatId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "GroupChatMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupChatId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReadMessageId = table.Column<Guid>(type: "uuid", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupChatId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SystemKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ForwardedFromMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uuid", nullable: true)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "IX_GroupAvatars_GroupChatId",
                table: "GroupAvatars",
                column: "GroupChatId");

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
                name: "IX_GroupChats_OwnerId_CreatedAt",
                table: "GroupChats",
                columns: new[] { "OwnerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessageAttachments_FileId",
                table: "GroupMessageAttachments",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessageAttachments_MessageId_FileId",
                table: "GroupMessageAttachments",
                columns: new[] { "MessageId", "FileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_ForwardedFromMessageId",
                table: "GroupMessages",
                column: "ForwardedFromMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_GroupChatId_DeletedAt_SentAt_Id",
                table: "GroupMessages",
                columns: new[] { "GroupChatId", "DeletedAt", "SentAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_GroupChatId_SentAt_Id",
                table: "GroupMessages",
                columns: new[] { "GroupChatId", "SentAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupAvatars");

            migrationBuilder.DropTable(
                name: "GroupChatMembers");

            migrationBuilder.DropTable(
                name: "GroupMessageAttachments");

            migrationBuilder.DropTable(
                name: "GroupMessages");

            migrationBuilder.DropTable(
                name: "GroupChats");
        }
    }
}
