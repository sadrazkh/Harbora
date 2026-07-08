using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiServerAndPublishedPort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentClientCertPfx",
                table: "Servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AgentUseMtls",
                table: "Servers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PublishedHostPort",
                table: "Apps",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentClientCertPfx",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "AgentUseMtls",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "PublishedHostPort",
                table: "Apps");
        }
    }
}
