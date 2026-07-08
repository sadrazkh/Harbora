using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Harbora.Domain.Templates;
using Harbora.Domain.Tenancy;
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

        await SeedTenancyAsync();

        await db.SaveChangesAsync();
    }

    /// <summary>Seed instance sizes + default plans and ensure every workspace has a plan.</summary>
    private async Task SeedTenancyAsync()
    {
        const long MB = 1024 * 1024;
        foreach (var s in new[]
        {
            new InstanceSize { Key = "nano",   Name = "Nano",   NameFa = "نانو",   CpuCores = 0.25, MemoryBytes = 256 * MB,  IsBuiltIn = true, SortOrder = 1 },
            new InstanceSize { Key = "micro",  Name = "Micro",  NameFa = "میکرو",  CpuCores = 0.5,  MemoryBytes = 512 * MB,  IsBuiltIn = true, SortOrder = 2 },
            new InstanceSize { Key = "small",  Name = "Small",  NameFa = "کوچک",   CpuCores = 1,    MemoryBytes = 1024 * MB, IsBuiltIn = true, SortOrder = 3 },
            new InstanceSize { Key = "medium", Name = "Medium", NameFa = "متوسط",  CpuCores = 2,    MemoryBytes = 2048 * MB, IsBuiltIn = true, SortOrder = 4 },
            new InstanceSize { Key = "large",  Name = "Large",  NameFa = "بزرگ",   CpuCores = 4,    MemoryBytes = 4096 * MB, IsBuiltIn = true, SortOrder = 5 },
        })
        {
            if (!await db.InstanceSizes.AnyAsync(x => x.Key == s.Key)) db.InstanceSizes.Add(s);
        }

        if (!await db.Plans.AnyAsync())
        {
            db.Plans.AddRange(
                // The provider's own workspace runs on this unlimited default.
                new Plan { Name = "Provider", NameFa = "اپراتور", IsDefault = true },
                new Plan { Name = "Starter", NameFa = "شروع", MaxApps = 2, MaxServices = 1,
                    MaxMemoryBytes = 1024 * MB, MaxCpuCores = 1, AllowedSizeKeys = "nano,micro", MonthlyPrice = 5 },
                new Plan { Name = "Pro", NameFa = "حرفه‌ای", MaxApps = 10, MaxServices = 5,
                    MaxMemoryBytes = 8192 * MB, MaxCpuCores = 8, AllowedSizeKeys = "nano,micro,small,medium", MonthlyPrice = 25 });
        }
        await db.SaveChangesAsync();

        // Ensure existing workspaces point at the default plan.
        var defaultPlanId = await db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefaultAsync();
        if (defaultPlanId != Guid.Empty)
            await db.Workspaces.Where(w => w.PlanId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.PlanId, defaultPlanId));
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
