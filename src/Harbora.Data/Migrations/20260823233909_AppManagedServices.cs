using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppManagedServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppManagedServices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagedServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Alias = table.Column<string>(type: "text", nullable: false),
                    AttachOrder = table.Column<int>(type: "integer", nullable: false),
                    HasUnpublishedChanges = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppManagedServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppManagedServices_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppManagedServices_ManagedServices_ManagedServiceId",
                        column: x => x.ManagedServiceId,
                        principalTable: "ManagedServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppManagedServices_AppId_Alias",
                table: "AppManagedServices",
                columns: new[] { "AppId", "Alias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppManagedServices_AppId_ManagedServiceId",
                table: "AppManagedServices",
                columns: new[] { "AppId", "ManagedServiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppManagedServices_ManagedServiceId",
                table: "AppManagedServices",
                column: "ManagedServiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppManagedServices");
        }
    }
}
