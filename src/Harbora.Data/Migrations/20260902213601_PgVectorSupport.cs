using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class PgVectorSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PgVectorEnabled",
                table: "ManagedServices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasVectorExtension",
                table: "ManagedServiceDatabases",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VectorExtensionCheckedAt",
                table: "ManagedServiceDatabases",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PgVectorEnabled",
                table: "ManagedServices");

            migrationBuilder.DropColumn(
                name: "HasVectorExtension",
                table: "ManagedServiceDatabases");

            migrationBuilder.DropColumn(
                name: "VectorExtensionCheckedAt",
                table: "ManagedServiceDatabases");
        }
    }
}
