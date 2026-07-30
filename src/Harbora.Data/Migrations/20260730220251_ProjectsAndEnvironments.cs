using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProjectsAndEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "ManagedServices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EnvironmentId",
                table: "Apps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Apps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Environments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsProtected = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Environments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Environments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedServices_EnvironmentId",
                table: "ManagedServices",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Apps_EnvironmentId",
                table: "Apps",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Environments_ProjectId_Slug",
                table: "Environments",
                columns: new[] { "ProjectId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_WorkspaceId_Slug",
                table: "Projects",
                columns: new[] { "WorkspaceId", "Slug" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Apps_Environments_EnvironmentId",
                table: "Apps",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ManagedServices_Environments_EnvironmentId",
                table: "ManagedServices",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Every workspace gets one project and one "production" environment, and everything that
            // exists today is pointed at it. Without this, an upgrade would leave every customer's
            // apps and databases belonging to no project — visible nowhere in the new UI while still
            // running perfectly, which is the worst of both.
            //
            // Deterministic and idempotent: the project slug is fixed, and each insert skips
            // workspaces that already have one, so re-running changes nothing.
            migrationBuilder.Sql("""
                INSERT INTO "Projects" ("Id", "WorkspaceId", "Name", "Slug", "Description", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), w."Id", w."Name", 'default', NULL, now(), now()
                FROM "Workspaces" w
                WHERE NOT EXISTS (
                    SELECT 1 FROM "Projects" p WHERE p."WorkspaceId" = w."Id" AND p."Slug" = 'default');
                """);

            migrationBuilder.Sql("""
                INSERT INTO "Environments"
                    ("Id", "WorkspaceId", "ProjectId", "Name", "Slug", "IsDefault", "IsProtected", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), p."WorkspaceId", p."Id", 'Production', 'production', true, false, now(), now()
                FROM "Projects" p
                WHERE NOT EXISTS (
                    SELECT 1 FROM "Environments" e WHERE e."ProjectId" = p."Id" AND e."Slug" = 'production');
                """);

            // Existing apps and databases join that environment. Matched through the workspace, so an
            // app can never be attached to another tenant's project.
            migrationBuilder.Sql("""
                UPDATE "Apps" a
                SET "EnvironmentId" = e."Id"
                FROM "Environments" e
                WHERE e."WorkspaceId" = a."WorkspaceId" AND e."Slug" = 'production'
                  AND a."EnvironmentId" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "ManagedServices" m
                SET "EnvironmentId" = e."Id"
                FROM "Environments" e
                WHERE e."WorkspaceId" = m."WorkspaceId" AND e."Slug" = 'production'
                  AND m."EnvironmentId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Apps_Environments_EnvironmentId",
                table: "Apps");

            migrationBuilder.DropForeignKey(
                name: "FK_ManagedServices_Environments_EnvironmentId",
                table: "ManagedServices");

            migrationBuilder.DropTable(
                name: "Environments");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_ManagedServices_EnvironmentId",
                table: "ManagedServices");

            migrationBuilder.DropIndex(
                name: "IX_Apps_EnvironmentId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "ManagedServices");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Apps");
        }
    }
}
