# Learning Centre Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The nine tutorial chapters already in `docs/tutorial/` are readable inside the panel, and every screen has a Help control that opens the chapter for *that* screen.

**Architecture:** Chapters stay as markdown on disk and are rendered on request — one source, so the docs and the panel cannot drift. A Help control in the topbar maps the current route to a chapter. Images are served through a guard that admits only `*.annotated.png`.

**Tech Stack:** .NET 10, ASP.NET MVC, Razor, Markdig (new dependency — see Task 1), xUnit, FluentAssertions.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-15-learning-centre-design.md`. Read it before Task 1, including its correction section.
- **Zero NEW build warnings.** Exactly **2 pre-existing NU1903** on SSH.NET in `Harbora.Postgres.Tests` — leave them. **Security is out of scope by the owner's standing instruction.**
- **Baseline that must not drop:** build 0 errors; **4,314 passing, 0 failing**.
- **No database migration.** Chapters are files, not rows.
- **Bilingual.** Every string a user sees goes through `@T["…"]` or the `isFa` ternary. Note the chapters themselves are written in Persian — that is content, not UI copy, and is not translated.
- **The panel renders Persian by default in tests.** Assert on route fragments, `data-` attributes and file names.
- **Test names read as sentences.**
- **Narrative commit messages** — read `git log --oneline -10`. End each with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Shared tree.** Never `git stash`, `git stash pop`, `git checkout -- .`, `git reset --hard` or `git clean`. Stage by explicit path; never `git add -A`.
- **`main.ts` carries a hand-maintained lucide icon list** and `IconCoverageTests` fails when a view uses one that is missing. Six agents have hit this. If you add an icon, add it there.
- **Environmental trap.** `MSB3491 "Access to the path … denied"` on `obj/` files with a green suite means leftover MSBuild processes hold locks. Run `dotnet build-server shutdown`, then rebuild.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Harbora.Infrastructure/Learning/LearningLibrary.cs` **(create)** | Finds the chapters on disk, reads one, and decides which files may be served. No HTTP, no Razor. |
| `src/Harbora.Web/Controllers/LearnController.cs` **(create)** | `/learn` (index), `/learn/{slug}` (a chapter), `/learn/img/{file}` (a guarded image). |
| `src/Harbora.Web/Views/Learn/Index.cshtml`, `Chapter.cshtml` **(create)** | The two screens. |
| `src/Harbora.Web/Views/Shared/Design/_Topbar.cshtml` **(modify)** | The Help control. |
| `src/Harbora.Infrastructure/Learning/HelpMap.cs` **(create)** | Route path → chapter slug, and the honest "no chapter for this screen" answer. |
| `tests/Harbora.Tests/LearningLibraryTests.cs` **(create)** | The library, including the image guard. |
| `tests/Harbora.Tests/Http/LearnHttpTests.cs` **(create)** | The screens and the Help control over HTTP. |
| `tests/Harbora.Tests/LearningCensusTests.cs` **(create)** | Every chapter reachable; every mapped slug exists. |

---

## Task 1: A library that reads the chapters, and refuses the wrong images

**Files:**
- Create: `src/Harbora.Infrastructure/Learning/LearningLibrary.cs`
- Test: `tests/Harbora.Tests/LearningLibraryTests.cs`
- Modify: `src/Harbora.Infrastructure/Harbora.Infrastructure.csproj` (add Markdig)

**Interfaces:**
- Produces: `LearningChapter` (record: `Slug`, `Number`, `Title`, `FileName`), `LearningLibrary.Chapters()`, `LearningLibrary.ReadAsync(slug, ct)` returning rendered HTML or null, `LearningLibrary.MayServeImage(fileName)`.

**The dependency decision, which is yours to make and to justify.** There is no markdown renderer in this solution today. **Markdig** is the standard .NET choice and is what this plan assumes. Before adding it, check whether the solution has a policy on new packages (look for `Directory.Packages.props` or a pinned-version convention) and follow it. If you conclude a dependency is not wanted, the alternative is a deliberately tiny renderer covering only what these nine chapters use — but say which you chose and why, and do not silently hand-roll a half-renderer that mangles a chapter.

