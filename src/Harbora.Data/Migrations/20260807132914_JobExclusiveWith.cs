using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class JobExclusiveWith : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExclusiveWith",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            // Stamp the deployment jobs already in the queue when this runs.
            //
            // The column says what a job must not run beside. Null means "my own target", which is
            // right for every kind except a deployment: a deployment's target is the Deployment row
            // and every redeploy is a new row, so two of them for one app exclude on nothing in
            // common. Under the serial worker that only meant they queued behind each other; beside
            // the parallel worker released with this column it means two docker builds for one app,
            // two containers under one name, two host-port reservations and two proxy applies. The
            // rows this finds were written by the build being upgraded away from, so there is no
            // enqueue path that could have stamped them — this is the only chance to.
            //
            // Additive: it writes nothing but nulls, in one kind, in rows that have not finished.
            // Idempotent by the IS NULL guard, so a re-run changes nothing. And it cannot invent a
            // value — every id it writes is read from the deployment's own row, so a job whose
            // deployment has been deleted is left null rather than given Guid.Empty, which would
            // have made it exclude every other keyless deployment on the platform.
            //
            // Literals rather than names because a migration must go on meaning what it meant when
            // it was written: Kind 0 is JobKind.Deployment, Status 0 and 1 are JobStatus.Pending and
            // Running. Those wire values are frozen — see the note on the enums.
            migrationBuilder.Sql("""
                UPDATE "Jobs" SET "ExclusiveWith" = d."AppId"
                FROM "Deployments" d
                WHERE "Jobs"."TargetId" = d."Id"
                  AND "Jobs"."Kind" = 0
                  AND "Jobs"."Status" IN (0, 1)
                  AND "Jobs"."ExclusiveWith" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExclusiveWith",
                table: "Jobs");
        }
    }
}
