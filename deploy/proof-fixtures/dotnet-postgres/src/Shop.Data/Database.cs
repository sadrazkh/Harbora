using Npgsql;

namespace Shop.Data;

/// <summary>What the database said when it was asked, and where the connection string pointed. Never its password.</summary>
public sealed record Answer(bool Ok, int? One, string? Host, string? Error);

public static class Database
{
    public static async Task<Answer> SelectOneAsync(string? connectionString)
    {
        string? host = null;
        try
        {
            var parsed = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 5 };
            host = parsed.Host;

            await using var connection = new NpgsqlConnection(parsed.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            return new Answer(true, (int?)await command.ExecuteScalarAsync(), host, null);
        }
        catch (Exception error)
        {
            return new Answer(false, null, host, error.Message);
        }
    }
}
