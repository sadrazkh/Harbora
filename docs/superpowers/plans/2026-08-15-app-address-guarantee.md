# App Address Guarantee Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every app that serves HTTP gets a hostname through every door it can be created by, a name clash produces a working address and a message instead of silence, and the operator can give existing addressless apps one from an explicit control that shows what it will do first.

**Architecture:** One pure decision (`AppAddress`) and one database-aware assigner (`AppAddressAssigner`) replace the three parallel implementations and the one missing implementation across the four app-creation paths. A census test reads the source for `db.Apps.Add` call sites and fails when a new one bypasses the assigner.

**Tech Stack:** .NET 10, ASP.NET Core MVC, EF Core (PostgreSQL in production, InMemory in tests), xUnit, FluentAssertions, Razor, Tailwind.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-14-app-address-guarantee-design.md`. Read it before Task 1.
- **Zero NEW build warnings.** `dotnet build Harbora.slnx -c Debug` currently reports exactly **2 pre-existing NU1903** NuGet-audit warnings on SSH.NET in `Harbora.Postgres.Tests`. Leave them alone — do not suppress, upgrade or "fix" them. **Security is out of scope by the owner's standing instruction.** Any other warning, or a third NU1903, is yours.
- **Baseline that must not drop:** build 0 errors; **4,200 passing, 0 failing** across the three assemblies; 17 Docker-gated and 72 Postgres-lane skips.
- **No database migration.** Nothing here changes the schema — `DomainName` already exists with every field needed. If a task appears to need a migration, stop and report.
- **Never renumber an existing enum value.** Append only.
- **Bilingual.** Every string a user sees goes through `@T["…"]` or the `isFa` ternary already used in these views and controllers. An English-only label is a defect.
- **The panel renders Persian by default in tests.** An HTTP assertion on an English literal never matches. Assert on untranslated route fragments, ids, CSS class names, or the exact hostname string (hostnames are not translated).
- **`docs/product-audit/19-do-not-change-list.md`** lists 30 protected behaviours. Item 21 (bilingual/RTL) is in this plan's path.
- **Test names read as sentences**, like the ones already in `tests/Harbora.Tests/`.
- **Narrative commit messages** — read `git log --oneline -10` for the register. End each with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Shared tree.** Never run `git stash`, `git stash pop`, `git checkout -- .`, `git reset --hard` or `git clean`. Stage by explicit path; never `git add -A`.
- **Environmental trap.** A build can fail with `MSB3491 "Access to the path … denied"` on `obj/` files while the test suite is green. That is leftover dotnet/MSBuild processes holding locks, not your code. Run `dotnet build-server shutdown`, then rebuild.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Harbora.Infrastructure/Networking/AppAddress.cs` **(create)** | The pure decision: may this kind have an address, is this host reserved, what does a discriminated retry look like. No database, no I/O. |
| `src/Harbora.Infrastructure/Networking/AppAddressAssigner.cs` **(create)** | The database-aware half: resolve the root domain, apply the rule, retry past collisions, attach the `DomainName`. The one thing every creation path calls. |
| `src/Harbora.Web/Controllers/AppsController.cs` **(modify)** | Creation calls the assigner instead of inlining the rule. |
| `src/Harbora.Infrastructure/Templates/TemplateDeploymentService.cs` **(modify)** | Same — deletes the hand-built `$"{slug}.{root}"`. |
| `src/Harbora.Infrastructure/Projects/PreviewEnvironmentService.cs` **(modify)** | Same, supplying its own branch-keyed candidate name. |
| `src/Harbora.Infrastructure/Projects/EnvironmentCloner.cs` **(modify)** | Gains an address for cloned apps, which it has never had. |
| `src/Harbora.Web/Controllers/AppsController.Addresses.cs` **(create)** | The backfill control: a preview action and an apply action, as a `partial class`. |
| `src/Harbora.Web/Views/Apps/Addresses.cshtml` **(create)** | The preview screen — which apps, what each would be called. |
| `src/Harbora.Web/Views/Apps/Details.cshtml` **(modify)** | Shows the address, or states why this kind has none. |
| `tests/Harbora.Tests/AppAddressTests.cs` **(create)** | The pure rule. |
| `tests/Harbora.Tests/AppAddressAssignerTests.cs` **(create)** | The assigner against an InMemory database. |
| `tests/Harbora.Tests/AppAddressCensusTests.cs` **(create)** | Every `db.Apps.Add` site went through the assigner. |
| `tests/Harbora.Tests/Http/AppAddressHttpTests.cs` **(create)** | The backfill screen and the Overview address, over real HTTP. |

---

## Task 1: The rule, with no database in it

**Files:**
- Create: `src/Harbora.Infrastructure/Networking/AppAddress.cs`
- Test: `tests/Harbora.Tests/AppAddressTests.cs`

**Interfaces:**
- Consumes: `ServicePlan.CanHaveDomains(ServiceKind)` and `ServicePlan.HostFor(ServiceKind, string?, string?, string?)` from `src/Harbora.Infrastructure/Deployments/ServicePlan.cs`; `ReservedHosts.IsReserved(string?, IEnumerable<string>)` from `src/Harbora.Infrastructure/Networking/ReservedHosts.cs`.
- Produces: `AppAddressOutcome` (enum), `AppAddressDecision` (readonly record struct), `AppAddress.Decide(...)`, `AppAddress.Discriminate(string, string)`.

**Why this is separate from Task 2.** `ServicePlan`'s own docstring says it is "kept pure so each rule is one testable statement rather than a condition buried in a 900-line pipeline". The same reasoning applies here: the decision about *what an address should be* is worth testing without a database standing in the way, and the retry loop that needs one is a different concern.

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/AppAddressTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What hostname an app should be given, decided without a database in the way.
///
/// Before this existed the answer depended on which of four creation paths the app came through:
/// three built it differently and the fourth never built one at all. A guarantee with four
/// implementations is not a guarantee.
/// </summary>
public class AppAddressTests
{
    private static readonly string[] NoReservedHosts = [];

