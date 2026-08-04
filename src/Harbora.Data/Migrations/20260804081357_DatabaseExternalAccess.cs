using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseExternalAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseAccessAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ManagedServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    ActorEmail = table.Column<string>(type: "text", nullable: true),
                    ClientIp = table.Column<string>(type: "text", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseAccessAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseAccessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagedServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    GatewayHost = table.Column<string>(type: "text", nullable: true),
                    GatewayPort = table.Column<int>(type: "integer", nullable: true),
                    TunnelId = table.Column<string>(type: "text", nullable: true),
                    AllowedIps = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "text", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExtensionCount = table.Column<int>(type: "integer", nullable: false),
                    TlsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseAccessGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseAccessGrants_ManagedServices_ManagedServiceId",
                        column: x => x.ManagedServiceId,
                        principalTable: "ManagedServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseAccessAudits_ManagedServiceId_CreatedAt",
                table: "DatabaseAccessAudits",
                columns: new[] { "ManagedServiceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseAccessGrants_ExpiresAt",
                table: "DatabaseAccessGrants",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseAccessGrants_ManagedServiceId_Status",
                table: "DatabaseAccessGrants",
                columns: new[] { "ManagedServiceId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseAccessAudits");

            migrationBuilder.DropTable(
                name: "DatabaseAccessGrants");
        }
    }
}
