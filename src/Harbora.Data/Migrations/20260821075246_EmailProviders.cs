using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Host = table.Column<string>(type: "text", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: false),
                    FromAddress = table.Column<string>(type: "text", nullable: false),
                    FromName = table.Column<string>(type: "text", nullable: true),
                    UseSsl = table.Column<bool>(type: "boolean", nullable: false),
                    LastTestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastTestSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastTestMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppEmailProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachOrder = table.Column<int>(type: "integer", nullable: false),
                    HasUnpublishedChanges = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppEmailProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppEmailProviders_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppEmailProviders_EmailProviders_EmailProviderId",
                        column: x => x.EmailProviderId,
                        principalTable: "EmailProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppEmailProviders_AppId_EmailProviderId",
                table: "AppEmailProviders",
                columns: new[] { "AppId", "EmailProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppEmailProviders_EmailProviderId",
                table: "AppEmailProviders",
                column: "EmailProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppEmailProviders");

            migrationBuilder.DropTable(
                name: "EmailProviders");
        }
    }
}
