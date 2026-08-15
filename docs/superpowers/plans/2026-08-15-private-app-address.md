# Private App Address Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An app is reachable from its neighbours at `http://{slug}:{port}` — a name that survives a deploy — unless that name would be ambiguous, in which case it is not registered at all and the app's page says why.

**Architecture:** `DeploymentPipeline` starts containers in two places and only the compose one passes `NetworkAliases`. The single-container call site starts passing it too. A collision check reads the `harbora.compose.service` label off containers belonging to apps in the same environment, and withholds the alias rather than registering an ambiguous one.

**Tech Stack:** .NET 10, Docker.DotNet, EF Core, xUnit, FluentAssertions, Razor.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-15-private-app-address-design.md`. Read it before Task 1.
- **Zero NEW build warnings.** `dotnet build Harbora.slnx -c Debug` reports exactly **2 pre-existing NU1903** NuGet-audit warnings on SSH.NET in `Harbora.Postgres.Tests`. Leave them — do not suppress or upgrade. **Security is out of scope by the owner's standing instruction.**
- **Baseline that must not drop:** build 0 errors; **4,233 passing, 0 failing**; 17 Docker-gated and 72 Postgres-lane skips.
- **Exactly one database migration**, in Task 2, adding `Apps.PrivateAddressState`. Verified while writing this plan: `MigrationConsistencyTests.The_model_has_no_changes_that_are_missing_from_a_migration` diffs the model against the snapshot, so a new column without one turns the suite red. Generate it with a **fresh build** — `dotnet ef migrations add` with `--no-build` against a stale assembly captures the previous model and produces a migration that is quietly wrong, which that same test then catches.
- **Never renumber an existing enum value.** Append only.
- **Bilingual.** Every string a user sees goes through `@T["…"]` or the `isFa` ternary. An English-only label is a defect.
- **The panel renders Persian by default in tests.** Assert on `data-` attributes, route fragments, or the alias string — never on a sentence in either language.
- **Test names read as sentences.**
- **Narrative commit messages** — read `git log --oneline -10` for the register. End each with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Shared tree.** Never run `git stash`, `git stash pop`, `git checkout -- .`, `git reset --hard` or `git clean`. Stage by explicit path; never `git add -A`.
- **Environmental trap.** A build can fail with `MSB3491 "Access to the path … denied"` on `obj/` files while the suite is green — leftover MSBuild processes hold locks. Run `dotnet build-server shutdown`, then rebuild.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Harbora.Domain/Common/PrivateAddressOutcome.cs` **(create)** | The outcome enum. In Domain, not Infrastructure: `Harbora.Domain.csproj` references only `Harbora.Shared`, so an `App` property cannot name a type from Infrastructure. |
| `src/Harbora.Infrastructure/Networking/PrivateAddress.cs` **(create)** | The pure decision: may this kind have a private name, what is the name, is a candidate ambiguous against a set of taken names. No Docker, no database. |
| `src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs` **(modify)** | Asks the collision question and passes the alias on the single-container path. |
| `src/Harbora.Domain/Apps/App.cs` **(modify)** | Records whether the last deployment registered the alias, so the page can say why not. |
| `src/Harbora.Web/Views/Apps/Details.cshtml` **(modify)** | Shows the private address beside the public one. |
| `tests/Harbora.Tests/PrivateAddressTests.cs` **(create)** | The pure rule. |
| `tests/Harbora.Tests/PrivateAddressPipelineTests.cs` **(create)** | The alias reaches the run request; a collision withholds it; a collision does not fail the deploy. |
| `tests/Harbora.Tests/Http/PrivateAddressHttpTests.cs` **(create)** | What the page shows in each state. |

---

## Task 1: The rule, with no Docker and no database in it

**Files:**
- Create: `src/Harbora.Infrastructure/Networking/PrivateAddress.cs`
- Test: `tests/Harbora.Tests/PrivateAddressTests.cs`

