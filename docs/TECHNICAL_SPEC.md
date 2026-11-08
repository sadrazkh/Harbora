Last verified: 2026-11-08
Scope: HTTP routes, enums, options classes, `appsettings.json`, compose file, CI workflows and subsystem code at commit `b3fd974`, read statically. Nothing was run. Short paths such as `Billing/BillingOptions.cs` are under `src/Harbora.Infrastructure/`; `Controllers/`, `Views/`, `Scripts/` and `Infrastructure/` paths are under `src/Harbora.Web/`; `Domain/` means `src/Harbora.Domain/`.

# Technical specification

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md). Unless a section says otherwise, everything below is **[Verified]** against the file named in that section.

## Stack

| Layer | Choice | Source |
|---|---|---|
| Runtime | .NET 10, all projects `net10.0` | `*.csproj` |
| Web | ASP.NET Core MVC, Razor, SignalR | `Harbora.Web.csproj` |
| Front end | Vue 3, Vite 6, Tailwind 3, CodeMirror, xterm, Lucide | `src/Harbora.Web/package.json` |
| Database | PostgreSQL 16, EF Core through Npgsql 10.0.2 | `Harbora.Data.csproj`, compose |
| Containers | `Docker.DotNet.Enhanced` 3.131.1 | `Harbora.Infrastructure.csproj` |
| Git | `LibGit2Sharp` 0.31.0 | same |
| Object storage client | `AWSSDK.S3` 4.0.100.2, plus `mc` in a one-off container | same, `Storage/ObjectStorageAdmin.cs` |
| Queue triggers | `RabbitMQ.Client` 7.2.2 | same |
| CLI | `Spectre.Console`, `YamlDotNet` | `Harbora.Cli.csproj` |
| Proxy | Traefik v3.6 | `deploy/docker-compose.yml` |

## Entry points

| Program | File | Notes |
|---|---|---|
| Panel | `src/Harbora.Web/Program.cs` | `harbora-panel`, port 8080 |
| Break-glass commands | `src/Harbora.Web/Infrastructure/AdminCommands.cs` | First argument `admin`; runs before anything else is configured |
| Developer CLI | `src/Harbora.Cli/Program.cs` | `init login whoami accounts status apps doctor launch deploy logs cancel update env run` |
| Server recovery script | `deploy/harbora` | Bash, installed at `/usr/local/bin/harbora` |
| Node agent | `src/Harbora.NodeAgent/Program.cs` | Linux systemd service |
| Legacy agent | `src/Harbora.Agent/Program.cs` | One file, 198 lines, frozen |

## HTTP surface

### Versioned API

| Base | Routes |
|---|---|
| `api/v1` (`ApiV1Controller`) | `POST auth/token` · `GET version` · `GET whoami` · `GET apps` · `GET apps/{slug}/env` · `POST apps/{slug}/deploy` · `POST apps/{slug}/deploy/archive` · `GET deployments/{id}` · `POST deployments/{id}/cancel` · `GET deployments/{id}/logs` |
| `api/v1/launch` | `POST /` · `POST archive` · `GET {id}` · `GET {id}/proposal` · `POST {id}/root` · `POST {id}/quote` · `POST {id}/confirm` · `POST {id}/retry` · `POST {id}/abandon` |
| `api/v1/nodes` | `GET /` · `GET {nodeId}` · `POST tokens` · `GET tokens` · `POST {nodeId}/revoke` · `POST {nodeId}/drain` · `POST {nodeId}/update-agent` · `GET {nodeId}/workloads/{workloadId}` |
| `api/v1/backup` | Repositories, targets, policies, snapshots, entries, restore jobs |
| `api/v1/sync` | Node, spaces, devices, pairings, conflicts |

### Other machine endpoints

