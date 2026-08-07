using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupInterruptionRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StagingPath",
                table: "BackupSnapshots",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            // Settle the duplicates the old read-then-insert guard could already have let through.
            //
            // The two indexes below are UNIQUE, so CREATE INDEX fails outright if a pair already
            // exists — and a migration that throws is a panel that will not boot, which is a worse
            // failure than the one being fixed here. A duplicate is exactly the corrupt state the
            // index exists to prevent, so one of the pair has to go: the newest active row per
            // target (per destination) survives and every older one is settled Failed with a reason
            // on it. Nothing is deleted, and a row that was going to be settled by the startup
            // reconciler moments later is settled here instead.
            //
            // Idempotent: a second run finds no group with two active rows and changes nothing.
            //
            // Literals rather than names because a migration must go on meaning what it meant when
            // it was written. BackupSnapshotStatus 0/1/2 are Pending/Preparing/Running and 6 is
            // Failed; RestoreJobStatus 0/1 are Pending/Running and 3 is Failed. Those wire values
            // are frozen — see the note on the enums.
            migrationBuilder.Sql("""
                UPDATE "BackupSnapshots" s
                SET "Status" = 6,
                    "FailureReason" = 'Settled during an upgrade: another backup of the same target was already active, which the platform now prevents. This one never completed, so treat it as not taken.',
                    "CompletedAt" = NOW(),
                    "UpdatedAt" = NOW()
                WHERE s."Status" IN (0, 1, 2)
                  AND EXISTS (
                      SELECT 1 FROM "BackupSnapshots" o
                      WHERE o."Status" IN (0, 1, 2)
                        AND o."WorkspaceId" = s."WorkspaceId"
                        AND o."TargetType" = s."TargetType"
                        AND o."TargetRef" = s."TargetRef"
                        AND (o."CreatedAt" > s."CreatedAt"
                             OR (o."CreatedAt" = s."CreatedAt" AND o."Id" > s."Id")));
                """);

            migrationBuilder.Sql("""
                UPDATE "RestoreJobs" r
                SET "Status" = 3,
                    "FailureReason" = 'Settled during an upgrade: another restore into the same destination was already active, which the platform now prevents. Check the destination before restoring again.',
                    "CompletedAt" = NOW(),
                    "UpdatedAt" = NOW()
                WHERE r."Status" IN (0, 1)
                  AND EXISTS (
                      SELECT 1 FROM "RestoreJobs" o
                      WHERE o."Status" IN (0, 1)
                        AND o."Destination" = r."Destination"
                        AND (o."CreatedAt" > r."CreatedAt"
                             OR (o."CreatedAt" = r."CreatedAt" AND o."Id" > r."Id")));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RestoreJobs_ActiveDestination",
                table: "RestoreJobs",
                column: "Destination",
                unique: true,
                filter: "\"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_BackupSnapshots_ActiveTarget",
                table: "BackupSnapshots",
                columns: new[] { "WorkspaceId", "TargetType", "TargetRef" },
                unique: true,
                filter: "\"Status\" IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RestoreJobs_ActiveDestination",
                table: "RestoreJobs");

            migrationBuilder.DropIndex(
                name: "IX_BackupSnapshots_ActiveTarget",
                table: "BackupSnapshots");

            migrationBuilder.DropColumn(
                name: "StagingPath",
                table: "BackupSnapshots");
        }
    }
}
