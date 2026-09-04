using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ErrorTrackingProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ErrorTrackingProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    EncryptedDsn = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErrorTrackingProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppErrorTrackingProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    ErrorTrackingProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachOrder = table.Column<int>(type: "integer", nullable: false),
                    HasUnpublishedChanges = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppErrorTrackingProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppErrorTrackingProviders_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppErrorTrackingProviders_ErrorTrackingProviders_ErrorTrack~",
                        column: x => x.ErrorTrackingProviderId,
                        principalTable: "ErrorTrackingProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppErrorTrackingProviders_AppId_ErrorTrackingProviderId",
                table: "AppErrorTrackingProviders",
                columns: new[] { "AppId", "ErrorTrackingProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppErrorTrackingProviders_ErrorTrackingProviderId",
                table: "AppErrorTrackingProviders",
                column: "ErrorTrackingProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppErrorTrackingProviders");

            migrationBuilder.DropTable(
                name: "ErrorTrackingProviders");
        }
    }
}