| Route | Purpose | Controller |
|---|---|---|
| `GET /healthz` | Liveness | `Program.cs` |
| `POST webhooks/git/{repositoryId}` | HMAC-verified push and tag events | `WebhooksController` |
| `POST events/ingest/{appId}` | Custom events from an app | `EventsIngestController` |
| `POST functions/{appId}/report` | Function host reports a public invocation | `FunctionInvocationReportController` |
| `GET v1/models`, `POST v1/chat/completions`, `POST v1/responses`, `POST v1/embeddings` | OpenAI-compatible AI gateway | `AiGatewayController` |
| `POST api/node-agent/v1/enroll`, `POST .../credential/renew` | Node enrollment and renewal | `NodeAgentController` |
| `api/node-artifacts/{id}` | Volume artifact transfer | `NodeArtifactController` |
| `/hubs/deployments` | SignalR live deployment logs | `Realtime/DeploymentHub.cs` |
| Node channel | Raw WebSocket for nodes | `Infrastructure/NodeChannelEndpoint.cs` |

## Authentication and authorization

- Cookie sessions for the UI: 7 days sliding, HttpOnly, SameSite Lax, always Secure outside Development (`Program.cs:110-125`). Bearer tokens for API and CLI through `TokenAuthenticationHandler`. Google, GitHub and generic OIDC appear only when configured.
- `SystemRole`: Owner, Admin, Member, Viewer, Operator (`Domain/Common/Enums.cs`).
- Capabilities are policy names: `apps.create`, `apps.deploy`, `apps.operate`, `apps.delete`, `apps.env`, `databases.manage`, `routes.manage`, `git.manage`, `alerts.manage`, `backups.run`, `backups.restore`, `backups.manage`, `servers.manage`, `platform.manage`, `tenants.manage`, `plans.manage` (`Domain/Authorization/Capabilities.cs`). Resolved in `Web/Infrastructure/CapabilityAuthorization.cs`.
- Feature entitlements are a separate axis. Keys: `functions`, `assistant` (`Domain/Features/PlatformFeatures.cs`). Resolution: workspace grant over plan grant over shipped default (`FeatureAccess.Resolve`). States: Enabled, Locked (shown greyed, refused server-side), Hidden. Enforced by `[RequireFeature]` and `IFeatureGate`.
- Rate limits per IP and minute: `auth` 10, `webhook` 60, `voucher` 10 (`Program.cs:171-183`).
- A bearer token has no workspace of its own; it acts in the user's personal workspace first (`TokenAuthenticationHandler.cs:43-48`, KI-030).

## Data model

128 `DbSet`s in `src/Harbora.Data/HarboraDbContext.cs`, grouped:

| Area | Tables |
|---|---|
| Identity | `Users`, `PasswordResetTokens`, `ApiTokens`, `UserSessions`, `EmailVerificationTokens`, `ExternalLogins`, `SupportSessions`, `SignInTokens` |
| Tenancy | `Workspaces`, `WorkspaceMembers`, `WorkspaceInvitations`, `ProjectGrants`, `Projects`, `Environments`, `Plans`, `InstanceSizes`, `ServerInstanceOffers`, `StoragePlans`, `UsageRecords`, `FeatureGrants` |
| Infrastructure | `Servers`, `Nodes`, `NodeEnrollmentTokens`, `NodeCommands`, `NodeEvents`, `HostPortAllocations` |
| Apps | `Apps`, `EnvironmentVariables`, `ConfigGroups`, `ConfigGroupEntries`, `AppConfigGroups`, `ConfigOverrideRules`, `Volumes`, `VolumeDownloadTokens`, `GitProviders`, `GitRepositories`, `RegistryCredentials` |
| Deploy | `Deployments`, `DeploymentApprovals`, `DeploymentLogs`, `CronRuns`, `AppLogLines`, `AppLaunches`, `AppVerifications`, `Jobs`, `IdempotencyRecords` |
| Routing | `Domains`, `CustomerDnsCredentials`, `Routes`, `Certificates` |
| Managed data | `ManagedServices`, `AppManagedServices`, `ManagedServiceDatabases`, `DatabaseMaintenanceSchedules`, `DatabaseMaintenanceRuns`, `DatabaseAccessGrants`, `DatabaseAccessAudits`, `StorageBuckets`, `AppStorageBuckets` |
| Legacy backups | `BackupDestinations`, `Backups`, `BackupDownloadTokens`, `BackupSchedules`, `BackupDeliveries`, `WalArchivingStatuses`, `WalSegments`, `ReplicationLagStatuses` |
| Backup module | `BackupRepositories`, `BackupPolicies`, `BackupSnapshots`, `RestoreJobs` |
| Sync module | `SyncSpaces`, `SyncDevices`, `SyncSpaceMembers`, `SyncConflicts` |
| Mail and email | `MailServers`, `MailDomains`, `MailMailboxes`, `EmailProviders`, `AppEmailProviders`, `ErrorTrackingProviders`, `AppErrorTrackingProviders` |
| Monitoring | `MonitoringMetrics`, `MetricRollups`, `ContainerLifecycleCursors`, `Alerts`, `AlertIncidents`, `AlertDedupMarks`, `UptimeChecks`, `UptimeCheckResults`, `StatusPages`, `StatusPageComponents`, `StatusIncidents` |
| Notifications | `NotificationDeliveries`, `UserNotifications`, `NotificationPreferences`, `NotificationDigestEntries`, `EventSubscriptions`, `EventDeliveries`, `Announcements`, `AnnouncementDismissals` |
| Templates | `AppTemplates`, `AppTemplateVersions`, `AppTemplateAssets` |
| Billing | `Wallets`, `BillingLedger`, `BillingRuns`, `BillingVouchers` |
| Functions | `FunctionDefinitions`, `FunctionInvocations`, `FunctionCodeRevisions`, `FunctionCustomEventKeys`, `FunctionQueueDeadLetters` |
| AI | `AiProviders`, `AiProviderCredentials`, `AiModels`, `AiPlans`, `AiPlanModels`, `AiSubscriptions`, `AiUserApiKeys`, `AiUsageRecords` |
| Assistant | `AgentConversations`, `AgentMessages`, `AgentPlans`, `AgentToolApprovals`, `AgentUsageDays` |
| Platform | `AuditLogs`, `Settings` |

