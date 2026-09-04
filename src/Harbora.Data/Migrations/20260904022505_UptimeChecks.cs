using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class UptimeChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OnUptimeCheckFailed",
                table: "Alerts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "UptimeCheckResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    UptimeCheckId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UptimeCheckResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UptimeChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    ExpectedStatus = table.Column<int>(type: "integer", nullable: false),
                    BodyContains = table.Column<string>(type: "text", nullable: true),
                    IntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    NextCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastOutcome = table.Column<int>(type: "integer", nullable: true),
                    LastHttpStatus = table.Column<int>(type: "integer", nullable: true),
                    LastLatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    LastDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UptimeChecks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UptimeCheckResults_AppId_CheckedAt",
                table: "UptimeCheckResults",
                columns: new[] { "AppId", "CheckedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UptimeChecks_AppId",
                table: "UptimeChecks",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UptimeChecks_IsEnabled_NextCheckAt",
                table: "UptimeChecks",
                columns: new[] { "IsEnabled", "NextCheckAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UptimeCheckResults");

            migrationBuilder.DropTable(
                name: "UptimeChecks");

            migrationBuilder.DropColumn(
                name: "OnUptimeCheckFailed",
                table: "Alerts");
        }
    }
}
