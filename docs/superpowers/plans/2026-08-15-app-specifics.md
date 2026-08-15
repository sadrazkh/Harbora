# App Specifics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The app Overview tab says what the app actually is — its size and limits, where it runs, what code is live, and how it is doing — and says "not known" rather than a zero when it cannot find out.

**Architecture:** Three of the four groups read data the panel already stores, so they are a view change. The fourth needs one new capability, `IDockerEngine.InspectAsync`, implemented across the engine's three production shapes and its test fake.

**Tech Stack:** .NET 10, Docker.DotNet, EF Core, ASP.NET Minimal APIs (the agent), xUnit, FluentAssertions, Razor.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-15-app-specifics-design.md`. Read it before Task 1.
- **Zero NEW build warnings.** `dotnet build Harbora.slnx -c Debug` reports exactly **2 pre-existing NU1903** on SSH.NET in `Harbora.Postgres.Tests`. Leave them. **Security is out of scope by the owner's standing instruction.**
- **Baseline that must not drop:** build 0 errors; **4,254 passing, 0 failing**; 17 Docker-gated and 72 Postgres-lane skips.
- **No database migration.** The digest comes from inspecting what is running, not from a column. If a task appears to need one, stop and report.
- **Never renumber an existing enum value.** Append only.
- **Bilingual.** Every string a user sees goes through `@T["…"]` or the `isFa` ternary. An English-only label is a defect.
- **The panel renders Persian by default in tests.** Assert on `data-` attributes or on values — never on a sentence in either language.
- **Test names read as sentences.**
- **Narrative commit messages** — read `git log --oneline -10` for the register. End each with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Shared tree.** Never `git stash`, `git stash pop`, `git checkout -- .`, `git reset --hard` or `git clean`. Stage by explicit path; never `git add -A`.
- **Environmental trap.** `MSB3491 "Access to the path … denied"` on `obj/` files with a green suite means leftover MSBuild processes hold locks. Run `dotnet build-server shutdown`, then rebuild.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Harbora.Web/ViewModels/AppOverviewViewModel.cs` **(modify)** | Carries the specifics the view renders. |
| `src/Harbora.Web/Controllers/AppsController.cs` **(modify)** | Loads the stored specifics; later, the live inspect. |
| `src/Harbora.Web/Views/Apps/Details.cshtml` **(modify)** | The specifics panel. |
| `src/Harbora.Application/Abstractions/IDockerEngine.cs` **(modify)** | Adds `InspectAsync` and the `ContainerDetail` record. |
| `src/Harbora.Infrastructure/Docker/DockerEngine.cs` **(modify)** | Local implementation against `InspectContainerAsync`. |
| `src/Harbora.Infrastructure/Docker/RemoteDockerEngine.cs` **(modify)** | Forwards to the agent. |
| `src/Harbora.Agent/Program.cs` **(modify)** | Serves the new endpoint. |
| `src/Harbora.Infrastructure/Nodes/NodeWorkloadEngine.cs` **(modify)** | Delegates to the node agent, which already inspects. |
| `tests/Harbora.Tests/Fakes/FakeDockerEngine.cs` **(modify)** | Serves a seeded detail so tests never invent one. |
| `tests/Harbora.Tests/Http/AppSpecificsHttpTests.cs` **(create)** | What the page shows, in each state. |

---

## Task 1: What the panel already knows

**Files:**
- Modify: `src/Harbora.Web/ViewModels/AppOverviewViewModel.cs`, `src/Harbora.Web/Controllers/AppsController.cs` (the `Details` action), `src/Harbora.Web/Views/Apps/Details.cshtml`
- Test: `tests/Harbora.Tests/Http/AppSpecificsHttpTests.cs` (create)

**Interfaces:**
- Consumes: `App.InstanceSizeKey`, `App.MemoryLimitBytes`, `App.CpuLimit`, `App.DesiredReplicas`, `App.ContainerPort`, `App.ServerId`; `InstanceSize.CpuCores`, `MemoryBytes`, `DiskBytes` (`src/Harbora.Domain/Tenancy/InstanceSize.cs`); `DeploymentPlanning.ContainerName(slug, number)`; `Deployment.CommitSha`, `CommitMessage`, `CommitAuthor`, `GitRef`, `ImageTag`.
- Produces: whatever fields you add to `AppOverviewViewModel`; Task 3 adds the live ones beside them.

