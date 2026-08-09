using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class BillingRatesNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "OverageMemoryGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "OverageDiskGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "OverageCpuCoreHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "DiskGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "BaseRatePerHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "StoppedRatePerHourMinor",
                table: "InstanceSizes",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "RunningRatePerHourMinor",
                table: "InstanceSizes",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            // Everything above this line was generated. Everything below it could not be: EF has no
            // way to express a data rewrite, and no way to know about a default it never recorded.
            //
            // Two things the generated diff leaves behind, both of which reinstate the ambiguity
            // this migration exists to remove.
            //
            // 1. The zeros. The preceding migration created these columns with
            //    `ADD ... bigint NOT NULL DEFAULT 0`, which writes 0 into every existing plan and
            //    size, and `DROP NOT NULL` does not undo that. Without the UPDATE below, every
            //    install that upgrades onto this branch lands with every plan and every built-in
            //    tier priced at zero — which now means "deliberately free" — and the tick would
            //    have nothing to complain about. This is not tidying up legacy rows; the pair of
            //    migrations manufactures the wrong answer out of nothing on a brand-new install.
            //    A 0 can only be that backfill: the columns did not exist before the migration
            //    immediately preceding this one, and nothing in the panel writes them yet, so a
            //    rate somebody actually typed cannot be sitting here. A nonzero rate on a
            //    developer's database is left exactly as it is.
            //
            // 2. The DEFAULT. `DROP NOT NULL` leaves `DEFAULT 0` on the column. The model records
            //    no default, so EF believes there is none and will never emit a DROP of its own —
            //    a divergence MigrationConsistencyTests cannot see, because it compares the model
            //    to the snapshot and neither of them knows. EF names every mapped column on insert
            //    so its own writes are unaffected, but a raw INSERT that omits the column — a
            //    restore, a repair by hand, a bulk load — would quietly create a free row.
            migrationBuilder.Sql("""
                ALTER TABLE "Plans" ALTER COLUMN "BaseRatePerHourMinor" DROP DEFAULT;
                ALTER TABLE "Plans" ALTER COLUMN "OverageCpuCoreHourMinor" DROP DEFAULT;
                ALTER TABLE "Plans" ALTER COLUMN "OverageMemoryGbHourMinor" DROP DEFAULT;
                ALTER TABLE "Plans" ALTER COLUMN "OverageDiskGbHourMinor" DROP DEFAULT;
                ALTER TABLE "Plans" ALTER COLUMN "DiskGbHourMinor" DROP DEFAULT;
                ALTER TABLE "InstanceSizes" ALTER COLUMN "RunningRatePerHourMinor" DROP DEFAULT;
                ALTER TABLE "InstanceSizes" ALTER COLUMN "StoppedRatePerHourMinor" DROP DEFAULT;

                UPDATE "Plans" SET
                    "BaseRatePerHourMinor"     = NULLIF("BaseRatePerHourMinor", 0),
                    "OverageCpuCoreHourMinor"  = NULLIF("OverageCpuCoreHourMinor", 0),
                    "OverageMemoryGbHourMinor" = NULLIF("OverageMemoryGbHourMinor", 0),
                    "OverageDiskGbHourMinor"   = NULLIF("OverageDiskGbHourMinor", 0),
                    "DiskGbHourMinor"          = NULLIF("DiskGbHourMinor", 0);

                UPDATE "InstanceSizes" SET
                    "RunningRatePerHourMinor" = NULLIF("RunningRatePerHourMinor", 0),
                    "StoppedRatePerHourMinor" = NULLIF("StoppedRatePerHourMinor", 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing hand-written is needed here, which was worth checking rather than assuming.
            // Because each alter below carries a defaultValue, the Npgsql generator emits its own
            // `UPDATE ... SET x = 0 WHERE x IS NULL` ahead of every `SET NOT NULL`, so going back
            // over the nulls Up created does not throw. It also restores the `DEFAULT 0` that Up
            // dropped, which leaves the schema exactly as the preceding migration left it. Going
            // back does lose the distinction — before this migration there was only zero.
            migrationBuilder.AlterColumn<long>(
                name: "OverageMemoryGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "OverageDiskGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "OverageCpuCoreHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "DiskGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "BaseRatePerHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "StoppedRatePerHourMinor",
                table: "InstanceSizes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "RunningRatePerHourMinor",
                table: "InstanceSizes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
