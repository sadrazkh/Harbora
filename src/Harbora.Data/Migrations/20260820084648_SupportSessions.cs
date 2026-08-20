using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class SupportSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupportAdminUserId",
                table: "AuditLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupportSessionId",
                table: "AuditLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SupportSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetWorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedBy = table.Column<int>(type: "integer", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_SupportSessionId",
                table: "AuditLogs",
                column: "SupportSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SupportSessions_TargetUserId_EndedAt",
                table: "SupportSessions",
                columns: new[] { "TargetUserId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportSessions_TargetWorkspaceId_StartedAt",
                table: "SupportSessions",
                columns: new[] { "TargetWorkspaceId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupportSessions");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_SupportSessionId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "SupportAdminUserId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "SupportSessionId",
                table: "AuditLogs");
        }
    }
}
