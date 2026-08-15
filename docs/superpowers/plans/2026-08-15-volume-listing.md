# Volume Listing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Somebody can see what is inside an app's volume — names, sizes, dates, one directory at a time — without being able to write anything or to see outside it.

**Architecture:** A one-off container with the volume mounted read-only lists a directory and exits, using `IDockerEngine.RunOneOffAsync`, which the backup stager and Adminer already use. A path that leaves the mount root is refused in a way distinguishable from an empty directory.

**Tech Stack:** .NET 10, Docker.DotNet via `IDockerEngine`, ASP.NET MVC, Razor, xUnit, FluentAssertions.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-15-volume-listing-design.md`. Read it fully before Task 1 — its fail-closed section is the point of this sub-project.
- **Zero NEW build warnings.** Exactly **2 pre-existing NU1903** on SSH.NET in `Harbora.Postgres.Tests` — leave them. **Security is out of scope by the owner's standing instruction; the path and read-only requirements below are correctness requirements the spec states, so implement them without writing attack-scenario analysis.**
- **Baseline that must not drop:** build 0 errors; **4,361 passing, 0 failing**.
- **No database migration.** A listing is read at request time and stored nowhere.
- **Bilingual.** Every user-visible string through `@T["…"]` or the `isFa` ternary.
- **The panel renders Persian by default in tests.** Assert on `data-` attributes and on the request handed to the engine — never on a sentence.
- **Test names read as sentences.**
- **Narrative commit messages** — read `git log --oneline -10`. End each with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Shared tree.** Never `git stash`, `git stash pop`, `git checkout -- .`, `git reset --hard` or `git clean`. Stage by explicit path; never `git add -A`.
- **`main.ts` carries a hand-maintained lucide icon list**; `IconCoverageTests` fails on a missing icon. Seven agents have hit this.
- `UiBaselineTests` and `LayoutConventionTests` enforce reserved CSS classes and forbid physical-direction utilities.
- **Environmental trap.** `MSB3491 "Access to the path … denied"` on `obj/` with a green suite means leftover MSBuild processes hold locks. Run `dotnet build-server shutdown`, then rebuild.

**Interfaces you will use, read from source rather than guessed:**

```csharp
Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct);

public record DockerOneOffRequest(
    string Image,
    IReadOnlyList<string> Command,
    IReadOnlyList<(string Source, string Target, bool ReadOnly)> Binds,
    IReadOnlyDictionary<string, string>? Env = null,
    string? NetworkMode = null);
```

The one-off returns an **exit code**, and its output arrives through the `IProgress<string>` log. So the listing is parsed from what the helper prints — which makes the output format part of the contract, and Task 1's job to pin.

---

## Task 1: A listing that cannot see outside the volume

**Files:**
- Create: `src/Harbora.Infrastructure/Volumes/VolumeListing.cs` — the pure part: what path is acceptable, and how a helper's output becomes entries
- Create: `src/Harbora.Infrastructure/Volumes/VolumeBrowser.cs` — the part that runs the one-off
- Test: `tests/Harbora.Tests/VolumeListingTests.cs`

**Interfaces produced:** `VolumeEntry` (record: `Name`, `IsDirectory`, `SizeBytes`, `ModifiedAt`), `VolumeListing.AcceptPath(string?)` returning a normalised relative path or null, `VolumeListing.Parse(IEnumerable<string> lines)`, and `VolumeBrowser.ListAsync(app, volume, path, ct)`.

**Split pure from impure for the reason `ServicePlan` gives about itself**: the path rule and the parser are each one testable statement, and neither needs Docker to prove.

**The path rule is this task's whole point.** `AcceptPath` returns null — meaning refuse — for anything that leaves the root. Cover at minimum: `..`, `a/../..`, a rooted path (`/etc`), a backslash variant, an empty segment, and a path that is acceptable. Normalise to a relative path the helper can be handed.

**Symlinks cannot be settled by `AcceptPath`**, because a symlink is resolved inside the container, not by string inspection. Decide how the helper avoids following one out of the mount — and **say what you chose in your report**. If the chosen listing command cannot express that, say so rather than shipping a rule the code does not enforce.

- [ ] **Step 1: Write the failing tests** for `AcceptPath` and `Parse`. `Parse` must be tested against the **exact output** of the command you intend to run, not an idealised format.
- [ ] **Step 2: Run and watch them fail.**
- [ ] **Step 3: Implement both.** Pick a small, always-present image for the helper — check what the backup stager already uses and reuse it rather than introducing a second one.
- [ ] **Step 4: Implement `VolumeBrowser.ListAsync`.** The mount is `ReadOnly: true`. `NetworkMode` stays null — a listing helper needs no network, and the docstring on that parameter explains what giving it one would mean.
- [ ] **Step 5: Run the tests, then the full suite. Commit.**

---

## Task 2: The screen

**Files:**
- Modify: `src/Harbora.Web/Controllers/AppsController.Tabs.cs`, `src/Harbora.Web/ViewModels/AppTabViewModel.cs`, `src/Harbora.Web/Views/Apps/Volumes.cshtml`
- Test: `tests/Harbora.Tests/Http/VolumeListingHttpTests.cs`

**Interfaces consumed:** everything Task 1 produced.

**Four things to get right:**

1. **The volume is resolved through the app's own tenant-filtered collection**, never through a name off the route. D1 (`378ecfe`) does exactly this and is the shape to copy; the cross-tenant defect fixed in `6b0f91a` was the opposite.
2. **Three states, and they must look different:** entries; empty volume; could not read. The last two are opposite facts and this programme has spent nine sub-projects making sure they never render the same — carry a `data-` marker for each.
3. **A refused path says the path is not in this volume** — not an empty listing.
4. **A volume on a remote node** may not be listable. **Find out in this task whether `RunOneOffAsync` reaches a remote engine**, the way sub-project B3 discovered `NodeWorkloadEngine` had no inspect verb — by reading, not assuming. If it does not, render that as a permanent, explained condition rather than an error, and say so in your report.

- [ ] **Step 1: Write the failing HTTP tests.** Follow `tests/Harbora.Tests/Http/VolumeBackupHttpTests.cs`, which D1 created and which already seeds an app with volumes. Cover: a listing renders its entries; an empty volume renders the empty marker; an unreadable volume renders the unreadable marker and not the empty one; a refused path renders the refusal; an app in another workspace 404s.
- [ ] **Step 2: Run and watch them fail.**
- [ ] **Step 3: Implement the action, view model and markup**, following the card conventions already in `Volumes.cshtml`.
- [ ] **Step 4: Run the tests, then the full suite. Commit.**

---

## What this plan is not

Writing anything — download and upload are D3, deliberately after this · external access (D4) ·
previewing file contents · a host file manager; this is one volume at a time, reached through the app
that owns it.
