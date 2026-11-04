# V report: the checks

Worktree `Harbora-r8-v`, branch `r8/v`, based on `377ae2c`. No migration added. No AI anywhere in this path.

## What was built

**`AppVerifier`** implements `IAppVerifier`. In report order: `running`, `restarts`, one `service:{id}` per attached database, `url` (only for kinds with public traffic), `logs`. Each runs under its own guard, so a throwing or hanging engine becomes that check's `Skipped` and the rest still run. Only the caller's cancellation propagates. Reads pin the app and ignore the workspace filter, and the workspace comes from the app. Timeouts: 2 minutes per `TestConnectionAsync`, 5 minutes for all of an app's databases together, 20 seconds per other engine call, 10 seconds per address probe.

**`AppLogDiagnosis`** is the pure log reader: `Find(IEnumerable<string>)` returns at most three findings, one per kind, with a stable code, a sentence in both languages and a short fragment. All expressions are `NonBacktracking`, because the input is whatever a customer's program printed. `MaskCredentials` hides passwords written into URLs or beside their own name, for quoting more than a fragment.

**The button and the card.** `POST /apps/{id}/verify` (antiforgery, `AppsOperate`, per-action `MayAsync`) and a card on the overview tab, directly under Address. Never checked is a sentence and the button. `_AppChecks` keeps its model type and its `data-check`/`data-check-status` attributes, and now also prints the status in words, so "not checked" is not left to a grey icon.

## Files

New:
- `src/Harbora.Infrastructure/Verification/AppVerifier.cs`
- `src/Harbora.Infrastructure/Verification/AppLogDiagnosis.cs`
- `src/Harbora.Web/Controllers/AppsController.Verify.cs`
- `tests/Harbora.Tests/Verification/VerifierHarness.cs`, `AppVerifierTests.cs`, `AppLogDiagnosisTests.cs`, `OneOffCleanupTests.cs`
- `tests/Harbora.Tests/Http/AppVerifyHttpTests.cs`

Changed:
- `src/Harbora.Infrastructure/DependencyInjection.cs` (registration)
- `src/Harbora.Infrastructure/Networking/ConnectionProbe.cs` (sentences moved to constants, `InPersian` added; the English is unchanged)
- `src/Harbora.Infrastructure/Services/ManagedServiceEngine.cs` (uses the `ServiceGone` constant)
- `src/Harbora.Infrastructure/Docker/DockerEngine.cs` (one-off cleanup, see below)
- `src/Harbora.Web/Controllers/AppsController.cs` (`Details` reads the latest report through `[FromServices]`, so the constructor the tests build by hand is unchanged)
- `src/Harbora.Web/ViewModels/AppTabViewModel.cs`, `Views/Apps/Details.cshtml`, `Views/Shared/Design/_AppChecks.cshtml`

## Runs

Final head of the code, before this report was committed:

| Command | Printed |
|---|---|
| `dotnet build Harbora.slnx --no-restore -v q -p:UseSharedCompilation=false` | `BUILD_EXIT=0`, 2 warnings, both NU1903 on `Harbora.Postgres.Tests`, the same 2 as the baseline build of `377ae2c`. `warning CS` count: 0 |
| `dotnet test Harbora.slnx --no-build -v q` | `EXIT=0` |
| Harbora.Tests | Passed 7615, Failed 0, Skipped 0 (baseline 7429) |
| Harbora.NodeAgent.Tests | Passed 500, Skipped 17 (baseline 500 + 17) |
| Harbora.NodeIngress.Tests | Passed 15 (baseline 15) |
| Harbora.Postgres.Tests | Passed 0, Skipped 115: no Docker on this machine |
| `grep -c "Harbora.Tests.dll"` | 2, so the main suite really ran |
| `node --test src/Harbora.Web/Scripts/tests/*.test.mjs` | 500 pass, 0 fail (baseline 500). No script was touched; `npm ci` was not needed |

A mutation batch was run once against the committed code: six behaviours broken at once (self-heal without connecting, settling window before restart count, any previous report accepted, redactor skipped, a pending address never giving up, server mismatch ignored). `AppVerifierTests` went from 93 passing to 8 failing, and each mutation was caught by the test named for it. The file was restored and touched, then the full build and suite above ran on the restored code. `OneOffCleanupTests.A_one_off_stopped_by_a_timeout_is_still_removed` was also seen failing against the old `DockerEngine` before the fix.

## Unverified

