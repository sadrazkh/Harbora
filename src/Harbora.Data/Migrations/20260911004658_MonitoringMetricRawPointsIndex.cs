using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class MonitoringMetricRawPointsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MonitoringMetrics_ServerId_Name_Timestamp",
                table: "MonitoringMetrics");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringMetrics_ServerId_Name_ResourceRef_Timestamp",
                table: "MonitoringMetrics",
                columns: new[] { "ServerId", "Name", "ResourceRef", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MonitoringMetrics_ServerId_Name_ResourceRef_Timestamp",
                table: "MonitoringMetrics");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringMetrics_ServerId_Name_Timestamp",
                table: "MonitoringMetrics",
                columns: new[] { "ServerId", "Name", "Timestamp" });
        }
    }
}
