# A — Disk usage report — final report

## Status

DONE

## Commit

- `d4526ce03ad062b0aba2d2757d1e0076a7afbcf2` — "Add read-only disk usage report (A-brief)" (all the
  feature code, view, and tests).
- `5b2dd88` — "Add A-brief final report" (this file).

Both on branch `r4-disk-report`. Working tree is clean.

**Note on the SHA above changing mid-task:** my first commit landed as `97e57bc`. Before I finished
verifying, this worktree was auto-rebased onto an updated base (reflog shows `rebase (start): checkout
master` / `rebase (finish)`), replaying my commit as `d4526ce` and pulling in five other agents'
already-completed commits from this round (none of them touch any file this task changed — they are
`deploy/`, `_Layout.cshtml`/sidebar, `.github/workflows/ci.yml`, and other agents' own brief/report
files, all outside this task's scope). I confirmed the replay was byte-for-byte the same change
(`git diff 97e57bc d4526ce --stat` shows only the newly-arrived commits' own files, none of mine) and
re-ran the full suite afterward — see below — rather than trusting the pre-rebase numbers.

## Test summary

Full solution suite, per CONSTRAINTS.md's verification recipe (`MSBUILDDISABLENODEREUSE=1`,
`-p:UseSharedCompilation=false`, exit code captured before piping, `Harbora.Tests.dll` line count
confirmed non-zero, log written to a branch-named file per this round's updated instruction rather
than a shared `/tmp/t.log`):

```
Passed!  - Failed: 0, Passed: 500,  Skipped: 17, Total: 517  - Harbora.NodeAgent.Tests.dll (net10.0)
Passed!  - Failed: 0, Passed: 15,   Skipped: 0,  Total: 15   - Harbora.NodeIngress.Tests.dll (net10.0)
Passed!  - Failed: 0, Passed: 6245, Skipped: 0,  Total: 6245 - Harbora.Tests.dll (net10.0)
EXIT=0
```

This is the post-rebase run (log: `/tmp/test-r4-disk-report.log`). `Harbora.Tests.dll` sits at **6245**
against the round's stated baseline of **6224** — a rise of 21: the 18 tests this task added (11 in
`DiskUsageReportTests.cs`, 7 in `MonitoringDiskReportHttpTests.cs`) plus a few more that arrived with
the rebased-in commits (e.g. `DocumentationDriftTests.cs`, added by another agent this round). Nothing
dropped, nothing failed, nothing skipped to make this pass. An earlier run taken before the rebase
landed showed exactly 6242 (6224 + my 18) — consistent with this one once the rebased-in tests are
accounted for, and evidence the numbers were not cross-contaminated by another concurrent agent's run
sharing the old unscoped `/tmp/t.log` path.

There is no Docker on this machine — every figure above is proven against `FakeDockerEngine` /
`FakeServerEngineFactory` (unit tests) and the shared `HarboraWebFactory.Docker` fake (HTTP tests),
never a real daemon. Nothing here was verified against a real Postgres, Traefik, or multi-node
cluster either; none of that was needed for this task.

## What was reused vs. written new

**Reused, called directly, not re-implemented (this was the point of the brief):**
- `CleanupPlan.OrphanedBuildImages` — orphaned-image detection, called with the exact same
  `(onHost, imagePrefix, everySlug)` shape `DiskCleanupService` uses.
- `DeploymentPlanning.ImagesToPrune` — "what a cleanup would remove right now", called per app with
  the same inputs the cleanup sweep itself passes. A test
  (`Prunable_now_tags_for_an_app_are_exactly_what_ImagesToPrune_would_prune`) independently calls
  `ImagesToPrune` with identical inputs and asserts the report's own list is equivalent, rather than
  asserting a hand-written expected list — per the brief's own test instruction.
- `DeploymentPlanning.RollbackEligibleDeploymentIds` — "which are rollback-eligible", the same
  question the Deployments tab already asks this helper.
- `DeploymentPlanning.BuildImagePrefix` — scopes an app's own image tags, the same prefix retention
  uses.
- `Harbora.Infrastructure.Tenancy.ByteSize` (`Format`/`Measured`) — every byte figure on the page.
- `NodeWorkloadEngine.NodeBehind` — detecting a v1 node with no image verbs, worded identically to
  `DiskCleanupService.SweepAsync`'s own refusal ("node {id} manages its own images; the panel can
  neither list nor remove them, so nothing here was examined").
