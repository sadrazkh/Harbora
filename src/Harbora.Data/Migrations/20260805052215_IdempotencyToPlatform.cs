using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <summary>
    /// Moves the idempotency table out of the backup module and up to the platform, because the sync
    /// module's API needs it too and the alternative was one module depending on the other.
    ///
    /// <para>
    /// <b>Hand-written as a rename.</b> EF scaffolded a DROP followed by a CREATE — correct in shape,
    /// and a data-loss migration: any environment that had already applied
    /// <c>20260804201737_BackupIdempotency</c> would silently lose its stored keys, and every
    /// in-flight retry would then start its work a second time. A rename preserves the rows and is
    /// reversible, so it is what runs.
    /// </para>
    /// </summary>
    public partial class IdempotencyToPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "BackupIdempotencyRecords",
                newName: "IdempotencyRecords");

            // Indexes and the primary key carry the old table's name. Renamed too, or the next
            // migration scaffolded against this schema will try to "fix" them.
            migrationBuilder.RenameIndex(
                name: "IX_BackupIdempotencyRecords_ExpiresAt",
                table: "IdempotencyRecords",
                newName: "IX_IdempotencyRecords_ExpiresAt");

            migrationBuilder.RenameIndex(
                name: "IX_BackupIdempotencyRecords_WorkspaceId_Endpoint_Key",
                table: "IdempotencyRecords",
                newName: "IX_IdempotencyRecords_WorkspaceId_Endpoint_Key");

            migrationBuilder.Sql(
                @"ALTER TABLE ""IdempotencyRecords"" RENAME CONSTRAINT ""PK_BackupIdempotencyRecords"" TO ""PK_IdempotencyRecords"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE ""IdempotencyRecords"" RENAME CONSTRAINT ""PK_IdempotencyRecords"" TO ""PK_BackupIdempotencyRecords"";");

            migrationBuilder.RenameIndex(
                name: "IX_IdempotencyRecords_WorkspaceId_Endpoint_Key",
                table: "IdempotencyRecords",
                newName: "IX_BackupIdempotencyRecords_WorkspaceId_Endpoint_Key");

            migrationBuilder.RenameIndex(
                name: "IX_IdempotencyRecords_ExpiresAt",
                table: "IdempotencyRecords",
                newName: "IX_BackupIdempotencyRecords_ExpiresAt");

            migrationBuilder.RenameTable(
                name: "IdempotencyRecords",
                newName: "BackupIdempotencyRecords");
        }
    }
}
