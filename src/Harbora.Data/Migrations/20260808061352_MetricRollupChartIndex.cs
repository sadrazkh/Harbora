using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class MetricRollupChartIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MetricRollups_ServerId_Name_ResourceRef_Period_PeriodStart",
                table: "MetricRollups",
                columns: new[] { "ServerId", "Name", "ResourceRef", "Period", "PeriodStart" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetricRollups_ServerId_Name_ResourceRef_Period_PeriodStart",
                table: "MetricRollups");
        }
    }
}
