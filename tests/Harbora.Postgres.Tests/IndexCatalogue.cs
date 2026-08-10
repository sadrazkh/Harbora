using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Harbora.Postgres.Tests;

/// <summary>
/// What Postgres says about an index, for the facts that assert on its shape rather than on its
/// behaviour.
///
/// <para>
/// Both kinds of fact are worth having and neither replaces the other. A behavioural fact — insert
/// twice, expect a refusal — is the one that would have caught a missing index, but it is silent
/// about an index that refuses the right rows for the wrong reason, and it cannot say anything at
/// all about a filter's far edge without seeding every row the filter excludes. Reading the
/// catalogue pins the definition itself, so a migration regenerated without one of its annotations
/// fails on the line that names the annotation instead of somewhere downstream.
/// </para>
///
/// <para>
/// Shared rather than copied per test class because the parsing below is the subtle part, and two
/// copies of a subtlety are two chances to fix only one of them.
/// </para>
/// </summary>
internal static class IndexCatalogue
{
    /// <summary>The <c>CREATE INDEX</c> Postgres would print for it, having reparsed it.</summary>
    public static async Task<string> DefinitionAsync(string connectionString, string index)
    {
        var definition = await PostgresLane.ScalarAsync<string>(connectionString,
            $"SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = '{index}'");

        definition.Should().NotBeNull($"the migration creates an index called {index}");
        return definition!;
    }

    /// <summary>
    /// Whether the index was built <c>NULLS NOT DISTINCT</c>, or null when there is no such index.
    ///
    /// <para>
    /// Read off <c>pg_index</c> rather than looked for in the printed definition, and that is the
    /// point of having it at all. The setting is one boolean column that has meant one thing since
    /// it was added in PostgreSQL 15, whereas the text around it is reprinted by whatever version is
    /// running — so a substring search is a test that can go red on an upgrade while the index is
    /// exactly right, and, worse, can go green against a spelling that never appears because the
    /// assertion was only ever "does not contain".
    /// </para>
    ///
    /// <para>
    /// Nullable so that "there is no index by that name" is a different answer from "there is one
    /// and it treats nulls as distinct". Those want different fixes and read identically as
    /// <c>false</c>.
    /// </para>
    /// </summary>
    public static Task<bool?> TreatsMissingValuesAsEqualAsync(string connectionString, string index) =>
        PostgresLane.ScalarAsync<bool?>(connectionString,
            $"""
             SELECT i.indnullsnotdistinct
             FROM pg_index i
             JOIN pg_class c ON c.oid = i.indexrelid
             JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public' AND c.relname = '{index}'
             """);

    /// <summary>
    /// The integers a partial index's filter admits, with the syntax thrown away.
    ///
    /// <para>
    /// Postgres does not hand back the <c>WHERE "Status" IN (0, 1, 2)</c> the migration wrote. It
    /// reprints the parsed predicate, which over the versions has been <c>= ANY (ARRAY[0, 1, 2])</c>
    /// and <c>= ANY ('{0,1,2}'::integer[])</c>, and could be a chain of <c>OR</c>s tomorrow. Pinning
    /// any one of those spellings would be a test that breaks on a Postgres upgrade while the index
    /// is still exactly right. So this reads the numbers out and ignores everything around them:
    /// every rendering of a membership test over integers prints those integers, and prints no
    /// others.
    /// </para>
    ///
    /// <para>
    /// Digits that touch a letter are not values — they are the tail of a type name like
    /// <c>int4</c>, which some renderings of a cast reach for. Excluding them costs nothing if
    /// Postgres never emits one, and is the difference between a green lane and an afternoon spent
    /// on a filter that was correct all along if it does. This lane runs only where a Docker daemon
    /// answers, and on a branch that has not reached <c>master</c> that is CI on a pull request and
    /// nowhere else — so which spelling PostgreSQL 16 actually prints is an argument until the lane
    /// has run. Every caller prints the definition it read for exactly that reason: the first
    /// failure should arrive with the text that caused it.
    /// </para>
    ///
    /// <para>
    /// What it buys over asking whether the definition merely mentions <c>WHERE</c> and the column:
    /// that pair is equally happy with a filter over entirely the wrong values, which is an index
    /// that guards rows nobody was worried about while the ones that mattered walk past. Which rows
    /// the filter covers is the whole of what the filter is.
    /// </para>
    /// </summary>
    /// <param name="because">Why this index having no filter at all would be wrong. It differs per
    /// index — an unfiltered index over-refuses, and what that costs is the caller's to say.</param>
    public static IReadOnlyList<int> FilteredValues(string definition, string because)
    {
        var filter = definition.IndexOf("WHERE", StringComparison.Ordinal);

        filter.Should().BeGreaterThanOrEqualTo(0, because);

        return Regex.Matches(definition[filter..], "(?<![A-Za-z0-9_])[0-9]+(?![A-Za-z0-9_])")
            .Select(match => int.Parse(match.Value, CultureInfo.InvariantCulture))
            .ToList();
    }
}
