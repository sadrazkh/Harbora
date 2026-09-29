# B report — dangling images: the half of disk cleanup that did not exist

Branch `r5-dangling-sweep`. Status: **DONE**.

## Commits

| sha | what |
|---|---|
| `939c7cb` | the feature: engine members, agent routes, third cleanup sweep, report and page, periodic sweeper, setting wired through compose and the RUNBOOK, tests |
| `0f51077` | the report page says when its reclaimable total leaves out dangling images that could not be read |
| (last) | this report, an agent-route contract test, one corrected comment |

All three are authored as the owner alone, no trailer. `git log 8669ec4..HEAD` lists them.

## Verification

Per CONSTRAINTS.md: `dotnet test Harbora.slnx` with `MSBUILDDISABLENODEREUSE=1` and
`-p:UseSharedCompilation=false`, log named for the branch, exit code captured before grepping.

- `EXIT=0`, `Harbora.Tests.dll` line present (count 2 in the log): **`Passed! 6363, Failed 0`**
  (baseline 6286, +77). `Harbora.NodeAgent.Tests` 500 passed / 17 skipped, `Harbora.NodeIngress.Tests` 15.
- No compiler warnings. The only warnings in the log are `NU1900` (no route to the package vulnerability
  feed from this machine) and `NU1903` (`SSH.NET` in `Harbora.Postgres.Tests`) — both predate this work.
- The sweeper's timer-driven tests were run six times in a row against one build: stable.

## Design

- **`IDockerEngine`** gains `GetDanglingImagesAsync` (count + size) and `PruneDanglingImagesAsync`
  (what the daemon reported), plus `ImageSweepUnavailableException` (carries a `Reason`). No default
  implementation, on purpose: an engine that cannot examine its disk must say so by throwing, not answer
  an empty result that would read as clean. Every implementation compiles: `DockerEngine`,
  `RemoteDockerEngine`, `NodeWorkloadEngine`, `FakeDockerEngine`, and the `StubEngine` in
  `NodeSchedulingTests`.
- **`DockerEngine`** (one hunk, after `RemoveImageAsync`; nothing near `BuildImageFromTarAsync` or
  `ExecAsync`): the daemon's own `PruneImagesAsync` with `Filters = { dangling: { true: true } }`, and a
  `ListImagesAsync` with the same filter for the read. The value is **explicit**: the daemon defaults to
  `dangling=true` when the filter is absent, and `dangling=false` is `prune -a`. It is not a list-and-remove
  loop. The image count is the daemon's `Deleted` entries (an untag-only entry is not a deletion); bytes are
  its `SpaceReclaimed`, clamped rather than wrapped.
- **`Harbora.Agent`** gets `GET /agent/images/dangling` and `POST /agent/images/dangling/prune`, one-line
  pass-throughs, and `RemoteDockerEngine` calls them.
