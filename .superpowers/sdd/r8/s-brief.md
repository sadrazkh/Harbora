# S — The launch server: fetch, propose, create, wait, deploy, finish

Read `.superpowers/sdd/r8/CONSTRAINTS.md` and `docs/superpowers/specs/2026-11-04-launch-design.md` first.

You implement `ILaunchService` (`src/Harbora.Infrastructure/Launch/LaunchContracts.cs`) and everything beneath it. Other
agents are writing in parallel: D fills in `SourceInspector.Inspect` (call it; until they merge it returns `Nothing`, so
test through fixtures or a seam, not through its answers). V implements `IAppVerifier` (resolve it from the scope only when
a launch reaches `Verifying`, and fake it in your tests). U writes the controller and views against your interface. Do
not touch `src/Harbora.Web/Views`, and do not write a controller.

**Every create goes through the service the forms use.** No `db.Apps.Add`, no `db.ManagedServices.Add` in your code.

## 1. Zip uploads

`SourceArchive.ExtractAsync` (`src/Harbora.Infrastructure/Deployments/SourceArchive.cs`) gains zip by sniffing the first
bytes (`1F 8B` gzip, `50 4B` zip), so the pipeline's existing `SourceArchivePath` path works with no other change. Same guards
as the tar path: traversal, links, entry count, uncompressed ceiling, the same friendly error for junk. Zip entries under
`.git/`, `node_modules/`, and files named `.env` or `.env.local` are skipped (the CLI filters these on the client; a browser
upload has no such step, and `.env` usually holds real keys). Tests alongside the existing archive tests.

## 2. Aliases on an attachment

- `AttachEnvRoles.SourceKeyFor(ManagedServiceType, EnvAliasRole)` in `src/Harbora.Infrastructure/Services/`: for each engine,
  the `AttachEnv` key holding that role (Postgres Host → `PGHOST`, Mongo Url → `MONGODB_URI`, …), null when it has none. Test
  it against `ServiceCatalog.All` for every engine, so every answer is a key that really exists.
- `ManagedServiceAttachEnv.EntriesFor` also emits each `AttachEnvAlias` in `EnvAliasesJson` under its `Name`, with the value and
  secrecy of its `SourceKey`, unprefixed. An alias must not outrank an app's own `EnvironmentVariable` row: check
  `ConfigGroupMerge`/`AttachedDatabaseEnv` and pin it. A malformed value is ignored, never an exception at deploy time. Confirm
  aliases also reach `EffectiveEnvironmentBuilder` (so `harbora env pull` sees them) and pin a rotation: after a password
  rotation the alias carries the new password.
- `IDatabaseAttachmentService.AttachAsync` gains a trailing optional `IReadOnlyList<AttachEnvAlias>? envAliases = null`.
  Existing callers are untouched.

## 3. Reading the source (`InspectAsync`, the `LaunchInspect` job)

An `IJobHandler` for `JobKind.LaunchInspect` (see `FunctionInvokeJobHandler` for the shape, and how `IJobQueue` is used). No
session here: `IgnoreQueryFilters()`. Runs twice safely: a launch not in `Inspecting` is left alone.

- `IGitService`: a blank `gitRef` must mean the repository's default branch, and the branch actually checked out must come back
  (add a trailing optional `string? Branch = null` to `GitCheckout`, filled in `LibGit2GitService`, with blank handled in
  `ResolveRef`). Store it in `ResolvedBranch`, which is what the app is created tracking.
- Work in `Path.Combine(runtime.WorkDir, "_launch", id.ToString())`, always deleted in `finally`. The archive stays, for the deployment.
- Store the inspection (`System.Text.Json`, enums as names) and move to `AwaitingConfirmation`.
- On failure: `InspectFailed` with a message in both languages that says what to do. A clone failure says the URL may be wrong or
  the repository private, and offers the token field. Never let a token reach a message, a log line or the row: use
  `ISecretRedactor`, and test it with a URL carrying credentials.
- `StartAsync` validates: `http`/`https` only (say so plainly for `git@…` and suggest the https form), a URL with embedded credentials has
  them moved to the token and stripped from `CloneUrl`. It checks the workspace is not suspended and that the caller may create apps,
  the way `AppProvisioningService` does. It writes one audit entry `launch.started`, never the token.

## 4. The proposal (`ProposeAsync`, `QuoteAsync`)

`ProposeAsync` turns the inspection into `LaunchChoices` defaults: name from the repository or archive, kind and port, root
directory, release and start command, the default environment (`ProjectService.ResolveEnvironmentAsync`), the default size
(`SettingKeys.DefaultInstanceSize`, else what `SizePickerService` would preselect; a launch must never choose "no limit"), every `Certain`
service ticked with its aliases resolved through `AttachEnvRoles`, and `EnvFields` for every variable that is not `ProvidedByAttach`.
`ServiceOptions` lists the whole catalogue with what was detected, so the screen can offer "add a database" for what the inspector missed.

`QuoteAsync` must read the same rate the charge does. `ResourceCreationBilling` resolves it inline: **extract** that resolution into one
method both call, with no behaviour change, and pin it with a test that creates the resources and asserts the wallet moved by exactly
the quoted figure. Exempt means billing off or the workspace is the provider's own (`IsDefault`), exactly as `SaveAsync` decides. An
unpriced size, a suspended workspace or too small a balance is `Refusal`, with both languages.

