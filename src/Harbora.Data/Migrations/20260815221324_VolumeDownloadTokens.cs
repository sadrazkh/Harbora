using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class VolumeDownloadTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VolumeDownloadTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    VolumeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumeDownloadTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolumeDownloadTokens_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolumeDownloadTokens_Volumes_VolumeId",
                        column: x => x.VolumeId,
                        principalTable: "Volumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolumeDownloadTokens_AppId",
                table: "VolumeDownloadTokens",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_VolumeDownloadTokens_TokenHash",
                table: "VolumeDownloadTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolumeDownloadTokens_VolumeId",
                table: "VolumeDownloadTokens",
                column: "VolumeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VolumeDownloadTokens");
        }
    }
}
