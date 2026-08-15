using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueAppSlugPlatformWide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Apps_WorkspaceId_Slug",
                table: "Apps");

            migrationBuilder.CreateIndex(
                name: "IX_Apps_Slug",
                table: "Apps",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Apps_Slug",
                table: "Apps");

            migrationBuilder.CreateIndex(
                name: "IX_Apps_WorkspaceId_Slug",
                table: "Apps",
                columns: new[] { "WorkspaceId", "Slug" },
                unique: true);
        }
    }
}
