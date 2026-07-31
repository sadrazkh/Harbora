using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreviewEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviewBranch",
                table: "Apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreviewLastPushedAt",
                table: "Apps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviewOfAppId",
                table: "Apps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PreviewsEnabled",
                table: "Apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviewBranch",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PreviewLastPushedAt",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PreviewOfAppId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "PreviewsEnabled",
                table: "Apps");
        }
    }
}
