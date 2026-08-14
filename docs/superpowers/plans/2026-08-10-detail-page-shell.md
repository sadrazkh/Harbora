# Detail-page shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the app and database detail pages into server-rendered tabs behind a shared shell, and make the right rail release its column when nothing is in it.

**Architecture:** Each tab is a real route (`/apps/{id}`, `/apps/{id}/usage`, …) whose action loads only that tab's data. A nested Razor layout (`_Shell.cshtml`) draws the header and tab strip once; each tab view sets `Layout` and renders only its own content. The shell is typed to a base view model that every tab's model inherits, so the compiler — not a `ViewBag` — guarantees the header has what it needs.

**Tech Stack:** ASP.NET Core MVC (.NET 10), Razor views, EF Core, Tailwind via `Scripts/app.css`, xUnit + FluentAssertions, `WebApplicationFactory` HTTP lane.

## Global Constraints

- **Zero build warnings.** `dotnet build Harbora.slnx -c Debug` must report `0 Warning(s)`.
- **Baseline before this plan:** build 0/0; **3,591 + 498 + 15 = 4,104 passing, 0 failing**; 17 Docker-gated + 70 Postgres-lane skips. Nothing may reduce the passing count.
- **No database migration in this plan.** Nothing here changes the schema. If a task appears to need one, stop and report.
- **Never renumber an existing enum value.** Append only.
- **`docs/product-audit/19-do-not-change-list.md`** lists 30 protected behaviours. Item 21 (bilingual/RTL) and item 23 (fold, never remove) are directly in this plan's path.
- **Bilingual.** Every string a user sees goes through `@T["…"]` or the `isFa` ternary already used in these views. Do not introduce an English-only label.
- **Test names read as sentences**, like the ones already in `tests/Harbora.Tests/`.
- **Narrative commit messages** — read `git log --oneline -10` for the register. End each with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Shared tree.** Never run `git stash`, `git stash pop`, `git checkout -- .`, `git reset --hard` or `git clean`. Stage by explicit path; never `git add -A`.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `src/Harbora.Web/ViewModels/AppTabViewModel.cs` | Base view model: the fields the shell's header draws, plus which tab is current |
| `src/Harbora.Web/Views/Apps/_Shell.cshtml` | App header + tab strip + `@RenderBody()` |
| `src/Harbora.Web/Views/Apps/Usage.cshtml` | Usage tab |
| `src/Harbora.Web/Views/Apps/Volumes.cshtml` | Volumes tab |
| `src/Harbora.Web/Views/Apps/Deployments.cshtml` | Deployments tab |
| `src/Harbora.Web/Controllers/AppsController.Tabs.cs` | `partial class` holding the three new tab actions and the shared header loader |
| `src/Harbora.Web/Views/Databases/_Shell.cshtml` | Database header + tab strip + `@RenderBody()` |
| `src/Harbora.Web/Views/Databases/Usage.cshtml` | Database usage tab |
| `src/Harbora.Web/Controllers/DatabasesController.Tabs.cs` | `partial class` for the database usage action and its header loader |
| `tests/Harbora.Tests/Http/AppDetailTabsHttpTests.cs` | Content preservation, tenancy, and tab reachability over real HTTP |
| `tests/Harbora.Tests/DetailTabCensusTests.cs` | Every tab link resolves to an action; every tab action appears in a strip |

**Modified**

| File | Change |
|---|---|
| `src/Harbora.Web/Views/Apps/Details.cshtml` | Becomes the Overview tab: keeps header content that moves to the shell removed, loses the three moved sections |
| `src/Harbora.Web/Controllers/AppsController.cs` | `class` → `partial class`; `Details` sheds the loads that moved |
| `src/Harbora.Web/Views/Databases/Details.cshtml` | Becomes the Overview tab under the shell |
| `src/Harbora.Web/Controllers/DatabasesController.cs` | `class` → `partial class` |
| `src/Harbora.Web/Views/Shared/_Layout.cshtml:73-78` | Rail renders only when a panel is open |
| `src/Harbora.Web/Views/Apps/Index.cshtml` | Toolbar gains the reopen control |
| `src/Harbora.Web/Views/Databases/Index.cshtml` | Same |
| `src/Harbora.Infrastructure/Navigation/` (rail defaults) | Default becomes closed |