    [Fact]
    public void A_web_app_under_a_root_domain_is_given_slug_dot_root()
    {
        var decision = AppAddress.Decide(ServiceKind.Web, requested: null, slug: "shop",
            rootDomain: "apps.example.com", reservedHosts: NoReservedHosts);

        decision.Outcome.Should().Be(AppAddressOutcome.Assigned);
        decision.Host.Should().Be("shop.apps.example.com");
    }

    [Fact]
    public void A_worker_is_given_nothing_and_says_why()
    {
        var decision = AppAddress.Decide(ServiceKind.Worker, requested: null, slug: "mailer",
            rootDomain: "apps.example.com", reservedHosts: NoReservedHosts);

        decision.Host.Should().BeNull();
        decision.Outcome.Should().Be(AppAddressOutcome.KindTakesNoTraffic,
            "a page that shows an empty slot with no reason is the promise-without-a-feature this project keeps removing");
    }

    [Fact]
    public void With_no_root_domain_configured_the_outcome_names_that_rather_than_looking_like_a_refusal()
    {
        var decision = AppAddress.Decide(ServiceKind.Web, requested: null, slug: "shop",
            rootDomain: null, reservedHosts: NoReservedHosts);

        decision.Host.Should().BeNull();
        decision.Outcome.Should().Be(AppAddressOutcome.NoRootDomain);
    }

    [Fact]
    public void A_platform_host_name_is_refused_rather_than_routed_to_an_app()
    {
        var decision = AppAddress.Decide(ServiceKind.Web, requested: "panel.example.com", slug: "shop",
            rootDomain: "apps.example.com", reservedHosts: ["panel.example.com"]);

        decision.Host.Should().BeNull();
        decision.Outcome.Should().Be(AppAddressOutcome.Reserved);
    }

    [Fact]
    public void A_typed_name_wins_over_the_derived_one()
    {
        var decision = AppAddress.Decide(ServiceKind.Web, requested: "Shop.Example.COM", slug: "shop",
            rootDomain: "apps.example.com", reservedHosts: NoReservedHosts);

        decision.Host.Should().Be("shop.example.com", "hostnames are compared lowercased everywhere else");
    }

    [Fact]
    public void The_discriminator_lands_on_the_leftmost_label_so_the_root_domain_still_matches_the_wildcard()
    {
        AppAddress.Discriminate("shop.apps.example.com", "k3f").Should().Be("shop-k3f.apps.example.com",
            "the certificate is a wildcard for *.apps.example.com — a suffix anywhere else would not be covered by it");
    }

    [Fact]
    public void A_single_label_host_can_still_be_discriminated()
    {
        AppAddress.Discriminate("shop", "k3f").Should().Be("shop-k3f");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppAddressTests"`

Expected: FAIL to compile — `The type or namespace name 'AppAddress' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Harbora.Infrastructure/Networking/AppAddress.cs`:

```csharp
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;

namespace Harbora.Infrastructure.Networking;

/// <summary>Why an app ended up with the address it did — or with none.</summary>
public enum AppAddressOutcome
{
    /// <summary>It got the name it asked for.</summary>
    Assigned = 0,

    /// <summary>The name was taken, so it got a discriminated one. The person is told.</summary>
    Discriminated = 1,

    /// <summary>This kind of service takes no inbound traffic, so an address would answer nothing.</summary>
    KindTakesNoTraffic = 2,

    /// <summary>No platform root domain is configured, so there is nothing to build a name under.</summary>
    NoRootDomain = 3,

    /// <summary>The name is one of the platform's own.</summary>
    Reserved = 4,

    /// <summary>Every discriminated attempt was taken too. Rare, and said out loud rather than skipped.</summary>
    Exhausted = 5
}

/// <summary>The decision, and the reason for it. <see cref="Host"/> is null unless one was settled on.</summary>
public readonly record struct AppAddressDecision(string? Host, AppAddressOutcome Outcome)
{
    public bool HasAddress => Host is not null;
}

/// <summary>
/// What hostname an app should be given.
///
/// Pure on purpose, for the reason <see cref="ServicePlan"/> gives about itself: each rule is one
/// testable statement rather than a condition buried in a creation path. There were four such paths
/// and they disagreed — one skipped silently on a clash, one checked nothing at all, one had no rule
/// whatsoever. The database half lives in <c>AppAddressAssigner</c>.
/// </summary>
public static class AppAddress
{
    /// <summary>
    /// The name this app should be given, or null with the reason why not.
    ///
    /// <paramref name="requested"/> is a name somebody typed, which wins over the derived one — and is
    /// still subject to every check below, because the reserved-host rule exists precisely for names
    /// people type.
    /// </summary>
    public static AppAddressDecision Decide(
        ServiceKind kind, string? requested, string? slug, string? rootDomain,
        IEnumerable<string> reservedHosts)
    {
        if (!ServicePlan.CanHaveDomains(kind))
            return new(null, AppAddressOutcome.KindTakesNoTraffic);

        var host = ServicePlan.HostFor(kind, requested, slug, rootDomain);
        if (string.IsNullOrWhiteSpace(host))
            return new(null, AppAddressOutcome.NoRootDomain);

        // "localhost" is what appsettings ships as RootDomain for a developer machine. Building
        // {slug}.localhost from it produces a name that resolves nowhere and a certificate request
        // that cannot be answered — TemplateDeploymentService already refused it by hand, and that
        // refusal belongs here with the rest of them.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return new(null, AppAddressOutcome.NoRootDomain);

        return ReservedHosts.IsReserved(host, reservedHosts)
            ? new(null, AppAddressOutcome.Reserved)
            : new(host, AppAddressOutcome.Assigned);
    }

    /// <summary>
    /// The same name with a discriminator on its leftmost label: <c>shop.apps.example.com</c> becomes
    /// <c>shop-k3f.apps.example.com</c>.
    ///
    /// Leftmost, because the certificate is a wildcard for <c>*.apps.example.com</c>. A discriminator
    /// added anywhere else would produce a name that is not covered by it, and the app would answer
    /// with a certificate error rather than a page — a worse outcome than the clash it was solving.
    /// </summary>
    public static string Discriminate(string host, string suffix)
    {
        var dot = host.IndexOf('.');
        return dot < 0 ? $"{host}-{suffix}" : $"{host[..dot]}-{suffix}{host[dot..]}";
    }
}
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppAddressTests"`

Expected: PASS, 7 of 7.

- [ ] **Step 5: Commit**

```bash
git add src/Harbora.Infrastructure/Networking/AppAddress.cs tests/Harbora.Tests/AppAddressTests.cs
git commit -m "Put the address rule in one place, with no database in it"
```

---

## Task 2: The assigner, which is the half that needs a database

**Files:**
- Create: `src/Harbora.Infrastructure/Networking/AppAddressAssigner.cs`
- Test: `tests/Harbora.Tests/AppAddressAssignerTests.cs`

**Interfaces:**
- Consumes: `AppAddress.Decide(...)`, `AppAddress.Discriminate(...)`, `AppAddressOutcome`, `AppAddressDecision` from Task 1.
- Produces: `AppAddressAssigner` with constructor `(HarboraDbContext db, IConfiguration config)`, and:
  - `Task<AppAddressDecision> AssignAsync(App app, string? requested, Func<string>? suffix, CancellationToken ct)` — decides, retries past collisions, and adds the `DomainName` to `app.Domains` when it settles on one. Does **not** call `SaveChangesAsync`; the caller owns the transaction.
  - `Task<AppAddressDecision> PreviewAsync(App app, CancellationToken ct)` — the same decision, writing nothing. Task 5's preview screen uses this rather than repeating the rule.
  - `Task<string?> RootDomainAsync(CancellationToken ct)` — the configured platform root domain, or null.

**Registration.** Add `services.AddScoped<AppAddressAssigner>();` alongside the other infrastructure registrations in `src/Harbora.Web/Program.cs`. Find the block that registers `RailPreferences` and put it there.

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/AppAddressAssignerTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Giving an app its address, against a real database.
///
/// The half of the rule that cannot be pure: whether a name is already taken is a question only the
/// database can answer, and what to do when it is taken is the behaviour this project got wrong —
/// AppsController skipped the insert and said nothing, so the app was created with no address and no
/// explanation.
/// </summary>
public class AppAddressAssignerTests
{
    private static HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("addr-" + Guid.NewGuid()).Options);

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection([]).Build();

    private static async Task<HarboraDbContext> DbWithRootDomain(string root)
    {
        var db = NewDb();
        db.Settings.Add(new Setting { Key = SettingKeys.PlatformRootDomain, Value = root });
        await db.SaveChangesAsync();
        return db;
    }

    private static App WebApp(string slug) => new()
    {
        Id = Guid.NewGuid(), Name = slug, Slug = slug, Kind = ServiceKind.Web, WorkspaceId = Guid.NewGuid()
    };

    [Fact]
    public async Task An_app_is_given_its_address_and_the_address_is_primary()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        var app = WebApp("shop");

        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, suffix: null, CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.Assigned);
        decision.Host.Should().Be("shop.apps.example.com");