- `IServerEngineFactory` / `IDockerEngine.ListImagesAsync` — the same per-server engine resolution
  `DiskCleanupService` and `DiskVolumeOrphanReport` already use.
- `Volume.StorageBytes` / `StorageMeasuredAt` — the existing `StorageMeasurer` background walk's own
  figures, read directly for "volumes with their sizes" rather than re-measuring anything. A volume
  never yet walked reports `null` (rendered as "not yet measured"), not zero.
- The gating pattern itself: `[Authorize(Policy = Capabilities.ServersManage)]` on the controller
  action (same policy `ServersController` and the `Cleanup` action already use) and
  `User.IsInRole("Owner") || User.IsInRole("Admin")` on the view link (identical condition to the
  existing cleanup-button block in `_Banners.cshtml`).
- `DiskVolumeOrphanReport`'s *shape* (per-server reached/refused/unreachable, never folded into a
  silent total) was followed for this report's own `ServerDiskUsage.NotExamined`, as the brief asked
  ("the closest existing shape ... follow it") — its actual orphan-volume detection was **not**
  re-invoked from inside this report; the new page instead links out to the existing
  `/servers/disk-volume-report` for that, since duplicating a second live orphan-volume sweep felt
  like exactly the kind of second implementation the brief was warning against, and the "volumes with
  their sizes" line item is satisfiable from the database alone.

**Written new:**
- `src/Harbora.Infrastructure/Maintenance/DiskUsageReport.cs` — the report itself: `AppImageUsage`,
  `OrphanedImagesUsage`, `VolumeUsage`, `ServerDiskUsage`, `DiskUsageReportResult` records, and the
  `DiskUsageReport` service class (`BuildAsync`). This is the only new "what is prunable" logic, and
  it is entirely glue over the calls named above — no independent orphan/prune rule was invented.
- `MonitoringController.DiskReport` — new `GET /monitoring/disk-report` action, gated with
  `Capabilities.ServersManage`.
- `src/Harbora.Web/Views/Monitoring/DiskReport.cshtml` — the read-only page: a top "reclaimable now"
  banner stating the upper-bound disclaimer in both languages, then one section per server (either
  "not examined: <reason>" or the per-app table + orphaned-images block + volumes block).
- A link from `_Banners.cshtml`, next to the existing "Clean up disk" button, gated identically.
- DI registration in `DependencyInjection.cs` (`services.AddScoped<Maintenance.DiskUsageReport>()`).
- Two new test files: `tests/Harbora.Tests/DiskUsageReportTests.cs` (11 unit tests against the fakes)
  and `tests/Harbora.Tests/Http/MonitoringDiskReportHttpTests.cs` (7 tests through the real booted
  panel via `HarboraWebFactory`).
- One existing test fixed for the new constructor parameter:
  `tests/Harbora.Tests/MonitoringControllerBackupStalenessTests.cs` (added a `DiskUsageReport`
  instance to its hand-built `MonitoringController`).

## How each brief requirement was verified

- **Per app: count/bytes, active tag, rollback-eligible, prunable-now** — `AppImageUsage` on the
  model; table columns on the page; `Prunable_now_tags_for_an_app_are_exactly_what_ImagesToPrune_would_prune`
  and `The_active_deployments_image_is_never_listed_as_prunable_now_or_counted_reclaimable`.
- **Per server: orphaned images (count+bytes), volumes with sizes, reclaimable-now total** —
  `ServerDiskUsage.Orphaned`, `.Volumes`, `.ReclaimableBytes`; `An_image_of_a_deleted_app_is_reported_as_orphaned_not_as_any_living_apps`,
  `Orphaned_bytes_and_count_come_straight_from_CleanupPlan`, `Reclaimable_total_is_orphan_bytes_plus_prunable_bytes_across_apps`,
  `A_volume_never_measured_reports_a_null_size_not_zero` / `A_measured_volume_reports_the_stored_size`.
- **Upper-bound honesty requirement** — stated in both languages in the top banner, unconditionally
  (not only when a server has anything to report); `The_upper_bound_disclaimer_is_in_Persian_by_default`
  and `...in_English_when_the_request_asks_for_it` assert the copy through a real rendered HTTP
  response (decoded with `WebUtility.HtmlDecode`, since Razor's default `HtmlEncoder` writes Persian
  as numeric character references, not literal glyphs — worth flagging below).
