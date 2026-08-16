using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServerInstancePricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Family",
                table: "InstanceSizes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ServerInstanceOffers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceSizeKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsOffered = table.Column<bool>(type: "boolean", nullable: false),
                    RunningRatePerHourMinor = table.Column<long>(type: "bigint", nullable: true),
                    StoppedRatePerHourMinor = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerInstanceOffers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerInstanceOffers_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerInstanceOffers_ServerId_InstanceSizeKey",
                table: "ServerInstanceOffers",
                columns: new[] { "ServerId", "InstanceSizeKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerInstanceOffers");

            migrationBuilder.DropColumn(
                name: "Family",
                table: "InstanceSizes");
        }
    }
}