## 5. Confirming (`ValidateConfirmAsync`, `ConfirmAsync`)

`Validate` runs each service's own validate (`ValidateCreateAsync` and friends) plus: the quote's refusal; a slug available;
a `NeedsAnswer` may be left blank (some are optional; the screen warns), a key must be a valid variable name. Nothing changes.

`Confirm` moves `AwaitingConfirmation → Creating` **conditionally**, so a double submit creates nothing twice (the
`AgentPlanService.TryMoveAsync` pattern: the real statement on relational providers, read-then-write on InMemory; put the race in
`tests/Harbora.Postgres.Tests`, CI only, and say so). Then, on the request, in this order, **saving each id on the row as it exists**:

1. Each database through `IDatabaseProvisioningService.CreateAsync`. The first one's `ServerId` (or the chosen one) is then passed to
   everything else: one server for the whole launch. Name from the choice, else `{app}-{engine}`, and suffix on a collision.
2. The app through `IAppProvisioningService.CreateAsync` with `DeployNow = false`: Git (`CloneUrl`, `ResolvedBranch`, token) or `Upload`.
3. Each attachment through `IDatabaseAttachmentService.AttachAsync` with its aliases.
4. Variables through `IAppConfigurationService.SetEnvAsync`. A `GeneratedSecret` left null gets `ServiceCredentials.Generate()`-strength
   randomness, stored secret; `APP_KEY` beside an `artisan` file must be Laravel's `base64:` of 32 random bytes.

Then clear the token, store `ChoicesJson` **with every value removed**, audit `launch.confirmed`, and move to `WaitingForServices` (or
straight to `Deploying` with no services). Any refusal part-way: `Failed`, `FailedStep`, both messages, the created ids kept, and nothing
rolled back: a database that exists is named, not silently deleted.

`RetryAsync` re-enters at the failed step and creates only what the row says is missing. A failed first deployment of an Upload cannot be
repeated (the pipeline consumes the archive): `CanRetry = false`, and the message says to upload a new version from the app's page, which U builds.
`AbandonAsync` deletes the archive and clears the token.

## 6. The tick (`AdvanceAllAsync`, `LaunchTicker`)

A `BackgroundService` every 3 seconds, same shape as `FunctionCronScheduler` (try/catch per tick, no session, `IgnoreQueryFilters`), which
makes a scope and calls `AdvanceAllAsync`. Make the decision a pure function over a snapshot (status, service statuses, deployment status,
times) returning what to do, and test that directly. It must be restart-safe: a panel that restarts mid-launch simply carries on.

- `WaitingForServices`: all `ManagedService` rows `Running` → `Deploying`. Any `Failed` → `Failed` quoting `ManagedService.ErrorMessage`, link
  to that database. Over 20 minutes → `Failed`, saying which is still provisioning. **No deployment may be queued before this passes**: pin it.
- `Deploying`: no `DeploymentId` yet → `IDeploymentEngine.QueueDeploymentAsync(new(appId, DeploymentTrigger.Manual, launch.UserId, GitRef: ResolvedBranch,
  SourceArchivePath: ArchivePath))`, store the id, clear `ArchivePath`. `QuotaRefusedException` and `InvalidOperationException` become `Failed` with both
  languages (read `QuotaRefusedException.ReasonFa`). `PendingApproval` is not a failure: stay, and the step's detail says it is waiting for
  approval, with a link. `Succeeded` → `Verifying`. `Failed`/`Cancelled` → `Failed` with the deployment's `ErrorMessage` and a link to `/deployments/{id}`.
- `Verifying`: `IAppVerifier.RunAsync(appId, previous, VerifyStartedAt + 5 minutes, ct)` at most every 10 seconds for one launch.
  Complete: no failures → `Succeeded`; failures → `SucceededWithProblems`. Keep the previous report via `GetLatestAsync`.
- Sweeps: `AwaitingConfirmation` older than 24 hours → `Abandoned`, archive deleted, token cleared. `Inspecting` older than 20 minutes (its job was lost) → `InspectFailed`.
- One launch's exception is logged and must not stop the others.

## 7. `GetAsync`

Workspace-filtered read. Builds the steps (`LaunchStepKeys` in order, with state, detail and links) and `Checks` from `IAppVerifier.GetLatestAsync`.
`SourceLabel` has no credentials. `PublicUrl` from the app's primary domain. Nothing secret, ever.

Register everything in `DependencyInjection.cs` as one block, with a comment. Keep `ILaunchService` scoped.

## Tests

`tests/Harbora.Tests/Launch/`: fakes for docker and git in the style already there (`Fakes/`, `PipelineFakes`). At least: the full happy path with and
without a database; no deployment before the database runs; a failed provision; a failed deployment; `PendingApproval`; a part-way refusal then a retry that
creates nothing twice; a double confirm; a restart between every step (a new service instance over the same store carries on); another workspace's launch reads as
null; a token never in the view, the row after confirm, the choices JSON or a failure message; no variable value in `ChoicesJson`; one server for everything;
generated secrets are random and stored secret; `APP_KEY`; the quote equals the charge; abandon deletes the archive; the 24-hour sweep.

## Worktree

`E:/Cash.Net/source/repos/sadrazkh/Harbora-r8-s`, branch `r8/s`. Report: `.superpowers/sdd/r8/s-report.md`.
