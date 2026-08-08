using DotNet.Testcontainers.Builders;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// One throwaway PostgreSQL for the whole assembly, and the databases the tests carve out of it.
///
/// <para>
/// Everything else in the repository tests the database through EF InMemory, which is the right
/// trade for the three thousand facts that care about rules rather than about SQL. What it cannot
/// check is the part that only exists in Postgres: a partial unique index, a concurrency token
/// under a real race, <c>COALESCE</c> in a claim predicate, a global query filter turned into a
/// <c>DELETE</c> — and, above all, the hand-written <c>UPDATE</c>s inside four migrations that no
/// test has ever executed. <c>MigrationConsistencyTests</c> compares the model to the snapshot; it
/// never runs a statement.
/// </para>
///
/// <para>
/// The image is <c>postgres:16-alpine</c>, the same one <c>deploy/docker-compose.yml</c> runs, so
/// what passes here is what the product is installed onto rather than whatever the newest major
/// happens to do.
/// </para>
///
/// <para>
/// A test that writes gets a database of its own — <see cref="FreshlyMigratedAsync"/> — because a
/// unique-index violation in one test must not be visible to the next. The two expensive shared
/// ones (a head schema for read-only assertions, and one upgraded from the previous release) are
/// built once and memoised, since neither is written to.
/// </para>
/// </summary>
public sealed class PostgresLane : IAsyncLifetime
{
    /// <summary>Every test class joins this collection, so they share one container and run in order.</summary>
    public const string Collection = "postgres";

    /// <summary>Matches <c>deploy/docker-compose.yml</c>. Changing this changes what is proven.</summary>
    public const string Image = "postgres:16-alpine";

    private PostgreSqlContainer? _container;
    private int _databases;

    private readonly Lazy<Task<string>> _headSchema;
    private readonly Lazy<Task<UpgradedInstall>> _upgraded;

