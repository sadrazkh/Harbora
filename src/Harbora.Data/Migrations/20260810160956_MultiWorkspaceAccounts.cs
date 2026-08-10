using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiWorkspaceAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPersonal",
                table: "Workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Workspaces",
                type: "uuid",
                nullable: true);

            // The original provider workspace is the setup owner's personal home. New accounts are
            // provisioned by the application; this preserves that invariant immediately on upgrade,
            // even before the owner signs in again.
            migrationBuilder.Sql("""
                UPDATE "Workspaces" AS w
                SET "OwnerUserId" = owner."UserId", "IsPersonal" = TRUE
                FROM (
                    SELECT wm."UserId", wm."WorkspaceId"
                    FROM "WorkspaceMembers" wm
                    INNER JOIN "Users" u ON u."Id" = wm."UserId"
                    INNER JOIN "Workspaces" existing ON existing."Id" = wm."WorkspaceId"
                    WHERE existing."IsDefault" = TRUE AND u."Role" = 0
                    ORDER BY u."CreatedAt"
                    LIMIT 1
                ) AS owner
                WHERE w."Id" = owner."WorkspaceId";
                """);

            migrationBuilder.CreateTable(
                name: "WorkspaceInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TokenHint = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceInvitations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_OwnerUserId",
                table: "Workspaces",
                column: "OwnerUserId",
                unique: true,
                filter: "\"IsPersonal\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_TokenHash",
                table: "WorkspaceInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_WorkspaceId_Email",
                table: "WorkspaceInvitations",
                columns: new[] { "WorkspaceId", "Email" });

            migrationBuilder.AddForeignKey(
                name: "FK_Workspaces_Users_OwnerUserId",
                table: "Workspaces",
                column: "OwnerUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Workspaces_Users_OwnerUserId",
                table: "Workspaces");

            migrationBuilder.DropTable(
                name: "WorkspaceInvitations");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_OwnerUserId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "IsPersonal",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Workspaces");
        }
    }
}
