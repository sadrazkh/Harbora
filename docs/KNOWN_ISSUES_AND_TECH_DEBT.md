Last verified: 2026-11-08
Scope: static read of the code at commit `b3fd974`, plus the round ledgers in `.superpowers/sdd`. None of these defects was reproduced by running the code. Paths are relative to `src/`, or to `src/Harbora.Infrastructure/` when they start with a subsystem folder such as `Billing/`, unless they start with another top-level folder. Updated 2026-11-12 for the round-nine work merged on `r9/int` (`7d7a0ae`), which is not on master or released; nothing else was re-verified.

# Known issues and technical debt

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md).

The **Check** column says how far each item was confirmed:

- **Verified**: the cited code was re-read directly for this baseline.
- **Verified\***: found by a read-only code audit that cites this location; not independently re-read.
- **Assumption**: recorded in an earlier ledger and not re-checked.

"Impact" is a judgement, so read it as a [Recommendation].

## Billing and metering

| ID | Issue | Where | Effect | Impact | Check |
|---|---|---|---|---|---|
| KI-001 | A past hour is priced from today's rows, with no creation-time check | `Harbora.Infrastructure/Billing/BillingTick.cs` (no `CreatedAt` in the file; `:588`, `:613`) | A retried or backfilled hour charges resources created after it | High | Verified |
| KI-002 | `MaxBackfillHours` bounds one poll only, and the next poll 60 s later continues from the latest run. The scheduler path logs no warning, though `appsettings.json` promises one. `BillingTick.CatchUpAsync`, the path with the warning, has no caller | `Billing/BillingScheduler.cs:78-99`, `Harbora.Web/appsettings.json:46` | After billing is switched off and on again, the whole gap is billed | High | Verified |
| KI-003 | Incomplete hours retry with no age limit, re-scanning every workspace, and each retry adds a `Job` | `BillingScheduler.cs:63-76` | One unpriced size or unmeasured volume keeps an hour retrying indefinitely | Medium | Verified |
| KI-004 | Crossing the monthly spend limit skips the whole hour and marks it complete | `BillingTick.cs:789-796` | Lines under the limit are never billed for that hour | Medium | Verified |
| KI-005 | `StorageMeasurer` throws on v1 nodes before stamping the row, so that volume stays first in line. Its comment assumes NULLs sort first, but PostgreSQL ascending puts them last | `Tenancy/StorageMeasurer.cs:58-96`, `Nodes/NodeWorkloadEngine.cs:621` | Node disks stay unmeasured and can block other volumes; feeds KI-003 | Medium | Verified |
| KI-006 | Bucket creation has no suspension or balance check and is not billed; `MaxBuckets` is outside the creation lock | `Harbora.Web/Controllers/StorageController.cs:181-236`, `Harbora.Domain/Storage/StorageBucket.cs:81` | Storage is free and can pass the plan limit | Medium | Verified\* |
| KI-007 | A second meter counts running apps only, with non-idempotent increments, and is shown to operators | `Tenancy/MeteringService.cs:14,45-67`, `TenantsController.cs:579` | Its figures disagree with the ledger | Low | Verified\* |

## Deploy and routing

