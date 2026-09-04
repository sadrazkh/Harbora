using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadReplicas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PrimaryManagedServiceId",
                table: "ManagedServices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReplicationLagStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagedServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LagSeconds = table.Column<double>(type: "double precision", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplicationLagStatuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReplicationLagStatuses_ManagedServices_ManagedServiceId",
                        column: x => x.ManagedServiceId,
                        principalTable: "ManagedServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedServices_PrimaryManagedServiceId",
                table: "ManagedServices",
                column: "PrimaryManagedServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ReplicationLagStatuses_ManagedServiceId",
                table: "ReplicationLagStatuses",
                column: "ManagedServiceId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ManagedServices_ManagedServices_PrimaryManagedServiceId",
                table: "ManagedServices",
                column: "PrimaryManagedServiceId",
                principalTable: "ManagedServices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ManagedServices_ManagedServices_PrimaryManagedServiceId",
                table: "ManagedServices");

            migrationBuilder.DropTable(
                name: "ReplicationLagStatuses");

            migrationBuilder.DropIndex(
                name: "IX_ManagedServices_PrimaryManagedServiceId",
                table: "ManagedServices");

            migrationBuilder.DropColumn(
                name: "PrimaryManagedServiceId",
                table: "ManagedServices");
        }
    }
}
