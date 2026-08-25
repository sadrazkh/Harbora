using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class MemoryOvercommitFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1.0, not the scaffolded 0.0: this backfills every existing server row with "no memory
            // overcommit beyond its reserved-memory headroom" — exactly the behaviour NodeCapacityService
            // already had before this column existed. A server already in production must not have its
            // placement math change just because this migration ran; any overcommit is an administrator's
            // later, explicit choice from the node's Capacity policy form.
            migrationBuilder.AddColumn<double>(
                name: "MemoryOvercommitFactor",
                table: "Servers",
                type: "double precision",
                nullable: false,
                defaultValue: 1.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MemoryOvercommitFactor",
                table: "Servers");
        }
    }
}