| ID | Issue | Where | Effect | Impact | Check |
|---|---|---|---|---|---|
| KI-010 | A pinned deployment stores a commit SHA as `GitRef`, and checkout resolves only branches and tags, then falls back to the default branch. A mistyped ref does the same | `Deployments/DeploymentEngine.cs:469-473`, `Git/LibGit2GitService.cs:85-101` | An approved "pinned" deploy can build different code from what was approved | High | Verified |
| KI-011 | Cancelling a running deployment writes Cancelled in another scope, then the pipeline's stale tracked row overwrites it with Failed; `Deployment` has no concurrency token | `DeploymentEngine.cs:290-296`, `DeploymentPipeline.cs:753,793-794,828` | A cancel raises a failure, an incident and an alert | Medium | Verified |
| KI-012 | Compose gaps: failed-deploy cleanup uses names that do not match; old and new stacks share aliases and volumes; rollback cannot succeed though it is marked instant; service images escape pruning; remote ports are never released; `env_file` is dropped; `ReleaseCommand` and replicas are ignored | `DeploymentPipeline.cs:780-784,1181-1215,342,325`, `DeploymentPlanning.cs:172,300-304`, `ComposeFile.cs:193` | Compose apps behave differently from what the UI and class docs say | High | Verified\* |
| KI-013 | Only the single-container path clears "applies on next deploy" flags and emits success events | `DeploymentPipeline.cs:675-703` versus `:501-513`, `:375-397` | Compose and cron apps keep stale flags and raise no success event | Medium | Verified\* |
| KI-015 | Routers exist only on `websecure`, and `:80` redirects unconditionally; `EntryPointWeb` is unused, so SSL off and ForceHttps off have no effect as described. By Traefik semantics (inferred, not run) an "HTTP" domain would not work | `Proxy/TraefikProxyEngine.cs:310-319`, `deploy/docker-compose.yml:22-23` | Page promises behaviour the proxy cannot give | Medium | Verified |
| KI-016 | A push during a build returns the in-flight id, and is counted as queued, though that build checked out older code. The image route does the same; the archive route already guards against it. The webhook catch also swallows quota refusals | `DeploymentEngine.cs:91-98`, `Git/GitWebhookProcessor.cs:80-84`, `Controllers/Api/ApiV1Controller.cs:276-281` | A newer commit can silently not deploy | Medium | Verified\* |
| KI-017 | Rollback re-runs the release task unconditionally, and prebuilt or template tags are stored mutable and only checked for existence | `DeploymentPipeline.cs:520,970,325` | Rollback is not an exact return | Medium | Verified\* |
| KI-018 | The designer offers `harbora-{slug}`, a name no current container has; Save deletes routes missing from a possibly stale list; domain removal deletes rows without applying | `Harbora.Web/Controllers/RoutesController.cs:41,110`, `Apps/AppConfigurationService.cs:246-267` | Routes break, or linger until the next apply | Medium | Verified\* |

## Backups

| ID | Issue | Where | Effect | Impact | Check |
|---|---|---|---|---|---|
| KI-020 | "Full platform" restore puts back only `Settings`; "app config" restores only variables. Apps, routes, services, domains and volumes are captured and ignored | `Backups/BackupEngine.cs:788-824`, label at `BackupsController.cs:49` | An operator trusts a restore that does far less than its name | High | Verified |
| KI-021 | The backup module uses the panel's own Docker engine, with no server handling. A volume on another server becomes an empty local one and is recorded as success; verification then passes | `Modules/Backup/.../BackupTargetResolver.cs:83,163,207-245`, `ContainerDatabaseBackupProvider.cs:25`, `BackupJobHandlers.cs:105-108` | Silent empty backups once the module is on with remote workloads. `BackupEngine.cs:25-29` documents this exact defect as fixed in the legacy path | High | Verified |
| KI-022 | Module restore branches only on `RestoreType.Database`; Volume, Application and FullServer land in a directory | `Modules/Backup/.../RestoreService.cs:135` | The restore type chosen is not honoured | Medium | Verified |
| KI-023 | Retention groups by target without excluding expiring self-serve exports | `BackupEngine.cs:863-880` | Repeated exports can delete older scheduled backups | Medium | Verified |
| KI-024 | Legacy retention removes the row even when the artifact delete failed | `BackupEngine.cs:884-887` | Orphaned artifacts nobody can find | Low | Verified |
| KI-025 | A prune job is queued per policy every minute for 6 hours after each success, with no de-duplication | `Modules/Backup/.../BackupPolicyScheduler.cs:115-122`, `Jobs/DatabaseJobQueue.cs:27` | About 360 jobs per success, adding to KI-040 | Medium | Verified |
| KI-026 | Module verification lists the snapshot root only, while legacy restores into a scratch database | `BackupJobHandlers.cs:105` | "Verified" means two different things | Medium | Verified\* |

## Tenancy, access and nodes

| ID | Issue | Where | Effect | Impact | Check |
|---|---|---|---|---|---|
| KI-030 | `ApiToken` has no workspace; bearer calls use the personal workspace first with no tie-break | `Harbora.Web/Infrastructure/TokenAuthenticationHandler.cs:43-48` | The API and CLI cannot target another workspace | Medium | Verified |
| KI-031 | The default-plan rule differs: `FeatureGate` requires `IsEnabled`, `QuotaService` and `BillingTick` do not | `Features/FeatureGate.cs:51`, `Tenancy/QuotaService.cs:105`, `BillingTick.cs:219` | Entitlements and quotas can disagree about which plan applies | Medium | Verified |
| KI-032 | A missing server row falls back to the panel's Docker, against the class's own rule; a bad mTLS certificate silently drops to token-only; `SafeUnprotect` returns ciphertext as the token | `Docker/ServerEngineFactory.cs:41,73-76,85-88` | Work can land on the wrong host, or the client certificate is lost with no message | Medium | Verified |
| KI-033 | Nine verbs have no panel caller; database tunnels authorise only from grant records nothing issues; `FakeNodeAgentClient` is registered unconditionally, against its own doc comment | `NodeTunnelGateway.cs:366-373`, `DependencyInjection.cs:427`, `Nodes/FakeNodeAgentClient.cs:15` | External database access through a node does not work | Medium | Verified (fake) / Verified\* (verbs) |
| KI-034 | `BillingController` hard-codes workspace Admin; Member permissions are defined twice | `Harbora.Web/Controllers/BillingController.cs:145`, `RolePermissions`, `WorkspaceRolePermissions` | Rules can drift | Low | Verified\* |