**Interfaces:**
- Consumes: `ServicePlan.JoinsInternalNetwork(ServiceKind)` from `src/Harbora.Infrastructure/Deployments/ServicePlan.cs`; `ServiceKind` from `Harbora.Domain.Common`.
- Produces: `Harbora.Domain.Common.PrivateAddressOutcome` (enum), `PrivateAddressDecision` (readonly record struct), `PrivateAddress.Decide(ServiceKind, string?, IReadOnlyCollection<string>)`, `PrivateAddress.Url(string, int)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/PrivateAddressTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whether an app gets a name its neighbours can call it by, decided without Docker or a database.
///
/// The mechanism this feeds already existed and was already used — for compose services only. An
/// ordinary app was reachable solely as harbora-{slug}-{number}, and the deployment number in that
/// name changes every time it ships.
/// </summary>
public class PrivateAddressTests
{
    private static readonly string[] NothingTaken = [];

    [Fact]
    public void An_ordinary_app_is_reachable_by_its_slug()
    {
        var decision = PrivateAddress.Decide(ServiceKind.Web, "shop", NothingTaken);

        decision.Outcome.Should().Be(PrivateAddressOutcome.Registered);
        decision.Alias.Should().Be("shop");
    }

    [Fact]
    public void A_worker_gets_one_too_because_its_siblings_may_scrape_it()
    {
        PrivateAddress.Decide(ServiceKind.Worker, "mailer", NothingTaken)
            .Outcome.Should().Be(PrivateAddressOutcome.Registered,
                "ServicePlan.JoinsInternalNetwork is true for a worker — a metrics port its siblings " +
                "read is the case that rule was written for");
    }

    [Fact]
    public void A_release_task_gets_none_because_it_runs_once_and_exits()
    {
        var decision = PrivateAddress.Decide(ServiceKind.ReleaseTask, "migrate", NothingTaken);

        decision.Alias.Should().BeNull();
        decision.Outcome.Should().Be(PrivateAddressOutcome.KindDoesNotJoin);
    }

    [Fact]
    public void A_name_another_container_already_answers_to_is_not_registered()
    {
        var decision = PrivateAddress.Decide(ServiceKind.Web, "db", ["db", "cache"]);

        decision.Alias.Should().BeNull(
            "docker balances between every container holding an alias, so registering this one sends " +
            "some calls to a stranger — an app reaching the wrong database is worse than no shortcut");
        decision.Outcome.Should().Be(PrivateAddressOutcome.Ambiguous);
    }

    [Fact]
    public void The_comparison_ignores_case_because_dns_does()
    {
        PrivateAddress.Decide(ServiceKind.Web, "DB", ["db"])
            .Outcome.Should().Be(PrivateAddressOutcome.Ambiguous);
    }

    [Fact]
    public void An_app_with_no_slug_gets_nothing_rather_than_an_empty_alias()
    {
        PrivateAddress.Decide(ServiceKind.Web, "  ", NothingTaken)
            .Outcome.Should().Be(PrivateAddressOutcome.NoSlug);
    }

    [Fact]
    public void The_url_carries_the_apps_own_port_not_a_guess()
    {
        PrivateAddress.Url("shop", 8080).Should().Be("http://shop:8080");
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~PrivateAddressTests"`

Expected: FAIL to compile — `The type or namespace name 'PrivateAddress' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Harbora.Infrastructure/Networking/PrivateAddress.cs`:

Create `src/Harbora.Domain/Common/PrivateAddressOutcome.cs`:

```csharp
namespace Harbora.Domain.Common;

/// <summary>Why an app can or cannot be called by a short name inside its own environment.</summary>
public enum PrivateAddressOutcome
{
    /// <summary>It answers to its slug.</summary>
    Registered = 0,

    /// <summary>Something else on the network already answers to that name, so this one is withheld.</summary>
    Ambiguous = 1,

    /// <summary>This kind does not join the internal network at all.</summary>
    KindDoesNotJoin = 2,

    /// <summary>No slug to build a name from.</summary>
    NoSlug = 3
}
```

