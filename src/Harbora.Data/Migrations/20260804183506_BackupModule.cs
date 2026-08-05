using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupRepositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Engine = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Endpoint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Bucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BasePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CredentialReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    EncryptedCredentials = table.Column<string>(type: "text", nullable: true),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: true),
                    EngineRepositoryId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastHealthCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulHealthCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    StorageUsageBytes = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotCount = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRepositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    TargetRef = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Schedule = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Retention_KeepLatest = table.Column<int>(type: "integer", nullable: false),
                    Retention_KeepHourly = table.Column<int>(type: "integer", nullable: false),
                    Retention_KeepDaily = table.Column<int>(type: "integer", nullable: false),
                    Retention_KeepWeekly = table.Column<int>(type: "integer", nullable: false),
                    Retention_KeepMonthly = table.Column<int>(type: "integer", nullable: false),
                    Retention_KeepYearly = table.Column<int>(type: "integer", nullable: false),
                    Retention_MaximumAgeDays = table.Column<int>(type: "integer", nullable: true),
                    Retention_MaximumRepositorySizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    CompressionAlgorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    EncryptionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IncludePatterns = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    ExcludePatterns = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    PreBackupHook = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PostBackupHook = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AlertAfterHoursWithoutSuccess = table.Column<int>(type: "integer", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupPolicies_BackupRepositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "BackupRepositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    TargetRef = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    EngineSnapshotId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OriginalSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoredSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DeduplicatedSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FilesCount = table.Column<long>(type: "bigint", nullable: false),
                    DatabaseDumpIncluded = table.Column<bool>(type: "boolean", nullable: false),
                    VerificationStatus = table.Column<int>(type: "integer", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VerificationNote = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Warnings = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    TriggeredBy = table.Column<int>(type: "integer", nullable: false),
                    TriggeredByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupSnapshots_BackupPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "BackupPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BackupSnapshots_BackupRepositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "BackupRepositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RestoreJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    RestoreType = table.Column<int>(type: "integer", nullable: false),
                    Destination = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OverwritesLiveTarget = table.Column<bool>(type: "boolean", nullable: false),
                    ConflictStrategy = table.Column<int>(type: "integer", nullable: false),
                    Entries = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Progress = table.Column<int>(type: "integer", nullable: false),
                    RestoredFilesCount = table.Column<long>(type: "bigint", nullable: false),
                    RestoredBytes = table.Column<long>(type: "bigint", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SafetySnapshotRef = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestoreJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestoreJobs_BackupSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "BackupSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupPolicies_Enabled_NextRunAt",
                table: "BackupPolicies",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupPolicies_RepositoryId",
                table: "BackupPolicies",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupPolicies_WorkspaceId_Enabled",
                table: "BackupPolicies",
                columns: new[] { "WorkspaceId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRepositories_WorkspaceId_Name",
                table: "BackupRepositories",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackupRepositories_WorkspaceId_Status",
                table: "BackupRepositories",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupSnapshots_PolicyId",
                table: "BackupSnapshots",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupSnapshots_RepositoryId_Status",
                table: "BackupSnapshots",
                columns: new[] { "RepositoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupSnapshots_WorkspaceId_CreatedAt",
                table: "BackupSnapshots",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupSnapshots_WorkspaceId_TargetType_TargetRef_CreatedAt",
                table: "BackupSnapshots",
                columns: new[] { "WorkspaceId", "TargetType", "TargetRef", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RestoreJobs_SnapshotId",
                table: "RestoreJobs",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_RestoreJobs_Status",
                table: "RestoreJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RestoreJobs_WorkspaceId_CreatedAt",
                table: "RestoreJobs",
                columns: new[] { "WorkspaceId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RestoreJobs");

            migrationBuilder.DropTable(
                name: "BackupSnapshots");

            migrationBuilder.DropTable(
                name: "BackupPolicies");

            migrationBuilder.DropTable(
                name: "BackupRepositories");
        }
    }
}
