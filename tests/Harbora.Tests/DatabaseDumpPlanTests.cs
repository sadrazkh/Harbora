using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Taking a database backup that will actually restore.
///
/// What this replaced: a "database" backup was a tar of the data directory taken while the database
/// was running — files being written to as they were read. What comes out of that may be torn, and
/// nothing finds out until someone tries to restore it. The panel reported success either way.
/// </summary>
public class DatabaseDumpPlanTests
{
    private static readonly ServiceCreds Creds =
        new("harbora-svc-shop-db", 5432, "shop", "s3cret", "shopdata");

    [Fact]
    public void Postgres_is_exported_by_the_engine_rather_than_copied_from_disk()
    {
        var plan = DatabaseDumpPlan.For(ManagedServiceType.PostgreSql, Creds, "/backup/shop.sql.gz");

        var command = string.Join(" ", plan!.Command);
        command.Should().Contain("pg_dump");
        command.Should().Contain("/backup/shop.sql.gz");
        plan.FileExtension.Should().Be(".sql.gz");
    }

    [Fact]
    public void The_password_travels_in_the_environment_and_never_on_the_command_line()
    {
        // A command line is visible to every process on the host, and lands in logs.
        foreach (var type in new[] { ManagedServiceType.PostgreSql, ManagedServiceType.MySql, ManagedServiceType.MongoDb })
        {
            var plan = DatabaseDumpPlan.For(type, Creds, "/backup/x")!;

            string.Join(" ", plan.Command).Should().NotContain("s3cret", $"{type} leaks the password");
            plan.Env.Values.Should().Contain("s3cret", $"{type} must still be able to authenticate");
        }
    }

    [Fact]
    public void A_failed_dump_cannot_be_hidden_by_a_successful_gzip()
    {
        // Without pipefail the pipeline reports gzip's exit code, so a dump that failed produces a
        // perfectly valid archive of an error message and a backup marked successful.
        var plan = DatabaseDumpPlan.For(ManagedServiceType.PostgreSql, Creds, "/backup/x")!;

        string.Join(" ", plan.Command).Should().Contain("pipefail");
    }

    [Fact]
    public void A_postgres_dump_can_be_restored_over_a_database_that_is_not_empty()
    {
        // Without --clean every CREATE TABLE fails on the objects already there, and with
        // ON_ERROR_STOP the restore stops at the first one. A backup that only restores into an
        // empty database is not much of a backup — and it fails at exactly the wrong moment.
        var command = string.Join(" ", DatabaseDumpPlan.For(ManagedServiceType.PostgreSql, Creds, "/b/x")!.Command);

        command.Should().Contain("--clean").And.Contain("--if-exists");
    }

    [Fact]
    public void A_mysql_dump_is_taken_in_one_transaction()
    {
        // Otherwise tables are dumped at different moments and the result is internally inconsistent
        // — the subtler version of the bug this whole class exists to fix.
        var command = string.Join(" ", DatabaseDumpPlan.For(ManagedServiceType.MySql, Creds, "/b/x")!.Command);

        command.Should().Contain("--single-transaction");
    }

    [Fact]
    public void Mariadb_is_dumped_the_same_way_as_mysql()
    {
        DatabaseDumpPlan.For(ManagedServiceType.MariaDb, Creds, "/b/x").Should().NotBeNull();
    }

    [Fact]
    public void A_restore_that_hits_an_error_stops_instead_of_reporting_success()
    {
        // Half a database restored, reported as restored, is worse than a failure: it is discovered
        // later, by someone who trusts it.
        var command = string.Join(" ", DatabaseDumpPlan.RestoreFor(ManagedServiceType.PostgreSql, Creds, "/b/x")!.Command);

        command.Should().Contain("ON_ERROR_STOP=1");
    }

    [Fact]
    public void Every_engine_that_can_be_dumped_can_also_be_restored()
    {
        // A backup with no way back is not a backup.
        foreach (var type in Enum.GetValues<ManagedServiceType>())
        {
            var canDump = DatabaseDumpPlan.For(type, Creds, "/b/x") is not null;
            var canRestore = DatabaseDumpPlan.RestoreFor(type, Creds, "/b/x") is not null;

            canRestore.Should().Be(canDump, $"{type} must be symmetrical");
        }
    }

    [Fact]
    public void Redis_says_why_it_is_copied_instead_of_exported()
    {
        // Not a gap — a cache's own snapshot file is the sensible artifact. But a screen that just
        // shows nothing here looks broken.
        DatabaseDumpPlan.For(ManagedServiceType.Redis, Creds, "/b/x").Should().BeNull();
        DatabaseDumpPlan.WhyNoDump(ManagedServiceType.Redis).Should().Contain("volume");
    }

    [Fact]
    public void Meilisearch_says_why_it_is_copied_instead_of_exported()
    {
        // Same shape as Redis above: not a gap that was overlooked, a real HTTP-only dump path this
        // task did not build a command for, said honestly rather than left to look like an oversight.
        DatabaseDumpPlan.For(ManagedServiceType.Meilisearch, Creds, "/b/x").Should().BeNull();
        DatabaseDumpPlan.WhyNoDump(ManagedServiceType.Meilisearch).Should().Contain("volume");
    }

    [Fact]
    public void An_engine_with_a_logical_dump_offers_no_excuse_for_not_having_one()
    {
        // The guard on the message above: if every engine had an explanation, the screen would show
        // one next to a working export.
        DatabaseDumpPlan.WhyNoDump(ManagedServiceType.PostgreSql).Should().BeNull();
    }

    [Fact]
    public void A_quote_in_a_database_name_cannot_end_the_quoting()
    {
        // Names come from configuration rather than from a visitor, but a name typed by hand is not
        // a trusted input either.
        var awkward = new ServiceCreds("h", 5432, "u", "p", "sh'op; rm -rf /");

        var command = string.Join(" ", DatabaseDumpPlan.For(ManagedServiceType.PostgreSql, awkward, "/b/x")!.Command);

        // The apostrophe is escaped, so the whole name stays one shell argument and the text after
        // it is data rather than a second command.
        command.Should().Contain(@"'sh'\''op; rm -rf /'");
        command.Should().NotContain("-d 'sh'op", "an unescaped apostrophe would end the quoting early");
    }
}
