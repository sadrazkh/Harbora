using System.Globalization;
using System.Threading.RateLimiting;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure;
using Harbora.Web.Data;
using Harbora.Web.Infrastructure;
using Harbora.Web.Realtime;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

// The SignalR-backed log stream is the host's implementation of the Application port.
builder.Services.AddScoped<IDeploymentLogStream, SignalRDeploymentLogStream>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
// Drives the DbContext's global query filters. Registered here (not in Infrastructure) because only
// the web host has requests to scope; background work resolves the system scope and spans tenants.
builder.Services.AddScoped<IWorkspaceScope, HttpWorkspaceScope>();
builder.Services.AddScoped<DbSeeder>();
builder.Services.AddSingleton<ViteManifest>();

// ---- MVC + bilingual localization (fa/en, RTL/LTR) ----
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddControllersWithViews()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();
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
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TokenAuthenticationHandler>(
        TokenAuthenticationHandler.SchemeName, _ => { });

// Action-level RBAC: one policy per capability, evaluated against the caller's role (doc 10 §2.12).
builder.Services.AddCapabilityAuthorization();
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");

// The panel runs behind Traefik, so the connection peer is the proxy. Unwind one forwarded hop from
// trusted proxy networks only — otherwise the per-IP rate limits below collapse into a single
// platform-wide bucket and every audit row records the proxy's IP.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    TrustedProxySetup.Configure(o, TrustedProxySetup.NetworksFromConfiguration(builder.Configuration)));

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
});

var app = builder.Build();

// ---- Migrate + seed on boot (safe to rerun) ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();
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

// Must run before anything that reads the client IP (rate limiter, audit logging).
app.UseForwardedHeaders();

app.UseRequestLocalization(app.Services.GetRequiredService<
    Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>().Value);
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Redirect everything to the setup wizard until the platform is initialised.
app.UseMiddleware<SetupGuardMiddleware>();

// Unauthenticated liveness probe for the installer / load balancer.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapControllers(); // attribute-routed API + controllers
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapHub<DeploymentHub>("/hubs/deployments");

await app.RunAsync();
return 0;
