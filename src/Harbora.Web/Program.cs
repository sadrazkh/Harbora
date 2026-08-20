using System.Globalization;
using System.Threading.RateLimiting;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure;
using Harbora.Infrastructure.Backups;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Modules.Sync.Infrastructure;
using Harbora.Web.Data;
using Harbora.Web.Infrastructure;
using Harbora.Web.Realtime;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Harbora is deployed as a container and its operational log is stdout/stderr. The Windows host
// adds EventLog by default; under an ordinary (non-admin) account that provider can throw while
// trying to create/open the ".NET Runtime" source, turning an otherwise harmless EF warning into a
// fatal startup exception. Console is portable, works with `docker logs`, and never needs host
// registry/Event Log privileges.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// Break-glass admin commands run before anything else is configured, so they still work when the
// app itself refuses to start (missing master key, lost admin password). See AdminCommands.
if (args.Length > 0 && string.Equals(args[0], AdminCommands.Verb, StringComparison.OrdinalIgnoreCase))
    return await AdminCommands.RunAsync(args, builder.Configuration);

// ---- Persistence ----
builder.Services.AddDbContext<HarboraDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
                ?? "Host=localhost;Port=5432;Database=harbora;Username=harbora;Password=harbora"));

// ---- Infrastructure adapters (Docker, Git, Traefik, security, jobs, deploy engine) ----
builder.Services.AddHarboraInfrastructure(builder.Configuration);

// ---- Backup module (docs/backup-sync/ARCHITECTURE.md) ----
// Registered unconditionally; Features:Backup governs what the module DOES, not whether its types
// can be constructed. A conditional registration would turn a mis-set flag into a resolution
// failure at request time rather than a feature that is simply off.
builder.Services.AddBackupModule(builder.Configuration);
// Sync is a separate module on purpose: deletions propagate, so it must never be mistaken for, or
// counted as, a backup (docs/backup-sync/THREAT_MODEL.md T9).
builder.Services.AddSyncModule(builder.Configuration);

// The SignalR-backed log stream is the host's implementation of the Application port.
builder.Services.AddScoped<IDeploymentLogStream, SignalRDeploymentLogStream>();
builder.Services.AddScoped<Harbora.Web.Infrastructure.PanelModeProvider>();
builder.Services.AddScoped<Harbora.Web.Infrastructure.RailPreferences>();
// Views ask this whether something is locked. Scoped so one request resolves entitlements once,
// however many controls on the page are gated by them.
builder.Services.AddScoped<Harbora.Web.Infrastructure.FeatureView>();

// The size chooser's rules — a plan's pool, its allowed tiers, a host's free capacity, a host's
// withdrawal of a tier and whether the pair is priced at all — assembled once rather than written
// into each of the four forms that ask them.
builder.Services.AddScoped<Harbora.Web.Infrastructure.SizePickerService>();

// Which logos ship in this build. A singleton because the answer cannot change while the process
// runs, and the alternative was a filesystem stat per tile per request.
builder.Services.AddSingleton<Harbora.Web.Infrastructure.TemplateLogoSet>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
// Who is really at the keyboard, when a platform administrator is signed in as a customer. Replaces
// the "nobody is" default AddHarboraInfrastructure registered above — only a request has claims.
builder.Services.AddScoped<ISupportSession, HttpSupportSession>();
// What the banner draws: the validated row, put on the request by the membership middleware.
builder.Services.AddScoped<Harbora.Web.Infrastructure.SupportSessionView>();
// Drives the DbContext's global query filters. Registered here (not in Infrastructure) because only
// the web host has requests to scope; background work resolves the system scope and spans tenants.
builder.Services.AddScoped<IWorkspaceScope, HttpWorkspaceScope>();
builder.Services.AddScoped<DbSeeder>();
builder.Services.AddSingleton<ViteManifest>();
// Resolves the client certificate a node presents, whether Kestrel or Traefik terminated the TLS.
builder.Services.AddSingleton<NodeClientCertificateResolver>();

