using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExternalMailProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "MailServerId",
                table: "MailDomains",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "ExternalAdminUrl",
                table: "MailDomains",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalImapHost",
                table: "MailDomains",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalImapPort",
                table: "MailDomains",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalProviderName",
                table: "MailDomains",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSmtpHost",
                table: "MailDomains",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExternalSmtpPort",
                table: "MailDomains",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "MailDomains",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalAdminUrl",
                table: "MailDomains");

            migrationBuilder.DropColumn(
                name: "ExternalImapHost",
                table: "MailDomains");

            migrationBuilder.DropColumn(
                name: "ExternalImapPort",
                table: "MailDomains");

            migrationBuilder.DropColumn(
                name: "ExternalProviderName",
                table: "MailDomains");

            migrationBuilder.DropColumn(
                name: "ExternalSmtpHost",
                table: "MailDomains");

            migrationBuilder.DropColumn(
                name: "ExternalSmtpPort",
                table: "MailDomains");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "MailDomains");

            migrationBuilder.AlterColumn<Guid>(
                name: "MailServerId",
                table: "MailDomains",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