## Unbounded growth

| ID | Issue | Where | Effect | Impact | Check |
|---|---|---|---|---|---|
| KI-042 | `Deployments`, `AppLaunches`, `BillingRuns` and `AiUsageRecords` have no retention | `Maintenance/DataRetentionSweeper.cs` | Slow growth | Low | Verified |

## AI, mail and functions

| ID | Issue | Where | Effect | Impact | Check |
|---|---|---|---|---|---|
| KI-060 | No production code creates an `AiSubscription`, and without one the gateway answers 401 and key creation refuses | `Ai/AiGatewayService.cs:56-60`, only tests use `new AiSubscription` | The AI gateway cannot be enabled through the product | High if it is meant to be sold | Verified |
| KI-061 | `PeriodTokens`, `PeriodSpend` and `MonthToDateSpend` only increase; `PeriodStartedAt` has no reader or writer; usage is not billed | `AiGatewayService.cs:165-168`, `Domain/Ai/AiModel.cs:143-145` | Monthly limits become permanent | Medium | Verified |
| KI-062 | No background health reconcile for `MailServer`, and no Stalwart cleanup on workspace removal or suspension was found | `Mail/MailPlatformService.cs`, `BillingTick.cs:701-710` | Mail keeps running for a removed tenant | Medium | Verified\* |
| KI-063 | The function editor has both the id and `data-island`, and `main.ts` mounts both | `Harbora.Web/Views/Functions/EditFunction.cshtml:40`, `Scripts/main.ts:111-113` | Two editors mount on one element | Low | Verified\* |

## Code and documents that disagree

| ID | Issue | Where | Check |
|---|---|---|---|
| KI-050 | ADR-001 says Web touches Infrastructure only through interfaces; Web uses concrete Infrastructure types, including in views | `docs/overhaul/09-architecture-decision-records.md`, `Harbora.Web/Views/Ai/Index.cshtml:94` | Verified |
| KI-051 | ADR-004 names a `Live` state; the code has `Succeeded` and `PendingApproval` | same, `Domain/Deployments/DeploymentStateMachine.cs` | Verified |
| KI-052 | ADR-005 keeps Redis for caching and fan-out. Nothing uses it, yet the stack starts it and the package stays referenced | `deploy/docker-compose.yml`, `Harbora.Infrastructure.csproj:32` | Verified |
| KI-053 | `Features:EncryptedSyncNode` and `Features:RemoteBackupAgent` have no reader, though `deploy/backup-sync.compose.yml:137` presents one as the control. `BackupModuleOptions.cs:18` says Sync is contracts only | `Harbora.Web/appsettings.json:102-103` | Verified\* |
| KI-054 | Stale comments: `FakeNodeAgentClient` (KI-033), `StorageMeasurer` (KI-005), `BillingSuspension.cs:90-104`, `DeploymentPipeline.cs:1875`, `DatabaseJobQueue.cs:86` | as listed | Verified\* |
| KI-055 | `docs/superpowers/STATUS.md` is a snapshot from 2026-08 and reads like the current state | that file | Verified |
| KI-056 | `.claude/launch.json` is tracked and holds absolute paths into sibling repositories on one machine | `.claude/launch.json` | Verified |
| KI-057 | `docs/tutorial` screenshots predate the current pages | accepted in round 8 | Assumption |

## Dead code and small debt

