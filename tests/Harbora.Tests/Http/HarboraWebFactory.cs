using System.Net.Http.Headers;
using System.Reflection;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Harbora.Tests;

/// <summary>
/// Boots <c>src/Harbora.Web/Program.cs</c> — the real one, in its shipped order — in-process, so a
/// test can make an actual HTTP request through it.
///
/// <para>
/// Every other test in this project constructs a controller and calls a method on it. That proves
/// what the method decides and nothing about what a request does: authentication, the capability
/// policies, antiforgery, the rate limiters, the setup guard, localisation, status-code re-execution
/// and Razor itself all live outside the method. A controller test cannot fail when one of them is
/// deleted. This can.
/// </para>
///
/// <para><b>What is substituted, and why each substitution is safe.</b></para>
/// <list type="bullet">
/// <item><b>The database</b> becomes EF InMemory. This machine has no PostgreSQL; the Postgres-shaped
/// lane is HARBORA-0020. Nothing under test here is a SQL behaviour — see the report for the list of
/// properties this choice puts out of reach.</item>
/// <item><b>The background workers</b> are removed. Two dozen hosted services start with the host,
/// and they talk to Docker, registries and GitHub. None of them is on a request path, and starting
/// them would make the harness both slow and flaky — the two reasons a test suite gets deleted.</item>
/// <item><b>The deployment engine</b> becomes a recorder. Queuing a real deployment builds images.
/// What is under test is who is allowed to ask for one, which is decided before the engine is
/// reached.</item>
/// <item><b>The client IP</b> is taken from a header this harness adds. The per-IP rate limiters
/// partition on <c>Connection.RemoteIpAddress</c>, which <c>TestServer</c> leaves null — so without
/// this every test in the process would share one bucket and poison the next. Giving each test its
/// own address is what separate clients are.</item>
/// </list>
/// </summary>
public sealed class HarboraWebFactory : WebApplicationFactory<Program>
{
    /// <summary>Header carrying the address the request should appear to come from.</summary>
    public const string RemoteIpHeader = "X-Harbora-Test-Remote-Ip";