---

## Task 1: A net under the refactor

Everything after this breaks a 914-line view apart. The failure mode is that a section is carried into no tab and **no test fails**, because nothing ever claimed it was there. This task writes that claim first.

**Files:**
- Test: `tests/Harbora.Tests/Http/AppDetailTabsHttpTests.cs` (create)

**Interfaces:**
- Consumes: the existing HTTP lane harness in `tests/Harbora.Tests/Http/` — read a neighbouring file (for example `BillingPageHttpTests.cs`) for how it seeds a workspace, signs in, and issues requests. Follow it exactly rather than inventing a second harness.
- Produces: `AppDetailTabsHttpTests`, extended by Tasks 2–5.

- [ ] **Step 1: Write the failing test**

One test that asserts today's page carries each landmark that must survive the split. Pick the marker strings by reading `src/Harbora.Web/Views/Apps/Details.cshtml` — do not copy the list below blind, it is the shape, and the exact rendered text is what you must assert.

```csharp
[Fact]
public async Task The_app_page_still_shows_everything_it_showed_before_the_split()
{
    // Written BEFORE the view is broken up. A pure refactor is where a section disappears with no
    // test failing, because no test claimed it was there. This is that claim.
    await using var app = new HarboraWebFactory();
    var client = await app.SignedInOwnerAsync();
    var appId = await app.SeedAppWithEverythingAsync();   // env var, domain, volume, one deployment

    var html = await client.GetStringAsync($"/apps/details/{appId}");

    html.Should().Contain("KEY_FROM_SEED", "environment variables are on the page today");
    html.Should().Contain("seeded.example.com", "domains are on the page today");
    html.Should().Contain("/data/seeded", "the volume's mount path is on the page today");
    html.Should().Contain("Rollback", "the deployment list offers rollback today");
}
```

- [ ] **Step 2: Run it and watch it pass**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppDetailTabsHttpTests"`
Expected: **PASS.** This one is green from the start on purpose — it describes what exists. Its value is in Tasks 2–5, where it must be **updated to point at the tab each landmark moved to**, and where forgetting to update it is the signal that something was dropped.

- [ ] **Step 3: Prove it can fail**

Comment out the environment-variables `<details>` block in `Views/Apps/Details.cshtml`, re-run, confirm the test goes red naming `KEY_FROM_SEED`, then restore the block. Record the failure text in your report.

**`touch` the view after restoring it and rebuild before re-running** — a restored file can still be served from a stale build, and this repository has been caught by exactly that.

- [ ] **Step 4: Commit**

```bash
git add tests/Harbora.Tests/Http/AppDetailTabsHttpTests.cs
git commit
```

---

## Task 2: The app shell

Overview keeps rendering everything it renders today. Only the header and tab strip move. Nothing is deleted in this task — that is what makes it reviewable on its own.

**Files:**
- Create: `src/Harbora.Web/ViewModels/AppTabViewModel.cs`, `src/Harbora.Web/Views/Apps/_Shell.cshtml`
- Modify: `src/Harbora.Web/Views/Apps/Details.cshtml` (header block moves out; `Layout` set)
- Test: `tests/Harbora.Tests/Http/AppDetailTabsHttpTests.cs`