It lives here rather than beside `PrivateAddress` because `App` carries it as a column and
`Harbora.Domain.csproj` references only `Harbora.Shared` — a Domain entity cannot name an
Infrastructure type. Same reasoning that put `ServiceKind` here.

Then create `src/Harbora.Infrastructure/Networking/PrivateAddress.cs`:

```csharp
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;

namespace Harbora.Infrastructure.Networking;

/// <summary>The name, and the reason. <see cref="Alias"/> is null unless one was settled on.</summary>
public readonly record struct PrivateAddressDecision(string? Alias, PrivateAddressOutcome Outcome)
{
    public bool HasAlias => Alias is not null;
}

/// <summary>
/// The short name an app's neighbours can reach it by.
///
/// Pure, for the reason <see cref="ServicePlan"/> gives about itself: one testable statement rather
/// than a condition buried in a deployment path where getting it wrong fails a deploy. The Docker and
/// database halves stay in the pipeline.
/// </summary>
public static class PrivateAddress
{
    /// <summary>
    /// The alias this app should answer to, or null with the reason why not.
    ///
    /// <paramref name="taken"/> is every name already answered to on this network by somebody else.
    /// Docker resolves an alias to <b>every</b> container holding it and balances between them, so a
    /// duplicate does not fail loudly — it sends a share of the calls to a stranger. An app that
    /// reaches the wrong database intermittently is worse off than one with no shortcut at all.
    /// </summary>
    public static PrivateAddressDecision Decide(
        ServiceKind kind, string? slug, IReadOnlyCollection<string> taken)
    {
        if (!ServicePlan.JoinsInternalNetwork(kind))
            return new(null, PrivateAddressOutcome.KindDoesNotJoin);

        var alias = slug?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(alias))
            return new(null, PrivateAddressOutcome.NoSlug);

        return taken.Any(t => string.Equals(t?.Trim(), alias, StringComparison.OrdinalIgnoreCase))
            ? new(null, PrivateAddressOutcome.Ambiguous)
            : new(alias, PrivateAddressOutcome.Registered);
    }

    /// <summary>The address as somebody would paste it into a config file.</summary>
    public static string Url(string alias, int port) => $"http://{alias}:{port}";
}
```

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~PrivateAddressTests"`

Expected: PASS, 7 of 7.

- [ ] **Step 5: Commit**

```bash
git add src/Harbora.Infrastructure/Networking/PrivateAddress.cs tests/Harbora.Tests/PrivateAddressTests.cs
git commit -m "Decide the private name without Docker or a database in the way"
```

---

## Task 2: The pipeline asks the question and passes the alias

**Files:**
- Modify: `src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs:426-430` (the single-container `RunContainerAsync`), plus one new private method beside `ResolveEnvironmentNetworkAsync` at `:604`
- Modify: `src/Harbora.Domain/Apps/App.cs`
- Test: `tests/Harbora.Tests/PrivateAddressPipelineTests.cs`

**Interfaces:**
- Consumes: `PrivateAddress.Decide(...)`, `PrivateAddressOutcome`, `PrivateAddressDecision` from Task 1. `IDockerEngine.ListContainersAsync(string? labelFilter, CancellationToken)` (`IDockerEngine.cs:41`), which filters on **label existence** and returns `ContainerInfo` carrying a `Labels` dictionary.
- Produces: `App.PrivateAddressState` (a `PrivateAddressOutcome` column) that Task 3's view reads.

**Why the state is stored rather than recomputed.** The page cannot ask Docker — it would be a live call per app on every render, and it would answer for the network as it is now rather than as it was when the app last deployed. The deployment already knows; it records what it did.

**How the taken-name set is built, and why it takes both a query and a listing.** Environment membership is the database's fact; compose service names are the containers' fact and exist nowhere else, because `ComposeService` (`ComposeFile.cs:4`) is parsed from the repository at deploy time and never persisted. So: read the slugs of apps in this environment from `db`, list containers carrying the `harbora.compose.service` label, and keep the service names of those whose `harbora.app` label is one of those slugs. Containers in other environments are on other networks and cannot collide.

