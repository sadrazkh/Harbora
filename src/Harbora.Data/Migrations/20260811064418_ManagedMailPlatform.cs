using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedMailPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailServers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicHostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ApiBaseUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Image = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContainerName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EncryptedAdminUser = table.Column<string>(type: "text", nullable: false),
                    EncryptedAdminPassword = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DomainRatePerHourMinor = table.Column<long>(type: "bigint", nullable: true),
                    MailboxRatePerHourMinor = table.Column<long>(type: "bigint", nullable: true),
                    MaxDomainsPerWorkspace = table.Column<int>(type: "integer", nullable: false),
                    MaxMailboxesPerWorkspace = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailServers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailServers_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MailDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ProviderObjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DnsZone = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RatePerHourMinor = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailDomains_MailServers_MailServerId",
                        column: x => x.MailServerId,
                        principalTable: "MailServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MailMailboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailDomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalPart = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderObjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    QuotaBytes = table.Column<long>(type: "bigint", nullable: false),
                    RatePerHourMinor = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailMailboxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailMailboxes_MailDomains_MailDomainId",
                        column: x => x.MailDomainId,
                        principalTable: "MailDomains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailDomains_Domain",
                table: "MailDomains",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailDomains_MailServerId",
                table: "MailDomains",
                column: "MailServerId");

            migrationBuilder.CreateIndex(
                name: "IX_MailDomains_WorkspaceId_Status",
                table: "MailDomains",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MailMailboxes_MailDomainId_LocalPart",
                table: "MailMailboxes",
                columns: new[] { "MailDomainId", "LocalPart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailMailboxes_WorkspaceId_Status",
                table: "MailMailboxes",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MailServers_IsActive",
                table: "MailServers",
                column: "IsActive",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_MailServers_ServerId",
                table: "MailServers",
                column: "ServerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailMailboxes");

            migrationBuilder.DropTable(
                name: "MailDomains");

            migrationBuilder.DropTable(
                name: "MailServers");
        }
    }
}