| ID | Issue | Where | Check |
|---|---|---|---|
| KI-070 | `DeploymentStatus.Pushing` is never entered; `AppStatus.Deploying` has no writer; `DeploymentPipeline.cs:1020-1025` is unreachable; `ComposeService.Dockerfile` is never assigned; `WebSocketEnabled` is never rendered; `ColorContrast` and `DesignTokens` have no production consumers | as listed | Verified (Pushing) / Verified\* |
| KI-071 | Localization is 2,831 inline `isFa ?` ternaries in 154 of 177 views, against 244 resx entries. Only two languages are practical | `Harbora.Web/Views`, `Resources/SharedResource.fa.resx` | Verified\* |
| KI-072 | Very large files: `DeploymentPipeline.cs` 2,493 lines, `AppsController.cs` 2,005, `Views/Apps/Details.cshtml` 1,885 | line counts | Verified |
| KI-073 | `install.sh` does not backfill `Runtime__DanglingImageSweepHours`, so it is not visible in `.env` | `deploy/install.sh` (no match) | Verified |
| KI-074 | `DomainsController` caches a failed IP lookup as `?` until restart | `Harbora.Web/Controllers/DomainsController.cs:141` | Verified\* |
| KI-075 | A remote-agent build does not carry the daemon's error text, so the cache-restore retry cannot fire there | `.superpowers/sdd/r5/progress.md` | Assumption |
| KI-076 | `PullImageAsync` has the `Progress<T>` race that was fixed for builds | same | Assumption |
| KI-077 | The assistant's "don't ask again" has no revoke control in the pane, only an API | `.superpowers/sdd/r6/progress.md` | Assumption |

## Accepted in the Launch round

Recorded on 2026-11-05 and deliberately left. **[Verified]** that they were accepted (`.superpowers/sdd/r8/*-report.md`); the code was not re-read for each.

| ID | Item |
|---|---|
| KI-082 | A cancelled upload deployment leaves its archive |
| KI-083 | `harbora deploy`'s "Stopped following" message is dead code |
| KI-084 | A later-typed colliding name previews without `-2` |
| KI-085 | A launched Git app needs its webhook pasted by hand when its repository is not on a connected account. For a connected account `r9/int` sets the hook up through the provider (see the fixed list) |
| KI-086 | `AppRefusals` and `ResourceCreationBilling` Persian copy is still plural, unlike the singular voice of Launch |
| KI-088 | The secret-name rule exists on both the service and the surfaces |
| KI-089 | MongoDB, RabbitMQ and NATS have no readiness wait or connection test |

## Limits by design

**[Verified]** from `README.md`, "Known limitations", and the code named there. Not defects.

- Routes between servers go through published host ports; there is no overlay network.
- A v1 node runs prebuilt images; building happens on the panel.
- Only the panel's own host can back up volumes and databases (legacy path).
- Functions do not scale to zero, are not billed per request, and cannot install packages.
- Bandwidth is not metered. Disk is billed on allocation, not on use.
- A 404 at an app's address counts as something answering, in both the health gate and the launch check.
- The AI gateway is labelled Preview.

## Fixed on `r9/int` in round nine, not yet on master or released

**[Verified]** that each fix is merged on `r9/int` and that the full suite passes there (9,701 in `Harbora.Tests`, 539 front-end tests, on `7d7a0ae`). None was run on a real server.

| ID | What was wrong | What changed | Where |
|---|---|---|---|
| KI-014 | A cron run got only the variables typed by hand | A scheduled run and "Run now" get the app's effective environment, without `PORT` | `Deployments/CronJobRunner.cs`, `Apps/EffectiveEnvironmentBuilder.cs` |
| KI-019 | A hard restart left a database at `Provisioning` for ever | A reconciler behind the startup gate requeues the setup, or marks it Failed with a sentence; it also sweeps every 5 minutes | `Services/StrandedProvisionReconciler.cs` |
| KI-040 | `Jobs` was never pruned | Settled jobs older than `Retention:JobDays` (30) are deleted in batches of 10,000; a Pending or Running job never is. Not measured on a table of production size | `Maintenance/DataRetentionSweeper.cs`, `RetentionRule.cs` |
| KI-041 | Build directories were never removed | The pipeline removes its own directory when a deployment ends, and a daily sweep removes old ones no unsettled deployment owns | `Maintenance/BuildDirectories.cs`, `BuildDirectorySweeper.cs` |
| KI-080 | A retried launch lost typed answers | The answers ride on the launch row, protected, until they are on the app; dropped after a day | `Launch/LaunchService.Confirm.cs` |
| KI-081 | The CLI uploaded `.env.production` and the like | Every `.env.*` that is not an example stays home on the CLI and for a browser zip, is named in the output, and can be opted in with a `!` line. `.env` and `.env.local` never go | `Harbora.Shared/EnvFiles.cs`, `Harbora.Cli/SourcePacker.cs`, `Deployments/SourceArchive.cs` |
| KI-087 | A database import over 128 MB failed with a bare 400 | Imports go through up to 2 GB and the backup upload up to 8 GB; a larger file is refused in a sentence. No real 2 GB upload was sent | `Harbora.Web/Controllers/DatabasesController.*.cs` |

