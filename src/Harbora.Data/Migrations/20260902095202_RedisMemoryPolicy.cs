using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class RedisMemoryPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasUnpublishedChanges",
                table: "ManagedServices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RedisEvictionPolicy",
                table: "ManagedServices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RedisMaxMemoryBytes",
                table: "ManagedServices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasUnpublishedChanges",
                table: "ManagedServices");

            migrationBuilder.DropColumn(
                name: "RedisEvictionPolicy",
                table: "ManagedServices");

            migrationBuilder.DropColumn(
                name: "RedisMaxMemoryBytes",
                table: "ManagedServices");
        }
    }
}