**Read first.** `src/Harbora.Web/Views/Apps/Details.cshtml` already has an Address section from B1 and B2. Your specifics panel is a sibling of it, in the same column, following the same `rounded-xl border border-line bg-surface` card shape the file uses throughout.

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/Http/AppSpecificsHttpTests.cs`. Read `tests/Harbora.Tests/Http/AppAddressHttpTests.cs` first and follow its shape exactly — the `[Collection(HarboraHttpCollection.Name)]` attribute, the `(HarboraHttpFixture fixture)` primary constructor, `private HarboraWebFactory Panel => fixture.Panel;`, and its `Panel.Seed` / `Panel.GivenUser` / `Panel.SignedInAs` sequence.

```csharp
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Tenancy;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the app's own page says the app is.
///
/// Overview showed almost none of this: the prebuilt image reference, when there was one, and
/// nothing else. Somebody asking "how big is this, where does it run, what code is live" had to
/// look in three other places or ask.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppSpecificsHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task An_apps_page_shows_the_size_it_is_actually_running_at()
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "spec-sized",
            Slug = "spec-sized",
            Kind = ServiceKind.Web,
            InstanceSizeKey = "spec-small",
            ContainerPort = 8080,
            DesiredReplicas = 3,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.InstanceSizes.Add(new InstanceSize
            {
                Key = "spec-small", Name = "Small", NameFa = "کوچک",
                CpuCores = 0.5, MemoryBytes = 536_870_912, DiskBytes = 5_368_709_120
            });
            db.Apps.Add(app);
        });
        Panel.GivenUser(fixture.WorkspaceId, "spec-sized@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.220", "spec-sized@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-replicas=\"3\"");
        html.Should().Contain("data-spec-port=\"8080\"",
            "the app's own ContainerPort, so a hard-coded 80 fails this");
        html.Should().Contain("data-spec-size=\"spec-small\"",
            "the size this app is on, read from its own InstanceSizeKey rather than a default");
    }

    [Fact]
    public async Task An_apps_page_names_the_container_and_the_place_it_runs()
    {
        var serverId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = serverId,
            Name = "spec-placed",
            Slug = "spec-placed",
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        Panel.GivenUser(fixture.WorkspaceId, "spec-placed@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.221", "spec-placed@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-container=", "the container name is how somebody finds it on the host");
    }

    [Fact]
    public async Task An_app_that_has_never_deployed_is_not_shown_an_invented_version()
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "spec-fresh",
            Slug = "spec-fresh",
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Created
        };
        Panel.Seed(db => db.Apps.Add(app));
        Panel.GivenUser(fixture.WorkspaceId, "spec-fresh@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.222", "spec-fresh@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-version=\"none\"",
            "an app with no deployment has no live version — saying so beats showing a blank field " +
            "that reads as a bug");
    }
}
```

**Check `db.InstanceSizes` is the real `DbSet` name** before relying on it; if it differs, use the real one and note the correction.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppSpecificsHttpTests"`

Expected: FAIL — none of those markers is on the page.

- [ ] **Step 3: Carry the specifics on the view model**

