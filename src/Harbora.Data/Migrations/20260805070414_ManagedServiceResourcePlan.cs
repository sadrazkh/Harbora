using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedServiceResourcePlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CpuLimit",
                table: "ManagedServices",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "InstanceSizeKey",
                table: "ManagedServices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemoryLimitBytes",
                table: "ManagedServices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CpuLimit",
                table: "ManagedServices");

            migrationBuilder.DropColumn(
                name: "InstanceSizeKey",
                table: "ManagedServices");

            migrationBuilder.DropColumn(
                name: "MemoryLimitBytes",
                table: "ManagedServices");
        }
    }
}
