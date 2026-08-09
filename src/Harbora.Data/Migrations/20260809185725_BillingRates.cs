using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class BillingRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowsOverage",
                table: "Plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "BaseRatePerHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DiskGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OverageCpuCoreHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OverageDiskGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OverageMemoryGbHourMinor",
                table: "Plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RunningRatePerHourMinor",
                table: "InstanceSizes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "StoppedRatePerHourMinor",
                table: "InstanceSizes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowsOverage",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "BaseRatePerHourMinor",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "DiskGbHourMinor",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "OverageCpuCoreHourMinor",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "OverageDiskGbHourMinor",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "OverageMemoryGbHourMinor",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "RunningRatePerHourMinor",
                table: "InstanceSizes");

            migrationBuilder.DropColumn(
                name: "StoppedRatePerHourMinor",
                table: "InstanceSizes");
        }
    }
}
