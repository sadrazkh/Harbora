using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class PanelModePreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PanelMode",
                table: "Users",
                type: "integer",
                nullable: true);

            // Everyone who already uses Harbora keeps the panel they have. Left null they would
            // fall to the new-account default and meet a reduced interface on upgrade, which reads
            // as "features were removed" — and the people who need Advanced are the least likely to
            // go hunting for a toggle to get it back.
            migrationBuilder.Sql(@"UPDATE ""Users"" SET ""PanelMode"" = 1 WHERE ""PanelMode"" IS NULL;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PanelMode",
                table: "Users");
        }
    }
}
