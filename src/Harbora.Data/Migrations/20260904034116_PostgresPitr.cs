using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class PostgresPitr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PitrEnabled",
                table: "ManagedServices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WalArchivingStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagedServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    SegmentsArchived = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalArchivingStatuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalArchivingStatuses_ManagedServices_ManagedServiceId",
                        column: x => x.ManagedServiceId,
                        principalTable: "ManagedServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WalSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagedServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArtifactPath = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalSegments_BackupDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "BackupDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalSegments_ManagedServices_ManagedServiceId",
                        column: x => x.ManagedServiceId,
                        principalTable: "ManagedServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalArchivingStatuses_ManagedServiceId",
                table: "WalArchivingStatuses",
                column: "ManagedServiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalSegments_DestinationId",
                table: "WalSegments",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_WalSegments_ManagedServiceId_ArchivedAt",
                table: "WalSegments",
                columns: new[] { "ManagedServiceId", "ArchivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalArchivingStatuses");

            migrationBuilder.DropTable(
                name: "WalSegments");

            migrationBuilder.DropColumn(
                name: "PitrEnabled",
                table: "ManagedServices");
        }
    }
}
