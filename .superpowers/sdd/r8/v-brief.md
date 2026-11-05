# V — The checks: does this app really work?

Read `.superpowers/sdd/r8/CONSTRAINTS.md` and `docs/superpowers/specs/2026-11-04-launch-design.md` first.

You implement `IAppVerifier` (`src/Harbora.Infrastructure/Verification/AppVerificationContracts.cs`), the pure log reader, and the button on an
app's page. S's launch tick calls `RunAsync` with no session; U renders `Views/Shared/Design/_AppChecks.cshtml` on the launch page. That partial
is yours to refine, but its model type and its `data-check`/`data-check-status` attributes stay. Do not touch `src/Harbora.Infrastructure/Launch/`.

## Use what exists

Read these before writing anything: `IManagedServiceEngine.TestConnectionAsync` and `Networking.ConnectionProbe`,
`IDockerEngine.GetLifecycleAsync`/`GetLogsAsync`/`ListContainersAsync`/`ConnectNetworkAsync`, `IServerEngineFactory.ResolveAsync`,
`IUptimeProbe.ProbeAsync` and how `UptimeChecker` calls it, `HealthProbeRule`, `HealthDiagnosis`, `ServicePlan` (`HasPublicTraffic`,
`IsLongRunning`, `HasHttpHealthCheck`), `DeploymentPlanning.ContainerName`, `Networking.EnvironmentNetwork`/`NetworkPlan`, `ISecretRedactor`, and
how the app overview already finds its running container and its primary domain.

## The checks, in this order

Each check returns an `AppCheck` in both languages. Plain words: a person who has never heard of a network namespace reads these.

**`running`** — Is the active deployment's container running? A kind that is not long-running (`ServicePlan.IsLongRunning`, a cron job) is `Skipped`
with the reason. No active deployment is `Failed`, with a link to the deployments tab. An engine that throws `NotSupportedException` is `Skipped`,
saying that this server cannot answer yet; do the same for every check below.

**`restarts`** — `GetLifecycleAsync`. The first 30 seconds after `StartedAt` are `Pending` (a container that crashes on its first query looks fine until
then), unless `giveUpAt` has passed. `RestartCount > 0` is `Failed`: it is crash-looping, with a short redacted tail of its log in `Detail`, and the fix
pointing at the logs. Null figures are `Skipped`, never zero.

**`service:{id}`**, one per attached `AppManagedService` (load them with `IgnoreQueryFilters`, scoped to this app).
- Service not `Running`: `Failed`, link to its page, `ErrorMessage` in the detail. `Provisioning` is `Pending`.
- `ConnectionProbe.WhyUnsupported(type)` non-null: `Skipped` with that reason, translated. It is not a pass.
- Otherwise `TestConnectionAsync`. On an error: connect the service to the environment's network (additive; `ConnectNetworkAsync` never
  restarts anything, and it swallows its own failure, which is why the second test is the proof), then test once more. The second attempt passing is `Passed`,
  with a detail saying the database was not on the app's network and now is. This is a real defect we have shipped: an attach gives correct credentials
  to a database the app cannot reach, and nothing logs it. Still failing: `Failed` with the probe's explanation.
- Be exact about what this proves. The probe uses the instance's own credentials on the environment's network (read `CredsFor`), so word it as "answers on this
  app's network" rather than claiming the app itself connected.
- `AppManagedService.HasUnpublishedChanges` true: `Warning`, the running container does not have these variables yet, redeploy. It outranks a pass.

**`url`** — Only when `ServicePlan.HasPublicTraffic(kind)`. No domain: `Skipped`, saying why. Otherwise probe `https://{primary host}/`. A response below 500 passes,
the same reasoning as `HealthProbeRule` for the root (check what `expectedStatus` means in `ProbeAsync`; you may need to read `HttpStatus` off a `Down` rather than ask for one status).
No answer yet: `Pending` until `giveUpAt` (a certificate is issued on the first hit, and that takes a while), then `Failed` with the probe's own detail and a fix that names DNS
and port 80. `CouldNotRun` is `Skipped`: that is a fault in the checker, not a fact about the app. A 5xx is `Failed`.

