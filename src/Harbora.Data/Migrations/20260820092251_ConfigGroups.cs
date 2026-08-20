using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConfigGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppConfigGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachOrder = table.Column<int>(type: "integer", nullable: false),
                    HasUnpublishedChanges = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppConfigGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppConfigGroups_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppConfigGroups_ConfigGroups_ConfigGroupId",
                        column: x => x.ConfigGroupId,
                        principalTable: "ConfigGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConfigGroupEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    IsSecret = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigGroupEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConfigGroupEntries_ConfigGroups_ConfigGroupId",
                        column: x => x.ConfigGroupId,
                        principalTable: "ConfigGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppConfigGroups_AppId_ConfigGroupId",
                table: "AppConfigGroups",
                columns: new[] { "AppId", "ConfigGroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppConfigGroups_ConfigGroupId",
                table: "AppConfigGroups",
                column: "ConfigGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigGroupEntries_ConfigGroupId_Key",
                table: "ConfigGroupEntries",
                columns: new[] { "ConfigGroupId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfigGroups_WorkspaceId_Name",
                table: "ConfigGroups",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppConfigGroups");

            migrationBuilder.DropTable(
                name: "ConfigGroupEntries");

            migrationBuilder.DropTable(
                name: "ConfigGroups");
        }
    }
}
