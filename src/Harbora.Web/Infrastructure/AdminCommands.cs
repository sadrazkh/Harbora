using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Nodes;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
                "node-ca" => await NodeCaAsync(config),
                "environment-report" => await EnvironmentReportAsync(config),
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

    /// <summary>
    /// Print the node CA certificate, and nothing else, so it can be redirected into the file
    /// Traefik's mTLS configuration names.
    ///
    /// <para>
    /// Creating the CA when none exists is deliberate. The authority is otherwise minted on first
    /// enrollment, which is too late for the installer: Traefik must already trust the CA before the
    /// node router is in place, and a named TLS option Traefik cannot build falls back to the default
    /// one — which asks for no client certificate at all. Running this is the "first use" the
    /// authority is documented to be created on; it just happens during the install rather than
    /// during the first enrollment.
    /// </para>
    ///
    /// <para>
    /// Like every other verb here it builds what it needs by hand instead of asking a container, so
    /// it works while the panel refuses to start. Unlike the others it needs the master key, because
    /// the CA's private key is protected with it — and it says so rather than writing a CA nothing
    /// could ever decrypt.
    /// </para>
    /// </summary>
    private static async Task<int> NodeCaAsync(IConfiguration config)
    {
        var masterKey = Coalesce(config["Harbora:MasterKey"], Environment.GetEnvironmentVariable("HARBORA_MASTER_KEY"));

        if (string.IsNullOrWhiteSpace(masterKey))
            return Fail("HARBORA_MASTER_KEY is not set, so the node CA's private key could not be protected. Run: harbora fix-key");

        await using var db = Open(config);

        // NullLogger, not the console: this command's stdout is a certificate file.
        var authority = new NodeCertificateAuthority(
            db, new AesGcmSecretProtector(masterKey), NullLogger<NodeCertificateAuthority>.Instance);

        Console.Out.Write(CaPemForRedirect(await authority.GetCaCertificatePemAsync(CancellationToken.None)));
        return 0;
    }

    /// <summary>
    /// P1 of the app/environment management phase — "the report nobody has run". Read-only: answers
    /// whether it is safe to write the migration that makes <c>EnvironmentId</c> required, without
    /// writing anything itself. See <see cref="Projects.EnvironmentPlacementReport"/> for what each
    /// of the four sections means and why a non-zero first section is a bug report, not a backfill.
    /// </summary>
    private static async Task<int> EnvironmentReportAsync(IConfiguration config)
    {
        await using var db = Open(config);
        var report = await EnvironmentPlacementReport.BuildAsync(db);
        Console.Write(EnvironmentPlacementReport.Render(report));
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
              harbora node-ca                         Print the node CA (PEM) for Traefik's mTLS config
              harbora environment-report              Read-only: workloads with no environment, empty
                                                      environments, dual-network workloads, projectless
                                                      workspaces. Writes nothing; safe to run any time.

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
    /// The exact bytes <c>node-ca</c> puts on stdout. Kept separate because the whole value of the
    /// verb is that its output can be redirected into a file Traefik parses: a trailing blank line
    /// or a missing final newline is not a cosmetic difference there.
    /// </summary>
    public static string CaPemForRedirect(string pem) => pem.TrimEnd('\r', '\n') + "\n";

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
