using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppRouteRateLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaulted to the recommended starting point (AppRateLimitPolicy), not to the CLR
            // type's own zero: RateLimitEnabled stays false for every row that already exists — so
            // nothing here is switched on for anybody — but the average/burst an operator sees the
            // first time they open the toggle should read the same suggested numbers a brand-new App
            // gets from its own C# property initializer, not an all-zero row nobody would recognise
            // as a suggestion.
            migrationBuilder.AddColumn<int>(
                name: "RateLimitAverage",
                table: "Routes",
                type: "integer",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<int>(
                name: "RateLimitBurst",
                table: "Routes",
                type: "integer",
                nullable: false,
                defaultValue: 150);

            migrationBuilder.AddColumn<bool>(
                name: "RateLimitEnabled",
                table: "Routes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RateLimitAverage",
                table: "Apps",
                type: "integer",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<int>(
                name: "RateLimitBurst",
                table: "Apps",
                type: "integer",
                nullable: false,
                defaultValue: 150);

            migrationBuilder.AddColumn<bool>(
                name: "RateLimitEnabled",
                table: "Apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RateLimitAverage",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "RateLimitBurst",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "RateLimitEnabled",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "RateLimitAverage",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "RateLimitBurst",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "RateLimitEnabled",
                table: "Apps");
        }
    }
}
