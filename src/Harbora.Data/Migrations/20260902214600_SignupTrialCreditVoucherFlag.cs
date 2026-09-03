using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignupTrialCreditVoucherFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTrialCredit",
                table: "BillingVouchers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_BillingVouchers_TrialCreditOwner",
                table: "BillingVouchers",
                column: "CreatedByUserId",
                unique: true,
                filter: "\"IsTrialCredit\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BillingVouchers_TrialCreditOwner",
                table: "BillingVouchers");

            migrationBuilder.DropColumn(
                name: "IsTrialCredit",
                table: "BillingVouchers");
        }
    }
}
