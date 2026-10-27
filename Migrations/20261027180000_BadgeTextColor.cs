using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Отдельный цвет текста плашки; у существующих плашек — null (вид не меняется).</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261027180000_BadgeTextColor")]
    public partial class BadgeTextColor : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""ALTER TABLE "Badges" ADD COLUMN IF NOT EXISTS "TextColorHex" character varying(9) NULL;""");
                return;
            }

            migrationBuilder.AddColumn<string>(name: "TextColorHex", table: "Badges", maxLength: 9, nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TextColorHex", table: "Badges");
        }
    }
}