In `src/Harbora.Web/ViewModels/AppOverviewViewModel.cs`, add the fields the panel needs: the resolved `InstanceSize` (or null when the app's key matches none), the replica count, the container port, the server the app is placed on, the container name of the current deployment, and the latest succeeded `Deployment` (or null).

Keep them nullable where the answer can legitimately be unknown. A missing `InstanceSize` is not "zero cores".

- [ ] **Step 4: Load them in the controller**

In `AppsController`'s `Details` action, populate the new fields. The action already loads the app and its collections — add the `InstanceSize` lookup by `app.InstanceSizeKey` and the latest succeeded deployment. **Do not add a Docker call here**; Task 3 does that, deliberately separately.

- [ ] **Step 5: Render the panel**

In `src/Harbora.Web/Views/Apps/Details.cshtml`, add a specifics card beside the Address section carrying `data-spec-replicas`, `data-spec-port`, `data-spec-size`, `data-spec-container` and `data-spec-version` (the last being `"none"` when there is no deployment). Every visible label bilingual. If you add a lucide icon, add it to the hand-maintained list in `main.ts` — `IconCoverageTests` fails otherwise.

- [ ] **Step 6: Run the tests, then the full suite**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppSpecificsHttpTests"
dotnet build-server shutdown && dotnet build Harbora.slnx -c Debug && dotnet test Harbora.slnx -c Debug --no-build
```

Expected: 3 of 3, then `0 Error(s)`, `2 Warning(s)`, `Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/Harbora.Web/ViewModels/AppOverviewViewModel.cs src/Harbora.Web/Controllers/AppsController.cs src/Harbora.Web/Views/Apps/Details.cshtml tests/Harbora.Tests/Http/AppSpecificsHttpTests.cs
git commit -m "Say what the app is, from what the panel already knew"
```

---

## Task 2: An engine that can be asked about one container

**Files:**
- Modify: `src/Harbora.Application/Abstractions/IDockerEngine.cs`
- Modify: `src/Harbora.Infrastructure/Docker/DockerEngine.cs`
- Modify: `src/Harbora.Infrastructure/Docker/RemoteDockerEngine.cs`
- Modify: `src/Harbora.Agent/Program.cs`
- Modify: `src/Harbora.Infrastructure/Nodes/NodeWorkloadEngine.cs`
- Modify: `tests/Harbora.Tests/Fakes/FakeDockerEngine.cs`, and `tests/Harbora.Tests/NodeSchedulingTests.cs` if it implements the interface too
- Test: `tests/Harbora.Tests/ContainerDetailTests.cs` (create)

**Interfaces:**
- Produces: `ContainerDetail` and `Task<ContainerDetail?> InspectAsync(string containerNameOrId, CancellationToken ct)` on `IDockerEngine`. Task 3 consumes both.

**There are four implementations of `IDockerEngine` and one more in the test project.** `DockerEngine` (local), `RemoteDockerEngine` (HTTP to `Harbora.Agent`), `NodeWorkloadEngine` (the node channel), `FakeDockerEngine`, and `NodeSchedulingTests` has one. **All must compile.** Do not add a default implementation on the interface to avoid touching them — that is how one of them silently keeps returning nothing.

**The shape to mirror.** `src/Harbora.NodeAgent/Runtime/IContainerRuntime.cs:80-95` already defines exactly this record for the node side, including `ImageDigest`, `Healthy`, `RestartCount` and `StartedAt`. Read it and match its field set and, more importantly, its discipline: **every figure that can be unreported is nullable**, and its sibling `RuntimeContainerStats` says why in a docstring worth reading.

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/ContainerDetailTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Asking the engine about one container.
///
/// The panel could list containers and read a State and a Status string, and that was all — so
/// "how long has this been up" and "which image is actually running" had no source at all. The node
/// agent has extracted both since it was written; this is the same question asked of the local engine.
/// </summary>
public class ContainerDetailTests
{
    [Fact]
    public async Task An_engine_answers_with_what_it_was_told_about_the_container()
    {
        var engine = new FakeDockerEngine();
        engine.SeedDetail("harbora-blog-2", new ContainerDetail(
            Id: "abc123", Name: "harbora-blog-2", Image: "harbora/blog:build-2",
            ImageDigest: "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            State: "running", Status: "Up 3 hours",
            Healthy: true, RestartCount: 2,
            StartedAt: new DateTimeOffset(2026, 8, 15, 6, 0, 0, TimeSpan.Zero)));

        var detail = await engine.InspectAsync("harbora-blog-2", CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.RestartCount.Should().Be(2);
        detail.ImageDigest.Should().Be(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111");
        detail.StartedAt.Should().Be(new DateTimeOffset(2026, 8, 15, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_container_the_engine_has_never_heard_of_is_null_not_an_empty_detail()
    {
        var engine = new FakeDockerEngine();

        (await engine.InspectAsync("harbora-nothing-1", CancellationToken.None))
            .Should().BeNull("an empty detail would reach the page as a real answer full of zeroes");
    }

    [Fact]
    public async Task A_container_with_no_health_check_reports_unknown_rather_than_unhealthy()
    {
        var engine = new FakeDockerEngine();
        engine.SeedDetail("harbora-worker-1", new ContainerDetail(
            Id: "def456", Name: "harbora-worker-1", Image: "harbora/worker:build-1",
            ImageDigest: null, State: "running", Status: "Up 10 minutes",
            Healthy: null, RestartCount: 0, StartedAt: null));

        var detail = await engine.InspectAsync("harbora-worker-1", CancellationToken.None);

        detail!.Healthy.Should().BeNull(
            "no health check configured is not 'unhealthy' — it is 'we were not told how to ask', " +
            "the distinction DockerContainerRuntime already makes explicitly");
    }
}
```

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~ContainerDetailTests"`

Expected: FAIL to compile — no `ContainerDetail`, no `InspectAsync`, no `SeedDetail`.

- [ ] **Step 3: Add the contract**

In `src/Harbora.Application/Abstractions/IDockerEngine.cs`, beside `ContainerInfo` (line 129):

```csharp
/// <summary>
/// One container, asked about directly.
///
/// <see cref="ContainerInfo"/> is what a listing can cheaply say — a state and a status line. This is
/// what an inspect adds: which image is actually running, how long it has been up, how often it has
/// restarted, and whether its health check is passing.
///
/// Every figure that a runtime may decline to report is nullable, and stays null rather than
/// defaulting. The reason is the same one <c>RuntimeContainerStats</c> states on the node side: a
/// zero is a specific claim, and making it because nobody answered is the panel asserting something
/// it does not know. <paramref name="Healthy"/> in particular is null when no health check is
/// configured — that is "we were not told how to ask", not "failing".
/// </summary>
public record ContainerDetail(
    string Id,
    string Name,
    string Image,
    string? ImageDigest,
    string State,
    string Status,
    bool? Healthy,
    int? RestartCount,
    DateTimeOffset? StartedAt);
```

And on the interface, beside `ListContainersAsync`:

```csharp
    /// <summary>One container in detail, or null when the engine has no such container.</summary>
    Task<ContainerDetail?> InspectAsync(string containerNameOrId, CancellationToken ct);
```

- [ ] **Step 4: Implement it everywhere**

- **`DockerEngine`** — against `client.Containers.InspectContainerAsync`. `src/Harbora.NodeAgent/Runtime/DockerContainerRuntime.cs:167-186` is the worked example of pulling these exact fields out of the response; follow it, including its null handling for `Healthy`. Return null on `DockerContainerNotFoundException` and on a 404 `DockerApiException`, as that file does.
- **`Harbora.Agent/Program.cs`** — add `app.MapGet("/agent/containers/{id}/inspect", …)` beside the existing `/stats` endpoint at line 65, following its shape exactly.
- **`RemoteDockerEngine`** — forward to that URL, following `GetStatsAsync` (line 132): a non-success response returns null rather than throwing.
- **`NodeWorkloadEngine`** — delegate to the node agent's existing inspect. If the node channel has no route for it yet, **say so in your report** and return null with a comment explaining that the capability is present on the agent but not yet exposed — do not invent a route.
- **`FakeDockerEngine`** — add a `SeedDetail(string name, ContainerDetail detail)` method and a dictionary behind it, and have `InspectAsync` return the seeded value or null. Tests must never construct the answer inside the assertion.
- **`NodeSchedulingTests`** — whatever its `IDockerEngine` needs to compile.

- [ ] **Step 5: Run the tests, then the full suite**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~ContainerDetailTests"
dotnet build-server shutdown && dotnet build Harbora.slnx -c Debug && dotnet test Harbora.slnx -c Debug --no-build
```

Expected: 3 of 3, then `0 Error(s)`, `2 Warning(s)`, `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Harbora.Application/Abstractions/IDockerEngine.cs src/Harbora.Infrastructure/Docker/DockerEngine.cs src/Harbora.Infrastructure/Docker/RemoteDockerEngine.cs src/Harbora.Agent/Program.cs src/Harbora.Infrastructure/Nodes/NodeWorkloadEngine.cs tests/Harbora.Tests/Fakes/FakeDockerEngine.cs tests/Harbora.Tests/ContainerDetailTests.cs
git commit -m "Let the panel ask about one container, the way the node agent always could"
```

---

## Task 3: How it is doing, and what is actually running

**Files:**
- Modify: `src/Harbora.Web/ViewModels/AppOverviewViewModel.cs`, `src/Harbora.Web/Controllers/AppsController.cs`, `src/Harbora.Web/Views/Apps/Details.cshtml`
- Test: `tests/Harbora.Tests/Http/AppSpecificsHttpTests.cs` (extend)

**Interfaces:**
- Consumes: `IDockerEngine.InspectAsync` and `ContainerDetail` from Task 2; `DeploymentPlanning.ContainerName(slug, number)` for the name to ask about.

**The rule this task exists to hold.** A failed, slow or null inspect renders **unknown** — never zero, never blank. There is a test named for it, and it is the assertion that matters most here.

- [ ] **Step 1: Write the failing test**

Append to `tests/Harbora.Tests/Http/AppSpecificsHttpTests.cs`:

```csharp
    /// <summary>An app with one succeeded deployment, so it has a container to ask about.</summary>
    private (Guid AppId, string ContainerName) SeedDeployedApp(string slug)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.Apps.Add(app);
            db.Deployments.Add(new Deployment
            {
                AppId = app.Id,
                WorkspaceId = fixture.WorkspaceId,
                Number = 7,
                Status = DeploymentStatus.Succeeded,
                ImageTag = "harbora/seeded:build-7"
            });
        });
        return (app.Id, DeploymentPlanning.ContainerName(slug, 7));
    }

    [Fact]
    public async Task An_apps_page_shows_how_long_it_has_been_up_and_what_is_running()
    {
        const string digest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
        var (appId, containerName) = SeedDeployedApp("spec-live");

        Panel.Docker.SeedDetail(containerName, new ContainerDetail(
            Id: "live123", Name: containerName, Image: "harbora/seeded:build-7",
            ImageDigest: digest, State: "running", Status: "Up 3 hours",
            Healthy: true, RestartCount: 2,
            StartedAt: new DateTimeOffset(2026, 8, 15, 6, 0, 0, TimeSpan.Zero)));

        Panel.GivenUser(fixture.WorkspaceId, "spec-live@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.223", "spec-live@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-restarts=\"2\"", "the count the engine reported, not a guess");
        html.Should().Contain(digest, "the digest of what is actually running, straight from the engine");
    }

    [Fact]
    public async Task When_the_engine_cannot_answer_the_page_says_it_does_not_know()
    {
        var (appId, _) = SeedDeployedApp("spec-silent");
        // Nothing seeded for this container, so InspectAsync returns null.

        Panel.GivenUser(fixture.WorkspaceId, "spec-silent@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.224", "spec-silent@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-spec-health=\"unknown\"");
        html.Should().NotContain("data-spec-restarts=\"0\"",
            "a zero here is a specific, reassuring claim — 'it has never restarted' — that nobody " +
            "made. This is the assertion this task exists for");
    }
