using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class SftpBackupDestination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedSftpPassword",
                table: "BackupDestinations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SftpDirectory",
                table: "BackupDestinations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SftpHost",
                table: "BackupDestinations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SftpHostKey",
                table: "BackupDestinations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SftpPort",
                table: "BackupDestinations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SftpUsername",
                table: "BackupDestinations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedSftpPassword",
                table: "BackupDestinations");

            migrationBuilder.DropColumn(
                name: "SftpDirectory",
                table: "BackupDestinations");

            migrationBuilder.DropColumn(
                name: "SftpHost",
                table: "BackupDestinations");

            migrationBuilder.DropColumn(
                name: "SftpHostKey",
                table: "BackupDestinations");

            migrationBuilder.DropColumn(
                name: "SftpPort",
                table: "BackupDestinations");

            migrationBuilder.DropColumn(
                name: "SftpUsername",
                table: "BackupDestinations");
        }
    }
}
