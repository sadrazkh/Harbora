using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <summary>
    /// Narrows <c>RestoreJobs.Destination</c> from 1024 characters to 512.
    ///
    /// <para>
    /// The previous migration put a btree UNIQUE index on this column. A btree index row cannot
    /// exceed roughly 2704 bytes, and 1024 multi-byte characters can. That refusal would reach
    /// <c>RestoreService.QueueAsync</c> as a <c>DbUpdateException</c>, which that method reports as
    /// "a restore into this destination is already running" — false, and it would send an operator
    /// looking for a restore that does not exist. 512 characters is at most 2048 bytes, so the
    /// value can never be the reason an insert is refused.
    /// </para>
    /// <para>
    /// This is the one narrowing in the phase, so it is guarded rather than assumed. A real
    /// destination is a resolved path under the restore root or a 36-character managed-service id,
    /// and <c>RestoreService</c> now refuses anything longer in words before it reaches the store.
    /// But an install that already holds a longer row is told what is in the way and how to look at
    /// it, instead of being handed PostgreSQL's bare "value too long for type character
    /// varying(512)". Nothing here deletes or truncates anything: the check only reads, and either
    /// passes or names the rows — a shortened destination would be a falsified audit record of a
    /// destructive act, which is not this migration's to write.
    /// </para>
    /// </summary>
    public partial class BoundRestoreDestination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE too_long bigint;
                BEGIN
                    SELECT count(*) INTO too_long
                    FROM "RestoreJobs" WHERE length("Destination") > 512;

                    IF too_long > 0 THEN
                        RAISE EXCEPTION 'Harbora cannot narrow RestoreJobs.Destination to 512 characters: % restore job(s) record a longer destination. Those rows are the audit trail of a destructive operation, so nothing here shortens them for you. Look at them with: SELECT "Id", "Status", length("Destination") FROM "RestoreJobs" WHERE length("Destination") > 512; then remove or correct them and start the panel again.', too_long;
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Destination",
                table: "RestoreJobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Destination",
                table: "RestoreJobs",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);
        }
    }
}
