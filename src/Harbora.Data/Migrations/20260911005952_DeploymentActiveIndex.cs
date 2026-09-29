using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentActiveIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Settle the duplicates the old read-then-insert coalescing could already have let through,
            // BEFORE the unique index — the same step 20260807151317_BackupInterruptionRecovery takes
            // before its own.
            //
            // This migration first shipped without it. The index below is UNIQUE, so CREATE INDEX
            // fails outright when one app already has two in-flight deployments, and a migration that
            // throws is a panel that will not boot. The code this replaced explicitly allowed that
            // state ("the duplicate queues rather than races"), so an install upgrading with a queued
            // deployment behind a building one could not upgrade at all. The live server applied this
            // migration while it happened to hold no such pair; the Postgres lane's upgrade seed does
            // hold one, and failed on exactly this the first time CI ran it after seven weeks off.
            // Adding the step here rather than in a new migration is deliberate: the failure is in
            // this migration, so only a change to it reaches the installs that have not applied it.
            // An install that already has it recorded is unaffected either way.
            //
            // The newest in-flight deployment per app survives — by Number, which is per-app and
            // strictly increasing, then by Id — and every older one is settled Failed with a reason a
            // person can read. Nothing is deleted. A job still pointing at a settled deployment finds
            // it terminal and does nothing (DeploymentPipeline.ExecuteAsync returns early for that).
            // Idempotent: a second run finds no app with two in-flight rows and changes nothing.
            //
            // Literals, not names: a migration must go on meaning what it meant when it was written.
            // DeploymentStatus 0/1/2/3/8/9 are Queued/Building/Pushing/Deploying/HealthChecking/
            // PendingApproval — the same set the index filter below names — and 5 is Failed.
            migrationBuilder.Sql("""
                UPDATE "Deployments" d
                SET "Status" = 5,
                    "ErrorMessage" = 'Settled during an upgrade: another deployment of this app was already in progress, which the platform now prevents. This one never completed; deploy again if it is still needed.',
                    "FinishedAt" = NOW(),
                    "UpdatedAt" = NOW()
                WHERE d."Status" IN (0, 1, 2, 3, 8, 9)
                  AND EXISTS (
                      SELECT 1 FROM "Deployments" o
                      WHERE o."Status" IN (0, 1, 2, 3, 8, 9)
                        AND o."AppId" = d."AppId"
                        AND (o."Number" > d."Number"
                             OR (o."Number" = d."Number" AND o."Id" > d."Id")));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_ActiveDeployment",
                table: "Deployments",
                column: "AppId",
                unique: true,
                filter: "\"Status\" IN (0, 1, 2, 3, 8, 9)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deployments_ActiveDeployment",
                table: "Deployments");
        }
    }
}