- **No real container, daemon, Traefik or certificate was involved.** Everything ran against fakes and the in-memory database. Whether the self-heal really makes a real database answer, and how a real Traefik answers during certificate issuance, is for H's live-host proof.
- **No Postgres.** The one-row-per-app unique index and the insert race between the button and the tick cannot be exercised on the in-memory provider, which does not enforce it. The retry in `StoreAsync` has run only on its normal path. No Postgres-lane test was added.
- **The card was not looked at in a browser.** The HTTP tests read the rendered HTML. `npm run build` was not run, so the Tailwind classes new to this card (`scroll-mt-20`, `whitespace-pre-line`) have not been seen compiled. The busy-button `onsubmit` is untested.
- **`S` and `U` have not been run against this.** Only the committed contracts were used.

## Decisions the brief did not spell out, worth a second look

1. **A database on another server, or in another environment, is `Failed` with no probe.** This is the important one. `TestConnectionAsync` runs on the *database's* server and the *database's* environment network, so in both cases it would pass while the app holds an address it cannot resolve, and "answers on this app's network" would be false. That is the defect noted from production (`harbora-svc-driveunion-db`), which the self-heal alone would not have caught. The cross-environment case is deliberately not healed: joining one environment's database to another's network undoes the isolation, and the probe could not prove it anyway. A database someone hand-connected across environments will read as `Failed`, which is an over-report, and I chose that over a false pass.
2. **A press continues a run that is still waiting.** Read literally, every press gets `giveUpAt = now + 30s`, so an address that will never answer would say Pending for ever. The action passes a waiting report younger than 10 minutes as `previous` and measures from its `StartedAt`, and the card tells the person to press again. It does not hold the request open in a loop.
3. **`previous` is only honoured for the same app and the same deployment.** S keeps it through `GetLatestAsync`, so its first tick can be handed a report from before the launch, and a pass from there would be a claim nobody re-checked. A `Warning` from unpublished changes is also not re-tested while it still holds, or the tick would start a container every 10 seconds; it is re-tested once the flag clears.
4. **The restart count is read before the 30-second window.** A restart resets `StartedAt`, so a crash loop always looks freshly started and would otherwise stay Pending until the give-up time.
5. **One check per database instance, not per attachment.** Two logical databases on one instance have the same `service:{id}`, and the probe uses the instance's credentials, so a second container would learn nothing. Unpublished on either attachment counts.
6. **An SSL-off domain is probed over `http`,** as `UptimeChecker` does; the brief says `https`. Every https probe would otherwise blame the app.
7. **Maintenance mode makes `url` `Skipped`,** since its 503 is on purpose. Not in the brief.
8. **Unpublished changes also turn an untested database `Skipped` into `Warning`.** The brief says it outranks a pass; the same fact is more useful than "could not test" for Mongo. A failure stays a failure.
9. **Two of three replicas running is `Warning`, none is `Failed`.** Compose stacks are found by the `harbora.compose.service` label, as the pipeline finds them, so a compose one-shot service that exits normally also reads as `Warning`.
10. **A fix outside the brief: `DockerEngine.RunOneOffAsync`.** Its cleanup was sent with the token that had just been cancelled, so it never reached the daemon and the stopped container stayed on the host with the database password in its environment. The new timeout makes that reachable, so it now removes with its own 30-second token. This affects every one-off caller, and only on cancellation.

## Limits I left as they are

- **`RestartCount > 0` is `Failed` until the next deploy,** as the brief says, including for one crash three weeks ago on an app stable since. The count is per container, so the sentence ("restarted N times since this deployment started") stays true, but the status is harsh on the app-page button. A later round might make a long-settled restart a `Warning`.
- **A Traefik 404 passes `url`.** A domain with no route gets 404 from the proxy, which is below 500, so it reads "something is serving there". `IUptimeProbe` returns no body on a status mismatch, so this cannot tell Traefik's 404 from the app's without changing that contract.
- **Attachments older than `AppManagedService` are not checked.** The migration has no backfill, so a database wired only by the old per-app variable copies gets no line in the report. It is an absence rather than a false claim.
- **`Authentication failed` in the log table can be an end user's failed login,** because the brief lists the phrase. It only produces a `Warning`.

## Contracts

No contract shape was changed. Three things I think are wrong or incomplete:

- **`IManagedServiceEngine.TestConnectionAsync` cannot be asked "from this app's network".** It always uses the service's own. Item 1 works around it by refusing to ask. An overload taking the network would turn those two `Failed` results into real answers.
- **`IUptimeProbe` hides the body of a non-expected status,** which is the Traefik 404 limit above.
- **The design table says `service:{id}` is "each attached database" and the brief says "one per attached `AppManagedService`".** With shared databases these differ; see item 5. S and U should treat `{id}` as the `ManagedService` id.
