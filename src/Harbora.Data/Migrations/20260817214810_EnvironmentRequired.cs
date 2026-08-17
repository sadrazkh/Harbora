using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Apps_Environments_EnvironmentId",
                table: "Apps");

            migrationBuilder.DropForeignKey(
                name: "FK_ManagedServices_Environments_EnvironmentId",
                table: "ManagedServices");

            // Belt and braces, not a second backfill: the 2026-07-30 migration
            // (20260730220251_ProjectsAndEnvironments.cs) already placed every row that existed then,
            // and every creation path has set EnvironmentId since — P1's report is what an operator
            // reads to confirm that against a live database before this migration runs. This is the
            // same idempotent SQL run again, so the expected effect here is zero rows changed. Its
            // purpose is what comes after it: AlterColumn below turns any row this still leaves null
            // into Guid.Empty, which the AddForeignKey at the end of this method then refuses outright
            // — a loud migration failure instead of a required column quietly holding a value that
            // names no real environment.
            migrationBuilder.Sql("""
                UPDATE "Apps" a SET "EnvironmentId" = e."Id" FROM "Environments" e
                WHERE e."WorkspaceId" = a."WorkspaceId" AND e."Slug" = 'production' AND a."EnvironmentId" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "ManagedServices" m SET "EnvironmentId" = e."Id" FROM "Environments" e
                WHERE e."WorkspaceId" = m."WorkspaceId" AND e."Slug" = 'production' AND m."EnvironmentId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "EnvironmentId",
                table: "ManagedServices",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "EnvironmentId",
                table: "Apps",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Apps_Environments_EnvironmentId",
                table: "Apps",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ManagedServices_Environments_EnvironmentId",
                table: "ManagedServices",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Apps_Environments_EnvironmentId",
                table: "Apps");

            migrationBuilder.DropForeignKey(
                name: "FK_ManagedServices_Environments_EnvironmentId",
                table: "ManagedServices");

            migrationBuilder.AlterColumn<Guid>(
                name: "EnvironmentId",
                table: "ManagedServices",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "EnvironmentId",
                table: "Apps",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_Apps_Environments_EnvironmentId",
                table: "Apps",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ManagedServices_Environments_EnvironmentId",
                table: "ManagedServices",
                column: "EnvironmentId",
                principalTable: "Environments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
