using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EngineDeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ConnectionKind = table.Column<int>(type: "integer", nullable: false),
                    Address = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ClientVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsUntrusted = table.Column<bool>(type: "boolean", nullable: false),
                    IsLocalNode = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncSpaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LocalPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    EngineFolderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    VersioningMode = table.Column<int>(type: "integer", nullable: false),
                    VersioningParameter = table.Column<int>(type: "integer", nullable: false),
                    IgnorePatterns = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IsPaused = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PendingFiles = table.Column<long>(type: "bigint", nullable: false),
                    PendingBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalFiles = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    ConflictCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSpaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OriginalRelativePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OriginatingDevice = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Resolution = table.Column<int>(type: "integer", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConflicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncConflicts_SyncSpaces_SyncSpaceId",
                        column: x => x.SyncSpaceId,
                        principalTable: "SyncSpaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncSpaceMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    AcceptedByPeer = table.Column<bool>(type: "boolean", nullable: false),
                    EncryptedFolderPassword = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSpaceMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncSpaceMembers_SyncDevices_SyncDeviceId",
                        column: x => x.SyncDeviceId,
                        principalTable: "SyncDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncSpaceMembers_SyncSpaces_SyncSpaceId",
                        column: x => x.SyncSpaceId,
                        principalTable: "SyncSpaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_SyncSpaceId_RelativePath",
                table: "SyncConflicts",
                columns: new[] { "SyncSpaceId", "RelativePath" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_SyncSpaceId_Resolution",
                table: "SyncConflicts",
                columns: new[] { "SyncSpaceId", "Resolution" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncDevices_WorkspaceId_EngineDeviceId",
                table: "SyncDevices",
                columns: new[] { "WorkspaceId", "EngineDeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncSpaceMembers_SyncDeviceId",
                table: "SyncSpaceMembers",
                column: "SyncDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncSpaceMembers_SyncSpaceId_SyncDeviceId",
                table: "SyncSpaceMembers",
                columns: new[] { "SyncSpaceId", "SyncDeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncSpaces_EngineFolderId",
                table: "SyncSpaces",
                column: "EngineFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncSpaces_WorkspaceId_Name",
                table: "SyncSpaces",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncConflicts");

            migrationBuilder.DropTable(
                name: "SyncSpaceMembers");

            migrationBuilder.DropTable(
                name: "SyncDevices");

            migrationBuilder.DropTable(
                name: "SyncSpaces");
        }
    }
}