- [ ] **Step 1: Add the column**

In `src/Harbora.Domain/Apps/App.cs`, beside `ContainerPort` (line 109), add:

```csharp
    /// <summary>
    /// What the last deployment did about this app's private name, so the page can say "no address,
    /// and here is why" instead of showing a blank. Recomputing it on render would need a live Docker
    /// call per app, and would answer for the network as it is now rather than as it was when this app
    /// last shipped.
    /// </summary>
    public PrivateAddressOutcome? PrivateAddressState { get; set; }
```

`PrivateAddressOutcome` is already in `Harbora.Domain.Common` from Task 1, so no extra using is needed.

Then generate the migration — **with a fresh build, never `--no-build`**:

```bash
dotnet build src/Harbora.Data/Harbora.Data.csproj -c Debug
dotnet ef migrations add PrivateAddressState --project src/Harbora.Data --startup-project src/Harbora.Web
```

Then confirm the model and the snapshot agree:

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~MigrationConsistency"
```

Expected: PASS. If it fails, the migration was built against a stale assembly and captured the
previous model — rebuild and regenerate rather than hand-editing the migration.

- [ ] **Step 2: Write the failing test**

**First, extend the fake.** `FakeDockerEngine.SeedContainer(name, slug, state, image)` (`tests/Harbora.Tests/Fakes/FakeDockerEngine.cs:165`) sets `harbora.managed` and `harbora.app` but not `harbora.compose.service`, which these tests need. Give it an optional parameter:

```csharp
    /// <summary>Seeds a container as if a previous deployment had left it running.</summary>
    /// <param name="composeService">
    /// The compose service name this container answers to, when it came from a stack. That label is
    /// the only place the name survives — ComposeFile is parsed at deploy time and never stored — so
    /// it is what the collision check reads.
    /// </param>
    public string SeedContainer(string name, string slug, string state = "running",
        string image = "img:old", string? composeService = null)
    {
        var id = $"container-{Interlocked.Increment(ref _idSeq):D4}-{name}";
        var labels = new Dictionary<string, string>
        {
            ["harbora.managed"] = "true",
            ["harbora.app"] = slug
        };
        if (composeService is not null) labels["harbora.compose.service"] = composeService;

        _containers[id] = new ContainerInfo(id, name, image, state, "Up", labels);
        return id;
    }
```

Then create `tests/Harbora.Tests/PrivateAddressPipelineTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The alias reaches the container, and an ambiguous one does not.
///
/// Asserted against the recorded <c>DockerRunRequest.NetworkAliases</c> specifically, never against
/// the request as a whole: the container name is <c>harbora-{slug}-{number}</c> and contains the slug
/// too, so "the request mentions shop" is true whether or not the alias was ever passed.
/// </summary>
public class PrivateAddressPipelineTests
{
    /// <summary>The aliases the just-started container was given, or an empty list.</summary>
    private static IReadOnlyList<string> AliasesOf(PipelineHarness harness, string containerName) =>
        harness.Docker.RunRequests.Single(r => r.ContainerName == containerName).NetworkAliases ?? [];

