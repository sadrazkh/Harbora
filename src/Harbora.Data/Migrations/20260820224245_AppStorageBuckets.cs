using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppStorageBuckets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppStorageBuckets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageBucketId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachOrder = table.Column<int>(type: "integer", nullable: false),
                    HasUnpublishedChanges = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppStorageBuckets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppStorageBuckets_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppStorageBuckets_StorageBuckets_StorageBucketId",
                        column: x => x.StorageBucketId,
                        principalTable: "StorageBuckets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppStorageBuckets_AppId_StorageBucketId",
                table: "AppStorageBuckets",
                columns: new[] { "AppId", "StorageBucketId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppStorageBuckets_StorageBucketId",
                table: "AppStorageBuckets",
                column: "StorageBucketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppStorageBuckets");
        }
    }
}
