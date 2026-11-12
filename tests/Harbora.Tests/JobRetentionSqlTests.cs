using System.Data.Common;
using FluentAssertions;
using Harbora.Data;
using Harbora.Infrastructure.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The statements the job sweep sends to PostgreSQL, read with no database in the room.
///
/// <para>
/// The in-memory provider every other retention test uses has no <c>ExecuteDeleteAsync</c>, so the
/// sweeper falls back to loading the rows there, and nothing in those tests ever produces the SQL.
/// For the job queue the SQL is the point: a batch has to become a bounded statement, and a
/// <c>Take</c> that Npgsql dropped, or could not translate inside a <c>DELETE</c>, would turn the
/// first pass back into the one enormous statement it exists to avoid. So the real Npgsql provider
/// builds each statement here, and an interceptor answers in place of the server: the connection is
/// never opened and each statement reports that it deleted nothing, which ends each status's loop
/// after one statement.
/// </para>
/// <para>
/// What this cannot show is how PostgreSQL plans or runs the statement on a table that size; the
/// Postgres lane runs it against rows (<c>JobRetentionPostgresTests</c>).
/// </para>
/// </summary>
public class JobRetentionSqlTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class NoServer : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override InterceptionResult ConnectionClosing(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed record Sent(string Sql, IReadOnlyDictionary<string, object?> Parameters);

    private sealed class Statements : DbCommandInterceptor
    {
        public List<Sent> Sent { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new Sent(command.CommandText, command.Parameters.Cast<DbParameter>()
                .ToDictionary(p => p.ParameterName, p => p.Value)));
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
        }
    }

    private static async Task<List<Sent>> StatementsTheSweepSendsAsync(int batchSize)
    {
        var statements = new Statements();
        await using var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused")
            .AddInterceptors(new NoServer(), statements)
            .Options);

        var deleted = await DataRetentionSweeper.SweepJobsAsync(
            db, new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.Zero), batchSize, CancellationToken.None);

        deleted.Should().Be(0, "the interceptor answers for the server, and it deleted nothing");
        return statements.Sent;
    }

    [Fact]
    public async Task Each_statement_deletes_a_bounded_batch_of_one_settled_status_oldest_first()
    {
        var sent = (await StatementsTheSweepSendsAsync(batchSize: 10_000)).Select(s => s.Sql).ToList();
        // Written out so a reader of the test log can see the statement itself.
        foreach (var sql in sent) output.WriteLine(sql);

        sent.Should().HaveCount(3, "one statement per settled status, each of which came back short");
        sent.Should().OnlyContain(sql => sql.Contains("DELETE FROM \"Jobs\""));
        sent.Should().OnlyContain(sql => sql.Contains("LIMIT"),
            "a batch that reached the server without its limit is the single enormous statement again");
        sent.Should().OnlyContain(sql => sql.Contains("ORDER BY") && sql.Contains("\"CreatedAt\""),
            "oldest first, in the order the (Status, CreatedAt) index already holds them");
        sent.Should().OnlyContain(sql => sql.Contains("\"FinishedAt\""),
            "a job is due only once its finish is past the cutoff too");
    }

    [Fact]
    public async Task No_statement_can_reach_a_job_that_is_still_queued_or_running()
    {
        var sent = await StatementsTheSweepSendsAsync(batchSize: 10_000);

        // Statuses are persisted by value. The rule names the three settled ones as constants, and the
        // batch narrows to one of them through a parameter; neither may ever be a live status.
        int[] live = [(int)Harbora.Domain.Jobs.JobStatus.Pending, (int)Harbora.Domain.Jobs.JobStatus.Running];
        var narrowedTo = new List<int>();
        foreach (var (sql, parameters) in sent)
        {
            var constants = System.Text.RegularExpressions.Regex
                .Matches(sql, @"""Status""\s*(?:=|IN)\s*\(?([0-9][0-9, ]*)\)?")
                .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Select(int.Parse)
                .ToList();
            constants.Should().BeEquivalentTo([2, 3, 4], "the rule's own statuses reach the statement: {0}", sql);

            var status = parameters.Where(p => p.Key.Contains("status", StringComparison.OrdinalIgnoreCase))
                .Should().ContainSingle("the batch's own status is a parameter: {0}", sql).Subject.Value;
            narrowedTo.Add(Convert.ToInt32(status));
        }

        narrowedTo.Should().Equal(2, 3, 4);
        narrowedTo.Should().NotContain(live);
    }

    [Fact]
    public async Task The_batch_size_is_what_bounds_each_statement()
    {
        var sent = await StatementsTheSweepSendsAsync(batchSize: 250);

        sent.Should().OnlyContain(s => s.Parameters.Values.Any(v => Equals(v, 250)),
            "the LIMIT is the configured batch, not something the provider chose");
    }
}
