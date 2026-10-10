using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Видимость статуса «в звонке» для друзей: 0 — скрыт, 1 — «в звонке», 2 — «в звонке с ник».</summary>
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(AppDbContext))]
    [Migration("20261011100000_CallStatusVisibility")]
    public partial class CallStatusVisibility : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "CallStatusVisibility" integer NOT NULL DEFAULT 1;""");
                return;
            }

            migrationBuilder.AddColumn<int>(name: "CallStatusVisibility", table: "Users", nullable: false, defaultValue: 1);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CallStatusVisibility", table: "Users");
        }
    }
}
