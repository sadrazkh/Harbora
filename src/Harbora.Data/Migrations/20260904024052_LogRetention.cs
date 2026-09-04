using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class LogRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LogRetentionBudgetCapped",
                table: "Apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LogRetentionDays",
                table: "Apps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LogRetentionEnabledAt",
                table: "Apps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppLogLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContainerId = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppLogLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppLogLines_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppLogLines_AppId_ContainerId_Timestamp",
                table: "AppLogLines",
                columns: new[] { "AppId", "ContainerId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AppLogLines_AppId_Timestamp",
                table: "AppLogLines",
                columns: new[] { "AppId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AppLogLines_Timestamp",
                table: "AppLogLines",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppLogLines");

            migrationBuilder.DropColumn(
                name: "LogRetentionBudgetCapped",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "LogRetentionDays",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "LogRetentionEnabledAt",
                table: "Apps");
        }
    }
}