## Found during round nine, not fixed

| ID | Issue | Where | Impact | Check |
|---|---|---|---|---|
| KI-090 | A database-only template returns before committing its creation transaction, so on PostgreSQL the database, its charge and its provision job would roll back while the page reports success. The test database has no transactions, so no test sees it | `Templates/TemplateDeploymentService.cs:79,188,295`, `Tenancy/QuotaService.cs:31-57` | High | Verified by reading; not run |
| KI-091 | `harbora env pull` never returns `SENTRY_DSN`: the env endpoint lacks the error-tracking include | `Harbora.Web/Controllers/Api/ApiV1Controller.cs` (`Env`) | Low | Verified\* |
| KI-092 | A scheduled run mounts none of the app's volumes and has no memory or CPU limit | `Deployments/CronJobRunner.cs` | Medium | Verified\* |
| KI-093 | The ignore-file loader trims slashes at both ends, so `/build` still matches a nested `build/`, against the guide's own advice | `Harbora.Cli/SourcePacker.cs` (`LoadIgnorePatterns`) | Low | Verified\* |
| KI-094 | A backup schedule aimed at a deleted logical database fails every day | `Backups/BackupEngine.cs` | Low | Verified\* |
| KI-095 | On a v1 node a "database" backup is a volume snapshot, not a dump, and the automatic schedule inherits that | `Backups/BackupEngine.cs` | Medium | Verified\* |
| KI-096 | A webhook Harbora created is not removed from the provider when its app is deleted, and only the first page of a repository's hooks is read | `Git/GitHooks.cs`, `Deployments/AppOperationsService.cs` | Low | Verified\* |
| KI-097 | By decision, the automatic backup is not given to a database made from a template, to Redis, Meilisearch, RabbitMQ or NATS, or on a plan that keeps fewer than seven | `Services/AutomaticDatabaseBackup.cs` | Low | Verified\* |
| KI-098 | `LogicalDatabaseBackupHttpTests.Restoring_into_a_brand_new_database...` fails when run alone: nothing on that path creates the staging directory | `tests/Harbora.Tests/Http` | Low | Verified\* |
| KI-099 | `examples/harbora.yml` advertises `env:` and `domains:` keys the parser ignores, and `docs/cli-deploy.md` leaves one rule out of how the deploy mode is chosen | as listed | Low | Verified\* |
| KI-100 | `Jobs` grows by roughly 48,000 rows a day in production and nobody has looked at why | figure derived from the 2026-11-05 count | Medium | Assumption |
| KI-101 | On the Git page the OAuth error is English only, and a Gitea account can be connected with no address and then never listed | `Harbora.Web/Controllers/GitController.cs` | Low | Verified\* |

## Build and test traps

These are working rules rather than defects in the product, so they live in [AI_WORKING_GUIDELINES.md](AI_WORKING_GUIDELINES.md).

## Baseline consistency review, 2026-11-08

[Verified] Run at the end of the baseline, following section 5 of `DOCUMENTATION_RULES.md`.

| Check | Result |
|---|---|
| Every document starts with `Last verified` and a scope | Pass, by script |
| Every `KI-`, `D-` and `OQ-` reference resolves | Pass, by script |
| Every file path and code symbol named exists in the repo | Pass, by script. The only unmatched items are generated or deployment-time names (`wwwroot/build`, `deploy/.env`, `docker-compose.override.yml`, `Storage__S3__ClientImage`) |
| Status against code | One audit count was wrong and corrected: 29 assistant tools are registered, not 30. Other figures were recounted directly |
| Decisions against code | Differences logged, not resolved: D-001 (KI-050), D-004 (KI-051), D-005 (KI-052), D-007 (single gate rather than three probes) |
| Known issues against roadmap | KI-081 and KI-089 were accepted for the Launch round; the roadmap proposes reversing them, and says so |
| Older documents | Left unchanged, as instructed. Their standing is in [MASTER_CONTEXT.md](MASTER_CONTEXT.md) |
