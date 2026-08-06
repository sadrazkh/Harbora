using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserTotp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecoveryCodesHash",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TotpEnabledAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecretEncrypted",
                table: "Users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecoveryCodesHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TotpEnabledAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TotpSecretEncrypted",
                table: "Users");
        }
    }
}
