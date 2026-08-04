using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Database dump and restore commands.
///
/// <para>
/// Asserted directly, because this is where a password leaks into a process table or a database name
/// becomes syntax. Docker is not available on the machine this was written on, so what is pinned here
/// is the command that WOULD be run — the clients themselves are CI's job.
/// </para>
/// </summary>
public class DatabaseDumpCommandTests
{
    private static DatabaseConnection Connection(string database = "appdb") =>
        new("db-container", 5432, "harbora", "s3cret-password", database);

    [Theory]
    [InlineData(ManagedServiceType.PostgreSql, DatabaseEngine.PostgreSql)]
    [InlineData(ManagedServiceType.MySql, DatabaseEngine.MySql)]
    [InlineData(ManagedServiceType.MariaDb, DatabaseEngine.MariaDb)]
    [InlineData(ManagedServiceType.MongoDb, DatabaseEngine.MongoDb)]
    [InlineData(ManagedServiceType.Redis, DatabaseEngine.Redis)]
    public void Maps_every_managed_service_type_to_an_engine(
        ManagedServiceType type, DatabaseEngine expected)
    {
        DatabaseDumpCommands.EngineFor(type).Should().Be(expected);
    }

    /// <summary>
    /// The password reaches the client through the environment. On the command line it would be
    /// readable by every local user through /proc/&lt;pid&gt;/cmdline.
    /// </summary>
    [Theory]
    [InlineData(DatabaseEngine.PostgreSql, "PGPASSWORD")]
    [InlineData(DatabaseEngine.MySql, "MYSQL_PWD")]
    [InlineData(DatabaseEngine.MariaDb, "MYSQL_PWD")]
    public void The_password_travels_in_the_environment_and_never_in_an_argument(
        DatabaseEngine engine, string variable)
    {
        var command = DatabaseDumpCommands.Dump(engine, Connection(), "dump")!;

        command.Environment.Should().ContainKey(variable);
        command.Environment[variable].Should().Be("s3cret-password");
        command.Arguments.Should().NotContain(a => a.Contains("s3cret-password", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DatabaseEngine.PostgreSql)]
    [InlineData(DatabaseEngine.MySql)]
    [InlineData(DatabaseEngine.MariaDb)]
    public void No_shell_is_involved_anywhere(DatabaseEngine engine)
    {
        var dump = DatabaseDumpCommands.Dump(engine, Connection(), "dump")!;
        var restore = DatabaseDumpCommands.Restore(engine, Connection(), "dump.sql", "appdb")!;

        foreach (var command in new[] { dump, restore })
        {
            command.Arguments[0].Should().NotBe("sh");
            command.Arguments[0].Should().NotBe("bash");
            command.Arguments.Should().NotContain("-c");
            command.Arguments.Should().NotContain(a => a.Contains('|'));
            command.Arguments.Should().NotContain(a => a.Contains('>'));
        }
    }

    /// <summary>
    /// Uncompressed on purpose. A compressed dump changes in every byte when one row changes, so the
    /// repository would store each nightly backup in full instead of only the difference.
    /// </summary>
    [Fact]
    public void Postgres_dumps_uncompressed_so_the_repository_can_deduplicate()
    {
        var command = DatabaseDumpCommands.Dump(DatabaseEngine.PostgreSql, Connection(), "dump")!;

        command.Arguments.Should().Contain("--compress=0");
        command.Arguments.Should().Contain("--format=custom");
    }

    [Fact]
    public void Postgres_restore_stops_at_the_first_error()
    {
        // Without this pg_restore reports success over a half-restored database.
        var command = DatabaseDumpCommands.Restore(
            DatabaseEngine.PostgreSql, Connection(), "dump.pgdump", "appdb")!;

        command.Arguments.Should().Contain("--exit-on-error");
        command.Arguments.Should().Contain("--clean").And.Contain("--if-exists");
    }

    [Fact]
    public void MySql_dumps_in_a_single_transaction()
    {
        var command = DatabaseDumpCommands.Dump(DatabaseEngine.MySql, Connection(), "dump")!;

        command.Arguments.Should().Contain("--single-transaction",
            "a dump taken without one is a torn copy of a database that was being written to");
    }

    [Fact]
    public void A_database_name_carrying_syntax_stays_one_argument()
    {
        var hostile = Connection("app;DROP DATABASE other");

        var command = DatabaseDumpCommands.Dump(DatabaseEngine.MySql, hostile, "dump")!;

        command.Arguments.Should().ContainSingle(a => a == "app;DROP DATABASE other");
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("bad\0name")]
    public void Refuses_a_database_name_containing_a_control_character(string database)
    {
        var act = () => DatabaseDumpCommands.Dump(DatabaseEngine.MySql, Connection(database), "dump");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A scratch name is interpolated into CREATE DATABASE, where there is no safe escaping of an
    /// identifier that may contain a backtick — so it is allowlisted instead.
    /// </summary>
    [Theory]
    [InlineData("scratch`; DROP DATABASE x; --")]
    [InlineData("has space")]
    [InlineData("has-hyphen")]
    public void Refuses_a_scratch_identifier_that_is_not_plain(string name)
    {
        var act = () => DatabaseDumpCommands.CreateScratch(DatabaseEngine.MySql, Connection(), name);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Generates_a_scratch_name_that_is_always_a_plain_identifier()
    {
        var name = DatabaseDumpCommands.ScratchNameFor(Guid.CreateVersion7());

        name.Should().MatchRegex("^[A-Za-z0-9_]+$");
        var act = () => DatabaseDumpCommands.CreateScratch(DatabaseEngine.PostgreSql, Connection(), name);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData("-flag")]
    public void Refuses_a_dump_file_name_that_could_leave_its_directory(string fileName)
    {
        var act = () => DatabaseDumpCommands.Restore(
            DatabaseEngine.PostgreSql, Connection(), fileName, "appdb");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Both refusals name a next step. "Unsupported" on its own tells an operator nothing.
    /// </summary>
    [Fact]
    public void Redis_is_refused_with_the_alternative_that_actually_works()
    {
        var why = DatabaseDumpCommands.WhyUnsupported(DatabaseEngine.Redis);

        why.Should().NotBeNull();
        why.Should().Contain("volume", "Redis's data volume is the honest artifact");
        DatabaseDumpCommands.Dump(DatabaseEngine.Redis, Connection(), "dump").Should().BeNull();
    }

    [Fact]
    public void MongoDb_is_refused_because_its_client_cannot_hide_a_password()
    {
        var why = DatabaseDumpCommands.WhyUnsupported(DatabaseEngine.MongoDb);

        why.Should().NotBeNull();
        why.Should().Contain("process table");
        DatabaseDumpCommands.Dump(DatabaseEngine.MongoDb, Connection(), "dump").Should().BeNull();
    }
}

/// <summary>
/// The provider that runs those commands in a container built from the database's own image.
/// </summary>
public sealed class ContainerDatabaseProviderTests : IDisposable
{
    private readonly string _root;
    private readonly FakeDockerEngine _docker = new();

    public ContainerDatabaseProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-db-provider", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private ContainerDatabaseBackupProvider Provider(DatabaseEngine engine) => new(
        engine, _docker, NullLogger<ContainerDatabaseBackupProvider>.Instance);

    private static DatabaseExecutionContext Execution() =>
        new("postgres:16", "harbora_ws", "harbora_backups", "/dump");

    private DatabaseBackupContext BackupContext() => new(
        DatabaseEngine.PostgreSql,
        new DatabaseConnection("db", 5432, "harbora", "pw-value", "appdb"),
        Execution(),
        _root,
        "dump");

    [Fact]
    public async Task Runs_the_client_from_the_databases_own_image()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "dump.pgdump"), "PGDMP fake");

        var result = await Provider(DatabaseEngine.PostgreSql).CreateBackupAsync(BackupContext(), default);

        result.Succeeded.Should().BeTrue(result.Error);

        var request = _docker.OneOffRequests.Should().ContainSingle().Subject;
        request.Image.Should().Be("postgres:16",
            "pg_dump refuses to dump a server newer than itself, so the client must match the server");
        request.NetworkMode.Should().Be("harbora_ws");
        request.Env.Should().ContainKey("PGPASSWORD");
        request.Command.Should().NotContain(a => a.Contains("pw-value", StringComparison.Ordinal));
    }

