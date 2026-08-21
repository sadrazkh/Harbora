using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class FunctionQueueTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QueueLastAttemptAt",
                table: "FunctionDefinitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueueLastError",
                table: "FunctionDefinitions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueueName",
                table: "FunctionDefinitions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QueueServiceId",
                table: "FunctionDefinitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FunctionQueueDeadLetters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FunctionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    QueueName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunctionQueueDeadLetters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FunctionQueueDeadLetters_FunctionDefinitions_FunctionId",
                        column: x => x.FunctionId,
                        principalTable: "FunctionDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FunctionDefinitions_Trigger_IsEnabled",
                table: "FunctionDefinitions",
                columns: new[] { "Trigger", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_FunctionQueueDeadLetters_FunctionId_CreatedAt",
                table: "FunctionQueueDeadLetters",
                columns: new[] { "FunctionId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FunctionQueueDeadLetters");

            migrationBuilder.DropIndex(
                name: "IX_FunctionDefinitions_Trigger_IsEnabled",
                table: "FunctionDefinitions");

            migrationBuilder.DropColumn(
                name: "QueueLastAttemptAt",
                table: "FunctionDefinitions");

            migrationBuilder.DropColumn(
                name: "QueueLastError",
                table: "FunctionDefinitions");

            migrationBuilder.DropColumn(
                name: "QueueName",
                table: "FunctionDefinitions");

            migrationBuilder.DropColumn(
                name: "QueueServiceId",
                table: "FunctionDefinitions");
        }
    }
}
