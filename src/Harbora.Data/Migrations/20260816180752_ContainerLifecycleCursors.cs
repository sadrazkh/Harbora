using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContainerLifecycleCursors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContainerLifecycleCursors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceRef = table.Column<string>(type: "text", nullable: false),
                    LastRestartCount = table.Column<int>(type: "integer", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerLifecycleCursors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContainerLifecycleCursors_ServerId_ResourceRef",
                table: "ContainerLifecycleCursors",
                columns: new[] { "ServerId", "ResourceRef" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContainerLifecycleCursors");
        }
    }
}
