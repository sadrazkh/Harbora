using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Harbora.Domain.Templates;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Data;

/// <summary>
/// Idempotent seed run at every boot: ensures the local server row and the built-in one-click
/// templates exist. The admin user + first workspace are created by the setup wizard instead.
/// </summary>
public sealed class DbSeeder(HarboraDbContext db)
{
    public async Task SeedAsync()
    {
        if (!await db.Servers.AnyAsync())
        {
            db.Servers.Add(new Server { Name = "Local", Hostname = "localhost", IsLocal = true, Status = ServerStatus.Online });
        }

        foreach (var t in BuiltInTemplates())
        {
            var existing = await db.AppTemplates.FirstOrDefaultAsync(x => x.Key == t.Key);
            if (existing is null) db.AppTemplates.Add(t);
            else existing.ManifestJson = t.ManifestJson; // keep manifests current
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Templates are data, not code — each carries a JSON manifest the deploy engine reads.
    /// Adding a template later means inserting a row, not editing C#.
    /// </summary>
    private static IEnumerable<AppTemplate> BuiltInTemplates() =>
    [
        new()
        {
            Key = "nginx-static", Name = "Static Site (Nginx)", NameFa = "سایت استاتیک (Nginx)",
            Category = "static", IsBuiltIn = true,
            Description = "Serve a static site or SPA build behind Nginx.",
            DescriptionFa = "میزبانی سایت استاتیک یا خروجی SPA پشت Nginx.",
            ManifestJson = """{"image":"nginx:alpine","port":80,"volumes":[{"mount":"/usr/share/nginx/html"}],"env":[]}"""
        },
        new()
        {
            Key = "node", Name = "Node.js App", NameFa = "اپلیکیشن Node.js",
            Category = "app", IsBuiltIn = true,
            Description = "Deploy a Node.js app from a Git repo with a Dockerfile.",
            DescriptionFa = "استقرار اپ Node.js از مخزن Git با Dockerfile.",
            ManifestJson = """{"source":"git","port":3000,"env":[{"key":"NODE_ENV","default":"production"}]}"""
        },
        new()
        {
            Key = "aspnet", Name = "ASP.NET Core", NameFa = "ASP.NET Core",
            Category = "app", IsBuiltIn = true,
            Description = "Deploy an ASP.NET Core app from a Git repo.",
            DescriptionFa = "استقرار اپ ASP.NET Core از مخزن Git.",
            ManifestJson = """{"source":"git","port":8080,"env":[{"key":"ASPNETCORE_URLS","default":"http://+:8080"}]}"""
        },
        new()
        {
            Key = "laravel", Name = "Laravel (PHP)", NameFa = "لاراول (PHP)",
            Category = "app", IsBuiltIn = true,
            Description = "PHP/Laravel app with FPM + Nginx.",
            DescriptionFa = "اپ PHP/لاراول با FPM و Nginx.",
            ManifestJson = """{"source":"git","port":80,"env":[{"key":"APP_ENV","default":"production"},{"key":"APP_KEY","secret":true}]}"""
        },
        new()
        {
            Key = "wordpress", Name = "WordPress", NameFa = "وردپرس",
            Category = "app", IsBuiltIn = true,
            Description = "WordPress with a managed MySQL/MariaDB service.",
            DescriptionFa = "وردپرس همراه سرویس مدیریت‌شده MySQL/MariaDB.",
            ManifestJson = """{"image":"wordpress:php8.3-apache","port":80,"requires":["mariadb"],"volumes":[{"mount":"/var/www/html"}],"env":[{"key":"WORDPRESS_DB_HOST"},{"key":"WORDPRESS_DB_PASSWORD","secret":true}]}"""
        },
        new()
        {
            Key = "postgres", Name = "PostgreSQL", NameFa = "PostgreSQL",
            Category = "database", IsBuiltIn = true,
            Description = "Managed PostgreSQL database.",
            DescriptionFa = "دیتابیس مدیریت‌شده PostgreSQL.",
            ManifestJson = """{"service":"postgres","image":"postgres:16-alpine","port":5432,"volumes":[{"mount":"/var/lib/postgresql/data"}]}"""
        },
        new()
        {
            Key = "redis", Name = "Redis", NameFa = "Redis",
            Category = "database", IsBuiltIn = true,
            Description = "Managed Redis cache/queue.",
            DescriptionFa = "کش/صف مدیریت‌شده Redis.",
            ManifestJson = """{"service":"redis","image":"redis:7-alpine","port":6379}"""
        }
    ];
}
