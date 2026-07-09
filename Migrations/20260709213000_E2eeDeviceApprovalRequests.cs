using System;
using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JaeZoo.Server.Migrations
{
    [Microsoft.EntityFrameworkCore.Infrastructure.DbContext(typeof(AppDbContext))]
    [Migration("20260709213000_E2eeDeviceApprovalRequests")]
    public partial class E2eeDeviceApprovalRequests : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "E2eeDeviceApprovalRequests" (
                        "Id" uuid NOT NULL,
                        "UserId" uuid NOT NULL,
                        "DeviceId" character varying(64) NOT NULL,
                        "Fingerprint" character varying(128) NOT NULL,
                        "DeviceName" character varying(128) NULL,
                        "Platform" character varying(64) NULL,
                        "ClientVersion" character varying(32) NULL,
                        "LastIpAddress" character varying(64) NULL,
                        "Status" character varying(32) NOT NULL DEFAULT 'Pending',
                        "ApprovedByDeviceId" character varying(64) NULL,
                        "Reason" character varying(256) NULL,
                        "RequestedAt" timestamptz NOT NULL DEFAULT now(),
                        "ExpiresAt" timestamptz NOT NULL,
                        "ApprovedAt" timestamptz NULL,
                        "RejectedAt" timestamptz NULL,
                        CONSTRAINT "PK_E2eeDeviceApprovalRequests" PRIMARY KEY ("Id")
                    );

                    DO $$
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM pg_constraint WHERE conname = 'FK_E2eeDeviceApprovalRequests_Users_UserId'
                        ) THEN
                            ALTER TABLE "E2eeDeviceApprovalRequests"
                                ADD CONSTRAINT "FK_E2eeDeviceApprovalRequests_Users_UserId"
                                FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE;
                        END IF;
                    END $$;

                    CREATE INDEX IF NOT EXISTS "IX_E2eeDeviceApprovalRequests_User_Status_Expires"
                        ON "E2eeDeviceApprovalRequests" ("UserId", "Status", "ExpiresAt");

                    CREATE INDEX IF NOT EXISTS "IX_E2eeDeviceApprovalRequests_User_Device_Fingerprint_Status"
                        ON "E2eeDeviceApprovalRequests" ("UserId", "DeviceId", "Fingerprint", "Status");
                    """);
                return;
            }

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "E2eeDeviceApprovalRequests" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_E2eeDeviceApprovalRequests" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "DeviceId" TEXT NOT NULL,
                    "Fingerprint" TEXT NOT NULL,
                    "DeviceName" TEXT NULL,
                    "Platform" TEXT NULL,
                    "ClientVersion" TEXT NULL,
                    "LastIpAddress" TEXT NULL,
                    "Status" TEXT NOT NULL DEFAULT 'Pending',
                    "ApprovedByDeviceId" TEXT NULL,
                    "Reason" TEXT NULL,
                    "RequestedAt" TEXT NOT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "ApprovedAt" TEXT NULL,
                    "RejectedAt" TEXT NULL,
                    CONSTRAINT "FK_E2eeDeviceApprovalRequests_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS "IX_E2eeDeviceApprovalRequests_User_Status_Expires"
                    ON "E2eeDeviceApprovalRequests" ("UserId", "Status", "ExpiresAt");

                CREATE INDEX IF NOT EXISTS "IX_E2eeDeviceApprovalRequests_User_Device_Fingerprint_Status"
                    ON "E2eeDeviceApprovalRequests" ("UserId", "DeviceId", "Fingerprint", "Status");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "E2eeDeviceApprovalRequests");
        }
    }
}
