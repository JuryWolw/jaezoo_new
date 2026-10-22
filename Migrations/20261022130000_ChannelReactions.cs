using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Реакции на посты открытых каналов.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261022130000_ChannelReactions")]
    public partial class ChannelReactions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "GroupMessageReactions" (
                        "MessageId" uuid NOT NULL,
                        "UserId" uuid NOT NULL,
                        "Kind" character varying(16) NOT NULL,
                        "CreatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "PK_GroupMessageReactions" PRIMARY KEY ("MessageId", "UserId", "Kind")
                    );
                    """);
                return;
            }

            migrationBuilder.CreateTable(
                name: "GroupMessageReactions",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(nullable: false),
                    UserId = table.Column<Guid>(nullable: false),
                    Kind = table.Column<string>(maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_GroupMessageReactions", x => new { x.MessageId, x.UserId, x.Kind }));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GroupMessageReactions");
        }
    }
}
