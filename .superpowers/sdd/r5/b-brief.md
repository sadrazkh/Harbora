# B — Dangling images: the half of disk cleanup that did not exist

## What happened on the live server

The disk reached **96%** (92 of 96 GB). `docker system df` showed 199 images, 165 of them
**dangling** — untagged and referenced by no container — with **70.16 GB reclaimable**. Harbora's own
tagged build images were 13 images, about 10 GB, already at roughly two per app.

A manual `docker image prune -f` reclaimed **69.78 GB** and took the disk to **28%**. All 13 running
containers were unaffected (names diffed before and after).

Nothing in the platform would have found that. `DiskCleanupService` only considers images under the
`harbora/` build prefix — deliberately, so it can never delete a customer's image — and the new
`DiskUsageReport` reuses the same candidate rule. So on a machine that was 96% full, the report would
have said "about 10 GB of build images, little reclaimable", and **Clean up disk** would have freed
almost nothing. Every classic-builder rebuild leaves its predecessor's intermediate layers untagged,
and nothing removes them. It will fill again.

## Why dangling images are safe to remove, and the one thing that is not

A dangling image is untagged and is not the parent of any tagged image. Docker's own prune API with the
`dangling=true` filter removes only those, and **never** removes an image that any container — running
or stopped — references. That is the whole safety argument and it is Docker's, not ours: use the
daemon's prune call, **do not** list-and-remove dangling images yourself, because a hand-rolled loop
reintroduces the race the daemon's call already handles.

Not in scope, and must not happen: removing **tagged** unused images (`prune -a` semantics). That would
delete build bases such as `dotnet/sdk` and force every next build to re-pull, and it would delete the
panel's rollback tags.

## Build

1. **`IDockerEngine`** gains what the sweep and the report need — a read that returns the dangling
   images' count and size, and a prune that returns what the daemon says it reclaimed. You are the
   only agent adding members to `IDockerEngine` this round. Every implementation must compile:
   `DockerEngine`, `RemoteDockerEngine`, `NodeWorkloadEngine`, and every fake under `tests/`.
2. **`DockerEngine`** — the daemon's own image prune with the dangling filter, and an image list with
   the same filter for the read. Keep your edits away from `BuildImageFromTarAsync` and `ExecAsync`:
   two parallel agents are editing those two methods.
3. **`RemoteDockerEngine`** talks to the older `Harbora.Agent` (`src/Harbora.Agent/Program.cs`, which
   already exposes image list and remove endpoints). Add matching endpoints there. An agent already
   deployed on some machine will not have them until it is updated, so a 404 from the agent is **"this
   agent is too old to be swept"** — reported as not examined, with that reason — never as zero and
   never as a failure.
4. **`NodeWorkloadEngine`** — a v1 node manages its own images. Report it not examined, with the same
   reason `DiskCleanupService` already gives for that case. Read `NodeWorkloadEngine.NodeBehind`.
5. **`DiskCleanupService`** — a third sweep per server, after the two it already has, using the prune
   call. Report the count and the **daemon-reported** bytes reclaimed as their own figures in
   `DiskCleanupServerResult`/`DiskCleanupResult`, beside the existing orphan and retention figures.
   Keep the measured-before-and-after `FreedBytes` as it is — it stays the honest total.
6. **`DiskUsageReport`** and its view — show dangling images per server, count and size, so the page
   stops being blind to the thing that actually filled this disk. Keep the existing upper-bound
   wording: dangling layers can be shared too.
7. **A periodic sweep.** Today the only caller of `DiskCleanupService` is the Monitoring button, so
   nothing runs unless a person presses it. Add a hosted background sweep that prunes **dangling images
   only** — not orphans, not retention — once a day. It must never stop the panel starting and must log
   one line per run with what it reclaimed per server, including a run that reclaimed nothing. Follow
   the shape of an existing hosted service in `DependencyInjection.cs` (for example the retention
   sweeper, which already logs one line per run for exactly this reason). Make the interval a setting
   with a sensible default; `0` disables it.

## Tests

- The cleanup prunes dangling images through the engine's prune call and reports its figures.
- Tagged images, including unused ones, are never passed to anything that removes them.
- A v1 node and an agent that answers 404 are reported not examined, with their reasons, and do not
  read as clean.
- The report shows dangling count and size per server.
- The periodic sweep calls only the dangling prune, logs a line for a run that reclaimed nothing, and a
  thrown failure does not stop the host.

## Verify

Per `.superpowers/sdd/r5/CONSTRAINTS.md`. Baseline 6286. No Docker here: the prune's real behaviour is
proven only against fakes; say so. The one real observation you can cite is the manual prune above.
