using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class FunctionsAndFeatureGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FunctionInvokeSecret",
                table: "Apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FunctionRuntime",
                table: "Apps",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FeatureGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SetByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FunctionDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    Route = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CronExpression = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    EventKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Code = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HasUnpublishedChanges = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunctionDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FunctionDefinitions_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FunctionInvocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FunctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    EnvelopeJson = table.Column<string>(type: "text", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunctionInvocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FunctionInvocations_FunctionDefinitions_FunctionId",
                        column: x => x.FunctionId,
                        principalTable: "FunctionDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureGrants_Scope_TargetId_FeatureKey",
                table: "FeatureGrants",
                columns: new[] { "Scope", "TargetId", "FeatureKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FunctionDefinitions_AppId_Slug",
                table: "FunctionDefinitions",
                columns: new[] { "AppId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FunctionDefinitions_NextRunAt",
                table: "FunctionDefinitions",
                column: "NextRunAt");

            migrationBuilder.CreateIndex(
                name: "IX_FunctionInvocations_FunctionId_StartedAt",
                table: "FunctionInvocations",
                columns: new[] { "FunctionId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeatureGrants");

            migrationBuilder.DropTable(
                name: "FunctionInvocations");

            migrationBuilder.DropTable(
                name: "FunctionDefinitions");

            migrationBuilder.DropColumn(
                name: "FunctionInvokeSecret",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "FunctionRuntime",
                table: "Apps");
        }
    }
}