Enum wire values are stored as integers and must never be renumbered (`docs/product-audit/19-do-not-change-list.md` item 15).

## Applications

- `ServiceKind`: Web, Private, Worker, Cron.
- `AppSourceType`: GitRepository, Dockerfile, DockerCompose, PrebuiltImage, StaticSite, Template, Upload, InlineCode. A stored `SourceArchivePath` wins over all of them (`DeploymentPipeline.AcquireImageAsync:952`).
- `GitRepository` and `Dockerfile` behave the same: the repo's Dockerfile, else a buildpack.
- Buildpacks: Node, .NET, Go, PHP (Laravel aware), Python, frontend (multi-stage nginx), static (`Deployments/Buildpacks.cs:52`).
- Variables reach a container through `EffectiveEnvironmentBuilder`: the app's own, config groups, attached databases, buckets, email and error-tracking providers. A web app also gets `PORT` unless it set its own (`DeploymentPipeline.cs`, `DeploymentPipelinePortTests`).

## Deployment state machine

Source: `src/Harbora.Domain/Deployments/DeploymentStateMachine.cs:24-45`. Illegal transitions throw.

| From | To |
|---|---|
| PendingApproval | Queued, Cancelled |
| Queued | Building, Cancelled, Failed |
| Building | Pushing, Deploying, Failed, Cancelled |
| Pushing | Deploying, Failed, Cancelled (nothing enters this state today) |
| Deploying | HealthChecking, Succeeded, Failed, Cancelled |
| HealthChecking | Succeeded, Failed, Cancelled |
| Succeeded | RolledBack |

Health rule (`Deployments/HealthProbeRule.cs`): a configured path must answer below 400. The root path is only asked "is anything serving here?", so below 500 passes, including 404.

## Job queue

Source: `src/Harbora.Infrastructure/Jobs/`, `Domain/Jobs/Job.cs`. Default concurrency is `min(4, ProcessorCount)`; `Jobs:MaxConcurrency` overrides it.