        var added = app.Domains.Should().ContainSingle().Subject;
        added.Host.Should().Be("shop.apps.example.com");
        added.IsPrimary.Should().BeTrue("an app's own address is the one its links point at");
        added.SslEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_taken_name_produces_a_working_address_and_says_it_was_taken()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop.apps.example.com" });
        await db.SaveChangesAsync();

        var app = WebApp("shop");
        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, suffix: () => "k3f", CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.Discriminated,
            "the outcome is what the caller shows the person — silence here is the defect this replaces");
        decision.Host.Should().Be("shop-k3f.apps.example.com");
        app.Domains.Should().ContainSingle().Which.Host.Should().Be("shop-k3f.apps.example.com");
    }

    [Fact]
    public async Task A_worker_is_given_no_address_and_no_domain_row()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        var app = WebApp("mailer");
        app.Kind = ServiceKind.Worker;

        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, suffix: null, CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.KindTakesNoTraffic);
        app.Domains.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_root_domain_set_nothing_is_invented()
    {
        await using var db = NewDb();
        var app = WebApp("shop");

        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, suffix: null, CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.NoRootDomain);
        app.Domains.Should().BeEmpty();
    }

    [Fact]
    public async Task When_every_attempt_is_taken_it_says_so_rather_than_adding_nothing_quietly()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop.apps.example.com" });
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop-same.apps.example.com" });
        await db.SaveChangesAsync();

        var app = WebApp("shop");
        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, suffix: () => "same", CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.Exhausted);
        decision.Host.Should().BeNull();
        app.Domains.Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_taken_in_another_workspace_still_counts_as_taken()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop.apps.example.com" });
        await db.SaveChangesAsync();

        var app = WebApp("shop");
        var decision = await new AppAddressAssigner(db, EmptyConfig())
            .AssignAsync(app, requested: null, suffix: () => "k3f", CancellationToken.None);

        decision.Host.Should().Be("shop-k3f.apps.example.com",
            "DNS is not multi-tenant — two workspaces cannot both answer on one hostname");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppAddressAssignerTests"`

Expected: FAIL to compile — `The type or namespace name 'AppAddressAssigner' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Harbora.Infrastructure/Networking/AppAddressAssigner.cs`:

```csharp
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Networking;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Gives an app the address it should have — the one thing every path that creates an app calls.
///
/// There were four such paths and four different answers. AppsController skipped the insert when the
/// name was taken and told nobody. TemplateDeploymentService built the hostname by hand with no
/// kind check, no reserved-host check and no collision check. PreviewEnvironmentService had its own
/// third rule. EnvironmentCloner had none at all, so a cloned app was created with no address.
///
/// This does not call SaveChangesAsync. Every caller is already inside its own transaction — a save
/// here would commit half of somebody else's unit of work.
/// </summary>
public sealed class AppAddressAssigner(HarboraDbContext db, IConfiguration config)
{
    /// <summary>How many discriminated names to try before giving up and saying so.</summary>
    private const int MaxAttempts = 5;

    /// <summary>The platform's configured root domain, or null when none is set.</summary>
    public async Task<string?> RootDomainAsync(CancellationToken ct) =>
        await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.PlatformRootDomain)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// What this app would be given, writing nothing. The backfill preview screen renders this, and it
    /// must be the same answer <see cref="AssignAsync"/> would reach — a preview computed by a second
    /// copy of the rule is a preview that can lie about what the button will do.
    ///
    /// Collisions are deliberately not resolved here: the discriminator is chosen at assignment time,
    /// so promising a specific one on a screen the operator might sit on for a minute would be
    /// promising something this cannot keep.
    /// </summary>
    public async Task<AppAddressDecision> PreviewAsync(App app, CancellationToken ct) =>
        AppAddress.Decide(app.Kind, requested: null, app.Slug, await RootDomainAsync(ct), ReservedFor());

    private IReadOnlyList<string> ReservedFor() => ReservedHosts.ForPlatform(
        config["PANEL_DOMAIN"], config["NodeAgent:PublicUrl"], config["Storage:S3:PublicEndpoint"]);

    /// <summary>
    /// Decide this app's address and attach it. <paramref name="requested"/> is a name somebody typed,
    /// or a candidate a caller derives itself — branch previews pass their own branch-keyed name here,
    /// which is why the name is an input and only the checks are shared.
    ///
    /// <paramref name="suffix"/> exists so a test can pin the discriminator. Production passes null and
    /// gets a short random one.
    /// </summary>
    public async Task<AppAddressDecision> AssignAsync(
        App app, string? requested, Func<string>? suffix, CancellationToken ct)
    {
        var decision = AppAddress.Decide(
            app.Kind, requested, app.Slug, await RootDomainAsync(ct), ReservedFor());
        if (!decision.HasAddress) return decision;

        // IgnoreQueryFilters: a hostname taken by another workspace is still taken. DNS is not
        // multi-tenant, and a filtered query would report the name free and then route two apps to it.
        var host = decision.Host!;
        var outcome = AppAddressOutcome.Assigned;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (!await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.Host == host, ct))
            {
                app.Domains.Add(new DomainName
                {
                    Host = host, SslEnabled = true, ForceHttps = true, IsPrimary = true
                });
                return new(host, outcome);
            }

            host = AppAddress.Discriminate(decision.Host!, (suffix ?? NewSuffix)());
            outcome = AppAddressOutcome.Discriminated;
        }

        return new(null, AppAddressOutcome.Exhausted);
    }

    /// <summary>Three base-36 characters. Short enough to read aloud, wide enough that a second clash is rare.</summary>
    private static string NewSuffix() =>
        Guid.NewGuid().ToString("N")[..3];
}
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppAddressAssignerTests"`

Expected: PASS, 6 of 6.

- [ ] **Step 5: Register it**

In `src/Harbora.Web/Program.cs`, find the line registering `RailPreferences` and add beneath it:

```csharp
builder.Services.AddScoped<Harbora.Infrastructure.Networking.AppAddressAssigner>();
```

- [ ] **Step 6: Build and commit**

```bash
dotnet build Harbora.slnx -c Debug
```

Expected: `0 Error(s)`, `2 Warning(s)`.

```bash
git add src/Harbora.Infrastructure/Networking/AppAddressAssigner.cs tests/Harbora.Tests/AppAddressAssignerTests.cs src/Harbora.Web/Program.cs
git commit -m "Give the address rule the one database question it cannot answer alone"
```

---

## Task 3: Every door calls it, and the three other rules are deleted

**Files:**
- Modify: `src/Harbora.Web/Controllers/AppsController.cs:287-300`
- Modify: `src/Harbora.Infrastructure/Templates/TemplateDeploymentService.cs:262-273`
- Modify: `src/Harbora.Infrastructure/Projects/PreviewEnvironmentService.cs:204-215`
- Modify: `src/Harbora.Infrastructure/Projects/EnvironmentCloner.cs:230-306`
- Test: `tests/Harbora.Tests/AppAddressAssignerTests.cs` (extend)

**Interfaces:**
- Consumes: `AppAddressAssigner.AssignAsync(App, string?, Func<string>?, CancellationToken)` and `AppAddressOutcome` from Task 2.
- Produces: nothing new. This task deletes code.

**The one thing to get right.** `PreviewEnvironmentService` keeps its own **name** — `PreviewNaming.Host(parent.Slug, branch, rootDomain)` — because two previews of the same app must not collide by construction. What it stops doing is deciding for itself about kind, reserved hosts and collisions. It passes its name as `requested`. The name differs; the checks do not.

- [ ] **Step 1: Write the failing test**

Append to `tests/Harbora.Tests/AppAddressAssignerTests.cs`:

```csharp
    [Fact]
    public async Task A_caller_supplied_name_still_goes_through_the_collision_check()
    {
        await using var db = await DbWithRootDomain("apps.example.com");
        db.Domains.Add(new DomainName { AppId = Guid.NewGuid(), Host = "shop-main.apps.example.com" });
        await db.SaveChangesAsync();

        var app = WebApp("shop");
        var decision = await new AppAddressAssigner(db, EmptyConfig()).AssignAsync(
            app, requested: "shop-main.apps.example.com", suffix: () => "k3f", CancellationToken.None);

        decision.Outcome.Should().Be(AppAddressOutcome.Discriminated,
            "branch previews bring their own name and must still not be given one that is taken");
        decision.Host.Should().Be("shop-main-k3f.apps.example.com");
    }
```

- [ ] **Step 2: Run it and watch it pass already**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~A_caller_supplied_name"`

Expected: PASS. Task 2 already built this behaviour; this test pins it before four callers start depending on it. Say so in the commit message rather than pretending it was red.

