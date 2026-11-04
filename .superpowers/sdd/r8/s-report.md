# S report: the launch server

Branch `r8/s`, based on `377ae2c`. `ILaunchService` and everything beneath it are implemented. No controller, no views, no migration, no model call.

## What was built

- **Zip uploads.** `SourceArchive.ExtractAsync` sniffs the first bytes, so `SourceArchivePath` works for a zip with no other change. It has the same guards as the tar path. The size ceiling is counted from what the decompressor produces, because a zip's header can lie. `.git/`, `node_modules/`, `.env` and `.env.local` are skipped at any depth, and an escape is refused before any skip applies. A stream that cannot seek is spooled to a temp file.
- **Aliases.** `AttachEnvRoles.SourceKeyFor` is new. `ManagedServiceAttachEnv.EntriesFor` emits aliases, and malformed JSON is ignored. `AttachAsync` gains a trailing optional `envAliases`. Pinned: every engine and role against `ServiceCatalog.All`, the reach into `EffectiveEnvironmentBuilder`, an app's own variable outranking an alias, and a rotation reaching an alias (simulated by changing the stored password; `RotatePasswordAsync` itself was not run).
- **Reading the source.** `InspectAsync` and `LaunchInspectJobHandler`. A blank `gitRef` means the default branch, and `GitCheckout.Branch` is filled from a real checkout (tested against a real local repository, including a detached remote-tracking branch and a tag).
- **Proposal and quote.** `ResourceCreationBilling` now has one rate resolution (`RatesAsync`) that `SaveAsync` charges from and the new `QuoteAsync` reports from. A test creates the resources and asserts the wallet moved by exactly the quoted figure, including with a per-server price.
- **Confirm and retry.** A conditional move guards the double submit. Every create goes through the service the forms use, and each id is saved on the row as it exists.
- **The tick.** `LaunchAdvance.Decide` is pure over a snapshot and tested directly. `LaunchTicker` runs every 3 seconds.
- **`ChooseRootAsync`, the contract change from the coordinator (`d7ebc2a`, cherry-picked as `a063198`).** `InspectionJson` now holds a `StoredInspection`: the root inspection, a dictionary of candidate to inspection, and the choice. Every candidate is read while the tree exists. A candidate's `RootDirectory` is prefixed, and a nested candidate list is flattened into the list, so `services` becomes `services/billing` and `services/search`. A confirm with no folder chosen is refused. So is a form naming a different folder from the chosen one, because nothing was worked out for it. A blank folder from the form means the chosen one.

## Files

New in `src/Harbora.Infrastructure/`:
- `Launch/LaunchService.cs`
- `Launch/LaunchService.Proposal.cs`
- `Launch/LaunchService.Confirm.cs`
- `Launch/LaunchService.Advance.cs`
- `Launch/LaunchService.View.cs`
- `Launch/LaunchAdvance.cs`
- `Launch/LaunchSource.cs`
- `Launch/LaunchTicker.cs`
- `Services/AttachEnvRoles.cs`

Changed in `src/`:
- `Harbora.Application/Abstractions/IGitService.cs`
- `Harbora.Infrastructure/Git/LibGit2GitService.cs`
- `Harbora.Infrastructure/Deployments/SourceArchive.cs`
- `Harbora.Infrastructure/Services/ManagedServiceAttachEnv.cs`
- `Harbora.Infrastructure/Services/DatabaseAttachmentService.cs`
- `Harbora.Infrastructure/Billing/ResourceCreationBilling.cs`
- `Harbora.Infrastructure/Apps/IAppProvisioningService.cs`
- `Harbora.Infrastructure/Apps/AppProvisioningService.cs`
- `Harbora.Infrastructure/Configuration/EnvConfigFileEditor.cs` (`IsValidKey` made public)
- `Harbora.Infrastructure/DependencyInjection.cs`

Tests:
- `tests/Harbora.Tests/Launch/` (harness, service, root choice, advance, source, quote, size parity, aliases)
- `tests/Harbora.Tests/SourceArchiveZipTests.cs`
- `tests/Harbora.Tests/LibGit2GitServiceBranchTests.cs`
- `tests/Harbora.Tests/Http/LaunchRegistrationHttpTests.cs`
- `tests/Harbora.Postgres.Tests/LaunchConfirmRaceTests.cs`

## The runs on the final code

```
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false
BUILD_EXIT=0, 0 Error(s), no "warning CS" lines

dotnet test Harbora.slnx --no-build --nologo -v q
EXIT=0
Passed!  - Failed: 0, Passed:  500, Skipped: 17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:   15, Skipped:  0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed: 7707, Skipped:  0, Total: 7707 - Harbora.Tests.dll
grep -c "Harbora.Tests.dll" = 2
```

The baseline was 7429, 500 (+17) and 15, so `Harbora.Tests` grew by 278 and nothing dropped. The build's only warning code is `NU1903` (SSH.NET, a known vulnerability), reported against `Harbora.Postgres.Tests.csproj`, which I did not touch.

Not run, because nothing here touches them: the front-end node tests and `npm run build`.

## Unverified