- **`DiskCleanupService`**: a third sweep per server after orphans and retention. Per server it reports
  `DanglingRemoved`, `DanglingReclaimedBytes` (the daemon's figure) and `DanglingNotExamined` (why not),
  and the run totals `DanglingRemoved`/`DanglingReclaimedBytes` — beside the orphan and retention figures.
  `FreedBytes` is still the measured before/after, now spanning all three sweeps, and the daemon's figure
  is never blended into it. A new `PruneDanglingImagesAsync` entry point runs **only** the third sweep and
  has no code path to any named image removal; that is what the background sweeper calls.
- **`DiskUsageReport` and the page**: `ServerDiskUsage.Dangling` (count, size, or a not-examined reason),
  a dangling card per server, the count included in the reclaimable total only when it was actually read,
  and a line above the servers when the total leaves unread dangling images out. The existing upper-bound
  paragraph is untouched and the card carries its own upper-bound sentence. Persian and English.
  The Clean up disk banner text and result message are updated (they said "only build images").
- **`DanglingImageSweeper`** (hosted, registered below the gate opener like the retention sweeper).

## The periodic sweep's default interval

**24 hours** (`Runtime:DanglingImageSweepHours`, env `Runtime__DanglingImageSweepHours`; `0` or less, or a
non-number, is off; fractions work). Why a day:

- The leak is the shape of rebuilds — it grows over days and weeks, not minutes. A day bounds what can
  accumulate between passes to a day's rebuilds, where the live server needed weeks of them to reach 70 GB.
- A shorter period adds daemon work (prune walks every image) and more chances to overlap a build, for a
  disk that was not going to fill in that time anyway. A longer one lets it refill in between.
- The brief asks for once a day.

Related choices: the first pass is **ten minutes after boot**, not a full interval later, because a panel
that restarts at least daily (it redeploys itself; the host reboots) would otherwise restart its own timer
each time and never sweep — and ten minutes keeps it off the boot path where the reconcilers compete for
the daemon. Values are clamped into [1 minute, 30 days]: `Task.Delay` refuses a period past ~49 days, and a
configuration typo that threw inside a `BackgroundService` would stop the host — the one thing this must
never do. An unreadable value (`=daily`) is caught, logged, and the sweep is off in that process.

`docker-compose.yml` now passes the variable through as a bare key and the RUNBOOK documents it, because
this project has twice shipped a documented setting that was set, visible in `docker inspect`, and inert
(HARBORA-0065); drift tests pin both, plus RUNBOOK default == code default.

It logs exactly one line per run, fixed prefix `Dangling image sweep:`, including a run that reclaimed
nothing (`pruned 0 dangling image(s) … panel: 0 image(s), 0 B`). Warning level only when a prune was
attempted and failed; not-examined machines are Information, since a daily warning about an agent nobody has
updated would bury real ones.

## How a too-old agent and a v1 node are reported

Both as **not examined, with a reason, never as zero and never as a failure**.

- **Agent answering 404**: `RemoteDockerEngine` reads a 404 from either dangling route as "this agent is too
  old" and throws `ImageSweepUnavailableException(RemoteDockerEngine.AgentTooOldReason)` — *"this agent is
  too old to be swept for dangling images (it does not have the endpoint yet, and answered 404); update
  Harbora.Agent on this server and it will be examined"*. A 401, 403, 500 or an empty body is thrown as a real
  failure and is **not** laundered into "too old" (tested). The machine is otherwise still swept — an old
  agent lists and removes tagged images fine — so this is its own field, `DanglingNotExamined`, not `Skipped`:
  the row shows the orphan/retention work that happened and the dangling reason. On the report the server
  is examined and its dangling line is not.
- **v1 node**: `NodeWorkloadEngine.GetDanglingImagesAsync/PruneDanglingImagesAsync` throw the same exception
  with `NodeWorkloadEngine.ImagesManagedByNodeReason(nodeId)` — the sentence `DiskCleanupService` and
  `DiskUsageReport` already gave for this case, now extracted to one place used by all three (identical text;
  the existing tests still pass). The callers' existing `NodeBehind` check also short-circuits the whole server,
  so the row carries the same reason in `Skipped` and in `DanglingNotExamined`.
- A server the factory refuses, and a prune that was attempted and threw, are reported the same way (the
  latter as *"the dangling-image prune failed: …"*, and `Faulted` in the dangling-only result).
- **Where it shows**: the result message of Clean up disk (`Dangling images not examined: web-02 (reason)`),
  the disk report's dangling card, the report header line, and the sweeper's per-run line.

## What is proven only against fakes

There is no Docker on this machine. Nothing here talked to a daemon or a real agent.

- **What the real daemon does with the prune request** is Docker's documented behaviour, not reproduced.
  The one real observation is the manual `docker image prune -f` on the live server: 165 dangling images,
  69.78 GB reclaimed, disk 96% to 28%, all 13 containers unaffected.
- **`DockerEngine`** is tested by driving the real class with a `DispatchProxy` standing in for
  Docker.DotNet's `IImageOperations`, scripted with responses and recording every call. That pins the request
  that leaves the engine (`dangling=true` only, never `false`, one `PruneImagesAsync` call and nothing else,
  no removal by name) and the response mapping. It does not prove Docker.DotNet.Enhanced puts the filter on
  the wire in the form the daemon accepts; that is the same map form `ListContainersAsync` already sends to
  the live daemon for its label filter, which is the only evidence.
- **`RemoteDockerEngine`** is the real class against a scripted HTTP handler (URLs, bearer token, 404 vs other
  statuses, JSON). **`Harbora.Agent`** is not booted — it is its own host with no test project. A source-level
  contract test ties the agent's two route strings to the engine's URLs; the routes are one-line
  pass-throughs to `IDockerEngine`. If those ever drifted every agent would read "too old" silently, which is
  why that test exists. The JSON shapes assume the agent's default serializer camel-cases the two records.
- **Sizes**: the dangling size is the sum of `ImagesListResponse.Size`, an upper bound (shared layers), same
  as the tagged sizes the report already used. Not checked against a live daemon or the containerd image store.
- **The sweeper's timing** uses real timers of tens of milliseconds through an internal constructor; a full
  day elapsing is not exercised, and neither is a real host stopping (tested: `StartAsync`/`StopAsync` on the
  service, including stop during the startup delay, a failing first run followed by a successful one, and an
  unreadable setting).
- The HTTP tests run the booted panel against `FakeDockerEngine` and EF InMemory (no Postgres here).

## Notes and residual concerns

- **A build overlapping a prune.** The daemon never removes an image a container references, but a
  classic-builder build has a momentary window between a step's image being committed and the next
  container being created where the newest intermediate image is unreferenced. It is tiny and the manual
  prune ran the same way without incident, so I did not gate the sweep on "no deployment is Building": a
  deployment row stuck in Building would then starve the sweep forever, which is worse (a new structural blind
  spot of exactly the kind this fixes). If it ever bites, the answer is to skip a server with a build in flight.
- **Two `Server` rows on one daemon** (several `IsLocal` rows resolve to the same engine) are each pruned; the
  second reports zero. Harmless, and the same fan-out the existing sweeps already have.
- **`ExecuteTask.Status`**: on .NET 10 a `BackgroundService` stopped while awaiting reports `Canceled` even
  when `ExecuteAsync` returns normally (reproduced with a bare probe class). The tests assert "completed and
  not faulted" rather than "ran to completion".
- **CRLF checkouts**: `DocumentationDriftTests.ReachesAContainer`'s bare-key regex ended in `$`, which in
  multiline mode stops before `\n` only, so on a Windows checkout (`* text=auto`, CRLF working files) a bare
  compose key at end of line read as absent — the existing retention/jobs checks passed only because a comment
  in the compose file mentions `${...}` for each. Fixed with `\r?`; it is the reason my new drift test failed
  locally at first.
- Left alone deliberately: the historical planning documents under `docs/` that describe the old "build images
  only" cleanup; the RUNBOOK (the operator-facing document) is updated.
