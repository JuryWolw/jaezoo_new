using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    public partial class AddGroupVoiceSessions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupVoiceSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    GroupChatId = table.Column<Guid>(nullable: false),
                    RoomName = table.Column<string>(maxLength: 160, nullable: false),
                    StartedByUserId = table.Column<Guid>(nullable: false),
                    StartedAt = table.Column<DateTime>(nullable: false),
                    LastActivityAt = table.Column<DateTime>(nullable: false),
                    EndedAt = table.Column<DateTime>(nullable: true),
                    State = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupVoiceSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupVoiceSessions_GroupChats_GroupChatId",
                        column: x => x.GroupChatId,
                        principalTable: "GroupChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupVoiceParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    SessionId = table.Column<Guid>(nullable: false),
                    GroupChatId = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    JoinedAt = table.Column<DateTime>(nullable: false),
                    LastSeenAt = table.Column<DateTime>(nullable: false),
                    LeftAt = table.Column<DateTime>(nullable: true),
                    IsActive = table.Column<bool>(nullable: false),
                    ClientInfo = table.Column<string>(maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupVoiceParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupVoiceParticipants_GroupChats_GroupChatId",
                        column: x => x.GroupChatId,
                        principalTable: "GroupChats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupVoiceParticipants_GroupVoiceSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "GroupVoiceSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupVoiceSessions_GroupChatId_State_StartedAt",
                table: "GroupVoiceSessions",
                columns: new[] { "GroupChatId", "State", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupVoiceParticipants_GroupChatId_IsActive_LastSeenAt",
                table: "GroupVoiceParticipants",
                columns: new[] { "GroupChatId", "IsActive", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupVoiceParticipants_SessionId_UserId",
                table: "GroupVoiceParticipants",
                columns: new[] { "SessionId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupVoiceParticipants_GroupChatId",
                table: "GroupVoiceParticipants",
                column: "GroupChatId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GroupVoiceParticipants");
            migrationBuilder.DropTable(name: "GroupVoiceSessions");
        }
    }
}