- [ ] **Step 3: Rewrite the AppsController path**

In `src/Harbora.Web/Controllers/AppsController.cs`, replace lines 287-300 (the block from the comment `// Domain: use the one given…` through the `app.Domains.Add(...)` line) with:

```csharp
        // One rule, one place. This used to derive the host here, check reserved names here, and then
        // silently skip the insert when the name was taken — so an app could be created with no
        // address and no explanation. AppAddressAssigner answers all three, and says which happened.
        var addressed = await addresses.AssignAsync(app, model.Domain, suffix: null, ct);
        if (addressed.Outcome == AppAddressOutcome.Reserved)
        {
            ModelState.AddModelError(nameof(model.Domain), ReservedHostRefusal(model.Domain!));
            await PopulateTemplates(ct);
            return View(model);
        }
        if (addressed.Outcome == AppAddressOutcome.Discriminated)
            TempData["Message"] = IsFa
                ? $"نام درخواستی گرفته شده بود؛ این اپ روی «{addressed.Host}» در دسترس است."
                : $"That name was taken, so this app is reachable at '{addressed.Host}'.";
```

Add `AppAddressAssigner addresses` to the controller's primary constructor parameter list, and `using Harbora.Infrastructure.Networking;` to its usings.

- [ ] **Step 4: Rewrite the template path**

In `src/Harbora.Infrastructure/Templates/TemplateDeploymentService.cs`, replace lines 262-273 (from `var rootDomain = await db.Settings` through the closing `});` of the `app.Domains.Add`) with:

```csharp
        // Was a hand-built $"{appSlug}.{rootDomain}" with no kind check, no reserved-host check and no
        // collision check — three ways to hand somebody a hostname that answers nothing.
        await addresses.AssignAsync(app, requested: null, suffix: null, ct);
```

Add `AppAddressAssigner addresses` to this service's constructor and `using Harbora.Infrastructure.Networking;` to its usings.

- [ ] **Step 5: Rewrite the preview path**

In `src/Harbora.Infrastructure/Projects/PreviewEnvironmentService.cs`, replace lines 204-215 (from `var rootDomain = await db.Settings.IgnoreQueryFilters()` through the closing `}` of the `if` block that adds the domain) with:

```csharp
        // Its own address, never the parent's: a preview that answered on production's hostname would
        // be the worst possible outcome of this feature. The NAME is still this service's own — two
        // previews of one app must not collide by construction — but the checks around it are shared.
        var rootDomain = await addresses.RootDomainAsync(ct);
        await addresses.AssignAsync(
            preview, PreviewNaming.Host(parent.Slug, branch, rootDomain), suffix: null, ct);
```

Add `AppAddressAssigner addresses` to this service's constructor and `using Harbora.Infrastructure.Networking;` to its usings.

- [ ] **Step 6: Give the cloner an address, which it never had**

In `src/Harbora.Infrastructure/Projects/EnvironmentCloner.cs`, immediately before `db.Apps.Add(copy);` at line 306, insert:

```csharp
            // A cloned app used to arrive with no address at all — the one creation path that had no
            // rule rather than a wrong one. Its slug differs from the original's (spec.Slug), so this
            // does not contend with the app it was copied from.
            await addresses.AssignAsync(copy, requested: null, suffix: null, ct);
```

Add `AppAddressAssigner addresses` to this service's constructor and `using Harbora.Infrastructure.Networking;` to its usings.

- [ ] **Step 7: Build, then run the full suite**

```bash
dotnet build-server shutdown && dotnet build Harbora.slnx -c Debug
```

Expected: `0 Error(s)`, `2 Warning(s)`. Constructor changes ripple into whatever constructs these services — fix each call site rather than adding an optional parameter with a default, which would let a caller silently keep the old behaviour.

```bash
dotnet test Harbora.slnx -c Debug --no-build
```

Expected: `Failed: 0`. The count rises by exactly 1 from the new test.

- [ ] **Step 8: Commit**

```bash
git add src/Harbora.Web/Controllers/AppsController.cs src/Harbora.Infrastructure/Templates/TemplateDeploymentService.cs src/Harbora.Infrastructure/Projects/PreviewEnvironmentService.cs src/Harbora.Infrastructure/Projects/EnvironmentCloner.cs tests/Harbora.Tests/AppAddressAssignerTests.cs
git commit -m "Send all four doors through the one address rule"
```

---

## Task 4: The census that stops a fifth door

**Files:**
- Create: `tests/Harbora.Tests/AppAddressCensusTests.cs`

**Interfaces:**
- Consumes: nothing at compile time. It reads source files off disk, following `DetailTabCensusTests` (`tests/Harbora.Tests/DetailTabCensusTests.cs`) and `TestPaths.WebRoot`.
- Produces: nothing.

**Why this exists.** The defect this whole sub-project fixes was four independent implementations of one rule. Fixing them once fixes today. This is what fixes tomorrow — the fifth creation path somebody adds next quarter, who will not read this plan.

**`TestPaths` needs a second root.** It currently exposes only `WebRoot`, and its `Find()` hard-codes `"src", "Harbora.Web"`. Generalise it first — this is Step 1.

- [ ] **Step 1: Teach `TestPaths` about the second project**

Replace the body of `tests/Harbora.Tests/TestPaths.cs` with:

```csharp
namespace Harbora.Tests;

/// <summary>Locating source files the tests read, from wherever the runner happens to start.</summary>
public static class TestPaths
{
    /// <summary>The Harbora.Web project directory.</summary>
    public static string WebRoot { get; } = Find("Harbora.Web");

    /// <summary>The Harbora.Infrastructure project directory.</summary>
    public static string InfrastructureRoot { get; } = Find("Harbora.Infrastructure");

    private static string Find(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", project);
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate src/{project} from the test output directory.");
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/Harbora.Tests/AppAddressCensusTests.cs`:

```csharp
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Every place that creates an app hands it to the one address rule.
///
/// This suite exists because fixing the four paths fixes today. There were four, they disagreed, and
/// nobody noticed until somebody asked why a cloned app had no URL. The fifth path — added next
/// quarter by somebody who never read the spec — is what this catches, on the day it is written.
///
/// Reads the source rather than a list kept by hand, for the reason DetailTabCensusTests gives: a
/// hand-kept list is checked by a reviewer noticing an addition is missing from it, and a reviewer
/// noticing is exactly the step a real gap slips past.
/// </summary>
public class AppAddressCensusTests
{
    /// <summary>
    /// Files that create an app and are exempt, each with the reason. Keep this empty if you can — an
    /// entry here is a path where the guarantee does not hold, and it should read like one.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new();

    [Fact]
    public void Every_file_that_adds_an_app_to_the_database_also_assigns_its_address()
    {
        var roots = new[] { TestPaths.WebRoot, TestPaths.InfrastructureRoot };

        var creators = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path: f, Text: File.ReadAllText(f)))
            .Where(f => Regex.IsMatch(f.Text, @"\.Apps\.Add\("))
            .ToList();

        creators.Should().NotBeEmpty(
            "a regex that matches nothing would pass this test for ever — there are at least four such files");

        var missing = creators
            .Where(f => !Exempt.ContainsKey(Path.GetFileName(f.Path)))
            .Where(f => !f.Text.Contains("AssignAsync", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f.Path))
            .ToList();

        missing.Should().BeEmpty(
            "a path that creates an app without assigning its address is how this project ended up with " +
            "four different answers to one question — add the AssignAsync call, or an Exempt entry saying why not");
    }
}
```

- [ ] **Step 3: Run it and confirm it passes**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppAddressCensus"`

Expected: PASS. Task 3 wired all four.

- [ ] **Step 4: Prove it bites**

A census that reads source and asserts a correspondence is exactly the kind that can pass vacuously. Prove it fails before trusting it. Copy the file aside first — **never** `git checkout --`, this is a shared tree:

```bash
cp src/Harbora.Infrastructure/Projects/EnvironmentCloner.cs /tmp/cloner.bak
sed -i 's/await addresses.AssignAsync(copy, requested: null, suffix: null, ct);//' src/Harbora.Infrastructure/Projects/EnvironmentCloner.cs
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppAddressCensus"
```

Expected: FAIL, naming `EnvironmentCloner.cs`.

```bash
cp /tmp/cloner.bak src/Harbora.Infrastructure/Projects/EnvironmentCloner.cs
git status --porcelain
```

Expected: no changes to `EnvironmentCloner.cs`. Put both outputs in your report. **If the census cannot be made to fail, it is not a census — say so rather than committing it.**

- [ ] **Step 5: Commit**

```bash
git add tests/Harbora.Tests/TestPaths.cs tests/Harbora.Tests/AppAddressCensusTests.cs
git commit -m "Add the census that stops a fifth door writing its own address rule"
```

---

## Task 5: The backfill, as a control the operator presses

**Files:**
- Create: `src/Harbora.Web/Controllers/AppsController.Addresses.cs`
- Create: `src/Harbora.Web/Views/Apps/Addresses.cshtml`
- Test: `tests/Harbora.Tests/Http/AppAddressHttpTests.cs`

**Interfaces:**
- Consumes: `AppAddressAssigner.AssignAsync(...)`, `AppAddressAssigner.RootDomainAsync(...)`, `AppAddressOutcome` from Task 2.
- Produces: `GET /apps/addresses` (preview) and `POST /apps/addresses` (apply), plus `AppAddressPreviewViewModel`.

**Why a control and not a sweep.** This is the only part of B1 that rewrites live Traefik routing. The owner chose an explicit control: the operator sees which apps are affected and what each would be called, then decides. Setting the root domain does not silently rewrite anything.