- **Refused/v1-node servers named as not examined, never rendered as clean** —
  `A_server_the_factory_refuses_is_named_not_examined_with_its_reason`,
  `A_server_behind_a_v1_node_is_not_examined_for_the_same_reason_DiskCleanupService_gives`,
  `Every_server_appears_once_examined_or_not`, plus the HTTP-level
  `A_server_with_no_agent_endpoint_reads_as_not_examined_not_as_clean`. The view branches on
  `NotExamined is not null` before ever touching `ReclaimableBytes`, so "0 B" and "not examined" cannot
  render alike.
- **Bilingual, matching surrounding copy** — followed the existing inline-ternary idiom used
  throughout `Monitoring/*.cshtml` (no resx additions), Persian as default culture per
  `Program.cs`.
- **Gated exactly as the cleanup button** — see "reused" above; `A_viewer_may_not_read_the_disk_report`
  and `A_viewer_does_not_see_the_disk_report_link_on_the_monitoring_page` cover both the route and the
  view-level link.
- **Read-only, no per-image delete control** — the controller action is `[HttpGet]` only; nothing on
  the page posts anywhere.

## Things worth flagging (not blockers, but judgment calls a reviewer should see)

1. **Razor's default `HtmlEncoder` writes Persian as `&#xHHHH;` numeric references, not literal
   glyphs.** This is pre-existing platform behavior (no custom encoder is registered anywhere in
   `Program.cs`), not something this task changed. It only became visible here because this is
   apparently the first HTTP test in the suite that asserts a literal Persian substring against a
   fully-rendered HTML body (other bilingual tests either assert on a ViewModel string directly, as
   `QuotaRefusalBilingualismTests` and `MonitoringControllerBackupStalenessTests` do, or only assert
   ASCII structural markers like `lang="fa"`, as `ErrorPagesAndLocalizationHttpTests` does). My HTTP
   tests decode the response with `WebUtility.HtmlDecode` before asserting, which is correct and
   robust, but it's worth knowing this gotcha exists for anyone writing the next "assert real Persian
   copy through a real HTTP render" test.
2. **"Volumes with their sizes" is sourced from the database (`Volume.StorageBytes`), not from a live
   engine call.** `StorageMeasurer` walks each volume roughly every 10 minutes at most, so a
   freshly-created volume can legitimately show "not yet measured" for a while — this is the same
   honesty behavior the rest of the platform already accepts for that figure (billing, quota), just
   surfaced here too rather than re-measured synchronously (which would make loading this page slow
   and would itself compete with real work, per `StorageMeasurer`'s own doc comment).
3. **Per-app image accounting is scoped to the exact `{prefix}/{slug}:build-` tag family**
   (`DeploymentPlanning.BuildImagePrefix`), the same family `ImagesToPrune`/rollback-eligibility use —
   deliberately, so the four numbers shown for one app (count, bytes, active, rollback-eligible,
   prunable) can never disagree with each other. I noticed `DeploymentPipeline` also stamps a nominal
   `{prefix}/{slug}:compose-{n}` tag for Compose-stack deployments, but `DeploymentPlanning.ImagesToPrune`
   itself does not match that shape either (only `:build-`) — that is a pre-existing asymmetry in
   production retention, not something introduced or fixed here, and out of scope per "do not change
   what `DiskCleanupService` deletes."
4. **The reclaimable-now total is images only** (orphans + per-app prunable-now), matching exactly
   what the existing `DiskCleanupService`/cleanup button actually deletes. Known app-volume sizes are
   shown for information but are not added into "reclaimable now", since nothing on the cleanup path
   removes volumes.

## What could not be verified on this machine

- No live Docker daemon, so nothing above was checked against a real `docker images`/`docker volume
  ls` — only against `FakeDockerEngine`/`FakeServerEngineFactory` and the shared HTTP-test fake.
- No multi-node / real v1-agent hardware — the v1-node refusal path is proven by constructing the real
  `NodeWorkloadEngine` class directly (same idiom the existing `DiskCleanupTests` and
  `DiskVolumeOrphanReportTests` already use), not against an actual remote agent.
- No Postgres — this whole feature reads through EF Core's InMemory provider in every test, same as
  the rest of the suite's non-Postgres lane.