**Interfaces:**
- Produces: `AppTabViewModel` with `Guid Id`, `string Name`, `string Slug`, `ServiceKind Kind`, `AppStatus Status`, `string CurrentTab`. Tasks 3–5 inherit from it. `_Shell.cshtml` is typed `@model AppTabViewModel`.
- Produces: tab keys as literal strings — `"overview"`, `"usage"`, `"volumes"`, `"deployments"`. Task 8's census reads these.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Every_tab_of_an_app_is_reachable_from_the_page_itself()
{
    await using var app = new HarboraWebFactory();
    var client = await app.SignedInOwnerAsync();
    var appId = await app.SeedAppWithEverythingAsync();

    var html = await client.GetStringAsync($"/apps/details/{appId}");

    html.Should().Contain($"/apps/{appId}/usage");
    html.Should().Contain($"/apps/{appId}/volumes");
    html.Should().Contain($"/apps/{appId}/deployments");
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "Every_tab_of_an_app_is_reachable"`
Expected: FAIL — the strip does not exist yet.

- [ ] **Step 3: Add the base view model**

```csharp
namespace Harbora.Web.ViewModels;

/// <summary>
/// What the app shell's header and tab strip need, on every tab.
///
/// <para>
/// A base class rather than ViewData: the shell is typed to this, so a tab that forgets to supply
/// the header fails to compile instead of rendering a page with an empty title.
/// </para>
/// </summary>
public abstract class AppTabViewModel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
    public Harbora.Domain.Apps.ServiceKind Kind { get; init; }
    public Harbora.Domain.Apps.AppStatus Status { get; init; }

    /// <summary>Which tab is drawn as current. One of: overview, usage, volumes, deployments.</summary>
    public string CurrentTab { get; init; } = "overview";
}
```

- [ ] **Step 4: Write the shell**

Move the header block — lines ~16–58 of `Views/Apps/Details.cshtml`, the back link, title, status and the Deploy / Run now / Restart / Stop / Start / Logs buttons — into `_Shell.cshtml` verbatim, then add the strip beneath it. Keep every `@T[…]` exactly as it was; do not retype the labels.

```razor
@model Harbora.Web.ViewModels.AppTabViewModel
@{
    var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
    (string Key, string Href, string Label)[] tabs =
    [
        ("overview",    $"/apps/details/{Model.Id}",      isFa ? "نمای کلی"   : "Overview"),
        ("usage",       $"/apps/{Model.Id}/usage",        isFa ? "مصرف"       : "Usage"),
        ("volumes",     $"/apps/{Model.Id}/volumes",      isFa ? "والیوم‌ها"  : "Volumes"),
        ("deployments", $"/apps/{Model.Id}/deployments",  isFa ? "استقرارها"  : "Deployments"),
    ];
}

@* … the header block moved from Details.cshtml goes here, unchanged … *@

<nav class="detail-tabs" aria-label="@(isFa ? "بخش‌های برنامه" : "Application sections")">
    @foreach (var tab in tabs)
    {
        <a href="@tab.Href"
           class="detail-tab @(tab.Key == Model.CurrentTab ? "is-current" : "")"
           @(tab.Key == Model.CurrentTab ? "aria-current=page" : "")>@tab.Label</a>
    }
</nav>

@RenderBody()
```

Add the two classes to `src/Harbora.Web/Scripts/app.css` beside the existing `.rail-panel` rules, using only design tokens — no hard-coded colours (do-not-change item 22):

```css
.detail-tabs { @apply mt-4 flex flex-wrap gap-1 border-b border-line; }
.detail-tab  { @apply rounded-t-lg px-3 py-2 text-sm text-ink-muted hover:text-ink hover:bg-surface-2; }
.detail-tab.is-current { @apply border-b-2 border-accent text-ink font-semibold; }
```

- [ ] **Step 5: Point Details at the shell**

At the top of `Views/Apps/Details.cshtml` set `Layout = "_Shell";` and delete the header block you moved. Everything else in the file stays. Give the action's view model `CurrentTab = "overview"`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppDetailTabsHttpTests"`
Expected: PASS, including Task 1's preservation test — nothing moved out of Overview yet, so it must still be green untouched.

- [ ] **Step 7: Build and run the full suite**

Run: `dotnet build Harbora.slnx -c Debug` then `dotnet test Harbora.slnx -c Debug --no-build`
Expected: 0 warnings; passing count at or above baseline.

- [ ] **Step 8: Commit**

---

## Task 3: The usage tab

**Files:**
- Create: `src/Harbora.Web/Views/Apps/Usage.cshtml`, `src/Harbora.Web/Controllers/AppsController.Tabs.cs`
- Modify: `src/Harbora.Web/Controllers/AppsController.cs` (`class` → `partial class`; `Details` stops loading the usage figures), `src/Harbora.Web/Views/Apps/Details.cshtml` (usage block removed), `tests/Harbora.Tests/Http/AppDetailTabsHttpTests.cs`