| JobKind | Value | Time limit | Handler |
|---|---|---|---|
| Deployment | 0 | 60 min | `DeploymentPipeline` |
| Backup | 1 | 6 h | `BackupEngine` |
| ServiceProvision | 2 | 15 min | `ManagedServiceEngine` |
| CronRun | 3 | 75 min | `CronJobRunner` |
| BackupSnapshot | 4 | 7 h | module handler |
| BackupRestore | 5 | 7 h | module handler |
| BackupVerify | 6 | 1 h | module handler |
| BackupPrune | 7 | 1 h | module handler |
| RepositoryHealthCheck | 8 | 5 min | module handler |
| BillingHour | 9 | 30 min | `BillingRunHandler` |
| FunctionInvoke | 10 | 1 h (the policy's default case) | `IJobHandler` |
| NotificationDelivery | 11 | 2 min | `IJobHandler` |
| EventDelivery | 12 | 2 min | `IJobHandler` |
| DatabaseMaintenance | 13 | 6 h | `DatabaseMaintenanceService` |
| LaunchInspect | 14 | 15 min | `IJobHandler` |

Status: Pending, Running, Succeeded, Failed, Cancelled. Completed rows are never deleted (KI-040).

## Launch

Source: `src/Harbora.Infrastructure/Launch/`, `Verification/AppVerifier.cs`.

- Timeouts: Inspecting, services and queueing 20 min; Creating 10 min; checks 15 min; confirmation 24 h (`LaunchAdvance.Decide:98`).
- Checks: every replica running, restarts, a connection test per attached database, the public address, the log tail (`AppVerifier.cs:153-160`).
- Verdict (`LaunchAdvance.VerdictOn`): `Succeeded` needs no Failed check, no Warning check, and `running` Passed for long-running kinds. Anything else is `SucceededWithProblems`.
- The inspector lives in `Harbora.Shared/Inspection`, so the CLI and the panel read a project the same way. `NodeStartRule` is shared by `Buildpacks.Detect`, `StackDetector` and `harbora doctor`.

## Managed services

- `ManagedServiceType`: PostgreSql, MySql, MariaDb, Redis, MongoDb, RabbitMq, Nats, Meilisearch.
- Created by `DatabaseProvisioningService.CreateAsync`, which queues `ManagedServiceEngine.ProvisionAsync`. Attaching writes an `AppManagedServices` row, and the variables apply on the next deploy.
- Object storage: `ObjectStorageAdmin` runs `mc` in a one-off container for a per-bucket user, policy and quota.

## Backups: two independent systems

| | Legacy engine | Backup module |
|---|---|---|
| Code | `Infrastructure/Backups/BackupEngine.cs` | `src/Modules/Backup` |
| Tables | `Backups`, `BackupSchedules`, `BackupDestinations` | `BackupRepositories`, `BackupPolicies`, `BackupSnapshots`, `RestoreJobs` |
| Switch | Always on | `Features:Backup`, default false |
| Destinations | Local, S3, SFTP, plus Telegram or email copies | Local and S3 family; Kopia is Local only |
| Host awareness | Resolves the right host through `IServerEngineFactory` | Uses the panel's own `IDockerEngine` (KI-021) |

Postgres also has point-in-time recovery (`PitrRestoreService`). There is no migration path between the two systems.

## Billing

Source: `src/Harbora.Infrastructure/Billing/`. Defaults: enabled, currency `IRR`, `MaxBackfillHours` 72.

- One `Wallet` per workspace and an append-only `BillingLedger`. Credits are idempotent (`WalletService.CreditAsync:268`).
- Prepaid: creating a billable resource atomically debits one running hour (`ResourceCreationBilling.SaveAsync:75`).
- Hourly: `BillingScheduler` creates one `BillingRun` per ended UTC hour and queues a job; `BillingTick.ChargeHourAsync` writes one ledger line per resource and hour.
- Formula, from the design: `charge(hour) = max(planBaseRate, sum of resource rates)`. Charged on what is reserved, not measured. Bandwidth is not metered.
- Vouchers are SHA-256 hashed and single-use. Operators can credit directly. There is no payment gateway.
- A wallet at zero suspends workloads (`BillingSuspension.SuspendAsync:161`).

## Functions

- A function app is an `App` with `SourceType = InlineCode`; publishing is an ordinary deployment (`Functions/FunctionAppService.PublishAsync:155`).
- `DeploymentPipeline.BuildFromInlineCodeAsync:1402` calls `FunctionProject.Generate` for a C#, Node or Python host.
- Triggers: HTTP, cron, platform and custom events, RabbitMQ queue messages with dead letters.

## AI

- Gateway: OpenAI-compatible, with OpenRouter as the only adapter (`DependencyInjection.cs:301`).
- In-panel assistant: 29 tools registered in `DependencyInjection.cs:336` onward (6 read, 23 write). Consent levels are Read, SafeWrite, DangerousWrite and Destructive (`AgentConsentPolicy`). Plans expire after 15 minutes and are re-validated at execution. Limits: 8 model calls per message, 300 per workspace per day by default.

## Notifications

Durable outbox `NotificationDeliveries` with retries. Channels: email, Telegram, Discord, webhook, in-app. Digests, quiet hours, templates, and signed outbound event subscriptions (`Notifications/NotificationService.cs`, `EventDispatcher`).

## Retention defaults

Source: `Maintenance/RetentionOptions.cs`, in days; the sweep runs at 03:00 UTC.

| Table | Days | Table | Days |
|---|---|---|---|
| DeploymentLogs | 90 | AlertIncidents | 90 |
| AuditLogs | 365 | NotificationDeliveries | 90 |
| CronRuns | 90 | AlertDedupMarks | 7 |
| FunctionInvocations | 30 | UserNotifications | 180 |
| NodeCommands | 90 | NotificationDigestEntries | 90 |
| NodeEvents | 90 | PasswordResetTokens | 7 |

Also swept: `IdempotencyRecords`, `UserSessions`, `EmailVerificationTokens`, `AppLogLines` (per app). Not swept: `Jobs`, `Deployments`, `AppLaunches`, `BillingRuns`, `AiUsageRecords`, among others (`DataRetentionSweeper.cs`, `RetentionTables`).

## Configuration

Set through `appsettings.json` or environment variables with `__` as the separator. Operator-facing ones are in [ENVIRONMENT_AND_SETUP.md](ENVIRONMENT_AND_SETUP.md).

| Section | Options class |
|---|---|
| `Runtime` | `Deployments/HarboraRuntimeOptions.cs` |
| `Jobs` | `Jobs/JobQueueOptions.cs` |
| `Billing` | `Billing/BillingOptions.cs` |
| `Retention` | `Maintenance/RetentionOptions.cs` |
| `Monitoring` | `Monitoring/MonitoringOptions.cs` |
| `LogIngestion` | `Logging/LogIngestionOptions.cs` |
| `DeploymentApproval` | `Deployments/DeploymentApprovalOptions.cs` |
| `NodeAgent` | `Nodes/NodeAgentControlPlaneOptions.cs` |
| `Storage:S3` | `Storage/ObjectStorageOptions.cs` |
| `Traefik` | `Proxy/TraefikOptions.cs` |
| `Features` | `Terminal`, `Backup`, `Sync`, all default false |
| `Backups:Kopia`, `Backups:Module` | `Modules/Backup/.../BackupModuleOptions.cs` |
| `Sync:Syncthing`, `Sync:Module` | `Modules/Sync/.../SyncModuleOptions.cs` |

## CI and release

| Workflow | Trigger | Purpose |
|---|---|---|
| `ci.yml` | Push and PR to `master`, manual | .NET suites, Postgres lane, front end, shell syntax, agent publishes. The `complete` job fails if any job failed, was skipped or was cancelled. |
| `launch-proof.yml` | `master` when launch paths change, `proof/**`, manual | Installs from nothing and launches 12 real fixtures (`deploy/launch-proof.sh`). Not part of `complete`. |
| `nightly-live-host.yml` | 03:00 UTC, manual | Full proof on two real hosts; needs repository secrets the owner has not set. |
| `tag-release.yml` | Push to `master` changing `<Version>` | Pushes `vX.Y.Z` and dispatches the CLI release. |
| `release-cli.yml` | Tag, manual | Windows, Linux and macOS binaries with sha256 files, and the .NET tool package. |
| `release-node-agent.yml` | Tag, manual | Node agent binaries. |

## Test suites

| Project | What it needs | What it covers |
|---|---|---|
| `Harbora.Tests` | Nothing external; HTTP tests boot the real pipeline on EF InMemory | Almost everything, including census tests that hold docs and config to code |
| `Harbora.NodeAgent.Tests` | Nothing external; some tests skip without Docker | Agent and contract conformance |
| `Harbora.NodeIngress.Tests` | Nothing external | Ingress tunnel |
| `Harbora.Postgres.Tests` | Docker (Testcontainers style) | Real PostgreSQL behaviour |
| `src/Harbora.Web/Scripts/tests` | `npm ci` | Island logic |
