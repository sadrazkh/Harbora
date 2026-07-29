using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Break-glass admin commands, run as <c>dotnet Harbora.Web.dll admin …</c> (normally through the
/// <c>harbora</c> wrapper on the host).
///
/// Deliberately does NOT build the web host or call <c>AddHarboraInfrastructure</c>. The situations
/// these commands exist for are exactly the ones where the app refuses to start — a missing master
/// key, an unreachable database, a lost admin password. A recovery tool that needs a healthy app is
/// useless precisely when you need it, so this path touches only the DbContext and the password
/// hasher.
/// </summary>
public static class AdminCommands
{
    public const string Verb = "admin";

    public static async Task<int> RunAsync(string[] args, IConfiguration config)
    {
        var command = args.Length > 1 ? args[1].ToLowerInvariant() : "help";

        try
        {
            return command switch
            {
                "info" => await InfoAsync(config),
                "users" => await UsersAsync(config),
                "reset-password" => await ResetPasswordAsync(args, config),
                "make-owner" => await MakeOwnerAsync(args, config),
                "unlock" => await UnlockAsync(args, config),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✗ {ex.Message}");
            // A connection failure is the single most common cause; name it rather than dumping a stack.
            if (ex is Npgsql.NpgsqlException or InvalidOperationException)
                Console.Error.WriteLine("  Check that the database is running and reachable: harbora doctor");
            return 1;
        }
    }

    // ---- commands ----

    /// <summary>What the panel is actually configured with — the first thing to check when locked out.</summary>
    private static async Task<int> InfoAsync(IConfiguration config)
    {
        Console.WriteLine("Harbora — current configuration");
        Console.WriteLine("────────────────────────────────────────");

        var masterKey = Coalesce(config["Harbora:MasterKey"], Environment.GetEnvironmentVariable("HARBORA_MASTER_KEY"));
        Console.WriteLine($"Master key            : {AdminDiagnostics.DescribeMasterKey(masterKey)}");
        Console.WriteLine($"Panel domain          : {Env("PANEL_DOMAIN")}");
        Console.WriteLine($"Apps root domain      : {Env("ROOT_DOMAIN")}");
        Console.WriteLine($"Environment           : {Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}");
        Console.WriteLine($"Database              : {AdminDiagnostics.RedactConnectionString(config.GetConnectionString("Postgres"))}");

        await using var db = Open(config);
        var canConnect = await db.Database.CanConnectAsync();
        Console.WriteLine($"Database reachable    : {(canConnect ? "yes" : "NO")}");
        if (!canConnect) return 1;

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        Console.WriteLine($"Pending migrations    : {(pending.Count == 0 ? "none" : string.Join(", ", pending))}");
        Console.WriteLine($"Users                 : {await db.Users.CountAsync()}");
        Console.WriteLine($"Workspaces            : {await db.Workspaces.CountAsync()}");
        Console.WriteLine($"Apps                  : {await db.Apps.IgnoreQueryFilters().CountAsync()}");

        var owner = await db.Users.Where(u => u.Role == SystemRole.Owner)
            .OrderBy(u => u.CreatedAt).Select(u => u.Email).FirstOrDefaultAsync();
        Console.WriteLine($"Owner account         : {owner ?? "(none — setup never completed)"}");

        Console.WriteLine();
        Console.WriteLine("Sign in at: https://" + (Env("PANEL_DOMAIN") is var d && d != "(not set)" ? d : "<panel domain>"));
        return 0;
    }

    private static async Task<int> UsersAsync(IConfiguration config)
    {
        await using var db = Open(config);
        var users = await db.Users.OrderBy(u => u.CreatedAt).ToListAsync();
        if (users.Count == 0)
        {
            Console.WriteLine("No users exist yet — open the panel and complete the setup wizard.");
            return 0;
        }

        Console.WriteLine($"{"EMAIL",-38} {"ROLE",-10} {"ACTIVE",-7} CREATED");
        foreach (var u in users)
            Console.WriteLine($"{u.Email,-38} {u.Role,-10} {(u.IsActive ? "yes" : "no"),-7} {u.CreatedAt:yyyy-MM-dd}");
        return 0;
    }

    /// <summary>
    /// Sets a new password for an existing account. The whole point of the tool: getting back in
    /// without a working UI or a working email flow.
    /// </summary>
    private static async Task<int> ResetPasswordAsync(string[] args, IConfiguration config)
    {
        var email = Arg(args, "--email");
        var password = Arg(args, "--password");

        await using var db = Open(config);

        if (string.IsNullOrWhiteSpace(email))
        {
            // Convenience for the common single-owner install.
            email = await db.Users.Where(u => u.Role == SystemRole.Owner)
                .OrderBy(u => u.CreatedAt).Select(u => u.Email).FirstOrDefaultAsync();
            if (email is null) return Fail("No owner account exists. Pass --email, or complete setup in the panel first.");
            Console.WriteLine($"No --email given; using the owner account: {email}");
        }

        if (string.IsNullOrWhiteSpace(password))
            password = Prompt("New password (min 8 chars): ");
        if (password is null || password.Length < 8)
            return Fail("Password must be at least 8 characters.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null) return Fail($"No user with email '{email}'. Run: harbora users");

        user.PasswordHash = new Pbkdf2PasswordHasher().Hash(password);
        // A locked-out admin usually also needs the account re-enabled.
        user.IsActive = true;
        await db.SaveChangesAsync();

        Console.WriteLine($"✓ Password reset for {user.Email} (role {user.Role}).");
        Console.WriteLine("  Sign in now, then change it from Settings.");
        return 0;
    }

    /// <summary>Promotes an account to Owner — for when the only owner was deleted or demoted.</summary>
    private static async Task<int> MakeOwnerAsync(string[] args, IConfiguration config)
    {
        var email = Arg(args, "--email") ?? Prompt("Email to promote: ");
        if (string.IsNullOrWhiteSpace(email)) return Fail("An --email is required.");

        await using var db = Open(config);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null) return Fail($"No user with email '{email}'. Run: harbora users");

        user.Role = SystemRole.Owner;
        user.IsActive = true;
        await db.SaveChangesAsync();

        Console.WriteLine($"✓ {user.Email} is now an Owner.");
        return 0;
    }

    /// <summary>Re-enables a deactivated account without touching its password.</summary>
    private static async Task<int> UnlockAsync(string[] args, IConfiguration config)
    {
        var email = Arg(args, "--email") ?? Prompt("Email to unlock: ");
        if (string.IsNullOrWhiteSpace(email)) return Fail("An --email is required.");

        await using var db = Open(config);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null) return Fail($"No user with email '{email}'.");

        user.IsActive = true;
        await db.SaveChangesAsync();
        Console.WriteLine($"✓ {user.Email} re-enabled.");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            Harbora admin commands (run on the server)

              harbora info                            Show configuration, DB state and the owner account
              harbora users                           List accounts and their roles
              harbora reset-password [--email X]      Set a new password (prompts if omitted)
                                     [--password Y]
              harbora make-owner --email X            Promote an account to Owner
              harbora unlock --email X                Re-enable a deactivated account

            These work even when the panel refuses to start.
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"✗ Unknown admin command '{command}'.");
        Help();
        return 1;
    }

    // ---- helpers ----

    /// <summary>
    /// Minimal DbContext — system-scoped, no global filters in the way, and none of the
    /// infrastructure that would refuse to start without a master key.
    /// </summary>
    private static HarboraDbContext Open(IConfiguration config)
    {
        var connection = config.GetConnectionString("Postgres")
                         ?? "Host=localhost;Port=5432;Database=harbora;Username=harbora;Password=harbora";
        return new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseNpgsql(connection).Options);
    }

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : "(not set)";

    private static string? Arg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string? Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine()?.Trim();
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"✗ {message}");
        return 1;
    }

    private static string? Coalesce(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