**Interfaces:**
- Consumes: `AppTabViewModel` from Task 2.
- Produces: `AppsController.Usage(Guid id, CancellationToken ct)` at route `apps/{id:guid}/usage`; `AppUsageViewModel : AppTabViewModel` with `double? CpuPercent`, `double? MemoryUsed`, `long MemoryLimitBytes`, `double CpuLimit`, `long? DiskUsedBytes`, `string? DiskCaveat`, `DateTimeOffset? MeasuredAt`.
- Produces: `private async Task<App?> LoadHeaderAsync(Guid id, CancellationToken ct)` in `AppsController.Tabs.cs` — returns null when the caller may not see it. Tasks 4 and 5 call it.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task The_usage_tab_shows_what_the_overview_used_to()
{
    await using var app = new HarboraWebFactory();
    var client = await app.SignedInOwnerAsync();
    var appId = await app.SeedAppWithEverythingAsync();

    var usage = await client.GetStringAsync($"/apps/{appId}/usage");

    usage.Should().Contain("cpu", "the usage tab is where consumption lives now");
}

[Fact]
public async Task A_tab_of_another_workspaces_app_is_not_found_rather_than_shown()
{
    // Every tab is a new entry point, and each one is a new chance to forget the ownership check.
    await using var app = new HarboraWebFactory();
    var client = await app.SignedInOwnerAsync();
    var foreignId = await app.SeedAppInAnotherWorkspaceAsync();

    (await client.GetAsync($"/apps/{foreignId}/usage")).StatusCode
        .Should().Be(System.Net.HttpStatusCode.NotFound);
}
```

- [ ] **Step 2: Run and watch both fail**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~AppDetailTabsHttpTests"`
Expected: FAIL — 404 for a route that does not exist yet.

- [ ] **Step 3: Add the shared header loader**

Create `src/Harbora.Web/Controllers/AppsController.Tabs.cs`. Change `AppsController.cs`'s declaration to `public partial class AppsController` — nothing else in that file changes in this step.

```csharp
namespace Harbora.Web.Controllers;

/// <summary>
/// The app detail tabs. Separate file, same controller: the routes all live under
/// /apps/{id}/… and splitting them across two controllers sends the next reader hunting.
/// </summary>
public partial class AppsController
{
    /// <summary>
    /// The app behind any tab, or null when this caller may not see it.
    ///
    /// <para>
    /// Deliberately loads no collections. That is the whole point of one route per tab: the
    /// Overview no longer pays for volumes and twenty deployments, and this tab pays for neither.
    /// </para>
    /// </summary>
    private async Task<Harbora.Domain.Apps.App?> LoadHeaderAsync(Guid id, CancellationToken ct)
    {
        if (!await access.CanSeeAppAsync(id, ct)) return null;

        return await db.Apps
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
    }
}
```

- [ ] **Step 4: Add the action**

Read the block currently at `AppsController.cs` around lines 455–460 and 514–516 (the `ViewBag.CpuPercent` / `MemoryUsed` / `MeasuredAt` / `DiskUsed` / `DiskCaveat` assignments) and move that code here rather than rewriting it — it already handles the unmeasured case, and do-not-change item 18 forbids a missing measurement reading as zero.

```csharp
[HttpGet("apps/{id:guid}/usage")]
public async Task<IActionResult> Usage(Guid id, CancellationToken ct)
{
    var app = await LoadHeaderAsync(id, ct);
    if (app is null) return NotFound();

    // … the moved measurement loads …

    return View(new AppUsageViewModel
    {
        Id = app.Id, Name = app.Name, Slug = app.Slug, Kind = app.Kind, Status = app.Status,
        CurrentTab = "usage",
        // … the measured values …
    });
}
```

- [ ] **Step 5: Move the markup**

Cut the usage block out of `Details.cshtml` (the `cpuPercent` / `memoryUsed` / disk meters, around lines 199–215 and the disk panel) into `Usage.cshtml` with `Layout = "_Shell";`. Change the `ViewBag` reads to model properties. Delete the now-unused `ViewBag` assignments from `Details`.

- [ ] **Step 6: Update Task 1's preservation test**

