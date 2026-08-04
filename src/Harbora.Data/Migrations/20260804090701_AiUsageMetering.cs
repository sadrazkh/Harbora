using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiUsageMetering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiUsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AiUserApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    AiPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedModel = table.Column<string>(type: "text", nullable: false),
                    ProviderModelId = table.Column<string>(type: "text", nullable: true),
                    AiProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    AiProviderCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CachedInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ProviderCost = table.Column<decimal>(type: "numeric", nullable: false),
                    ChargedCost = table.Column<decimal>(type: "numeric", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    Streaming = table.Column<bool>(type: "boolean", nullable: false),
                    ClientDisconnected = table.Column<bool>(type: "boolean", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageRecords_AiUserApiKeyId",
                table: "AiUsageRecords",
                column: "AiUserApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageRecords_WorkspaceId_CreatedAt",
                table: "AiUsageRecords",
                columns: new[] { "WorkspaceId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsageRecords");
        }
    }
}
