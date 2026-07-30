using System.Globalization;
using FluentAssertions;
using Harbora.Infrastructure.Backups;
using Npgsql;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The restore point taken before an upgrade migrates the database.
///
/// `harbora update` pulls new code, rebuilds, and the panel applies migrations on boot. Nothing was
/// captured first, so a destructive migration — or a new version that turns out to be broken — left
/// no route back to the data as it was.
/// </summary>
public class UpgradeSafetyPlanTests
{
    private static NpgsqlConnectionStringBuilder Conn(string db = "harbora", string user = "harbora") =>
        new() { Host = "postgres", Port = 5432, Database = db, Username = user, Password = "secret" };

    // ---- when it is warranted ----

    [Fact]
    public void An_upgrade_of_an_existing_install_needs_a_restore_point()
        => UpgradeSafetyPlan.NeedsRestorePoint(pendingMigrations: 2, appliedMigrations: 7).Should().BeTrue();

    [Fact]
    public void A_first_install_does_not()
    {
        // Every migration is pending and there is nothing to lose. Backing up here would turn first
        // boot into a Docker round-trip whose only possible outcome is failure.
        UpgradeSafetyPlan.NeedsRestorePoint(pendingMigrations: 9, appliedMigrations: 0).Should().BeFalse();
    }

    [Fact]
    public void An_ordinary_restart_does_not()
    {
        // Same code, same schema. A dump on every restart would be pure cost.
        UpgradeSafetyPlan.NeedsRestorePoint(pendingMigrations: 0, appliedMigrations: 7).Should().BeFalse();
    }

    // ---- what actually runs ----

    [Fact]
    public void The_dump_fails_when_pg_dump_fails_rather_than_when_gzip_does()
    {
        var command = string.Join(" ", UpgradeSafetyPlan.DumpCommand(Conn(), "/backup/x.sql.gz"));

        // Without pipefail the exit code is gzip's, so a pg_dump that died halfway still reports
        // success — leaving a perfectly valid gzip of a truncated dump. That is the worst possible
        // restore point, because it only reveals itself at the moment it is needed.
        command.Should().Contain("set -o pipefail");
    }

    [Fact]
    public void The_password_is_never_put_on_the_command_line()
    {
        // Command lines are visible in `docker inspect` and in the host's process list.
        var command = string.Join(" ", UpgradeSafetyPlan.DumpCommand(Conn(), "/backup/x.sql.gz"));

        command.Should().NotContain("secret");
        command.Should().NotContain("PGPASSWORD");
    }

    [Fact]
    public void The_dump_carries_no_ownership_so_it_can_be_restored_anywhere()
    {
        // A dump full of GRANTs for roles that don't exist on the target fails halfway through the
        // restore — exactly when you are least able to debug it.
        var command = string.Join(" ", UpgradeSafetyPlan.DumpCommand(Conn(), "/backup/x.sql.gz"));

        command.Should().Contain("--no-owner").And.Contain("--no-privileges");
    }

    [Theory]
    [InlineData("harbora", "'harbora'")]
    [InlineData("my db", "'my db'")]
    [InlineData("it's", @"'it'\''s'")]
    [InlineData("a; rm -rf /", "'a; rm -rf /'")]
    public void Connection_values_are_quoted_for_the_shell(string value, string expected)
        => UpgradeSafetyPlan.Shell(value).Should().Be(expected);

    [Fact]
    public void A_database_name_with_a_quote_stays_one_argument()
    {
        var command = UpgradeSafetyPlan.DumpCommand(Conn(db: "it's"), "/backup/x.sql.gz")[2];

        command.Should().Contain(@"-d 'it'\''s'");
    }

    // ---- keeping the disk bounded ----

    [Fact]
    public void Only_the_newest_restore_points_are_kept()
    {
        string[] files =
        [
            "pre-upgrade-20260101-000000.sql.gz",
            "pre-upgrade-20260301-000000.sql.gz",
            "pre-upgrade-20260201-000000.sql.gz"
        ];

        var pruned = UpgradeSafetyPlan.DumpsToPrune(files, keep: 2);

        pruned.Should().BeEquivalentTo(["pre-upgrade-20260101-000000.sql.gz"], "the oldest goes first");
    }

    [Fact]
    public void Pruning_never_touches_anything_it_did_not_create()
    {
        // The staging directory also holds app volume archives and database backups. Deleting one of
        // those to make room for a restore point would trade a small problem for a much worse one.
        string[] files =
        [
            "volume-shop-20260101.tgz", "platform-20260101.json.gz", "database-db-20260101.sql.gz",
            "pre-upgrade-20260101-000000.sql.gz", "pre-upgrade-20260102-000000.sql.gz"
        ];

        var pruned = UpgradeSafetyPlan.DumpsToPrune(files, keep: 1);

        pruned.Should().BeEquivalentTo(["pre-upgrade-20260101-000000.sql.gz"]);
    }

    [Fact]
    public void Keeping_zero_still_only_removes_restore_points()
    {
        string[] files = ["volume-shop-20260101.tgz", "pre-upgrade-20260101-000000.sql.gz"];

        UpgradeSafetyPlan.DumpsToPrune(files, keep: 0)
            .Should().BeEquivalentTo(["pre-upgrade-20260101-000000.sql.gz"]);
    }

    // ---- naming ----

    [Fact]
    public void The_name_sorts_by_age_and_stays_gregorian()
    {
        // Retention orders by name, so a Jalali year would sort a new file as the oldest one and
        // delete exactly the restore point an upgrade just created.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            var name = UpgradeSafetyPlan.FileNameFor(new DateTimeOffset(2026, 7, 30, 9, 15, 0, TimeSpan.Zero));

            name.Should().Be("pre-upgrade-20260730-091500.sql.gz");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void The_name_is_recognised_as_a_restore_point()
    {
        UpgradeSafetyPlan.IsRestorePoint(UpgradeSafetyPlan.FileNameFor(DateTimeOffset.UnixEpoch))
            .Should().BeTrue("otherwise nothing would ever be pruned");
    }
}