Its usage assertions now belong to the usage tab. Move them; do not delete them. **If an assertion has no tab to move to, you have found the thing this plan exists to catch — stop and report it.**

- [ ] **Step 7: Run the tests, then the full suite**

Expected: all green, 0 warnings, count at or above baseline.

- [ ] **Step 8: Commit**

---

## Task 4: The volumes tab

**Files:**
- Create: `src/Harbora.Web/Views/Apps/Volumes.cshtml`
- Modify: `src/Harbora.Web/Controllers/AppsController.Tabs.cs`, `src/Harbora.Web/Views/Apps/Details.cshtml`, `AppsController.cs` (`Details` drops `.Include(a => a.Volumes)`), `tests/Harbora.Tests/Http/AppDetailTabsHttpTests.cs`

**Interfaces:**
- Consumes: `LoadHeaderAsync` from Task 3.
- Produces: `AppsController.Volumes(Guid id, CancellationToken ct)` at `apps/{id:guid}/volumes`; `AppVolumesViewModel : AppTabViewModel` with `IReadOnlyList<Harbora.Domain.Apps.AppVolume> Volumes`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task The_volumes_tab_lists_the_apps_storage_and_can_still_add_and_remove()
{
    await using var app = new HarboraWebFactory();
    var client = await app.SignedInOwnerAsync();
    var appId = await app.SeedAppWithEverythingAsync();

    var html = await client.GetStringAsync($"/apps/{appId}/volumes");

    html.Should().Contain("/data/seeded", "the seeded mount path is this tab's subject");
    html.Should().Contain("AddVolume", "adding storage must not be lost in the move");
    html.Should().Contain("RemoveVolume", "nor removing it");
}
```

- [ ] **Step 2: Run and watch it fail** — 404.

- [ ] **Step 3: Add the action**

```csharp
[HttpGet("apps/{id:guid}/volumes")]
public async Task<IActionResult> Volumes(Guid id, CancellationToken ct)
{
    var app = await LoadHeaderAsync(id, ct);
    if (app is null) return NotFound();

    var volumes = await db.AppVolumes.AsNoTracking()
        .Where(v => v.AppId == id)
        .OrderBy(v => v.MountPath)
        .ToListAsync(ct);

    return View(new AppVolumesViewModel
    {
        Id = app.Id, Name = app.Name, Slug = app.Slug, Kind = app.Kind, Status = app.Status,
        CurrentTab = "volumes",
        Volumes = volumes,
    });
}
```

Check the DbSet's real name before writing this — read `HarboraDbContext` rather than trusting `db.AppVolumes`.

- [ ] **Step 4: Move the markup**, including the Add and Remove forms, into `Volumes.cshtml` with `Layout = "_Shell";`. Remove `.Include(a => a.Volumes)` from `Details`.

- [ ] **Step 5: Move the volume assertion** out of the preservation test into this tab's test.

- [ ] **Step 6: Run the tests, then the full suite.**

- [ ] **Step 7: Commit**

---

## Task 5: The deployments tab

**Files:**
- Create: `src/Harbora.Web/Views/Apps/Deployments.cshtml`
- Modify: `src/Harbora.Web/Controllers/AppsController.Tabs.cs`, `src/Harbora.Web/Views/Apps/Details.cshtml`, `AppsController.cs` (`Details` drops the deployments include), `tests/Harbora.Tests/Http/AppDetailTabsHttpTests.cs`

**Interfaces:**
- Consumes: `LoadHeaderAsync` from Task 3.
- Produces: `AppsController.Deployments(Guid id, CancellationToken ct)` at `apps/{id:guid}/deployments`; `AppDeploymentsViewModel : AppTabViewModel` with `IReadOnlyList<Harbora.Domain.Deployments.Deployment> Deployments`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task The_deployments_tab_keeps_the_history_and_the_way_back()
{
    await using var app = new HarboraWebFactory();
    var client = await app.SignedInOwnerAsync();
    var appId = await app.SeedAppWithEverythingAsync();

    var html = await client.GetStringAsync($"/apps/{appId}/deployments");

    html.Should().Contain("Rollback", "rollback is the reason this history is kept");
}
```

- [ ] **Step 2: Run and watch it fail** — 404.