    public PostgresLane()
    {
        _headSchema = new Lazy<Task<string>>(
            () => FreshlyMigratedAsync("head"), LazyThreadSafetyMode.ExecutionAndPublication);
        _upgraded = new Lazy<Task<UpgradedInstall>>(
            () => UpgradeFromPreviousRelease.RunAsync(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task InitializeAsync()
    {
        // Deliberately does nothing where Docker is unreachable. xUnit builds a collection fixture
        // even when every test in the collection is skipped, so throwing here would turn an honest
        // "not run on this machine" into a red suite on every developer laptop.
        if (PostgresFactAttribute.SkipReason is not null) return;

        // Nothing is customised. The module's defaults put the connection on the "postgres"
        // database, which is the one that always exists and is therefore the one a CREATE DATABASE
        // has to be issued from — every database these tests use is carved out of it below.
        _container = new PostgreSqlBuilder(Image).Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>
    /// The running container. Never null in a test body — <see cref="PostgresFactAttribute"/> has
    /// already skipped the test when there is no daemon, so reaching here without one is a bug.
    /// </summary>
    private PostgreSqlContainer Container() =>
        _container ?? throw new InvalidOperationException(
            "The Docker probe said a daemon was reachable but the PostgreSQL container was never started.");

    /// <summary>An empty database of its own, and the connection string that reaches it.</summary>
    public async Task<string> NewDatabaseAsync(string label)
    {
        var name = $"harbora_{Sanitise(label)}_{Interlocked.Increment(ref _databases)}";

        await using (var admin = new NpgsqlConnection(Container().GetConnectionString()))
        {
            await admin.OpenAsync();
            // Identifier, not a value, so it cannot be a parameter. Sanitise() is what makes that safe.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(Container().GetConnectionString())
        {
            Database = name,
            // A constraint violation names the row that caused it. Off by default because values
            // can be personal data; on here because the values are this file's own inventions and a
            // CI failure with the offending key in it is worth several hours.
            IncludeErrorDetail = true
        }.ConnectionString;
    }

    /// <summary>A database of its own with every migration applied, for a test that writes.</summary>
    public async Task<string> FreshlyMigratedAsync(string label)
    {
        var connectionString = await NewDatabaseAsync(label);
        await using var db = Open(connectionString);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    /// <summary>
    /// A shared database at the head schema, for assertions that only read the catalogue. Built
    /// once: applying every migration takes long enough that doing it per fact would be the whole
    /// cost of this lane.
    /// </summary>
    public Task<string> HeadSchemaAsync() => _headSchema.Value;

    /// <summary>
    /// A shared database that was migrated to the previous release, seeded with the rows an
    /// upgrading install could be carrying, and then migrated the rest of the way. Built once, and
    /// only read afterwards — see <see cref="UpgradeFromPreviousRelease"/>.
    /// </summary>
    public Task<UpgradedInstall> UpgradedAsync() => _upgraded.Value;

    /// <summary>
    /// A context over one of these databases. <paramref name="scope"/> defaults to the system scope,
    /// which is what every background path in the product uses; pass a
    /// <see cref="FixedWorkspaceScope"/> to see the database the way a request does.
    /// </summary>
    public static HarboraDbContext Open(string connectionString, IWorkspaceScope? scope = null) =>
        new(new DbContextOptionsBuilder<HarboraDbContext>().UseNpgsql(connectionString).Options,
            scope ?? SystemWorkspaceScope.Instance);

    /// <summary>Applies migrations up to and including <paramref name="targetMigration"/>, and no further.</summary>
    public static async Task MigrateToAsync(HarboraDbContext db, string targetMigration) =>
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);

    /// <summary>An open connection, for the statements that have to be raw SQL.</summary>
    public static async Task<NpgsqlConnection> ConnectAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>One scalar, for a catalogue question.</summary>
    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = await ConnectAsync(connectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Lower-case letters, digits and underscores only — a database name is an identifier.</summary>
    private static string Sanitise(string label) =>
        new(label.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
}

/// <summary>
/// One container for the assembly, and therefore one collection. Serial rather than parallel not
/// because the databases collide — each test that writes has its own — but because the two shared
/// databases are built lazily, and a lane whose cost is dominated by container start has nothing to
/// gain from racing.
/// </summary>
[CollectionDefinition(PostgresLane.Collection, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresLane>;

/// <summary>
/// A fact that runs only where a Docker daemon is reachable, and reports an explicit skip reason
/// where one is not.
///
/// <para>
/// The same bargain <c>DockerFactAttribute</c> in <c>tests/Harbora.NodeAgent.Tests</c> makes, and
/// deliberately the same shape: a test that quietly succeeds because it did nothing is worse than
/// one that says it did not run. The probe happens once per test session.
/// </para>
///
/// <para>
/// It asks Testcontainers itself rather than opening the socket by hand, so the endpoint it reports
/// is the endpoint the container would actually have been started on — including whatever
/// <c>DOCKER_HOST</c>, Docker Desktop or a <c>~/.testcontainers.properties</c> file says about it.
/// A skip reason an operator cannot act on is barely better than no skip reason.
/// </para>
///
/// <para>
/// CI refuses the skip: the postgres job in <c>.github/workflows/ci.yml</c> reads the TRX and fails
/// when the suite did not really run, exactly as the consolidated workflow does for the node-agent
/// container tests. A lane that skips forever proves nothing and looks green doing it.
/// </para>
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Unavailable = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Null when a daemon answered, or the reason it did not. Read by <see cref="PostgresLane"/>.</summary>
    public static string? SkipReason => Unavailable.Value;

    public PostgresFactAttribute()
    {
        if (Unavailable.Value is { } reason) Skip = reason;
    }

    private static string? Probe()
    {
        try
        {
            // Build() resolves the endpoint and connects to it; it does not start anything. That is
            // the whole probe, and it is Testcontainers' own resolution rather than a guess at it.
            _ = new PostgreSqlBuilder(PostgresLane.Image).Build();
            return null;
        }
        catch (DockerUnavailableException e)
        {
            return "No reachable Docker daemon, so the PostgreSQL lane did not run. " +
                   "Start Docker and re-run: dotnet test tests/Harbora.Postgres.Tests/Harbora.Postgres.Tests.csproj. " +
                   $"Testcontainers said: {OneLine(e.Message)}";
        }
        catch (Exception e)
        {
            return $"Testcontainers could not prepare a {PostgresLane.Image} container " +
                   $"({e.GetType().Name}: {OneLine(e.Message)}). " +
                   "This lane is the only one that needs Docker; CI runs it on a host that has it.";
        }
    }

    /// <summary>
    /// Testcontainers puts the endpoint it actually tried on the third line of its message, which is
    /// the one piece an operator needs. A skip reason is a single line in a test report, so this
    /// flattens rather than truncates.
    /// </summary>
    private static string OneLine(string message) =>
        string.Join(' ', message.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0));
}
