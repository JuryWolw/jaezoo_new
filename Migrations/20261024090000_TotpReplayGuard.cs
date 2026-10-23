using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    /// <summary>Шаг последнего принятого TOTP-кода — защита от повторного ввода того же кода.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261024090000_TotpReplayGuard")]
    public partial class TotpReplayGuard : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""ALTER TABLE "Users" ADD COLUMN IF NOT EXISTS "TwoFactorLastUsedStep" bigint NULL;""");
                return;
            }

            migrationBuilder.AddColumn<long>(name: "TwoFactorLastUsedStep", table: "Users", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TwoFactorLastUsedStep", table: "Users");
        }
    }
}
