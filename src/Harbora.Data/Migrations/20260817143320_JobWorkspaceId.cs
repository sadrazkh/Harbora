using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobWorkspaceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkspaceId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_WorkspaceId_CreatedAt",
                table: "Jobs",
                columns: new[] { "WorkspaceId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_WorkspaceId_CreatedAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Jobs");
        }
    }
}
