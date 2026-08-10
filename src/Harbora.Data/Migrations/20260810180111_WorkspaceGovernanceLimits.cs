using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceGovernanceLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ScopedToProjects",
                table: "WorkspaceMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxBackupSchedules",
                table: "Plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxDomains",
                table: "Plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxEnvironments",
                table: "Plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxMembers",
                table: "Plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxProjects",
                table: "Plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxVolumes",
                table: "Plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Preserve the old access decision while moving it to the correct tenancy boundary.
            // One account can now be scoped differently in each workspace; every existing
            // membership starts with the account-wide value it had before this migration.
            migrationBuilder.Sql(
                """
                UPDATE "WorkspaceMembers" AS membership
                SET "ScopedToProjects" = users."ScopedToProjects"
                FROM "Users" AS users
                WHERE users."Id" = membership."UserId"
                  AND users."ScopedToProjects" = TRUE;
                """);

            // Existing installations already have Harbora's seeded Starter/Pro plans, so the
            // seeder's new values would otherwise apply only to fresh databases. Match their
            // original compute signatures to avoid rewriting a custom plan that merely reused a
            // common name. The provider/default plan intentionally stays unlimited.
            migrationBuilder.Sql(
                """
                UPDATE "Plans"
                SET "MaxMembers" = 3, "MaxProjects" = 3, "MaxEnvironments" = 6,
                    "MaxDomains" = 5, "MaxVolumes" = 5, "MaxBackupSchedules" = 2
                WHERE "Name" = 'Starter' AND "MaxApps" = 2 AND "MaxServices" = 1
                  AND "IsDefault" = FALSE;

                UPDATE "Plans"
                SET "MaxMembers" = 15, "MaxProjects" = 20, "MaxEnvironments" = 50,
                    "MaxDomains" = 50, "MaxVolumes" = 50, "MaxBackupSchedules" = 25
                WHERE "Name" = 'Pro' AND "MaxApps" = 10 AND "MaxServices" = 5
                  AND "IsDefault" = FALSE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScopedToProjects",
                table: "WorkspaceMembers");

            migrationBuilder.DropColumn(
                name: "MaxBackupSchedules",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "MaxDomains",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "MaxEnvironments",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "MaxMembers",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "MaxProjects",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "MaxVolumes",
                table: "Plans");
        }
    }
}
