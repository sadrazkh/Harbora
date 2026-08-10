using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class BillingRuntimeAndVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillingHour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    WorkspacesCharged = table.Column<int>(type: "integer", nullable: false),
                    LinesWritten = table.Column<int>(type: "integer", nullable: false),
                    WorkspacesSuspended = table.Column<int>(type: "integer", nullable: false),
                    FailureSummary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillingVouchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CodeHint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IsDisabled = table.Column<bool>(type: "boolean", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RedeemedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RedeemedWorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingVouchers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Kind_TargetId",
                table: "Jobs",
                columns: new[] { "Kind", "TargetId" },
                unique: true,
                filter: "\"Kind\" = 9 AND \"Status\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_BillingRuns_BillingHour",
                table: "BillingRuns",
                column: "BillingHour",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingVouchers_CodeHash",
                table: "BillingVouchers",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingVouchers_IsDisabled_RedeemedAt_ExpiresAt",
                table: "BillingVouchers",
                columns: new[] { "IsDisabled", "RedeemedAt", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingRuns");

            migrationBuilder.DropTable(
                name: "BillingVouchers");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_Kind_TargetId",
                table: "Jobs");
        }
    }
}
