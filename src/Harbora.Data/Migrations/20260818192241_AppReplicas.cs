using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppReplicas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtraUpstreamsJson",
                table: "Routes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoadBalancerHealthCheckPath",
                table: "Routes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplicaIndex",
                table: "HostPortAllocations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtraUpstreamsJson",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "LoadBalancerHealthCheckPath",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "ReplicaIndex",
                table: "HostPortAllocations");
        }
    }
}
