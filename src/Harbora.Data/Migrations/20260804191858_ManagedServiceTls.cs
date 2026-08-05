using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedServiceTls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TlsEnabled",
                table: "ManagedServices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // MySQL (1) and MariaDB (2) generate their own certificate at first start and have been
            // negotiating TLS all along. Left at the default they would be reported as unencrypted,
            // which is the same lie as the opposite one and would send people to rebuild a container
            // that needs nothing. PostgreSQL stays false until it is re-provisioned, because that is
            // the truth about it.
            migrationBuilder.Sql(
                "UPDATE \"ManagedServices\" SET \"TlsEnabled\" = true WHERE \"Type\" IN (1, 2);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TlsEnabled",
                table: "ManagedServices");
        }
    }
}
