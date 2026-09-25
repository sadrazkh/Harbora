using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignInTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SignInTokenId",
                table: "AuditLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SignInTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RedeemedIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SessionExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedBy = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_SignInTokenId",
                table: "AuditLogs",
                column: "SignInTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_SignInTokens_CreatedByUserId_CreatedAt",
                table: "SignInTokens",
                columns: new[] { "CreatedByUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SignInTokens_TokenHash",
                table: "SignInTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignInTokens");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_SignInTokenId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "SignInTokenId",
                table: "AuditLogs");
        }
    }
}