- [ ] **Step 3: Add the action.** Keep the existing `OrderByDescending(d => d.Number).Take(20)` bound — the page has always shown a window, and widening it here would be a change nobody asked for.

- [ ] **Step 4: Move the markup**, including the rollback links, into `Deployments.cshtml` with `Layout = "_Shell";`. Drop the deployments include from `Details`.

- [ ] **Step 5: Move the rollback assertion** out of the preservation test.

- [ ] **Step 6: Read what is left of `Details.cshtml`** and confirm every remaining section is one the design puts on Overview: status, internal link and domains, pod specifics, scheduled runs, environment variables. Anything else is a section the split forgot — report it rather than leaving it.

- [ ] **Step 7: Run the tests, then the full suite.**

- [ ] **Step 8: Commit**

---

## Task 6: The database shell

**Files:**
- Create: `src/Harbora.Web/Views/Databases/_Shell.cshtml`, `src/Harbora.Web/Views/Databases/Usage.cshtml`, `src/Harbora.Web/Controllers/DatabasesController.Tabs.cs`, `src/Harbora.Web/ViewModels/DatabaseTabViewModel.cs`
- Modify: `src/Harbora.Web/Views/Databases/Details.cshtml`, `src/Harbora.Web/Controllers/DatabasesController.cs` (`class` → `partial class`)
- Test: `tests/Harbora.Tests/Http/AppDetailTabsHttpTests.cs`

**Interfaces:**
- Produces: `DatabaseTabViewModel` mirroring `AppTabViewModel`; tab keys `"overview"`, `"access"`, `"usage"`, `"backups"`.
- The **Access** tab links to the existing `Databases/Access` page and the **Backups** tab to the existing backup surface for that database — neither is rebuilt here.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Every_tab_of_a_database_is_reachable_from_the_page_itself()
{
    await using var app = new HarboraWebFactory();
    var client = await app.SignedInOwnerAsync();
    var dbId = await app.SeedManagedDatabaseAsync();

    var html = await client.GetStringAsync($"/databases/details/{dbId}");

    html.Should().Contain($"/databases/{dbId}/usage");
    html.Should().Contain("/databases/access");
}
```

Read `DatabasesController` for the real route shapes before writing the assertions — these are the shape, not the literal paths.

- [ ] **Step 2: Run and watch it fail.**

- [ ] **Step 3: Build the shell and the usage tab**, following Task 2 and Task 3 exactly. The database page is 390 lines, so the header block is smaller, but the rule is the same: move it, do not retype it.

- [ ] **Step 4: Run the tests, then the full suite.**

- [ ] **Step 5: Commit**

---

## Task 7: The rail gives its space back

**Files:**
- Modify: `src/Harbora.Web/Views/Shared/_Layout.cshtml:73-78`, `src/Harbora.Web/Views/Apps/Index.cshtml`, `src/Harbora.Web/Views/Databases/Index.cshtml`, the rail default in `src/Harbora.Infrastructure/Navigation/`
- Test: `tests/Harbora.Tests/Http/RailLayoutHttpTests.cs` (create)

**Interfaces:**
- Consumes: the existing `Rails.IsOpenAsync` and `Account/SetRail`. **Do not replace this persistence.** It stores the choice server-side per user, which is better than `localStorage` — the same person gets the same layout on their laptop and their phone.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task With_every_rail_panel_closed_the_list_gets_the_whole_width()
{
    await using var app = new HarboraWebFactory();
    var client = await app.SignedInOwnerAsync();
    await app.CloseEveryRailPanelAsync();

    var html = await client.GetStringAsync("/apps");

    html.Should().NotContain("2xl:w-80", "an empty reserved column is the whole complaint");
}

[Fact]
public async Task A_closed_rail_still_offers_its_way_back()
{
    // Do-not-change item 23: a setting that makes a feature disappear entirely is one nobody finds
    // their way back from. The rail folds; it is not removed.
    await using var app = new HarboraWebFactory();
    var client = await app.SignedInOwnerAsync();
    await app.CloseEveryRailPanelAsync();

    var html = await client.GetStringAsync("/apps");

    html.Should().Contain("SetRail", "the toolbar control is what reopens it");
}
```

