# A — A disk usage report that says what is taking the space

## Why

`Monitoring/Index` shows one disk figure: used of total, plus a warning banner when it crosses a
ratio. There is a "🧹 Clean up disk" button beside it. What there is not, anywhere, is a breakdown —
so "the disk is filling up" cannot be turned into "this app's old build images are 14 GB", and a
person cannot tell before pressing the button whether it will reclaim 200 MB or 20 GB.

## Read these first — you are reusing them, not reimplementing them

- `src/Harbora.Infrastructure/Maintenance/DiskCleanupService.cs` — the existing sweep. Your report
  must describe exactly what this would delete. Read its doc comments; they explain the per-server
  design and the shared-layer honesty rule you inherit.
- `src/Harbora.Infrastructure/Maintenance/CleanupPlan.cs` — `OrphanedBuildImages`.
- `src/Harbora.Infrastructure/Deployments/DeploymentPlanning.cs` — `ImagesToPrune` and
  `RetainedImageTags`. **Call these.** A second notion of "what is prunable" would drift from the
  cleaner, and the drift would show as a report promising space a cleanup does not free.
- `src/Harbora.Infrastructure/Storage/DiskVolumeOrphanReport.cs` — the closest existing shape for a
  read-only disk report, including how it handles a server it cannot reach. Follow it.
- `src/Harbora.Infrastructure/Tenancy/ByteSize.cs` — the existing byte formatter. Use it.

## Build

A read-only report, per server, and a page for it.

Per server, per app: the app's build-image count and total bytes, which tag is the active one, which
are rollback-eligible, and which a cleanup would remove **right now**. Plus, per server: orphaned
build images (count and bytes), volumes with their sizes, and a stated "reclaimable now" total.

**The honesty requirement, inherited from `DiskCleanupResult.FreedBytes`' own doc.** Docker layers are
shared, so adding up per-image sizes **overstates** what a cleanup frees. `DiskCleanupService`
deliberately measures the disk's before/after difference instead. Your report cannot measure a future,
so it must say on the page that the reclaimable figure is an upper bound and why. A number that
overstates the win teaches people to distrust the page — the existing code says exactly that, and this
page must not undo it.

A server the engine factory refuses, or one behind a v1 node that has no image verbs, must be named as
not examined — the same discipline `DiskCleanupServerResult.Skipped` already keeps. "0 bytes" and "not
examined" are different facts and must not render alike.

**Where it goes:** a new action on `MonitoringController` and a view, reachable from the disk banner /
cleanup area on `Monitoring/Index`. Gate it exactly as the cleanup button is gated (Owner/Admin) — the
same list of hostnames and app slugs the servers page is already gated on.

Bilingual, English and Persian, matching the surrounding copy. The panel's request culture is `fa`.

## Scope boundaries

- **Read-only.** Do not add a per-image delete control. The existing cleanup button plus a settable
  retention covers the need; a second destructive control is out of scope this round.
- Do not touch `deploy/`, `docker-compose.yml`, `install.sh` or `RUNBOOK.md` — a parallel agent owns
  those.
- Do not touch `src/Harbora.Web/Views/Shared/_Layout.cshtml` or `Scripts/app.css` — another parallel
  agent owns those.
- Do not change what `DiskCleanupService` deletes.

## Tests

- The report names, for a fake engine with known images, exactly the tags `ImagesToPrune` would prune
  for the same inputs — assert they agree rather than asserting a hand-written list.
- An orphaned image (app deleted) is reported as orphaned, not as that app's.
- The active deployment's image is never counted as reclaimable.
- A server the factory refuses is reported as not examined, with its reason, and does not read as a
  clean server.
- A server behind a v1 node is reported as not examined for the same reason `DiskCleanupService`
  gives.
- The reclaimable total is labelled an upper bound on the page (assert the copy exists, both
  languages).

## Verify

Per `.superpowers/sdd/r4/CONSTRAINTS.md`. Baseline 6224 in `Harbora.Tests.dll`. There is no Docker
here, so every figure is proven against fakes only — say so in your report.
