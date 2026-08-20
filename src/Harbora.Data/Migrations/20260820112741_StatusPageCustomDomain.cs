using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class StatusPageCustomDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "AppId",
                table: "Domains",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "StatusPageId",
                table: "Domains",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Domains_StatusPageId",
                table: "Domains",
                column: "StatusPageId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Domains_StatusPages_StatusPageId",
                table: "Domains",
                column: "StatusPageId",
                principalTable: "StatusPages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Domains_StatusPages_StatusPageId",
                table: "Domains");

            migrationBuilder.DropIndex(
                name: "IX_Domains_StatusPageId",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "StatusPageId",
                table: "Domains");

            migrationBuilder.AlterColumn<Guid>(
                name: "AppId",
                table: "Domains",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
