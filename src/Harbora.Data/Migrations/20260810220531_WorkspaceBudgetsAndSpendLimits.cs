using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceBudgetsAndSpendLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MonthlyBudgetMinor",
                table: "Workspaces",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MonthlySpendLimitMinor",
                table: "Workspaces",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SpendLimitAtSuspensionMinor",
                table: "Workspaces",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SpendLimitResetsAt",
                table: "Workspaces",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonthlyBudgetMinor",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "MonthlySpendLimitMinor",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "SpendLimitAtSuspensionMinor",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "SpendLimitResetsAt",
                table: "Workspaces");
        }
    }
}