**`logs`** — `GetLogsAsync(container, 200)` through a pure `AppLogDiagnosis.Find(IEnumerable<string>)`, in its own file and tested on real-looking lines. It returns at most
three findings, each a stable code plus a sentence in both languages saying what it means and what to do. No finding: `Passed`. Findings: `Warning`. At least:

| Found | Meaning |
|---|---|
| `localhost:5432`, `127.0.0.1:5432`, `::1:5432` and the same for 3306, 27017, 6379, 5672 with a connect error | The app is connecting to itself. It is ignoring the address it was given: read `DATABASE_URL` (or the name shown on the app's page) |
| `ECONNREFUSED`, `Connection refused`, `could not connect to server`, `getaddrinfo ENOTFOUND`, `Name or service not known`, `could not translate host name`, `no such host` | It cannot reach something |
| `password authentication failed`, `Access denied for user`, `Authentication failed`, `WRONGPASS`, `NOAUTH` | It reached the database with the wrong credentials: often an old value in the app's own variables outranking the attachment |
| `relation "…" does not exist`, `Table '…' doesn't exist`, `no such table` | The database is empty: its migrations have not run. Set a release command |
| `Cannot find module`, `ModuleNotFoundError`, `ImportError: No module named`, `Could not load file or assembly` | A dependency is missing from the build |
| `EADDRINUSE`, `Address already in use` | Two things want one port |
| `Running on http://127.0.0.1`, `listening on 127.0.0.1`, `Now listening on: http://localhost`, `bind 127.0.0.1` | It listens only inside its own container, so nothing can reach it. Listen on `0.0.0.0` |

Quote at most a short matched fragment, redacted through `ISecretRedactor` with the app's secret values, cleaned by `LogText.Clean`. A log line is not trusted to be free of secrets.

## `RunAsync`

- `previous`: a `service:*` check that already `Passed` is not repeated in the same run, because each one costs a container. Everything else is re-read.
- Pending after `giveUpAt` becomes `Failed`, with what it was waiting for.
- **Never throws for anything about the app.** One check's failure is that check's `Failed` or `Skipped`, and the others still run. Only cancellation propagates.
- Stores the result in `AppVerifications` (one row per app, overwritten; `CompletedAt` set when complete). Works with no session: `IgnoreQueryFilters`, workspace from the app.
- Bound the whole run so a hung daemon cannot hold it: give each `TestConnectionAsync` a timeout (it can pull an image, so be generous, about two minutes); on timeout that check is `Skipped`, saying so.

`GetLatestAsync` is a workspace-filtered read (with no session the scope is unscoped, so the tick can use it too). Deserialise tolerantly.

## The button

`src/Harbora.Web/Controllers/AppsController.Verify.cs`, a new partial: `POST /apps/{id}/verify`, `[ValidateAntiForgeryToken]`, capability `Capabilities.AppsOperate`, with the
per-action access check `AppScopeCensusTests` demands. It runs with `giveUpAt` a short way ahead (about 30 seconds), then redirects to the app's overview with the result stored. On the overview
tab a card, "Is it working?" / «سالم کار می‌کند؟»: the latest report through `_AppChecks`, when it ran, and a "Check now" / «بررسی کن» button. Never checked: a sentence and the button, no empty box.
Check `DetailTabCensusTests` and how the overview gets its data. Icons you use must be imported in `Scripts/main.ts` (`IconCoverageTests`).

## Tests

`tests/Harbora.Tests/Verification/`: fakes in the style of `Fakes/`. Each check's every branch above. The self-heal: the second probe passes only after `ConnectNetworkAsync` was called with the right
network and container. `HasUnpublishedChanges`. A passed service check not repeated. Pending to Failed at `giveUpAt`. A throwing engine gives `Skipped`, and the rest still run. No session works.
A secret in a log line never reaches a check. `AppLogDiagnosis` on real-looking lines from Node, Python, .NET and Go, and silence on a healthy log (a log that merely mentions "connection" says nothing). HTTP tests for the button: 404 for another workspace's app, antiforgery, the card rendering.

## Worktree

`E:/Cash.Net/source/repos/sadrazkh/Harbora-r8-v`, branch `r8/v`. Report: `.superpowers/sdd/r8/v-report.md`.