**The image guard is the piece with a reason behind it.** Only `*.annotated.png` may be served. Raw captures are whole-screen shots of a working panel carrying webhook secrets, storage keys and account emails; `.gitignore` keeps them out of the repository but **not** out of a developer's working directory, and a render path that serves "everything in `img/`" publishes them from there with nothing in git ever looking wrong. `MayServeImage` must also refuse anything that traverses out of the directory.

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/LearningLibraryTests.cs`, covering at least:

```csharp
    [Fact]
    public void Every_chapter_on_disk_is_offered()
    // Chapters() returns nine, numbered, with titles read from each file's first heading —
    // not from a list written here, which would be the thing it is meant to protect.

    [Fact]
    public void An_annotated_capture_may_be_served()
    // MayServeImage("01-dashboard.annotated.png") is true.

    [Fact]
    public void A_raw_capture_may_not_be_served_even_though_git_already_ignores_it()
    // MayServeImage("01-dashboard.png") is false. gitignore protects the repository;
    // this protects the render path, and they are different questions.

    [Fact]
    public void A_name_that_climbs_out_of_the_directory_is_refused()
    // MayServeImage("../../appsettings.json") and a rooted path are both false.

    [Fact]
    public void A_chapter_that_does_not_exist_reads_as_null_rather_than_throwing()
```

Write each body as real code against `TestPaths` — add a `DocsRoot` member alongside `WebRoot` and `InfrastructureRoot` if one is not there.

- [ ] **Step 2: Run it and watch it fail.** `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Debug --filter "FullyQualifiedName~LearningLibraryTests"` — expected: does not compile.

- [ ] **Step 3: Implement `LearningLibrary`.** Chapters are discovered by reading the directory and sorting on the numeric prefix, never from a hard-coded list. `README.md` is the index text, not a chapter. Titles come from each file's first `#` heading.

- [ ] **Step 4: Run the tests; then the full suite.**

- [ ] **Step 5: Commit.**

---

## Task 2: The two screens, and the guarded image route

**Files:**
- Create: `src/Harbora.Web/Controllers/LearnController.cs`, `src/Harbora.Web/Views/Learn/Index.cshtml`, `src/Harbora.Web/Views/Learn/Chapter.cshtml`
- Test: `tests/Harbora.Tests/Http/LearnHttpTests.cs`

**Interfaces:** consumes everything Task 1 produced.

**Three things to get right:**

1. **The renderer must not execute markup embedded in a chapter.** These files are trusted content in the repository, but the render path is the kind that outlives its assumptions. Configure the pipeline to disable raw HTML, and write the test that proves it.
2. **`/learn/img/{file}` goes through `MayServeImage`** and returns 404 for anything it refuses — not 403, which confirms a file exists.
3. **A missing chapter is a 404 page offering the index**, not an exception.

- [ ] **Step 1: Write the failing HTTP tests.** Follow `tests/Harbora.Tests/Http/AppAddressHttpTests.cs` for the fixture shape — `[Collection(HarboraHttpCollection.Name)]`, `(HarboraHttpFixture fixture)`, `Panel.GivenUser` / `Panel.SignedInAs`. Cover: the index lists nine chapters; a chapter renders its heading; a raw image name 404s; an annotated one is served; embedded markup is not executed.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement the controller and the two views**, following the card and typography conventions already in `Views/Apps/Index.cshtml`.

- [ ] **Step 4: Run the tests; then the full suite.**

- [ ] **Step 5: Commit.**

---

## Task 3: A Help control that knows what screen you are on

**Files:**
- Create: `src/Harbora.Infrastructure/Learning/HelpMap.cs`
- Modify: `src/Harbora.Web/Views/Shared/Design/_Topbar.cshtml`
- Test: `tests/Harbora.Tests/LearningCensusTests.cs`, and extend `LearnHttpTests`

**Interfaces:** `HelpMap.ChapterFor(string routePath)` returning a chapter slug or null.

**Why this task is the point of G.** A Help button that opens a table of contents is a link to a filing cabinet. It should open the chapter for the screen the person is on — the applications chapter from an app page, networking from domains. Sub-project A made every tab a real route, which is what makes the mapping possible.

**A screen with no mapping opens the index and says so.** It must not open chapter one, and it must not 404. An unhelpful Help button is worse than a missing one, because it costs a click to find that out.

- [ ] **Step 1: Write the failing census.** `LearningCensusTests`: every slug `HelpMap` can return names a chapter that exists on disk, and the map is non-empty. This is the test that catches a chapter being renamed and the map going stale — read the directory, never a list in the test.

- [ ] **Step 2: Write the failing HTTP tests.** The Help control on an app page points at the applications chapter; on an unmapped screen it points at the index and carries a `data-help-state="index"` marker.

- [ ] **Step 3: Run and watch them fail.**

- [ ] **Step 4: Implement `HelpMap` and the topbar control.** The map is longest-prefix on the route path, so `/apps/{id}/volumes` and `/apps` can resolve differently without listing every route.

- [ ] **Step 5: Run the tests; then the full suite.**

- [ ] **Step 6: Commit.**

---

## What this plan is not

Re-taking screenshots — several of the 30 annotated captures are already out of date after sub-projects A–F, and that is upkeep to follow, not part of delivering the Learning Centre · a docs site · translating the chapters · new capability; the routing guide documents what exists, and if it turns out path-based routing does not work, that is a finding to report rather than to fix here.