// ---- MVC + bilingual localization (fa/en, RTL/LTR) ----
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddControllersWithViews()
    .AddViewLocalization()
    // Pointed at the shared resource, because the default looks for a per-model resx that was
    // never created — which left every validation message ("The Email field is required.") in
    // English on a Persian form, the same failure the per-view localiser had.
    .AddDataAnnotationsLocalization(o =>
        o.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(Harbora.Web.SharedResource)));
builder.Services.AddSignalR();

var supportedCultures = new[] { new CultureInfo("fa"), new CultureInfo("en") };
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.DefaultRequestCulture = new RequestCulture("fa");
    o.SupportedCultures = supportedCultures;
    o.SupportedUICultures = supportedCultures;
    // Cookie first, then Accept-Language.
    o.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider());
});

// ---- Auth: cookies for the UI, bearer tokens for API/CLI ----
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/account/login";
        o.AccessDeniedPath = "/account/denied";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // Production sits behind Traefik with TLS always on, so the session cookie must never be
        // offered to a plain-HTTP request — SameAsRequest would happily do so if anything ever
        // answered on port 80. Development runs on plain localhost and keeps the lenient policy.
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TokenAuthenticationHandler>(
        TokenAuthenticationHandler.SchemeName, _ => { })
    // Google, GitHub and a generic OIDC provider, each configured from the settings table and each
    // showing no button at all until an operator has configured it. They sign into a short-lived
    // cookie of their own, never into the session — the rules in AccountController.External decide
    // whether an external identity becomes a signed-in person.
    .AddHarboraExternalLogin();

// Action-level RBAC: one policy per capability, evaluated against the caller's role (doc 10 §2.12).
builder.Services.AddCapabilityAuthorization();
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");

// Auth cookies and antiforgery tokens are encrypted with the Data Protection keyring. By default it
// lives inside the container, so rebuilding the image — i.e. every update — destroys it and signs
// every user out, mid-session, with antiforgery failures on the way. Persist it to a mounted volume
// so a deploy is invisible to logged-in users. SetApplicationName keeps the purpose string stable
// across container names.
var keyRingPath = builder.Configuration["Harbora:DataProtectionKeysPath"] ?? "/var/lib/harbora/keys";
try
{
    var keyRing = Directory.CreateDirectory(keyRingPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(keyRing)
        .SetApplicationName("Harbora");
}
catch (Exception ex)
{
    // Development machines (and any host where that path isn't writable) keep the framework
    // default. Losing the keyring there costs a re-login, not a production incident.
    Console.Error.WriteLine($"⚠ Data Protection keys stay in their default location ({ex.Message}).");
}

// The panel runs behind Traefik, so the connection peer is the proxy. Unwind one forwarded hop from
// trusted proxy networks only — otherwise the per-IP rate limits below collapse into a single
// platform-wide bucket and every audit row records the proxy's IP.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    TrustedProxySetup.Configure(
        o,
        TrustedProxySetup.NetworksFromConfiguration(builder.Configuration),
        TrustedProxySetup.HopsFromConfiguration(builder.Configuration)));

// Per-IP rate limits (doc 10 §2.18): throttle login brute-force and webhook floods. Other traffic
// is unaffected. 429 on rejection.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static Func<HttpContext, RateLimitPartition<string>> PerIp(int permitPerMinute) => ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = permitPerMinute, QueueLimit = 0 });

    options.AddPolicy("auth", PerIp(10));      // login attempts / IP / minute
    options.AddPolicy("webhook", PerIp(60));   // inbound git webhooks / IP / minute
    options.AddPolicy("voucher", PerIp(10));   // guessed/replayed voucher codes / IP / minute
});

var app = builder.Build();