- [ ] **Step 2: Run and watch both fail.**

- [ ] **Step 3: Make the layout conditional**

```razor
@* The rail reserved a fixed 20rem whether or not anything was in it, so folding a panel shrank the
   panel and never widened the list. Rendered only when something is actually in it. *@
@if (IsSectionDefined("RightRail") && await Rails.AnyOpenAsync())
{
    <aside class="w-full shrink-0 space-y-5 border-t border-line bg-surface p-5 2xl:w-80 2xl:border-s 2xl:border-t-0">
        @await RenderSectionAsync("RightRail", required: false)
    </aside>
}
```

Add `AnyOpenAsync()` beside the existing `IsOpenAsync` rather than making the view ask about each panel by name — a view that enumerates the panels is a second list to keep in step with the first.

- [ ] **Step 4: Add the reopen control** to both `Index` toolbars, posting to the same `Account/SetRail` action the panel headings already use.

- [ ] **Step 5: Flip the defaults to closed.** A user who has already opened a panel has that stored and keeps it; only someone who never touched it sees the change.

- [ ] **Step 6: Run the tests, then the full suite.**

- [ ] **Step 7: Commit**

---

## Task 8: The census

A tab strip is exactly the thing that can look right and not be: a link to an action that does not exist gives a 404 nobody tried, and an action with no link is a page nobody finds. Both pass silently.

**Files:**
- Test: `tests/Harbora.Tests/DetailTabCensusTests.cs` (create)

**Interfaces:**
- Consumes: the shells from Tasks 2 and 6, and the tab actions from Tasks 3–6.

- [ ] **Step 1: Write the failing tests**

Read the source rather than a hand-maintained list. A list would become the very thing it was meant to protect — this follows `StartPathCensusTests` in `tests/Harbora.Tests/Billing/BillingGateTests.cs`, which caught a real gap when it was written. Read it before writing this.

```csharp
[Fact]
public void Every_tab_the_app_shell_links_to_is_an_action_that_exists()
{
    var shell = File.ReadAllText(ViewPath("Apps/_Shell.cshtml"));
    var linked = Regex.Matches(shell, @"\$""/apps/\{Model\.Id\}/(?<tab>[a-z]+)""")
        .Select(m => m.Groups["tab"].Value).ToList();

    linked.Should().NotBeEmpty("a regex that matches nothing would pass this test for ever");

    var actions = typeof(AppsController).GetMethods()
        .Select(m => m.Name.ToLowerInvariant()).ToHashSet();

    linked.Should().OnlyContain(tab => actions.Contains(tab));
}

[Fact]
public void Every_app_tab_action_is_reachable_from_the_shell()
{
    // The other direction. A tab somebody built and never linked is a page nobody finds.
    var shell = File.ReadAllText(ViewPath("Apps/_Shell.cshtml"));

    foreach (var name in new[] { "usage", "volumes", "deployments" })
    {
        shell.Should().Contain($"/{name}", $"the {name} tab exists and must be reachable");
    }
}
```

The `NotBeEmpty` line is not decoration: without it, a regex that stops matching turns this test into one that passes because it checked nothing.

- [ ] **Step 2: Run them.** They should pass — the tabs exist by now. Then **prove each can fail**: delete one link from the shell, watch the second test name it; point one link at `/apps/{Model.Id}/nowhere`, watch the first fail. Restore, `touch`, rebuild, re-run. Record both failures in your report.

- [ ] **Step 3: Add the same pair for the database shell.**

- [ ] **Step 4: Run the full suite.**

- [ ] **Step 5: Commit**

---

## What this plan is not

The internal subdomain link · usage charts · volume browsing, upload and external access · instant backup · the image maximum · the Learning Centre and the routing guide. Each is its own sub-project with its own spec.

**No empty slot is built for the internal link, and that is a correction to the spec.** The spec said
Overview would reserve a place and leave it blank. Writing this plan made the contradiction obvious:
a blank slot labelled for something that does not exist is the same promise-without-a-feature that
the spec itself refuses two paragraphs earlier when it declines to ship a Backups tab. The internal
link arrives with its sub-project, and nothing here pretends otherwise.