**The property that matters most.** An app that already has a domain is never touched. The failure worth guarding against is not "an app has no address" — it is "an app that had a working custom domain lost it".

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/Http/AppAddressHttpTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The backfill screen, over real HTTP.
///
/// This is the only part of the address work that rewrites live Traefik routing, which is why it is a
/// control the operator presses rather than a sweep that happens to them. The assertion this file
/// exists for is the third one: an app that already had a working custom domain still has it
/// afterwards. "An app has no address" is an inconvenience; "an app that had one lost it" is an
/// outage.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppAddressHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid SeedApp(string slug, ServiceKind kind, string? withDomain)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = kind,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };

        Panel.Seed(db =>
        {
            db.Apps.Add(app);
            if (withDomain is not null)
                db.Domains.Add(new DomainName
                {
                    AppId = app.Id, Host = withDomain, SslEnabled = true, ForceHttps = true, IsPrimary = true
                });
        });

        return app.Id;
    }

    private void SeedRootDomain(string root) => Panel.Seed(db =>
        db.Settings.Add(new Setting { Key = SettingKeys.PlatformRootDomain, Value = root }));

    [Fact]
    public async Task The_preview_lists_an_addressless_app_and_the_name_it_would_be_given()
    {
        SeedRootDomain("apps.example.com");
        SeedApp("addr-preview-shop", ServiceKind.Web, withDomain: null);
        Panel.GivenUser(fixture.WorkspaceId, "addr-preview@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.200", "addr-preview@example.com");

        var html = await (await client.GetAsync("/apps/addresses")).Content.ReadAsStringAsync();

        html.Should().Contain("addr-preview-shop.apps.example.com",
            "a hostname is not translated, so this holds whichever language rendered the page");
    }

    [Fact]
    public async Task The_preview_does_not_offer_to_rename_an_app_that_already_has_a_domain()
    {
        SeedRootDomain("apps.example.com");
        SeedApp("addr-keeps-its-own", ServiceKind.Web, withDomain: "chosen.example.com");
        Panel.GivenUser(fixture.WorkspaceId, "addr-keeps@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.201", "addr-keeps@example.com");

        var html = await (await client.GetAsync("/apps/addresses")).Content.ReadAsStringAsync();

        html.Should().NotContain("addr-keeps-its-own",
            "an app with a domain is not addressless, so it has no business on a screen offering to give it one");
    }

    [Fact]
    public async Task Applying_the_backfill_leaves_an_existing_custom_domain_exactly_as_it_was()
    {
        SeedRootDomain("apps.example.com");
        var kept = SeedApp("addr-apply-kept", ServiceKind.Web, withDomain: "chosen.example.com");
        var given = SeedApp("addr-apply-given", ServiceKind.Web, withDomain: null);
        Panel.GivenUser(fixture.WorkspaceId, "addr-apply@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.202", "addr-apply@example.com");

        var response = await client.PostAsync("/apps/addresses",
            new FormUrlEncodedContent([new KeyValuePair<string, string>(
                "__RequestVerificationToken", await Panel.AntiForgeryTokenAsync(client, "/apps/addresses"))]));
        response.IsSuccessStatusCode.Should().BeTrue();

        Panel.Read(db =>
        {
            var untouched = db.Domains.Where(d => d.AppId == kept).ToList();
            untouched.Should().ContainSingle().Which.Host.Should().Be("chosen.example.com",
                "this is the failure that would matter: not a missing address, but a working one replaced");
            untouched[0].IsPrimary.Should().BeTrue();

            db.Domains.Where(d => d.AppId == given).Should().ContainSingle()
                .Which.Host.Should().Be("addr-apply-given.apps.example.com");
        });
    }

    [Fact]
    public async Task A_worker_is_not_offered_an_address_because_nothing_would_answer_on_it()
    {
        SeedRootDomain("apps.example.com");
        SeedApp("addr-worker", ServiceKind.Worker, withDomain: null);
        Panel.GivenUser(fixture.WorkspaceId, "addr-worker@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.203", "addr-worker@example.com");

        var html = await (await client.GetAsync("/apps/addresses")).Content.ReadAsStringAsync();

        html.Should().NotContain("addr-worker.apps.example.com",
            "a worker takes no inbound traffic — an address for it is a certificate nothing ever answers on");
    }

    [Fact]
    public async Task Setting_the_root_domain_does_not_by_itself_change_any_existing_app()
    {
        var untouched = SeedApp("addr-untouched", ServiceKind.Web, withDomain: null);
        SeedRootDomain("apps.example.com");
        Panel.GivenUser(fixture.WorkspaceId, "addr-untouched@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.204", "addr-untouched@example.com");

        // Merely looking at the preview must write nothing — it is a GET.
        await client.GetAsync("/apps/addresses");

        Panel.Read(db => db.Domains.Where(d => d.AppId == untouched).Should().BeEmpty(
            "the backfill is a control the operator presses, not something that happens to them"));
    }
}
```

**Two harness members to confirm before writing this.** `Panel.AntiForgeryTokenAsync(client, path)` and `Panel.Read(action)` are the names used above. Open `tests/Harbora.Tests/Http/` and check what the factory actually calls them — other tests in that folder post forms and read the database back, so both capabilities exist. If the real names differ, use the real ones and note the correction in your report. Do **not** invent a helper that is not there.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppAddressHttpTests"`

Expected: FAIL — 404, because the route does not exist yet.

- [ ] **Step 3: Write the controller**

Create `src/Harbora.Web/Controllers/AppsController.Addresses.cs`:

```csharp
using Harbora.Domain.Apps;
using Harbora.Infrastructure.Networking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>What an app without an address would be called, and the button that gives it one.</summary>
/// <param name="Slug">The app's slug, so the operator recognises it.</param>
/// <param name="Candidate">The hostname it would be given, or null with a reason.</param>
/// <param name="Reason">Why it would get nothing — null when Candidate is set.</param>
public record AppAddressCandidate(Guid Id, string Name, string Slug, string? Candidate, string? Reason);

public sealed record AppAddressPreviewViewModel(
    string? RootDomain, IReadOnlyList<AppAddressCandidate> Candidates);

public partial class AppsController
{
    /// <summary>
    /// Which apps have no address, and what each would be given.
    ///
    /// A preview rather than a sweep: this rewrites live Traefik routing, and an operator who cannot
    /// see what a button will do before pressing it has not been given a choice.
    /// </summary>
    [HttpGet("apps/addresses")]
    public async Task<IActionResult> Addresses(CancellationToken ct)
    {
        var rootDomain = await addresses.RootDomainAsync(ct);

        var addressless = await db.Apps
            .Where(a => a.WorkspaceId == WorkspaceId && !a.Domains.Any())
            .OrderBy(a => a.Slug)
            .ToListAsync(ct);

        var candidates = new List<AppAddressCandidate>();
        foreach (var app in addressless)
        {
            // PreviewAsync, not a second copy of the rule: a preview computed separately from the
            // assignment is a preview that can disagree with what the button does, and the whole point
            // of this screen is that it does not.
            var decision = await addresses.PreviewAsync(app, ct);
            candidates.Add(new AppAddressCandidate(
                app.Id, app.Name, app.Slug, decision.Host, ReasonFor(decision.Outcome)));
        }

        return View(new AppAddressPreviewViewModel(rootDomain, candidates));
    }

    /// <summary>Gives every listed app the address the preview showed.</summary>
    [HttpPost("apps/addresses")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyAddresses(CancellationToken ct)
    {
        // Only apps with no domain at all. An app that already has one is never touched: the failure
        // worth guarding against here is not "an app has no address", it is "an app that had a working
        // custom domain lost it".
        var addressless = await db.Apps
            .Include(a => a.Domains)
            .Where(a => a.WorkspaceId == WorkspaceId && !a.Domains.Any())
            .ToListAsync(ct);

        var given = 0;
        foreach (var app in addressless)
            if ((await addresses.AssignAsync(app, requested: null, suffix: null, ct)).HasAddress)
                given++;

        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa
            ? $"{given} اپ آدرس گرفت."
            : $"{given} app(s) were given an address.";
        return RedirectToAction(nameof(Addresses));
    }

    private string? ReasonFor(AppAddressOutcome outcome) => outcome switch
    {
        AppAddressOutcome.KindTakesNoTraffic => IsFa
            ? "این سرویس ترافیک ورودی ندارد، پس آدرسی نمی‌گیرد."
            : "This service takes no inbound traffic, so it gets no address.",
        AppAddressOutcome.NoRootDomain => IsFa
            ? "دامنهٔ اصلی پلتفرم تنظیم نشده است."
            : "No platform root domain is configured.",
        AppAddressOutcome.Reserved => IsFa
            ? "این نام یکی از نام‌های خودِ سامانه است."
            : "That name is one of the platform's own.",
        _ => null
    };
}
```