// ---- Migrate + seed on boot (safe to rerun) ----
// Startup failures are fatal and must EXIT, not throw into the void: an unhandled exception here
// once left the process alive spinning a full CPU core, so Docker still reported the container as
// running, the restart policy never fired, and every request returned 502 with nothing to explain
// why. Exiting non-zero makes the failure visible (restart count) and recoverable (restart policy).
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
    // Only a relational provider has migrations, or a database a restore point could be taken of.
    // In production that is always Npgsql and this is always true. The HTTP test harness boots this
    // very pipeline on EF InMemory, where both calls answer with an exception rather than doing
    // nothing — which would have made the whole request surface untestable. Seeding runs either
    // way, so a test host starts from the same rows a real boot leaves behind.
    if (db.Database.IsRelational())
    {
        // A restore point first, when this boot is an upgrade of an existing install. Migrating with
        // no way back to the previous data is the one step of an upgrade that cannot be undone.
        await scope.ServiceProvider.GetRequiredService<UpgradeSafetyService>().EnsureRestorePointAsync(default);
        await db.Database.MigrateAsync();
    }
    else
    {
        // Unreachable in anything that ships — the registration above configures Npgsql and takes no
        // argument about it. Said out loud anyway, because the alternative to saying it is a panel
        // that came up with no schema and no restore point and looked exactly like a healthy one.
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup").LogWarning(
            "The database provider is {Provider}, which has no migrations: this boot applied no schema " +
            "and took no restore point. Only the HTTP test harness is expected to reach this.",
            db.Database.ProviderName);
    }
    await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
}
catch (Exception ex)
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    log.LogCritical(ex, "Harbora could not start: database migration or seeding failed.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("✗ Harbora could not start — " + ex.Message);
    Console.Error.WriteLine("  Diagnose on the server with:  harbora doctor");
    return 1;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// 404/403/401 never reach an action, so without this the user gets a blank page with a bare status
// code. Re-executing into the themed page keeps the real status code on the response while giving
// them something they can act on.
app.UseStatusCodePagesWithReExecute("/error/{0}");

// On every response, including static files and error pages. Deliberately no CSP: the panel's Vue
// islands and inline scripts would need a nonce pipeline first, and a broken CSP fails open in the
// worst way — by people turning it off.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        return Task.CompletedTask;
    });
    await next();
});

// Must run before anything that reads the client IP (rate limiter, audit logging).
app.UseForwardedHeaders();

// Resolves status-{workspaceSlug}.<platform domain> from the Host header and rewrites the request
// under StatusPageController's own path prefix — must run before UseRouting so the rewritten path is
// what endpoint matching actually sees. No auth/session state depends on it either way.
app.UseMiddleware<StatusPageHostMiddleware>();

app.UseRequestLocalization(app.Services.GetRequiredService<
    Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>().Value);

// A visitor to an app currently in maintenance must never reach a static file or a real panel route
// under that host — see the middleware's own doc for why it sits here, ahead of both.
app.UseMiddleware<Harbora.Web.Infrastructure.MaintenanceModeMiddleware>();

app.UseStaticFiles();
// The node channel is the only WebSocket the panel serves besides SignalR's. The keep-alive is
// shorter than a typical proxy idle timeout, so an idle channel is held open by pings rather than
// rediscovered by a reconnect.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<WorkspaceMembershipValidationMiddleware>();
app.UseAuthorization();

// Redirect everything to the setup wizard until the platform is initialised.
app.UseMiddleware<SetupGuardMiddleware>();

// Unauthenticated liveness probe for the installer / load balancer.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapControllers(); // attribute-routed API + controllers
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapHub<DeploymentHub>("/hubs/deployments");
app.MapNodeChannel();

await app.RunAsync();
return 0;

/// <summary>
/// Names the entry point so the HTTP tests can boot this exact pipeline
/// (<c>WebApplicationFactory&lt;Program&gt;</c>). Top-level statements generate this class as
/// internal, which no other assembly can name; declaring it here changes nothing about how the
/// panel runs and is the only way a test can prove the middleware order above is the one that ships.
/// </summary>
public partial class Program;
