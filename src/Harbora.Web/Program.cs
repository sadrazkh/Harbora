using System.Globalization;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure;
using Harbora.Web.Data;
using Harbora.Web.Infrastructure;
using Harbora.Web.Realtime;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");

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

app.UseRequestLocalization(app.Services.GetRequiredService<
    Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>().Value);
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Redirect everything to the setup wizard until the platform is initialised.
app.UseMiddleware<SetupGuardMiddleware>();

app.MapControllers(); // attribute-routed API + controllers
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapHub<DeploymentHub>("/hubs/deployments");

app.Run();
