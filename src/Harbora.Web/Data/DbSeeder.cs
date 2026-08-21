using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Harbora.Domain.Templates;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Fam = Harbora.Infrastructure.Tenancy.InstanceSizeFamily;

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
            else
            {
                // Built-ins are product data, not user content. Keep the full catalog current on an
                // upgrade so an existing installation receives better descriptions and safer pins,
                // not only the latest manifest body.
                existing.Name = t.Name;
                existing.NameFa = t.NameFa;
                existing.Description = t.Description;
                existing.DescriptionFa = t.DescriptionFa;
                existing.Category = t.Category;
                existing.IconUrl = t.IconUrl;
                existing.ManifestJson = t.ManifestJson;
                existing.IsBuiltIn = true;
                existing.IsEnabled = true;
            }
        }

        await SeedReadyAppsAsync();
        await SeedTenancyAsync();

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The versioned ready-app catalogue, plus a logo record for every template that has one.
    ///
    /// Versions are upserted by (template, version) and never deleted here: a version somebody is
    /// running must not vanish because a newer catalogue shipped without it. Retiring one is a
    /// lifecycle change an administrator makes, not a side effect of an upgrade.
    /// </summary>
    private async Task SeedReadyAppsAsync()
    {
        foreach (var app in Harbora.Infrastructure.Templates.ReadyAppCatalog.All())
        {
            var template = await db.AppTemplates.FirstOrDefaultAsync(x => x.Key == app.Template.Key);
            if (template is null)
            {
                template = app.Template;
                db.AppTemplates.Add(template);
            }
            else
            {
                template.Name = app.Template.Name;
                template.NameFa = app.Template.NameFa;
                template.Description = app.Template.Description;
                template.DescriptionFa = app.Template.DescriptionFa;
                template.Category = app.Template.Category;
                template.IconUrl = app.Template.IconUrl;
                template.ManifestJson = app.Template.ManifestJson;
                template.IsBuiltIn = true;
                template.IsEnabled = true;
            }

            await db.SaveChangesAsync();

            foreach (var version in app.Versions)
            {
                var stored = await db.AppTemplateVersions
                    .FirstOrDefaultAsync(v => v.AppTemplateId == template.Id && v.Version == version.Version);

                if (stored is null)
                {
                    version.AppTemplateId = template.Id;

                    // DiscoveredAt is deliberately left null. It means "the registry check found
                    // this tag", and a version that ships in this catalogue was not found — it was
                    // written here. Stamping it made every shipped version read as newly discovered
                    // on the admin page, and left nothing able to tell the two apart.
                    db.AppTemplateVersions.Add(version);
                    continue;
                }

                // Clears the stamp on rows seeded before that was fixed, so the distinction is true
                // of the data and not only of new rows.
                stored.DiscoveredAt = null;

                // The pin and the manifest are product data and are refreshed. Publication is not:
                // an administrator who withdrew a version did so deliberately.
                stored.ImageRepository = version.ImageRepository;
                stored.ImageTag = version.ImageTag;
                stored.ImageDigest = version.ImageDigest;
                stored.SupportedArchitectures = version.SupportedArchitectures;
                stored.ManifestJson = version.ManifestJson;
                stored.UpgradeNotes = version.UpgradeNotes;
                stored.MigrationWarnings = version.MigrationWarnings;
            }

            // A version this catalogue used to ship and no longer does — most often because a tag
            // was corrected, which leaves the old row behind pointing at an image that no longer
            // resolves. Published, it is still offered on the deploy form and fails at pull time.
            //
            // Withdrawn rather than deleted: an app deployed from it still refers to it, and the
            // row is the only record of what that app was installed from. Discovered versions are
            // excluded by DiscoveredAt — they were never in this catalogue, and removing them would
            // undo the registry job's work on every boot.
            var shipped = app.Versions.Select(v => v.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var withdrawn = await db.AppTemplateVersions
                .Where(v => v.AppTemplateId == template.Id
                            && v.DiscoveredAt == null
                            && v.Publication == VersionPublication.Published)
                .ToListAsync();

            foreach (var orphan in withdrawn.Where(v => !shipped.Contains(v.Version)))
                orphan.Publication = VersionPublication.Draft;

            var asset = await db.AppTemplateAssets.FirstOrDefaultAsync(a => a.AppTemplateId == template.Id);
            if (asset is null)
            {
                app.Asset.AppTemplateId = template.Id;
                db.AppTemplateAssets.Add(app.Asset);
            }
            else
            {
                asset.Path = app.Asset.Path;
                asset.SourceUrl = app.Asset.SourceUrl;
                asset.License = app.Asset.License;
                asset.LicenseNote = app.Asset.LicenseNote;
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Seed instance sizes + default plans and ensure every workspace has a plan.</summary>
    private async Task SeedTenancyAsync()
    {
        const long MB = 1024 * 1024;
        const long GB = 1024 * MB;
        foreach (var s in new[]
        {
            new InstanceSize { Key = "nano",   Name = "Nano",   NameFa = "نانو",   Family = Fam.General, CpuCores = 0.25, MemoryBytes = 256 * MB,  DiskBytes = 5 * GB,  IsBuiltIn = true, SortOrder = 1 },
            new InstanceSize { Key = "micro",  Name = "Micro",  NameFa = "میکرو",  Family = Fam.General, CpuCores = 0.5,  MemoryBytes = 512 * MB,  DiskBytes = 10 * GB, IsBuiltIn = true, SortOrder = 2 },
            new InstanceSize { Key = "small",  Name = "Small",  NameFa = "کوچک",   Family = Fam.General, CpuCores = 1,    MemoryBytes = 1024 * MB, DiskBytes = 20 * GB, IsBuiltIn = true, SortOrder = 3 },
            new InstanceSize { Key = "medium", Name = "Medium", NameFa = "متوسط",  Family = Fam.General, CpuCores = 2,    MemoryBytes = 2048 * MB, DiskBytes = 40 * GB, IsBuiltIn = true, SortOrder = 4 },
            new InstanceSize { Key = "large",  Name = "Large",  NameFa = "بزرگ",   Family = Fam.General, CpuCores = 4,    MemoryBytes = 4096 * MB, DiskBytes = 80 * GB, IsBuiltIn = true, SortOrder = 5 },
        })
        {
            var existing = await db.InstanceSizes.FirstOrDefaultAsync(x => x.Key == s.Key);
            if (existing is null) { db.InstanceSizes.Add(s); continue; }

            // Written onto every tier whose family is still blank, Harbora's own or not. Unlike the
            // disk backfill below, this is not a decision about somebody's tier — a blank family
            // already READS as general everywhere (InstanceSizeFamily.Normalise says so), so writing
            // it down changes no behaviour and only makes the row say what the code already believes.
            // The reason to write it at all is the provider's form: a select cannot show "blank" as a
            // choice without adding a fifth option that means the first one.
            if (string.IsNullOrWhiteSpace(existing.Family)) existing.Family = Fam.General;

            // No optimised tiers are seeded. A new size arrives unpriced, and an unpriced tier on a
            // customer's chooser is a card that cannot be bought — so which specialist tiers to sell
            // is the provider's decision, made on the size form, not one this seeder makes for them.

            // Backfilled onto the built-in tiers that predate the column, because a tier whose
            // storage reads as "no ceiling" is what every tier looked like before and shows the
            // operator nothing. Only where it is still unset, and only on Harbora's own tiers — a
            // size somebody created themselves is theirs, including their decision to leave it open.
            //
            // Safe to do without asking: a tier's disk figure only ever refuses a resize *down*
            // below what is already stored. Nothing running is stopped, moved or shrunk.
            if (existing.IsBuiltIn && existing.DiskBytes == 0) existing.DiskBytes = s.DiskBytes;
        }

        // Storage tiers, seeded like the compute ones. Without them the bucket form has no plan to
        // offer and every bucket gets no ceiling, which is the option nobody chooses on purpose.
        if (!await db.StoragePlans.AnyAsync())
        {
            db.StoragePlans.AddRange(
                new Harbora.Domain.Storage.StoragePlan
                {
                    Name = "Starter", NameFa = "شروع",
                    QuotaBytes = 10 * GB, MaxBuckets = 2, MonthlyPrice = 2, IsDefault = true, SortOrder = 1
                },
                new Harbora.Domain.Storage.StoragePlan
                {
                    Name = "Standard", NameFa = "استاندارد",
                    QuotaBytes = 100 * GB, MaxBuckets = 10, MonthlyPrice = 12, SortOrder = 2
                },
                new Harbora.Domain.Storage.StoragePlan
                {
                    Name = "Scale", NameFa = "بزرگ",
                    QuotaBytes = 1024 * GB, MaxBuckets = 0, MonthlyPrice = 80, SortOrder = 3
                });
        }

        if (!await db.Plans.AnyAsync())
        {
            db.Plans.AddRange(
                // The provider's own workspace runs on this unlimited default.
                new Plan { Name = "Provider", NameFa = "اپراتور", IsDefault = true },
                new Plan { Name = "Starter", NameFa = "شروع", MaxApps = 2, MaxServices = 1,
                    MaxMembers = 3, MaxProjects = 3, MaxEnvironments = 6, MaxDomains = 5,
                    MaxVolumes = 5, MaxBackupSchedules = 2, MaxCronJobs = 2,
                    MaxConcurrentDeployments = 1, MaxBackupRetentionCount = 7,
                    MaxMemoryBytes = 1024 * MB, MaxCpuCores = 1, AllowedSizeKeys = "nano,micro", MonthlyPrice = 5 },
                new Plan { Name = "Pro", NameFa = "حرفه‌ای", MaxApps = 10, MaxServices = 5,
                    MaxMembers = 15, MaxProjects = 20, MaxEnvironments = 50, MaxDomains = 50,
                    MaxVolumes = 50, MaxBackupSchedules = 25, MaxCronJobs = 25,
                    MaxConcurrentDeployments = 4, MaxBackupRetentionCount = 30,
                    MaxMemoryBytes = 8192 * MB, MaxCpuCores = 8, AllowedSizeKeys = "nano,micro,small,medium", MonthlyPrice = 25 });
        }
        await db.SaveChangesAsync();

        // The provider's own plan gets every feature; the customer plans get whatever each feature
        // ships as, which for anything sellable is Locked.
        //
        // Seeded rather than special-cased in the resolver: "the owner sees everything" as a rule in
        // code would be a second answer to who is entitled to what, unreachable from the console that
        // is supposed to be the only one. As a row it shows up in the grid, and the owner can take it
        // away again — which is the whole point of the grid.
        if (await db.Plans.FirstOrDefaultAsync(p => p.IsDefault) is { } providerPlan)
        {
            foreach (var feature in Harbora.Domain.Features.PlatformFeatures.All)
            {
                var already = await db.FeatureGrants.AnyAsync(g =>
                    g.Scope == Harbora.Domain.Features.FeatureScope.Plan
                    && g.TargetId == providerPlan.Id
                    && g.FeatureKey == feature.Key);
                if (already) continue;

                db.FeatureGrants.Add(new Harbora.Domain.Features.FeatureGrant
                {
                    Scope = Harbora.Domain.Features.FeatureScope.Plan,
                    TargetId = providerPlan.Id,
                    FeatureKey = feature.Key,
                    State = Harbora.Domain.Features.FeatureState.Enabled,
                    Note = "Seeded: the provider's own plan."
                });
            }
            await db.SaveChangesAsync();
        }

        // Ensure existing workspaces point at the default plan. Tracked rather than ExecuteUpdate:
        // this is a handful of rows on a path that runs once per boot, and ExecuteUpdate is a
        // relational-only statement — it was the single line that made the whole startup sequence,
        // and therefore every HTTP-level test of the pipeline it starts, impossible to run against
        // anything but PostgreSQL.
        var defaultPlanId = await db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefaultAsync();
        if (defaultPlanId != Guid.Empty)
        {
            foreach (var workspace in await db.Workspaces.Where(w => w.PlanId == null).ToListAsync())
                workspace.PlanId = defaultPlanId;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Templates are data, not code — each carries a JSON manifest the deploy engine reads.
    /// Adding a template later means inserting a row, not editing C#.
    ///
    /// Internal rather than private so tests/Harbora.Tests can validate this list directly (see
    /// InternalsVisibleTo in Harbora.Web.csproj) — the same thing ReadyAppCatalogTests already does
    /// for the other, versioned catalogue. Without that, a test could only check a hand-copied
    /// duplicate of a manifest string, which is exactly the kind of copy that drifts unnoticed from
    /// what actually seeds.
    /// </summary>
    internal static IEnumerable<AppTemplate> BuiltInTemplates() =>
    [
        new()
        {
            Key = "wordpress", Name = "WordPress", NameFa = "وردپرس",
            Category = "cms", IsBuiltIn = true,
            Description = "Production-ready WordPress with a private MariaDB database and persistent content.",
            DescriptionFa = "وردپرس آمادهٔ تولید همراه MariaDB خصوصی و فضای ذخیره‌سازی پایدار.",
            ManifestJson = """{"image":"wordpress:6-php8.3-apache","port":80,"healthPath":"/wp-login.php","requires":["mariadb"],"volumes":[{"mount":"/var/www/html"}],"env":[{"key":"WORDPRESS_DB_HOST","default":"${{mariadb.host}}:${{mariadb.port}}"},{"key":"WORDPRESS_DB_USER","default":"${{mariadb.user}}"},{"key":"WORDPRESS_DB_PASSWORD","default":"${{mariadb.password}}","secret":true},{"key":"WORDPRESS_DB_NAME","default":"${{mariadb.database}}"}],"featured":true,"tags":["CMS","Website","MariaDB"],"website":"https://wordpress.org","documentation":"https://hub.docker.com/_/wordpress"}"""
        },
        new()
        {
            Key = "ghost", Name = "Ghost", NameFa = "گوست",
            Category = "cms", IsBuiltIn = true,
            Description = "Modern publishing platform with MySQL, private networking and persistent content.",
            DescriptionFa = "پلتفرم انتشار مدرن با MySQL، شبکهٔ خصوصی و محتوای پایدار.",
            ManifestJson = """{"image":"ghost:5-alpine","port":2368,"healthPath":"/ghost/","requires":["mysql"],"volumes":[{"mount":"/var/lib/ghost/content"}],"env":[{"key":"database__client","default":"mysql"},{"key":"database__connection__host","default":"${{mysql.host}}"},{"key":"database__connection__port","default":"${{mysql.port}}"},{"key":"database__connection__user","default":"${{mysql.user}}"},{"key":"database__connection__password","default":"${{mysql.password}}","secret":true},{"key":"database__connection__database","default":"${{mysql.database}}"}],"featured":true,"tags":["Blog","Publishing","MySQL"],"website":"https://ghost.org","documentation":"https://ghost.org/docs/install/docker/"}"""
        },
        new()
        {
            Key = "uptime-kuma", Name = "Uptime Kuma", NameFa = "آپ‌تایم کوما",
            Category = "observability", IsBuiltIn = true,
            Description = "Self-hosted uptime monitoring with a persistent status history.",
            DescriptionFa = "پایش آپ‌تایم به‌صورت سلف‌هاست با تاریخچهٔ وضعیت پایدار.",
            ManifestJson = """{"image":"louislam/uptime-kuma:1","port":3001,"healthPath":"/","volumes":[{"mount":"/app/data"}],"env":[],"featured":true,"tags":["Monitoring","Status","SQLite"],"website":"https://uptime.kuma.pet","documentation":"https://github.com/louislam/uptime-kuma/wiki"}"""
        },
        new()
        {
            Key = "meilisearch", Name = "Meilisearch", NameFa = "میلی‌سرچ",
            Category = "data", IsBuiltIn = true,
            Description = "Fast full-text search with a generated master key and persistent indexes.",
            DescriptionFa = "جست‌وجوی متن سریع با کلید اصلی تولیدشده و ایندکس‌های پایدار.",
            ManifestJson = """{"image":"getmeili/meilisearch:v1.15","port":7700,"healthPath":"/health","volumes":[{"mount":"/meili_data"}],"env":[{"key":"MEILI_ENV","default":"production"},{"key":"MEILI_MASTER_KEY","secret":true}],"tags":["Search","API","Data"],"website":"https://www.meilisearch.com","documentation":"https://www.meilisearch.com/docs/learn/self_hosted/getting_started_with_self_hosted_meilisearch"}"""
        },
        new()
        {
            // F6 (2026-08-21 functions-and-services plan, HARBORA-0038 phase 1): the Dev Inbox half.
            // Checked first, per the plan's own instruction, whether a one-click template is cheaper
            // than a bespoke viewer — it is: Mailpit is exactly the Mailtrap-class catch-all SMTP
            // server doc 10 §3/§5 describes, the platform already runs one-click apps with their own
            // public URL, and deploying one gives a developer a real inbox (SMTP in, web UI out) for
            // a JSON manifest instead of a new EmailMessage table, a capture listener and a viewer
            // page. MP_SMTP_AUTH_ACCEPT_ANY, MP_DATABASE and the /readyz health path are read
            // straight from Mailpit's own source (cmd/root.go, internal/healthcheck) on 2026-08-21 —
            // not from documentation paraphrase — and the image is pinned by the real digest Docker
            // Hub returns for axllent/mailpit:v1.30.7 (verified against the registry the same day),
            // not invented. No Simple Icons mark exists for it, so it ships here — the simpler,
            // undigested-icon catalogue — rather than in ReadyAppCatalog, whose own tests require a
            // real project logo this app has none to offer honestly.
            Key = "mailpit", Name = "Dev Inbox (Mailpit)", NameFa = "صندوق توسعه (Mailpit)",
            Category = "developer-tools", IsBuiltIn = true,
            Description = "A catch-all SMTP server for non-production: attach it like any BYO email provider and every message an app sends lands here instead of a real inbox, with a web UI to read it.",
            DescriptionFa = "یک سرور SMTP گیرندهٔ همه‌چیز برای محیط‌های غیرتولیدی: مثل هر ارائه‌دهندهٔ ایمیل شخصی آن را متصل کنید تا هر پیامی که اپ می‌فرستد، به‌جای صندوق واقعی، اینجا بنشیند — با یک رابط وب برای خواندنش.",
            ManifestJson = """{"image":"axllent/mailpit:v1.30.7@sha256:d5ecbb067db3705fa953d79e1b7f81ef84038df67aba6c52825d8c02a1ea748a","port":8025,"healthPath":"/readyz","volumes":[{"mount":"/data"}],"env":[{"key":"MP_DATABASE","default":"/data/mailpit.db"},{"key":"MP_SMTP_AUTH_ACCEPT_ANY","default":"1"},{"key":"MP_MAX_MESSAGES","default":"5000"}],"tags":["Email","Dev Inbox","SMTP","Testing"],"website":"https://mailpit.axllent.org","documentation":"https://mailpit.axllent.org/docs/"}"""
        },
        new()
        {
            Key = "redis-commander", Name = "Redis Commander", NameFa = "ردیس کامندر",
            Category = "developer-tools", IsBuiltIn = true,
            Description = "A web console wired to its own private Redis instance.",
            DescriptionFa = "کنسول وب متصل به یک Redis خصوصی و اختصاصی.",
            ManifestJson = """{"image":"rediscommander/redis-commander:0.8.1","port":8081,"healthPath":"/","requires":["redis"],"env":[{"key":"REDIS_HOSTS","default":"local:${{redis.host}}:${{redis.port}}:0:${{redis.password}}","secret":true}],"tags":["Redis","Admin","Developer tool"],"documentation":"https://github.com/joeferner/redis-commander"}"""
        },
        new()
        {
            Key = "nginx-static", Name = "Static Site (Nginx)", NameFa = "سایت استاتیک (Nginx)",
            Category = "starter", IsBuiltIn = true,
            Description = "Serve a static site or SPA build behind Nginx with persistent content.",
            DescriptionFa = "میزبانی سایت استاتیک یا خروجی SPA پشت Nginx با محتوای پایدار.",
            ManifestJson = """{"image":"nginx:1.27-alpine","port":80,"healthPath":"/","volumes":[{"mount":"/usr/share/nginx/html"}],"env":[],"tags":["Static","Nginx","Website"],"documentation":"https://hub.docker.com/_/nginx"}"""
        },
        new()
        {
            Key = "node", Name = "Node.js Starter", NameFa = "شروع با Node.js",
            Category = "starter", IsBuiltIn = true,
            Description = "Bring a Git repository; Harbora detects and builds the Node.js application.",
            DescriptionFa = "مخزن Git را بدهید؛ Harbora برنامهٔ Node.js را تشخیص می‌دهد و می‌سازد.",
            ManifestJson = """{"source":"git","port":3000,"healthPath":"/","env":[{"key":"NODE_ENV","default":"production"}],"tags":["Node.js","Git","Starter"],"documentation":"https://nodejs.org/en/learn/getting-started/introduction-to-nodejs"}"""
        },
        new()
        {
            Key = "aspnet", Name = "ASP.NET Core Starter", NameFa = "شروع با ASP.NET Core",
            Category = "starter", IsBuiltIn = true,
            Description = "Deploy an ASP.NET Core repository with production URL binding included.",
            DescriptionFa = "استقرار مخزن ASP.NET Core با تنظیم آمادهٔ آدرس اجرا در محیط تولید.",
            ManifestJson = """{"source":"git","port":8080,"healthPath":"/","env":[{"key":"ASPNETCORE_URLS","default":"http://+:8080"}],"tags":[".NET","Git","Starter"],"documentation":"https://learn.microsoft.com/aspnet/core/host-and-deploy/docker/"}"""
        },
        new()
        {
            Key = "laravel", Name = "Laravel Starter", NameFa = "شروع با لاراول",
            Category = "starter", IsBuiltIn = true,
            Description = "Deploy a Laravel repository with production mode and a generated application key.",
            DescriptionFa = "استقرار مخزن لاراول با حالت تولید و کلید برنامهٔ تولیدشده.",
            ManifestJson = """{"source":"git","port":80,"healthPath":"/","env":[{"key":"APP_ENV","default":"production"},{"key":"APP_KEY","secret":true}],"tags":["PHP","Laravel","Git"],"documentation":"https://laravel.com/docs/deployment"}"""
        },
        new()
        {
            // F7 (2026-08-21 functions-and-services plan): "kind":"worker" is what makes this
            // honestly deployable — a long-polling bot answers no HTTP, and before "kind" existed
            // every template-created app was silently Web, which would have given this a public
            // domain and a health check waiting forever for a response the bot never sends.
            // TELEGRAM_BOT_TOKEN is "secret" + "required": a credential only the person deploying
            // has, asked for on the deploy form and encrypted at rest rather than invented.
            Key = "telegram-bot", Name = "Telegram Bot (long-polling)", NameFa = "ربات تلگرام (long-polling)",
            Category = "automation", IsBuiltIn = true,
            Description = "A background worker that long-polls Telegram for updates — no domain, no public URL, no webhook to configure. Bring a Node.js repository; set your bot token and deploy.",
            DescriptionFa = "یک Worker پس‌زمینه که آپدیت‌های تلگرام را به روش long-polling می‌گیرد — بدون دامنه، بدون آدرس عمومی، بدون نیاز به تنظیم webhook. مخزن Node.js خودتان را بدهید، توکن ربات را وارد کنید و مستقر کنید.",
            ManifestJson = """{"source":"git","kind":"worker","env":[{"key":"TELEGRAM_BOT_TOKEN","secret":true,"required":true,"description":"از @BotFather در خودِ تلگرام بگیرید."}],"tags":["Telegram","Bot","Automation","Node.js"],"website":"https://core.telegram.org/bots/api","documentation":"/learn/10-telegram-bot"}"""
        },
        new()
        {
            Key = "postgres", Name = "PostgreSQL", NameFa = "PostgreSQL",
            Category = "database", IsBuiltIn = true,
            Description = "Managed PostgreSQL with generated credentials, private networking and persistent storage.",
            DescriptionFa = "PostgreSQL مدیریت‌شده با رمز تولیدشده، شبکه خصوصی و ذخیره‌سازی پایدار.",
            ManifestJson = """{"service":"postgres","port":5432,"featured":true,"tags":["SQL","Database","Managed"],"documentation":"https://www.postgresql.org/docs/"}"""
        },
        new()
        {
            Key = "mariadb", Name = "MariaDB", NameFa = "MariaDB",
            Category = "database", IsBuiltIn = true,
            Description = "Managed MariaDB with private networking and persistent storage.",
            DescriptionFa = "MariaDB مدیریت‌شده با شبکهٔ خصوصی و ذخیره‌سازی پایدار.",
            ManifestJson = """{"service":"mariadb","port":3306,"tags":["SQL","Database","Managed"],"documentation":"https://mariadb.com/kb/en/documentation/"}"""
        },
        new()
        {
            Key = "mysql", Name = "MySQL", NameFa = "MySQL",
            Category = "database", IsBuiltIn = true,
            Description = "Managed MySQL with generated credentials and persistent storage.",
            DescriptionFa = "MySQL مدیریت‌شده با رمز تولیدشده و ذخیره‌سازی پایدار.",
            ManifestJson = """{"service":"mysql","port":3306,"tags":["SQL","Database","Managed"],"documentation":"https://dev.mysql.com/doc/"}"""
        },
        new()
        {
            Key = "redis", Name = "Redis", NameFa = "Redis",
            Category = "database", IsBuiltIn = true,
            Description = "Managed Redis cache and queue with authentication and persistence.",
            DescriptionFa = "کش و صف Redis مدیریت‌شده با احراز هویت و ماندگاری داده.",
            ManifestJson = """{"service":"redis","port":6379,"featured":true,"tags":["Cache","Queue","Managed"],"documentation":"https://redis.io/docs/latest/"}"""
        },
        new()
        {
            Key = "mongodb", Name = "MongoDB", NameFa = "MongoDB",
            Category = "database", IsBuiltIn = true,
            Description = "Managed MongoDB with private networking and a persistent data volume.",
            DescriptionFa = "MongoDB مدیریت‌شده با شبکهٔ خصوصی و فضای دادهٔ پایدار.",
            ManifestJson = """{"service":"mongodb","port":27017,"tags":["Document","Database","Managed"],"documentation":"https://www.mongodb.com/docs/"}"""
        }
    ];
}
