using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Harbora.Data;

/// <summary>
/// Lets `dotnet ef migrations` create the context without booting the web host.
/// Connection string can be overridden via HARBORA_DB env var; falls back to a local default.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HarboraDbContext>
{
    public HarboraDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("HARBORA_DB")
                   ?? "Host=localhost;Port=5432;Database=harbora;Username=harbora;Password=harbora";

        var options = new DbContextOptionsBuilder<HarboraDbContext>()
            .UseNpgsql(conn, o => o.MigrationsAssembly(typeof(HarboraDbContext).Assembly.FullName))
            .Options;

        return new HarboraDbContext(options);
    }
}
