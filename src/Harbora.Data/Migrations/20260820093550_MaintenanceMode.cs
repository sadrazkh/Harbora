using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MaintenanceRedirected",
                table: "Routes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SavedExtraUpstreamsJson",
                table: "Routes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SavedLoadBalancerHealthCheckPath",
                table: "Routes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SavedTargetPort",
                table: "Routes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SavedTargetService",
                table: "Routes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceMessage",
                table: "Apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceMessageFa",
                table: "Apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MaintenanceMode",
                table: "Apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MaintenanceSince",
                table: "Apps",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaintenanceRedirected",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "SavedExtraUpstreamsJson",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "SavedLoadBalancerHealthCheckPath",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "SavedTargetPort",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "SavedTargetService",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "MaintenanceMessage",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "MaintenanceMessageFa",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "MaintenanceMode",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "MaintenanceSince",
                table: "Apps");
        }
    }
}