Ensure `AppsController` is declared `public partial class` in `AppsController.cs`, and that `config` is available on it (it already is — `IsReservedHost` at line 55 uses it).

- [ ] **Step 4: Write the view**

Create `src/Harbora.Web/Views/Apps/Addresses.cshtml`. Follow the markup conventions in `src/Harbora.Web/Views/Apps/Index.cshtml` — same card, table and button classes. It must show, for each candidate, the app name and either the hostname it would get or the reason it would get none; a submit button posting to `ApplyAddresses` with `@Html.AntiForgeryToken()`; and, when the list is empty, a plain statement that every app already has an address rather than a blank card.

Every visible string goes through `@T["…"]` or the `isFa` ternary.

- [ ] **Step 5: Run the tests and watch them pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppAddressHttpTests"`

Expected: PASS, 5 of 5.

- [ ] **Step 6: Commit**

```bash
git add src/Harbora.Web/Controllers/AppsController.Addresses.cs src/Harbora.Web/Views/Apps/Addresses.cshtml tests/Harbora.Tests/Http/AppAddressHttpTests.cs
git commit -m "Let the operator see what the backfill would do before it does it"
```

---

## Task 6: The address on Overview, and the reason when there is none

**Files:**
- Modify: `src/Harbora.Web/Views/Apps/Details.cshtml:491-505`
- Test: `tests/Harbora.Tests/Http/AppAddressHttpTests.cs` (extend)

**Interfaces:**
- Consumes: `ServicePlan.CanHaveDomains(ServiceKind)`; the `app.Domains` collection the Domains panel already renders.
- Produces: nothing.

**What this fills.** Sub-project A's plan deliberately built no empty slot for this, on the grounds that a blank labelled space is a promise without a feature. The feature now exists, so the space is earned.

- [ ] **Step 1: Write the failing test**

Append to `tests/Harbora.Tests/Http/AppAddressHttpTests.cs`:

```csharp
    [Fact]
    public async Task An_apps_overview_shows_its_address_as_a_link_you_can_follow()
    {
        var id = SeedApp("addr-overview-shop", ServiceKind.Web, withDomain: "addr-overview-shop.apps.example.com");
        Panel.GivenUser(fixture.WorkspaceId, "addr-overview@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.205", "addr-overview@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        // On the href, not the bare hostname: the hostname also appears in the Domains table further
        // down the page, so Contain("addr-overview-shop.apps.example.com") would pass whether or not
        // the link this test is about was ever built.
        html.Should().Contain("href=\"https://addr-overview-shop.apps.example.com\"",
            "the address is meant to be one click, not something to read and retype");
    }

    [Fact]
    public async Task A_workers_overview_states_why_it_has_no_address_instead_of_showing_a_gap()
    {
        var id = SeedApp("addr-overview-worker", ServiceKind.Worker, withDomain: null);
        Panel.GivenUser(fixture.WorkspaceId, "addr-ovw-worker@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.206", "addr-ovw-worker@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        // On the marker, not the sentence: the panel renders Persian by default, so an assertion on
        // the English wording would never match — and one on the Persian wording would break the day
        // somebody improves the phrasing, which is not what this test is about.
        html.Should().Contain("data-address-state=\"no-traffic\"",
            "an unexplained blank is the promise-without-a-feature this project keeps removing");
    }
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~overview"`

Expected: FAIL — neither the link nor the explanation is on the page.

- [ ] **Step 3: Add the address block**

In `src/Harbora.Web/Views/Apps/Details.cshtml`, immediately above the `@* ---- Domains ---- *@` comment at line 491, insert a block that:

- when `app.Domains.FirstOrDefault(d => d.IsPrimary)` is non-null, renders its host as an `<a href="https://{host}">` opening in a new tab, beside a copy control using the same `data-copy-text` attribute the prebuilt-image block at line 310 already uses;
- when the app has no primary domain and `ServicePlan.CanHaveDomains(app.Kind)` is false, renders a one-line explanation carrying `data-address-state="no-traffic"`;
- when the app has no primary domain and the kind *can* have one, renders a one-line statement with `data-address-state="none"` and a link to `/apps/addresses`.

Every visible string goes through `@T["…"]` or the `isFa` ternary.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppAddressHttpTests"`

Expected: PASS, 7 of 7.

- [ ] **Step 5: Run the full suite**

```bash
dotnet build-server shutdown && dotnet build Harbora.slnx -c Debug && dotnet test Harbora.slnx -c Debug --no-build
```

Expected: `0 Error(s)`, `2 Warning(s)`, `Failed: 0` in all three assemblies.

- [ ] **Step 6: Commit**

```bash
git add src/Harbora.Web/Views/Apps/Details.cshtml tests/Harbora.Tests/Http/AppAddressHttpTests.cs
git commit -m "Show an app its address, or tell it why it has none"
```

---

## What this plan is not

The private in-network address (B2) · pod specifics (B3) · any change to how custom domains are added, which works · certificate issuance, which `DomainName.SslEnabled` and the existing ACME path already handle · the routing guide, which belongs to sub-project G.

**No database migration.** `DomainName` already carries `Host`, `SslEnabled`, `ForceHttps` and `IsPrimary`.

**No automatic backfill.** Decided by the owner. Setting the root domain rewrites nothing on its own; the operator presses the control in Task 5.