```

**`HarboraWebFactory` needs a Docker override, and it does not have one.** It already exposes `public RecordingDeploymentEngine Deployments { get; } = new();` and registers it with `services.AddSingleton<IDeploymentEngine>(Deployments);` (around lines 61 and 100). Add the same pair for the engine — `public FakeDockerEngine Docker { get; } = new();` and `services.AddSingleton<IDockerEngine>(Docker);` — so a test can seed what the panel will be told. That addition is part of this task.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppSpecificsHttpTests"`

Expected: FAIL.

- [ ] **Step 3: Ask, and carry the answer**

Add the live fields to `AppOverviewViewModel` as a single nullable `ContainerDetail?` rather than as loose properties — one null means "we could not ask", which is exactly the state the page must render.

In `AppsController.Details`, call `InspectAsync` for the current deployment's container name. **Wrap it** so a throw or a timeout yields null rather than a 500: the specifics are an enrichment, and an app page that fails to load because Docker was busy is a worse outcome than one that says "not known".

- [ ] **Step 4: Render the three states**

In the specifics card:

- detail present → uptime from `StartedAt`, `data-spec-restarts` with the count, the health state, and the digest (shortened for display, full value in a `data-copy-text` element);
- detail present but a figure null → that figure alone reads "not known"; `Healthy: null` reads "not checked", **distinct from unhealthy**;
- detail null → `data-spec-health="unknown"` and no restart count at all.

Every visible string bilingual.

- [ ] **Step 5: Run the tests, then the full suite**

```bash
dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppSpecificsHttpTests"
dotnet build-server shutdown && dotnet build Harbora.slnx -c Debug && dotnet test Harbora.slnx -c Debug --no-build
```

Expected: 5 of 5, then `0 Error(s)`, `2 Warning(s)`, `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Harbora.Web/ViewModels/AppOverviewViewModel.cs src/Harbora.Web/Controllers/AppsController.cs src/Harbora.Web/Views/Apps/Details.cshtml tests/Harbora.Tests/Http/AppSpecificsHttpTests.cs
git commit -m "Show how the app is doing, and say so when nobody answered"
```

---

## What this plan is not

Usage over time (sub-project C) · volume detail (D) · deployment history, which has its own tab ·
either address, which B1 and B2 settled · a metrics store — this reads the current state and keeps
nothing.

**No migration.** The digest is inspected from the running container, not stored.
