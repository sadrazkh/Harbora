using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentActiveIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Deployments_ActiveDeployment",
                table: "Deployments",
                column: "AppId",
                unique: true,
                filter: "\"Status\" IN (0, 1, 2, 3, 8, 9)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deployments_ActiveDeployment",
                table: "Deployments");
        }
    }
}