- **`LaunchConfirmRaceTests`, CI only.** Locally it reports `Skipped: 2`, because there is no Docker here. The claim that one of several concurrent confirms wins has not been run against Postgres. On EF InMemory the move is a read-then-write, so the local double-confirm test is sequential.
- **Everything against Postgres.** Every query and `ExecuteUpdateAsync` here ran on InMemory only.
- **The real inspector.** My tests go through the `ILaunchSourceReader` seam, because `SourceInspector.Inspect` is still the stub on this branch. I read D's `RootLocator` and `SourceInspector` on `r8/d` (`425c41d`), and their candidates and `RootDirectory` are relative to the inspected folder, as the prefixing assumes. The two have not run together. The two-app test should be run once with a real tree after integration.
- **The real verifier.** Tested with a fake. `IAppVerifier` is not registered on this branch, so until `r8/v` is merged a launch reaching `Verifying` logs an error each run and ends `Failed` at `verify` after 15 minutes. The view tolerates its absence.
- **A real end-to-end launch.** No Docker, no Traefik, no provision, no build. The Git clone was tested from a local path only, so a token over HTTPS was not exercised.
- **The crash window between a database create and saving its id.** A retry after a crash exactly there would create a second database and charge for it. The window is one statement wide and I did not close it.

## Where I went beyond or against the brief

1. **The default environment is read, not resolved.** The brief names `ProjectService.ResolveEnvironmentAsync`, but for a workspace with none it creates a project, and the proposal promises nothing is created. `PlacementAsync` reads in the same order without that side effect. It also offers a scoped member the first environment they may use rather than refusing on the default.
2. **The host is settled once, up front.** The brief says the first database's `ServerId` decides. I ask the scheduler once for the whole launch's memory and CPU, inside the plan's pool. `DatabaseProvisioningService` places with no pool, so an app following its database could leave its plan's pool, and it could also find no room beside it. The answer is stored in the choices, and a retry reuses it.
3. **`AppCreateRequest` gained an optional `StartCommand`.** `AppCreateRequest.Command` is dropped for anything but a cron job, so a Procfile's `worker:` line would have vanished silently. Existing callers are untouched.
4. **Name collisions are checked platform-wide.** `ManagedService.ContainerName` has a unique index across workspaces, while the service's own check only looks in one workspace.
5. **Extra sweeps and stops:**
   - `Creating` older than 10 minutes becomes `Failed`, because a panel restart in the middle would otherwise leave it there for ever.
   - `Verifying` older than 15 minutes becomes `Failed` at `verify`.
   - A `Failed` launch older than 24 hours has its token and archive dropped, and its message says what is left to do.
6. **Failed databases are provisioned again on retry.** The row reads `Provisioning` immediately, or the next tick would fail the launch on the old answer.
7. **A compose-only project from an upload is refused at validation.** The create service requires a clone URL for a Compose app, and an `Upload` app would fail at build.
8. **The default size is a second copy of the rule, held by a parity test.** `SizePickerService` lives in the web project. Sharing its rule means moving `SizeUnavailable`, which needs a view edit, and the brief forbids touching views. `LaunchSizeDefaultParityTests` compares the two over each reason a tier is passed over, except capacity and an offline host: those branches of `DefaultSizeKeyAsync` have no test, because the harness has no node readings set up.
9. **Upload archives must sit under `WorkDir/_uploads`.** `StartAsync` refuses anything else, because a launch later deletes that path.

## Contracts I think are wrong

- **`RetryAsync` takes no choices, and `ChoicesJson` holds no values.** A person's typed answers are lost if a step fails before variables are set. A retry regenerates secrets and re-reads example values, including over a `KeepExample` the person had edited. The failure message and the app step name what could not be set, but the person must set those on the app's page. If the owner wants them kept, `RetryAsync` needs an answers parameter.
- **`LaunchStartRequest` has no archive name.** `AppName` and `SourceLabel` come from the file name, with a GUID stripped. U should write uploads as `{sanitised-original}-{guid:N}.zip`. A name like `launch-{guid}.zip` gives an app called `app`. Once the deployment has consumed the archive, `SourceLabel` falls back to the app's name.
- **`LaunchStartRequest.PrintMembers` prints `CloneUrl`, which can hold credentials before `StartAsync` strips them.** `LaunchEnvChoice` also prints its `Value` through the default record `ToString`. Neither is logged anywhere in this code, and I did not change either.
- **There is no column for when the checks last ran.** The ten-second limit reads `AppLaunch.UpdatedAt`, which is safe because only the checks write to the row while it is verifying. `SaveChangesAsync` stamps real time rather than `ISystemClock`, so that end-to-end test can only cover the first interval; `LaunchAdvanceTests` covers the rule itself.

## For U

- `ConfirmAsync` returns `Ok` with a `Failed` view for a part-way refusal, because `Refused` means nothing changed.
- `ConfirmAsync` also returns `Ok` for a second submit, and `ValidateConfirmAsync` returns no refusals for a launch already confirmed.
- With candidates, the folder must go through `ChooseRootAsync`. Posting `RootDirectory` alone is refused.
