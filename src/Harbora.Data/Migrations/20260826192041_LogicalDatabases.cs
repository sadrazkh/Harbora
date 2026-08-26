using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class LogicalDatabases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppManagedServices_AppId_ManagedServiceId",
                table: "AppManagedServices");

            migrationBuilder.AddColumn<Guid>(
                name: "ManagedServiceDatabaseId",
                table: "AppManagedServices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ManagedServiceDatabases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagedServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedServiceDatabases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManagedServiceDatabases_ManagedServices_ManagedServiceId",
                        column: x => x.ManagedServiceId,
                        principalTable: "ManagedServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppManagedServices_AppId_ManagedServiceDatabaseId",
                table: "AppManagedServices",
                columns: new[] { "AppId", "ManagedServiceDatabaseId" },
                unique: true,
                filter: "\"ManagedServiceDatabaseId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppManagedServices_AppId_ManagedServiceId",
                table: "AppManagedServices",
                columns: new[] { "AppId", "ManagedServiceId" },
                unique: true,
                filter: "\"ManagedServiceDatabaseId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppManagedServices_ManagedServiceDatabaseId",
                table: "AppManagedServices",
                column: "ManagedServiceDatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ManagedServiceDatabases_ManagedServiceId_Name",
                table: "ManagedServiceDatabases",
                columns: new[] { "ManagedServiceId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AppManagedServices_ManagedServiceDatabases_ManagedServiceDa~",
                table: "AppManagedServices",
                column: "ManagedServiceDatabaseId",
                principalTable: "ManagedServiceDatabases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Every instance that has a database at all gets its existing admin database materialised
            // as its own first logical database — ManagedServiceDatabase.DefaultFor's own rule,
            // restated in SQL for rows that already existed before this shipped. Skipped for an
            // instance whose engine has no database name (Redis/RabbitMQ/NATS, DatabaseName ''), and
            // idempotent: a second run of this migration finds every instance already has one and
            // inserts nothing.
            migrationBuilder.Sql("""
                INSERT INTO "ManagedServiceDatabases"
                    ("Id", "WorkspaceId", "ManagedServiceId", "Name", "Username", "EncryptedPassword",
                     "IsDefault", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), m."WorkspaceId", m."Id", m."DatabaseName", m."Username",
                       m."EncryptedPassword", true, now(), now()
                FROM "ManagedServices" m
                WHERE m."DatabaseName" <> ''
                  AND NOT EXISTS (
                      SELECT 1 FROM "ManagedServiceDatabases" d WHERE d."ManagedServiceId" = m."Id");
                """);

            // Every attachment that existed before this migration is re-pointed at its instance's new
            // default logical database — a copy of the very same Name/Username/EncryptedPassword the
            // attachment already resolved to, not a new credential, so an app already attached reads
            // byte-identical values after this runs. See
            // AttachedServiceConnectionResolverMigrationParityTests for the fact this exists to make
            // true, and AttachedDatabaseCreds for the resolution logic this backfill feeds.
            migrationBuilder.Sql("""
                UPDATE "AppManagedServices" a
                SET "ManagedServiceDatabaseId" = d."Id"
                FROM "ManagedServiceDatabases" d
                WHERE d."ManagedServiceId" = a."ManagedServiceId" AND d."IsDefault" = true
                  AND a."ManagedServiceDatabaseId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppManagedServices_ManagedServiceDatabases_ManagedServiceDa~",
                table: "AppManagedServices");

            migrationBuilder.DropTable(
                name: "ManagedServiceDatabases");

            migrationBuilder.DropIndex(
                name: "IX_AppManagedServices_AppId_ManagedServiceDatabaseId",
                table: "AppManagedServices");

            migrationBuilder.DropIndex(
                name: "IX_AppManagedServices_AppId_ManagedServiceId",
                table: "AppManagedServices");

            migrationBuilder.DropIndex(
                name: "IX_AppManagedServices_ManagedServiceDatabaseId",
                table: "AppManagedServices");

            migrationBuilder.DropColumn(
                name: "ManagedServiceDatabaseId",
                table: "AppManagedServices");

            migrationBuilder.CreateIndex(
                name: "IX_AppManagedServices_AppId_ManagedServiceId",
                table: "AppManagedServices",
                columns: new[] { "AppId", "ManagedServiceId" },
                unique: true);
        }
    }
}