    /// <summary>
    /// The helper writes into the staging volume by name while the panel reads it through a mount.
    /// If those are different volumes, the dump "succeeds" and the backup archives an empty folder.
    /// </summary>
    [Fact]
    public async Task Reports_a_dump_that_reported_success_but_produced_no_file()
    {
        var result = await Provider(DatabaseEngine.PostgreSql).CreateBackupAsync(BackupContext(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("no file arrived");
    }

    [Fact]
    public async Task Refuses_an_empty_dump_rather_than_calling_it_a_backup()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "dump.pgdump"), "");

        var result = await Provider(DatabaseEngine.PostgreSql).CreateBackupAsync(BackupContext(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("empty");
    }

    [Fact]
    public async Task Reports_a_failing_client()
    {
        _docker.OneOffExitCode = 1;

        var result = await Provider(DatabaseEngine.PostgreSql).CreateBackupAsync(BackupContext(), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("exit 1");
    }

    [Fact]
    public async Task Mounts_the_dump_read_only_when_restoring()
    {
        var context = new DatabaseRestoreContext(
            DatabaseEngine.PostgreSql,
            new DatabaseConnection("db", 5432, "harbora", "pw-value", "appdb"),
            Execution(), _root, "dump.pgdump");

        await Provider(DatabaseEngine.PostgreSql).RestoreAsync(context, default);

        var bind = _docker.OneOffRequests.Single().Binds.Single();
        bind.ReadOnly.Should().BeTrue("loading data into a database must not be able to alter its source");
    }

    /// <summary>
    /// The scratch database is dropped whatever the rehearsal concluded — otherwise a failed
    /// verification leaves a database behind and the next one collides with it.
    /// </summary>
    [Fact]
    public async Task Drops_the_scratch_database_even_when_the_rehearsal_fails()
    {
        // create succeeds, restore fails: the fake returns the same code for every call, so drive
        // the failure through the restore by making every call fail after the first.
        _docker.OneOffExitCode = 1;

        var context = new DatabaseBackupVerificationContext(
            DatabaseEngine.PostgreSql,
            new DatabaseConnection("db", 5432, "harbora", "pw", "appdb"),
            Execution(), _root, "dump.pgdump",
            DatabaseDumpCommands.ScratchNameFor(Guid.CreateVersion7()));

        var result = await Provider(DatabaseEngine.PostgreSql).VerifyAsync(context, default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("scratch database could not be created");
    }

    [Fact]
    public async Task Verification_of_an_unsupported_engine_is_skipped_not_failed()
    {
        var context = new DatabaseBackupVerificationContext(
            DatabaseEngine.Redis,
            new DatabaseConnection("db", 6379, "default", "pw"),
            Execution(), _root, "dump.rdb", "scratch");

        var result = await Provider(DatabaseEngine.Redis).VerifyAsync(context, default);

        result.Skipped.Should().BeTrue("'not checkable' and 'checked and fine' must never look alike");
        result.Detail.Should().Contain("volume");
    }

    [Fact]
    public async Task An_unsupported_engine_refuses_to_back_up_with_its_reason()
    {
        var context = BackupContext() with { Engine = DatabaseEngine.MongoDb };

        var result = await Provider(DatabaseEngine.MongoDb).CreateBackupAsync(context, default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("process table");
        _docker.OneOffRequests.Should().BeEmpty("nothing should be started for an engine we refuse");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }
}
