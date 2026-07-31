using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Finding out whether a backup would actually restore.
///
/// Verification could already say an artifact was present, matched its checksum, decrypted and was a
/// readable archive. None of that answers the only question anyone has. A gzip full of SQL that
/// references a missing extension, or was cut short mid-write, is a perfectly readable archive and a
/// worthless backup — and it is discovered during an incident, the one moment it must not be.
/// </summary>
public class RestoreRehearsalTests
{
    private static readonly ServiceCreds Creds =
        new("harbora-svc-shop-db", 5432, "shop", "s3cretpassword", "shopdata");

    private static readonly Guid BackupId = Guid.Parse("abcdef12-3456-7890-abcd-ef1234567890");

    [Fact]
    public void The_rehearsal_restores_into_a_database_of_its_own()
    {
        // The whole safety property: a check that could touch the live database is not a check
        // anyone would run.
        var plan = RestoreRehearsal.For(ManagedServiceType.PostgreSql, Creds, "/backup/x.sql.gz", BackupId)!;

        plan.ScratchDatabase.Should().NotBe(Creds.Database);
        string.Join(" ", plan.Restore).Should().Contain(plan.ScratchDatabase);
        string.Join(" ", plan.Restore).Should().NotContain("-d 'shopdata'");
    }

    [Fact]
    public void The_scratch_database_explains_itself_and_cannot_collide()
    {
        // It is visible in the server's database list while the check runs.
        var name = RestoreRehearsal.ScratchName(BackupId);

        name.Should().StartWith("harbora_restore_check_");
        name.Should().NotBe(RestoreRehearsal.ScratchName(Guid.NewGuid()));
        name.Should().MatchRegex("^[a-z0-9_]+$", "it goes into a SQL identifier");
    }

    [Fact]
    public void The_same_backup_always_rehearses_into_the_same_name()
    {
        // So a rehearsal interrupted halfway leaves one droppable leftover, not a new one each time.
        RestoreRehearsal.ScratchName(BackupId).Should().Be(RestoreRehearsal.ScratchName(BackupId));
    }

    [Fact]
    public void There_is_always_a_command_to_remove_it_again()
    {
        var plan = RestoreRehearsal.For(ManagedServiceType.PostgreSql, Creds, "/b/x", BackupId)!;

        string.Join(" ", plan.Drop).Should().Contain("DROP DATABASE").And.Contain(plan.ScratchDatabase);
        string.Join(" ", plan.Drop).Should().Contain("IF EXISTS", "cleanup must not fail on a rehearsal that never got started");
    }

    [Fact]
    public void A_restore_that_hits_an_error_stops_rather_than_reporting_a_pass()
    {
        var plan = RestoreRehearsal.For(ManagedServiceType.PostgreSql, Creds, "/b/x", BackupId)!;

        string.Join(" ", plan.Restore).Should().Contain("ON_ERROR_STOP=1").And.Contain("pipefail");
    }

    [Fact]
    public void The_password_never_appears_on_a_command_line()
    {
        foreach (var type in new[] { ManagedServiceType.PostgreSql, ManagedServiceType.MySql })
        {
            var plan = RestoreRehearsal.For(type, Creds, "/b/x", BackupId)!;

            foreach (var command in new[] { plan.Create, plan.Restore, plan.Count, plan.Drop })
                string.Join(" ", command).Should().NotContain("s3cretpassword", $"{type} leaks the password");

            plan.Env.Values.Should().Contain("s3cretpassword");
        }
    }

    [Fact]
    public void The_count_only_looks_at_the_restored_database()
    {
        // Counting the server's tables instead would pass on an empty restore, which is precisely
        // the failure being looked for.
        var plan = RestoreRehearsal.For(ManagedServiceType.PostgreSql, Creds, "/b/x", BackupId)!;

        string.Join(" ", plan.Count).Should().Contain(plan.ScratchDatabase);
        string.Join(" ", plan.Count).Should().Contain("pg_catalog", "the engine's own tables do not count");
    }

    [Fact]
    public void An_engine_that_cannot_be_rehearsed_says_so()
    {
        // "Not checked" and "checked and fine" must never look the same on a screen.
        RestoreRehearsal.For(ManagedServiceType.Redis, Creds, "/b/x", BackupId).Should().BeNull();
        RestoreRehearsal.WhyUnsupported(ManagedServiceType.Redis).Should().NotBeNullOrWhiteSpace();
        RestoreRehearsal.WhyUnsupported(ManagedServiceType.MongoDb).Should().NotBeNullOrWhiteSpace();
        RestoreRehearsal.WhyUnsupported(ManagedServiceType.PostgreSql).Should().BeNull();
    }

    [Theory]
    [InlineData("14", 14)]
    [InlineData("\0\0\0\0\0\0\b7\n", 7)]
    public void The_table_count_is_read_through_dockers_framing(string output, int expected)
    {
        RestoreRehearsal.ReadCount(output).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("psql: error: connection refused")]
    public void No_usable_answer_is_not_the_same_as_zero(string? output)
    {
        RestoreRehearsal.ReadCount(output).Should().BeNull();
    }

    [Fact]
    public void A_backup_that_restores_nothing_is_a_failed_rehearsal()
    {
        // However cleanly it ran. An empty or truncated dump looks exactly like this.
        RestoreRehearsal.Explain(0).Should().Contain("empty");
        RestoreRehearsal.Explain(null).Should().Contain("cannot be trusted");

        RestoreRehearsal.Explain(12).Should().BeNull();
    }
}
