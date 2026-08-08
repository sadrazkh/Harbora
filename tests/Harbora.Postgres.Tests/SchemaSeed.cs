using Npgsql;

namespace Harbora.Postgres.Tests;

/// <summary>
/// Writes rows into a schema that is <b>older than the model</b>.
///
/// <para>
/// The upgrade tests have to put rows into the database as it stood at the previous release, then
/// migrate. EF cannot do that — its model is the head model, so every INSERT it generates names
/// columns the old schema has not got yet. So these are raw statements.
/// </para>
///
/// <para>
/// Only the columns a test's assertion depends on are given values. Everything else that the table
/// insists on is filled from <c>information_schema</c> with a neutral value of the right type. That
/// is not laziness: a seed row that spells out forty columns says all forty matter, and the next
/// person cannot tell which three the migration under test actually reads. It also means a column
/// added to <c>Apps</c> next month does not break a test about backup snapshots.
/// </para>
///
/// <para>
/// It fills only what it must — <c>NOT NULL</c> with no default — so a nullable column stays null,
/// which is the state an upgrading install is most likely to be in.
/// </para>
/// </summary>
internal sealed class SchemaSeed(NpgsqlConnection connection)
{
    private readonly Dictionary<string, IReadOnlyList<Column>> _tables = [];

    /// <summary>Inserts one row, naming only the columns that matter to the caller.</summary>
    public async Task InsertAsync(string table, params (string Column, object? Value)[] values)
    {
        var columns = await DescribeAsync(table);

        foreach (var (column, _) in values)
            if (columns.All(c => c.Name != column))
                throw new InvalidOperationException(
                    $"\"{table}\" has no column \"{column}\" at this point in the migration history. " +
                    $"It has: {string.Join(", ", columns.Select(c => c.Name))}.");

        var supplied = values.Select(v => v.Column).ToHashSet(StringComparer.Ordinal);

        var names = new List<string>();
        var expressions = new List<string>();

        for (var i = 0; i < values.Length; i++)
        {
            names.Add($"\"{values[i].Column}\"");
            expressions.Add($"@p{i}");
        }

        foreach (var column in columns)
        {
            if (supplied.Contains(column.Name) || column.Nullable || column.HasDefault) continue;
            names.Add($"\"{column.Name}\"");
            expressions.Add(Neutral(table, column));
        }

        await using var command = new NpgsqlCommand(
            $"INSERT INTO \"{table}\" ({string.Join(", ", names)}) VALUES ({string.Join(", ", expressions)})",
            connection);

        for (var i = 0; i < values.Length; i++)
            command.Parameters.AddWithValue($"p{i}", values[i].Value ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<Column>> DescribeAsync(string table)
    {
        if (_tables.TryGetValue(table, out var cached)) return cached;

        await using var command = new NpgsqlCommand(
            """
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
            ORDER BY ordinal_position
            """, connection);
        command.Parameters.AddWithValue("table", table);

        var columns = new List<Column>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                columns.Add(new Column(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2) == "YES",
                    !await reader.IsDBNullAsync(3)));

        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"There is no table \"{table}\" in this database. Has the migration that creates it run yet?");

        _tables[table] = columns;
        return columns;
    }

    /// <summary>
    /// A value of the right type that means nothing. The all-zeros uuid is safe because every
    /// <c>NOT NULL</c> foreign key in the tables these tests touch is supplied by name — an unhandled
    /// one would fail here as a foreign-key violation naming the column, which is the right way to
    /// find out.
    /// </summary>
    private static string Neutral(string table, Column column) => column.DataType switch
    {
        "uuid" => "'00000000-0000-0000-0000-000000000000'::uuid",
        "character varying" or "text" or "character" => "''",
        "integer" or "bigint" or "smallint" => "0",
        "double precision" or "real" or "numeric" => "0",
        "boolean" => "false",
        "timestamp with time zone" or "timestamp without time zone" => "now()",
        "date" => "now()::date",
        "jsonb" or "json" => "'{}'",
        "bytea" => "''::bytea",
        "interval" => "'0 seconds'::interval",
        "ARRAY" => "'{}'",
        _ => throw new NotSupportedException(
            $"\"{table}\".\"{column.Name}\" is NOT NULL and of type {column.DataType}, which this seed " +
            "has no neutral value for. Add one, or give the column a value at the call site.")
    };

    private sealed record Column(string Name, string DataType, bool Nullable, bool HasDefault);
}
