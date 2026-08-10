using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <summary>
    /// Drops the three overage surcharge columns <c>BillingRates</c> created and nothing ever read.
    ///
    /// <para>
    /// A workspace allowed past its plan's caps is charged at the ordinary meter — its instance
    /// size's hourly rate for compute, the plan's gibibyte-hour for volume — so a plan with these
    /// three blank already billed correctly for everything it handed over. Keeping them meant three
    /// columns that look like prices and set none, and the admin form that made the other four
    /// reachable would have made these reachable too: an operator would have saved a burst rate,
    /// been charged nothing extra for ever, and watched every hourly tick report success.
    /// </para>
    ///
    /// <para>
    /// Data loss is nil rather than accepted: these columns were added, made nullable and dropped
    /// inside one unmerged branch, and every row that has ever held them holds null.
    /// </para>
    /// </summary>
    public partial class BillingOverageRatesRemoved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OverageCpuCoreHourMinor",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "OverageDiskGbHourMinor",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "OverageMemoryGbHourMinor",
                table: "Plans");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OverageCpuCoreHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OverageDiskGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OverageMemoryGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: true);
        }
    }
}
