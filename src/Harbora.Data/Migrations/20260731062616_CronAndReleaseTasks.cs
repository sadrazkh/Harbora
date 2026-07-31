using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class CronAndReleaseTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CronExpression",
                table: "Apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRunAt",
                table: "Apps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseCommand",
                table: "Apps",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CronRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CronRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CronRuns_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CronRuns_AppId_StartedAt",
                table: "CronRuns",
                columns: new[] { "AppId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CronRuns");

            migrationBuilder.DropColumn(
                name: "CronExpression",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "NextRunAt",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "ReleaseCommand",
                table: "Apps");
        }
    }
}
