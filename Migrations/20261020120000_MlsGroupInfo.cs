using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>GroupInfo текущей эпохи для External Commit: новое устройство участника входит в чат само.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261020120000_MlsGroupInfo")]
    public partial class MlsGroupInfo : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "MlsGroupInfos" (
                        "GroupId" uuid NOT NULL PRIMARY KEY,
                        "Epoch" bigint NOT NULL,
                        "PublishedByDeviceId" uuid NOT NULL,
                        "Data" bytea NOT NULL,
                        "UpdatedAt" timestamp with time zone NOT NULL,
                        CONSTRAINT "FK_MlsGroupInfos_MlsGroups_GroupId" FOREIGN KEY ("GroupId") REFERENCES "MlsGroups" ("Id") ON DELETE CASCADE
                    );
                    """);
                return;
            }

            migrationBuilder.CreateTable(
                name: "MlsGroupInfos",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(nullable: false),
                    Epoch = table.Column<long>(nullable: false),
                    PublishedByDeviceId = table.Column<Guid>(nullable: false),
                    Data = table.Column<byte[]>(nullable: false),
                    UpdatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MlsGroupInfos", x => x.GroupId);
                    table.ForeignKey("FK_MlsGroupInfos_MlsGroups_GroupId", x => x.GroupId, "MlsGroups", "Id", onDelete: ReferentialAction.Cascade);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MlsGroupInfos");
        }
    }
}
