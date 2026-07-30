using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackedHostPorts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostPortAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostPortAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostPortAllocations_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostPortAllocations_ServerId_AppId_DeploymentNumber",
                table: "HostPortAllocations",
                columns: new[] { "ServerId", "AppId", "DeploymentNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_HostPortAllocations_ServerId_Port",
                table: "HostPortAllocations",
                columns: new[] { "ServerId", "Port" },
                unique: true);

            // Apps already serving on a remote node hold a host port that nothing has recorded. Without
            // this backfill the allocator would consider those ports free and hand one to another app —
            // whose container would then be reachable at the first app's address. Ports in use before
            // the upgrade must stay reserved after it.
            migrationBuilder.Sql("""
                INSERT INTO "HostPortAllocations"
                    ("Id", "ServerId", "Port", "AppId", "DeploymentNumber", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), a."ServerId", a."PublishedHostPort", a."Id",
                       COALESCE(d."Number", 0), now(), now()
                FROM "Apps" a
                LEFT JOIN "Deployments" d ON d."Id" = a."ActiveDeploymentId"
                WHERE a."PublishedHostPort" IS NOT NULL
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostPortAllocations");
        }
    }
}