    [Fact]
    public async Task A_deployed_app_answers_to_its_slug()
    {
        using var harness = new PipelineHarness();
        var deployment = harness.QueueDeployment();

        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number))
            .Should().ContainSingle().Which.Should().Be(harness.App.Slug,
                "the compose path has always registered its service names; the ordinary path never " +
                "did, so an app was reachable only at a name carrying the deployment number");
    }

    [Fact]
    public async Task A_release_task_is_started_with_no_alias()
    {
        using var harness = new PipelineHarness();
        harness.App.Kind = ServiceKind.ReleaseTask;
        await harness.Db.SaveChangesAsync();

        var deployment = harness.QueueDeployment();
        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number)).Should().BeEmpty(
            "a release task runs once and exits — ServicePlan.JoinsInternalNetwork excludes it");
    }

    [Fact]
    public async Task A_slug_a_neighbours_compose_service_already_answers_to_is_not_registered()
    {
        using var harness = new PipelineHarness();
        SeedSiblingRunning(harness, "sibling", harness.Environment.Id, composeService: harness.App.Slug);

        var deployment = harness.QueueDeployment();
        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number)).Should().BeEmpty(
            "docker balances between every container holding a name, so registering this one would " +
            "send a share of the calls to a stranger's service");

        var app = await harness.Db.Apps.FindAsync(harness.App.Id);
        app!.PrivateAddressState.Should().Be(PrivateAddressOutcome.Ambiguous,
            "the page has to be able to say why there is no address, rather than showing a blank");
    }

    [Fact]
    public async Task A_name_clash_does_not_fail_the_deployment()
    {
        using var harness = new PipelineHarness();
        SeedSiblingRunning(harness, "sibling", harness.Environment.Id, composeService: harness.App.Slug);

        var deployment = harness.QueueDeployment();
        var result = await harness.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded,
            "a convenience must never cost somebody a release — this is the assertion that matters most");
    }

    [Fact]
    public async Task A_compose_service_outside_this_environment_does_not_block_the_name()
    {
        using var harness = new PipelineHarness();
        // No EnvironmentId: on another network entirely, so it cannot collide. The collision query
        // filters on environment for exactly this reason.
        SeedSiblingRunning(harness, "stranger", environmentId: null, composeService: harness.App.Slug);

        var deployment = harness.QueueDeployment();
        await harness.RunAsync(deployment);

        AliasesOf(harness, harness.ContainerFor(deployment.Number))
            .Should().ContainSingle().Which.Should().Be(harness.App.Slug);
    }

    /// <summary>Another app in the workspace, with a running compose-stack container of its own.</summary>
    private static void SeedSiblingRunning(
        PipelineHarness harness, string slug, Guid? environmentId, string composeService)
    {
        harness.Db.Apps.Add(new App
        {
            WorkspaceId = harness.Workspace.Id,
            ServerId = harness.Server.Id,
            EnvironmentId = environmentId,
            Name = slug,
            Slug = slug,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = $"ghcr.io/example/{slug}:1.0"
        });
        harness.Db.SaveChanges();
        harness.Docker.SeedContainer($"harbora-{slug}-1-svc", slug, composeService: composeService);
    }
}
```

**Check the harness members before relying on them.** `PipelineHarness` exposes `Db`, `Docker`, `Workspace`, `Server`, `App`, `Project`, `Environment`, `QueueDeployment(number)`, `RunAsync(deployment)` and `ContainerFor(number)`; `FakeDockerEngine` exposes `RunRequests` (`FakeDockerEngine.cs:263`). If `RunAsync` returns something other than the `Deployment`, use what is actually there and note the correction in your report.

- [ ] **Step 3: Run and watch them fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~PrivateAddressPipelineTests"`

Expected: FAIL — no aliases are passed on the single-container path yet.

- [ ] **Step 4: Add the collision query**

In `src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs`, beside `ResolveEnvironmentNetworkAsync` (line 604), add:

```csharp
    /// <summary>
    /// Every short name already answered to on this app's network by somebody else.
    ///
    /// Two authorities, because neither holds both halves. Which apps share an environment is the
    /// database's fact. What their compose services are called exists only on the containers —
    /// ComposeFile is parsed from the repository at deploy time and never stored — so the
    /// harbora.compose.service label is the only place that name survives.
    ///
    /// A failure to answer yields an empty set, which withholds nothing: the alias is registered and
    /// the deployment proceeds. Refusing a shortcut because Docker was briefly unreachable would trade
    /// a rare wrong-target risk for a common lost-feature one.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> TakenAliasesAsync(App app, CancellationToken ct)
    {
        if (app.EnvironmentId is not { } environmentId) return [];

        var siblingSlugs = await db.Apps
            .Where(a => a.EnvironmentId == environmentId && a.Id != app.Id)
            .Select(a => a.Slug)
            .ToListAsync(ct);

        if (siblingSlugs.Count == 0) return [];

        try
        {
            var containers = await docker.ListContainersAsync("harbora.compose.service", ct);
            return containers
                .Where(c => c.Labels.TryGetValue("harbora.app", out var owner) && siblingSlugs.Contains(owner))
                .Select(c => c.Labels["harbora.compose.service"])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read compose service names for {Slug}; registering its alias unchecked.", app.Slug);
            return [];
        }
    }
```