    private static readonly string MasterKey =
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private readonly string _databaseName = "harbora-http-" + Guid.NewGuid().ToString("N");
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "harbora-http-" + Guid.NewGuid().ToString("N"));

    /// <summary>Records what the API and the UI asked the deploy engine to do.</summary>
    public RecordingDeploymentEngine Deployments { get; } = new();

    /// <summary>
    /// How many background workers were taken out. Asserted by the smoke test: if the registration
    /// idiom in <c>DependencyInjection</c> ever changes shape, this harness must not quietly start
    /// running Docker jobs during a unit-test run.
    /// </summary>
    public int RemovedBackgroundWorkers { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_stateDirectory);

        // Razor views, wwwroot and the Vite manifest are all found relative to the content root, and
        // the test runner's output directory has none of them.
        builder.UseContentRoot(TestPaths.WebRoot);

        // Production, not WebApplicationFactory's Development default. Program.cs branches on the
        // environment three times — the exception handler, HSTS, and the auth cookie's SecurePolicy
        // — and a harness that tested the other branch would be proving a pipeline nobody runs. The
        // clients below speak https so the always-secure cookie is satisfied.
        builder.UseEnvironment(Environments.Production);

        // A real key, generated per run: outside Development the panel refuses to start without one,
        // and refusing is the point of that check.
        builder.UseSetting("Harbora:MasterKey", MasterKey);
        builder.UseSetting("Harbora:DataProtectionKeysPath", Path.Combine(_stateDirectory, "keys"));
        builder.UseSetting("Harbora:WorkDir", Path.Combine(_stateDirectory, "work"));

        builder.ConfigureTestServices(services =>
        {
            UseInMemoryDatabase(services);
            RemovedBackgroundWorkers = RemoveBackgroundWorkers(services);

            services.RemoveAll<IDeploymentEngine>();
            services.AddSingleton<IDeploymentEngine>(Deployments);

            services.AddSingleton<IStartupFilter, RemoteIpFromHeader>();
        });
    }

    // ---- clients -------------------------------------------------------------------------------

    /// <summary>
    /// A client whose requests arrive from <paramref name="remoteIp"/>. Give every test its own
    /// address: the rate limiters are per-IP and their windows outlive a single test.
    /// The addresses used here come from TEST-NET-3 (203.0.113.0/24), which is outside every network
    /// <see cref="TrustedProxySetup.DefaultProxyNetworks"/> trusts — so a forwarded header cannot be
    /// believed and the address the limiter sees is the one the test asked for.
    /// </summary>
    public HttpClient ClientFrom(string remoteIp, bool followRedirects = false)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = followRedirects,
            // https, so the auth cookie's SecurePolicy is satisfied under either environment.
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });
        client.DefaultRequestHeaders.Add(RemoteIpHeader, remoteIp);
        return client;
    }

    /// <summary>The same, presenting an API token — the CLI's shape of request.</summary>
    public HttpClient BearerClientFrom(string remoteIp, string token)
    {
        var client = ClientFrom(remoteIp);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---- fixture data --------------------------------------------------------------------------

    /// <summary>Writes rows through a request-less scope, which is unscoped and so sees every tenant.</summary>
    public void Seed(Action<HarboraDbContext> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        seed(db);
        db.SaveChanges();
    }

    /// <summary>Reads back through the same unscoped route, for asserting on what a request wrote.</summary>
    public T Read<T>(Func<HarboraDbContext, T> read)
    {
        using var scope = Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<HarboraDbContext>());
    }

    /// <summary>Resolves a platform service the way the panel would.</summary>
    public T Resolve<T>() where T : notnull
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// A user who can sign in, with the panel's own password hasher — a hand-written hash would make
    /// every login test pass or fail for a reason that is not the login.
    /// </summary>
    public User GivenUser(Guid workspaceId, string email, SystemRole role, string password = TestPassword,
        bool scopedToProjects = false)
    {
        var hasher = Resolve<IPasswordHasher>();
        var user = new User
        {
            Email = email.ToLowerInvariant(),
            DisplayName = email,
            PasswordHash = hasher.Hash(password),
            Role = role,
            ScopedToProjects = scopedToProjects,
            IsActive = true
        };

        Seed(db =>
        {
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = workspaceId,
                UserId = user.Id,
                Role = role is SystemRole.Owner or SystemRole.Admin ? WorkspaceRole.Admin : WorkspaceRole.Member
            });
        });

        return user;
    }

    /// <summary>The plaintext of a CLI token for <paramref name="userId"/>, issued the way the panel issues one.</summary>
    public string GivenApiToken(Guid userId, string name = "test")
    {
        var issued = Resolve<ITokenService>().Issue(userId, name, TokenType.Cli, null);
        Seed(db => db.ApiTokens.Add(new ApiToken
        {
            UserId = userId,
            Name = name,
            Prefix = issued.Prefix,
            TokenHash = issued.Hash,
            Type = TokenType.Cli
        }));
        return issued.PlaintextToken;
    }

    /// <summary>The password every seeded user gets, unless a test says otherwise.</summary>
    public const string TestPassword = "correct-horse-battery-staple";

    // ---- setup guard ---------------------------------------------------------------------------

    /// <summary>
    /// Clears <c>SetupGuardMiddleware</c>'s process-wide "setup is done" cache.
    ///
    /// The flag is a private static, deliberately: after first run the guard must not query the
    /// database on every request. That makes it the one piece of pipeline state a test cannot reach
    /// by owning its own host, so it is reached by reflection instead. Tests that touch it live in
    /// the same non-parallel collection as everything else here.
    /// </summary>
    public static void ForgetSetupCompleted() =>
        typeof(SetupGuardMiddleware)
            .GetField("_setupCompleted", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, false);

    // ---- wiring --------------------------------------------------------------------------------

    private void UseInMemoryDatabase(IServiceCollection services)
    {
        // EF 9 onwards keeps the provider choice in IDbContextOptionsConfiguration, and a second
        // AddDbContext appends rather than replaces — leaving Npgsql and InMemory both configured,
        // which fails at first use with "only a single database provider can be registered".
        services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<HarboraDbContext>>();
        services.RemoveAll<DbContextOptions<HarboraDbContext>>();
        services.RemoveAll<DbContextOptions>();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_databaseName));
    }

    /// <summary>
    /// Takes out every hosted service Harbora itself registers, and nothing else — the host's own
    /// <c>GenericWebHostService</c> is what serves the requests, so a blanket removal would leave a
    /// server that never listens.
    /// </summary>
    private static int RemoveBackgroundWorkers(IServiceCollection services)
    {
        var doomed = services
            .Where(d => !d.IsKeyedService
                        && d.ServiceType == typeof(IHostedService)
                        && IsHarbora(ImplementationTypeOf(d)))
            .ToList();

        foreach (var descriptor in doomed) services.Remove(descriptor);
        return doomed.Count;
    }

    private static bool IsHarbora(Type? type) =>
        type?.Assembly.GetName().Name?.StartsWith("Harbora", StringComparison.Ordinal) == true;

    /// <summary>
    /// The concrete type behind a descriptor. <c>AddHostedService&lt;T&gt;(factory)</c> leaves
    /// <c>ImplementationType</c> null, so the delegate's return type is read instead — the registry
    /// discovery service is registered that way so the admin page can also resolve it directly.
    /// </summary>
    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is { } type) return type;
        if (descriptor.ImplementationInstance is { } instance) return instance.GetType();

        var factory = descriptor.ImplementationFactory?.GetType();
        return factory is { IsGenericType: true } && factory.GetGenericArguments() is { Length: 2 } arguments
            ? arguments[1]
            : null;
    }

    /// <summary>
    /// Puts the header's address on the connection before anything reads it. Registered as an
    /// <see cref="IStartupFilter"/> so it runs ahead of the whole shipped pipeline, including
    /// <c>UseForwardedHeaders</c> and <c>UseRateLimiter</c>.
    /// </summary>
    private sealed class RemoteIpFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var value) &&
                    System.Net.IPAddress.TryParse(value.ToString(), out var address))
                    context.Connection.RemoteIpAddress = address;

                await following();
            });

            next(app);
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, recursive: true); }
        catch (IOException) { /* a keyring file still open on Windows is not a test failure */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Stands in for the deploy engine. Every method answers the way a successful queue does and writes
/// down what it was asked, so a test can tell "the request was refused" from "the request was
/// allowed through and the engine declined".
/// </summary>
public sealed class RecordingDeploymentEngine : IDeploymentEngine
{
    private readonly List<DeploymentRequest> _queued = [];
    private readonly List<Guid> _cancelled = [];

    public IReadOnlyList<DeploymentRequest> Queued { get { lock (_queued) return _queued.ToList(); } }
    public IReadOnlyList<Guid> Cancelled { get { lock (_cancelled) return _cancelled.ToList(); } }

    /// <summary>Set to make the next queue attempt fail the way a rollback in flight does.</summary>
    public string? RefuseWith { get; set; }

    public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct)
    {
        if (RefuseWith is { } reason) throw new InvalidOperationException(reason);
        lock (_queued) _queued.Add(request);
        return Task.FromResult(Guid.CreateVersion7());
    }

    public Task CancelAsync(Guid deploymentId, CancellationToken ct)
    {
        lock (_cancelled) _cancelled.Add(deploymentId);
        return Task.CompletedTask;
    }
}
