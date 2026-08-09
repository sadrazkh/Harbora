using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class Billing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingLedger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillingHour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    ResourceType = table.Column<int>(type: "integer", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResourceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RunState = table.Column<int>(type: "integer", nullable: false),
                    RatePerHourMinor = table.Column<long>(type: "bigint", nullable: false),
                    Hours = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingLedger", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Wallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BalanceMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    LowBalanceHours = table.Column<int>(type: "integer", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingLedger_WorkspaceId_BillingHour",
                table: "BillingLedger",
                columns: new[] { "WorkspaceId", "BillingHour" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingLedger_WorkspaceId_ResourceType_ResourceId",
                table: "BillingLedger",
                columns: new[] { "WorkspaceId", "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingLedger_WorkspaceId_ResourceType_ResourceId_BillingHo~",
                table: "BillingLedger",
                columns: new[] { "WorkspaceId", "ResourceType", "ResourceId", "BillingHour" },
                unique: true,
                filter: "\"Kind\" IN (0, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_WorkspaceId",
                table: "Wallets",
                column: "WorkspaceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingLedger");

            migrationBuilder.DropTable(
                name: "Wallets");
        }
    }
}
