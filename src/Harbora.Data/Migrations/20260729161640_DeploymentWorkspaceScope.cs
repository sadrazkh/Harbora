using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentWorkspaceScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "Deployments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill from the owning app. Without this every existing deployment keeps the empty
            // default and the new workspace filter hides it from its own tenant — the whole
            // deployment history would appear to vanish on upgrade.
            migrationBuilder.Sql("""
                UPDATE "Deployments" d
                SET "WorkspaceId" = a."WorkspaceId"
                FROM "Apps" a
                WHERE a."Id" = d."AppId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_WorkspaceId",
                table: "Deployments",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Deployments_WorkspaceId",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Deployments");
        }
    }
}
