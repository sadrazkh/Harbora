using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseStorageAndVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunningImage",
                table: "ManagedServices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StorageBytes",
                table: "ManagedServices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StorageMeasuredAt",
                table: "ManagedServices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunningImage",
                table: "ManagedServices");

            migrationBuilder.DropColumn(
                name: "StorageBytes",
                table: "ManagedServices");

            migrationBuilder.DropColumn(
                name: "StorageMeasuredAt",
                table: "ManagedServices");
        }
    }
}
