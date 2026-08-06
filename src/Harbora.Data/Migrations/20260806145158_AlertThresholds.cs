using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AppId",
                table: "Alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Metric",
                table: "Alerts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SustainedMinutes",
                table: "Alerts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ThresholdFiredAt",
                table: "Alerts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ThresholdPercent",
                table: "Alerts",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppId",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "Metric",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "SustainedMinutes",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "ThresholdFiredAt",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "ThresholdPercent",
                table: "Alerts");
        }
    }
}
