using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentToolDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "AgentMessages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ToolSucceeded",
                table: "AgentMessages",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "AgentMessages");

            migrationBuilder.DropColumn(
                name: "ToolSucceeded",
                table: "AgentMessages");
        }
    }
}
