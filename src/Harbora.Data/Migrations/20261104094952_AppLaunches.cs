using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppLaunches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvAliasesJson",
                table: "AppManagedServices",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppLaunches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CloneUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    GitRef = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ResolvedBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    EncryptedGitToken = table.Column<string>(type: "text", nullable: true),
                    ArchivePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    InspectionJson = table.Column<string>(type: "text", nullable: true),
                    ChoicesJson = table.Column<string>(type: "text", nullable: true),
                    AppId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServiceIdsJson = table.Column<string>(type: "text", nullable: true),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    VerifyStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedStep = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FailureMessageFa = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppLaunches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppLaunches_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AppVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChecksJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppVerifications_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppLaunches_AppId",
                table: "AppLaunches",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppLaunches_Status",
                table: "AppLaunches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AppLaunches_WorkspaceId_CreatedAt",
                table: "AppLaunches",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AppVerifications_AppId",
                table: "AppVerifications",
                column: "AppId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppLaunches");

            migrationBuilder.DropTable(
                name: "AppVerifications");

            migrationBuilder.DropColumn(
                name: "EnvAliasesJson",
                table: "AppManagedServices");
        }
    }
}