Check the field names: this class's Docker engine and logger may not be called `docker` and `logger`. Use whatever they actually are.

- [ ] **Step 5: Pass the alias**

Replace the single-container run call at `DeploymentPipeline.cs:426-430` with:

```csharp
            // The compose path twenty lines below has always done this; the ordinary path never did,
            // so an app was reachable only as harbora-{slug}-{number} — a name that changes every
            // time it ships. An ambiguous alias is withheld rather than registered: docker balances
            // between every container answering to a name, so a duplicate silently sends a share of
            // the calls to a stranger.
            var privateAddress = PrivateAddress.Decide(app.Kind, app.Slug, await TakenAliasesAsync(app, ct));
            app.PrivateAddressState = privateAddress.Outcome;

            var containerId = await docker.RunContainerAsync(new DockerRunRequest(
                imageTag, containerName, network, env, labels,
                app.Volumes.Select(v => (v.Name, v.MountPath, v.ReadOnly)).ToList(),
                containerPort, app.MemoryLimitBytes, app.CpuLimit, app.HealthCheckPath,
                Command: null, PublishToHostPort: publishPort,
                NetworkAliases: privateAddress.HasAlias ? [privateAddress.Alias!] : null), ct);
```

Add `using Harbora.Infrastructure.Networking;` if it is not already there. `app.PrivateAddressState` is persisted by whatever save the pipeline already performs on `app`; if it performs none, save it explicitly and say so in your report.

- [ ] **Step 6: Run the tests and watch them pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~PrivateAddressPipelineTests"`

Expected: PASS, 5 of 5.

- [ ] **Step 7: Full suite and commit**

```bash
dotnet build-server shutdown && dotnet build Harbora.slnx -c Debug && dotnet test Harbora.slnx -c Debug --no-build
```

Expected: `0 Error(s)`, `2 Warning(s)`, `Failed: 0`.

```bash
git add src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs src/Harbora.Domain/Apps/App.cs tests/Harbora.Tests/PrivateAddressPipelineTests.cs
git commit -m "Give an ordinary app the alias the compose path always had"
```

---

## Task 3: The private address on Overview

**Files:**
- Modify: `src/Harbora.Web/Views/Apps/Details.cshtml` — the address block B1 added, around line 496
- Test: `tests/Harbora.Tests/Http/PrivateAddressHttpTests.cs`

