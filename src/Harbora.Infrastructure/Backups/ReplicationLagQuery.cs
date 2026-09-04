using System.Globalization;
using Harbora.Infrastructure.Services;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Asks a running PostgreSQL replica, in its own words, when it last replayed a transaction (3.2,
/// round-2 market-gaps plan) — the one fact <see cref="ReplicationLagMonitor"/> turns into a lag
/// figure, and the only question this asks: <c>pg_last_xact_replay_timestamp()</c> is a standby-side
/// function with no meaning on a primary, so this is run against the replica's own connection, never
/// the primary's.
///
/// <para>
/// Deliberately returns a raw timestamp rather than computing the lag inside SQL
/// (<c>now() - pg_last_xact_replay_timestamp()</c> would be one line shorter). Two reasons: the
/// subtraction is trivial and more legible in C# next to the clock every other "how long ago" figure
/// on this platform already reads from <see cref="Harbora.Application.Abstractions.ISystemClock"/>
/// rather than the database server's own idea of now; and a NULL timestamp is unambiguous evidence
/// that PostgreSQL itself does not know yet (the standby has replayed nothing since it last
/// restarted), whereas a NULL or garbage <em>interval</em> string would need its own, separate "is
/// this actually unknown" parse.
/// </para>
/// </summary>
public static class ReplicationLagQuery
{
    /// <summary>The client image and argv — a client for this engine, matching the pattern every
    /// other <c>psql</c> one-off on this platform already follows (<see cref="DatabaseGrantSql"/>).</summary>
    public static IReadOnlyList<string> Command(string host, int port, string adminUser, string database) =>
        [
            "psql", "-h", host, "-p", port.ToString(), "-U", adminUser, "-d", database,
            // -t (no headers/footers) -A (unaligned): the whole point is a bare value on one line,
            // never a table this parser would have to strip.
            "-t", "-A", "-c", "SELECT pg_last_xact_replay_timestamp();"
        ];

    public static IReadOnlyDictionary<string, string> Environment(string adminPassword) =>
        new Dictionary<string, string> { ["PGPASSWORD"] = adminPassword };

    /// <summary>
    /// Turns the client's raw stdout into a moment, or null.
    ///
    /// <para>
    /// Null covers two cases this deliberately does not tell apart from each other: <c>psql -t -A</c>
    /// prints an empty line for a real SQL NULL (the "has not replayed anything yet" case
    /// <see cref="Harbora.Domain.Services.ReplicationLagStatus.LagSeconds"/>'s own doc describes), and
    /// anything this parser does not recognise is treated the same honest way — never guessed at.
    /// A caller that already knows the query itself failed (a non-zero exit) has that as a separate,
    /// stronger signal (<see cref="Harbora.Domain.Services.ReplicationLagStatus.LastError"/>); this
    /// only ever answers "did the client's own output name a moment".
    /// </para>
    /// </summary>
    public static DateTimeOffset? ParseReplayTimestamp(string rawOutput)
    {
        var line = Deployments.LogText.Clean(rawOutput).Trim();
        if (line.Length == 0) return null;

        // PostgreSQL's own default output for a timestamptz, e.g. "2026-09-04 10:15:23.123456+00" —
        // parsed with the round-trip-friendly styles .NET actually accepts for it rather than a
        // hand-rolled format string that would silently reject a locale or precision this platform
        // has not seen in testing. A string this cannot parse is exactly as unknown as an empty one —
        // never guessed at, never coerced to "now" or to zero.
        return DateTimeOffset.TryParse(
            line, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}
