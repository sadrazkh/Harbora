using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadyAppVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppTemplateAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Format = table.Column<string>(type: "text", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: true),
                    License = table.Column<int>(type: "integer", nullable: false),
                    LicenseNote = table.Column<string>(type: "text", nullable: true),
                    WorksOnBothThemes = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppTemplateAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppTemplateAssets_AppTemplates_AppTemplateId",
                        column: x => x.AppTemplateId,
                        principalTable: "AppTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppTemplateVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    ImageRepository = table.Column<string>(type: "text", nullable: false),
                    ImageTag = table.Column<string>(type: "text", nullable: false),
                    ImageDigest = table.Column<string>(type: "text", nullable: true),
                    Lifecycle = table.Column<int>(type: "integer", nullable: false),
                    Publication = table.Column<int>(type: "integer", nullable: false),
                    SupportedArchitectures = table.Column<string>(type: "text", nullable: false),
                    MinimumNodeVersion = table.Column<string>(type: "text", nullable: true),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    UpgradeNotes = table.Column<string>(type: "text", nullable: true),
                    UpgradeNotesFa = table.Column<string>(type: "text", nullable: true),
                    MigrationWarnings = table.Column<string>(type: "text", nullable: true),
                    MigrationWarningsFa = table.Column<string>(type: "text", nullable: true),
                    AllowsDowngrade = table.Column<bool>(type: "boolean", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DiscoveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppTemplateVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppTemplateVersions_AppTemplates_AppTemplateId",
                        column: x => x.AppTemplateId,
                        principalTable: "AppTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppTemplateAssets_AppTemplateId",
                table: "AppTemplateAssets",
                column: "AppTemplateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppTemplateVersions_AppTemplateId_Version",
                table: "AppTemplateVersions",
                columns: new[] { "AppTemplateId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppTemplateAssets");

            migrationBuilder.DropTable(
                name: "AppTemplateVersions");
        }
    }
}