**Interfaces:**
- Consumes: `App.PrivateAddressState` from Task 2, `PrivateAddress.Url(string, int)` from Task 1, `app.ContainerPort`.

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/Http/PrivateAddressHttpTests.cs`. Read `tests/Harbora.Tests/Http/AppAddressHttpTests.cs` first and reuse its shape — it has `SeedApp`, `Panel.GivenUser`, `Panel.SignedInAs` and the `HarboraHttpCollection` fixture, and it already corrected two harness names other plans got wrong.

```csharp
    /// <summary>An app in the fixture's workspace, in whatever private-address state the test needs.</summary>
    private Guid SeedApp(string slug, ServiceKind kind, PrivateAddressOutcome? state, int port = 80)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = kind,
            ContainerPort = port,
            PrivateAddressState = state,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app.Id;
    }

    [Fact]
    public async Task An_apps_overview_shows_the_name_its_neighbours_can_call_it_by()
    {
        var id = SeedApp("priv-shop", ServiceKind.Web, PrivateAddressOutcome.Registered, port: 8080);
        Panel.GivenUser(fixture.WorkspaceId, "priv-shop@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.210", "priv-shop@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        html.Should().Contain("http://priv-shop:8080",
            "the app's own ContainerPort, so a hard-coded 80 fails this");
    }

    [Fact]
    public async Task An_app_whose_name_was_taken_is_told_so_rather_than_shown_a_blank()
    {
        var id = SeedApp("priv-taken", ServiceKind.Web, PrivateAddressOutcome.Ambiguous);
        Panel.GivenUser(fixture.WorkspaceId, "priv-taken@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.211", "priv-taken@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-private-address-state=\"ambiguous\"");
        html.Should().NotContain("http://priv-taken:",
            "offering an address that resolves to somebody else's service is worse than offering none");
    }

    [Fact]
    public async Task A_release_task_is_not_offered_a_private_address_at_all()
    {
        var id = SeedApp("priv-migrate", ServiceKind.ReleaseTask, PrivateAddressOutcome.KindDoesNotJoin);
        Panel.GivenUser(fixture.WorkspaceId, "priv-migrate@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.212", "priv-migrate@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-private-address-state=\"no-join\"");
    }

    [Fact]
    public async Task An_app_that_has_not_deployed_since_this_shipped_is_not_shown_an_address_it_does_not_have()
    {
        var id = SeedApp("priv-unknown", ServiceKind.Web, state: null);
        Panel.GivenUser(fixture.WorkspaceId, "priv-unknown@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.213", "priv-unknown@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-private-address-state=\"unknown\"");
        html.Should().NotContain("http://priv-unknown:",
            "the alias is registered at deploy time — showing it before then is the " +
            "promise-without-a-feature this project keeps removing");
    }
```

The class needs the same `[Collection(HarboraHttpCollection.Name)]` attribute and
`(HarboraHttpFixture fixture)` primary constructor `AppAddressHttpTests` uses, plus its
`private HarboraWebFactory Panel => fixture.Panel;`. Expected count below is **4**, not 3.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~PrivateAddressHttpTests"`

Expected: FAIL — the page shows nothing about a private address.

- [ ] **Step 3: Add the block**

In `src/Harbora.Web/Views/Apps/Details.cshtml`, inside the Address section B1 created, beneath the public-address markup, add a private-address row that:

- when `app.PrivateAddressState == PrivateAddressOutcome.Registered` and `app.Slug` is set, shows `PrivateAddress.Url(app.Slug, app.ContainerPort)` in a `<code dir="ltr" data-copy-text>` element — the same copy mechanism the public address and the prebuilt-image reference use. Not a link: it resolves only from inside the network, so a browser click would fail;
- when `Ambiguous`, renders a one-line explanation carrying `data-private-address-state="ambiguous"` saying the name is already answered to by another service in this environment;
- when `KindDoesNotJoin`, renders `data-private-address-state="no-join"`;
- when the state is null — the app has not deployed since this shipped — renders `data-private-address-state="unknown"` with a line saying it will be assigned on the next deploy. **Do not show an address that has not been registered yet**; that is the promise-without-a-feature this project keeps removing.

Every visible string through `@T["…"]` or the `isFa` ternary. If you add a lucide icon, add it to the hand-maintained import list in `main.ts` — `IconCoverageTests` fails otherwise.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~PrivateAddressHttpTests"`

Expected: PASS, 4 of 4.

- [ ] **Step 5: Full suite and commit**

```bash
dotnet build-server shutdown && dotnet build Harbora.slnx -c Debug && dotnet test Harbora.slnx -c Debug --no-build
```

Expected: `0 Error(s)`, `2 Warning(s)`, `Failed: 0`.

```bash
git add src/Harbora.Web/Views/Apps/Details.cshtml tests/Harbora.Tests/Http/PrivateAddressHttpTests.cs
git commit -m "Show an app the name its neighbours can call it by"
```

---

## What this plan is not

Environment-variable injection — the owner chose display-only · cross-environment reach, which the
per-environment networks prevent by construction · an attach flow like the managed-database one · pod
specifics (B3) · anything about the public address, which B1 settled.

**No versioned alias.** The compose path registers `{name}-{number}` as well, to disambiguate across
stacks. An app does not need one: `harbora-{slug}-{number}` **is** its container name and already
resolves.
