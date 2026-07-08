using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlansAndInstanceSizes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSuspended",
                table: "Workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PlanId",
                table: "Workspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CpuOvercommitFactor",
                table: "Servers",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Pool",
                table: "Servers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "ReservedMemoryRatio",
                table: "Servers",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "InstanceSizeKey",
                table: "Apps",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InstanceSizes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    NameFa = table.Column<string>(type: "text", nullable: false),
                    CpuCores = table.Column<double>(type: "double precision", nullable: false),
                    MemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceSizes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    NameFa = table.Column<string>(type: "text", nullable: false),
                    MaxApps = table.Column<int>(type: "integer", nullable: false),
                    MaxServices = table.Column<int>(type: "integer", nullable: false),
                    MaxMemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxCpuCores = table.Column<double>(type: "double precision", nullable: false),
                    MaxDiskBytes = table.Column<long>(type: "bigint", nullable: false),
                    AllowedSizeKeys = table.Column<string>(type: "text", nullable: false),
                    NodePool = table.Column<string>(type: "text", nullable: true),
                    MonthlyPrice = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstanceSizes_Key",
                table: "InstanceSizes",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstanceSizes");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropColumn(
                name: "IsSuspended",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "PlanId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "CpuOvercommitFactor",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "Pool",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "ReservedMemoryRatio",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "InstanceSizeKey",
                table: "Apps");
        }
    }
}
