Last verified: 2026-11-08
Scope: feature code at commit `b3fd974`, read statically; `docs/product-audit/backlog.json`; launch proof and CI of 2026-11-05. Nothing was built or run for this baseline. Short paths are under `src/Harbora.Infrastructure/`, or `src/Harbora.Web/` for `Controllers/`, `Views/` and `Scripts/`.

# Implementation status

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md).

**Status words.** *Done*: the feature's main path exists end to end in the code. *Partial*: part of it is missing, or it has a known defect that changes the outcome. *Not started*: no working code. *Done* says nothing about whether it has run on a real server; the "Proven" tables below say that.

**[Verified]** every row in the feature tables, by reading the file named. Where a defect is the reason for *Partial*, the `KI-` number links to [KNOWN_ISSUES_AND_TECH_DEBT.md](KNOWN_ISSUES_AND_TECH_DEBT.md).

## Deploy and run

| Feature | Status | Evidence, note |
|---|---|---|
| Single-container cutover with replicas | Done | `DeploymentPipeline.cs:537-660` |
| Buildpacks: Node, .NET, Go, PHP/Laravel, Python, frontend, static | Done | `Deployments/Buildpacks.cs:52` |
| Git deploy, webhooks | Partial | `Git/LibGit2GitService.cs`; a commit SHA or mistyped ref builds the default branch (KI-010) |
| CLI upload deploy | Done | `Controllers/Api/ApiV1Controller.cs` `DeployArchive` |
| Prebuilt image deploy | Done | `DeploymentPipeline.AcquireImageAsync:952` |
| Static site | Done | same |
| Docker Compose apps | Partial | Runs, with several gaps (KI-012) |
| Cron apps | Partial | Receive only literal variables (KI-014) |
| Templates | Done | `Deployments/TemplateResolver.cs`; multi-service live proof is open in the backlog (HARBORA-0044) |
| Rollback | Partial | Works for ordinary images; not for Compose, and not byte-exact for mutable tags (KI-012, KI-017) |
| Deployment approval | Partial | Pinned commit is not what gets built (KI-010) |
| Cancelling a deployment | Partial | A running one ends Failed, not Cancelled (KI-011) |
| Previews, promotion, environment clone | Present, not traced | `Deployments/PromotionPlan.cs`, `Projects/`; backlog HARBORA-0040 says previews are not GA |
| Registry push | Not started | `DeploymentStatus.Pushing` is never entered |
| Fair queueing between tenants | Not started | `Jobs/JobClaimQuery.cs` is plain FIFO |
| More than one panel instance | Not started | `Deployments/DeploymentReconciler.cs:152-155` |
| Launch: inspect, confirm, create, deploy, verify | Done | `src/Harbora.Infrastructure/Launch` |

## Routing and networking

| Feature | Status | Evidence, note |
|---|---|---|
| Traefik file generation, atomic apply | Done | `Proxy/TraefikProxyEngine.cs:174` |
| Visual route designer | Partial | Offers a container name no current container has; can delete on a stale list (KI-018) |
| SSL off, ForceHttps off | Partial | The switches do not do what the page says (KI-015) |
| Custom domains, Let's Encrypt | Done | `Controllers/DomainsController*.cs`; takes effect on the next deploy |
| Custom certificates, DNS-01 wildcard | Partial | Backlog HARBORA-0050 |
| Cloudflare platform mode, customer DNS | Done | `Networking/CloudflarePlatformService.cs`, `CustomerCloudflareService.cs` |
| Per-environment private networks | Done | `Networking/EnvironmentNetwork.cs` |

## Data services

| Feature | Status | Evidence, note |
|---|---|---|
| Eight managed services | Done on the panel host | `Services/ManagedServiceEngine.cs` |
| Managed services on a v1 node | Partial | No volume measurement; external access not wired (KI-005, KI-033) |
| Read replicas, PITR | Done, Postgres only | `PitrSupport`, `ReplicaPipelineTests` |
| Object storage buckets | Partial | Not billed; no suspension or balance check (KI-006) |
| Customer mail hosting (Stalwart) | Partial | No health reconcile or cleanup on workspace removal found (KI-062) |
| BYO SMTP, error tracking | Done, as variable injection | `Apps/EffectiveEnvironmentBuilder`; no customer sending API |

## Backups

| Feature | Status | Evidence, note |
|---|---|---|
| Legacy database and volume backup and restore | Done | `Backups/BackupEngine.cs` |
| Legacy "app config" and "full platform" restore | Partial | Restores less than it captures (KI-020) |
| Backup copies to Telegram or email | Done | `BackupDeliveryService` |
| Upgrade restore point | Done | `UpgradeSafetyService`, `Program.cs:205` |
| Backup module (`Features:Backup`) | Partial, off by default | Panel host only, restore types not honoured (KI-021, KI-022) |
| Kopia engine | Partial | Local repositories only; needs `deploy/Dockerfile.panel-kopia` |
| Sync module (`Features:Sync`) | Partial, off by default | Needs the Syncthing overlay `deploy/backup-sync.compose.yml` |
| Backing up a volume on another server | Not started | Refused before starting (README, Known limitations) |

## Tenancy, access and money

| Feature | Status | Evidence, note |
|---|---|---|
| Workspaces, query-filter isolation, membership recheck | Done | `HarboraDbContext.cs`, `WorkspaceMembershipValidationMiddleware` |
| Roles and capabilities | Done | `CapabilityAuthorization.cs` |
| Feature entitlements | Done | `Domain/Features`, `Infrastructure/Features/FeatureGate.cs` |
| API and CLI tokens | Partial | Tokens cannot target a non-personal workspace (KI-030) |
| External sign-in (Google, GitHub, OIDC) | Done | `AccountController.External.cs` |
| Wallet, ledger, prepaid first hour, vouchers | Done | `Billing/WalletService.cs`, `ResourceCreationBilling.cs`, `VoucherService.cs` |
| Hourly billing | Partial | Can overcharge and can skip hours (KI-001 to KI-004) |
| Suspension at zero balance | Done | `Billing/BillingSuspension.cs` |
| Online payment | Not started, on purpose | `appsettings.json`, `Billing._comment_funding` |
| Bandwidth metering | Not started, on purpose | Design doc, "Known gaps" |

## Nodes

| Feature | Status | Evidence, note |
|---|---|---|
| Enrollment, mTLS channel, heartbeat | Done | `Nodes/NodeEnrollmentService.cs`, `NodeChannelEndpoint.cs` |
| 25-verb contract | Done | `NodeCommands.cs`; 9 verbs have no panel caller (KI-033) |
| Ingress tunnel for NAT | Done | `NodeTunnelGateway`, `TunnelSupervisor`; throughput never measured |
| Agent self-update | Partial | Operator types version, URL and hash; Linux only (`NodesController.UpdateAgent:267`) |
| Build, exec, one-off on a v1 node | Not started, on purpose | `NodeWorkloadEngine.cs:179,621` |
| Moving an app between servers | Not started | Backlog HARBORA-0034 |
| Legacy HTTP agent | Done, frozen, deprecated | `src/Harbora.Agent` |

## Observability

| Feature | Status | Evidence, note |
|---|---|---|
| Live deployment logs | Done | `Realtime/DeploymentHub.cs` |
| App log retention | Done, per-app opt-in | `LogIngestionHostedService:55` |
| Metrics, alerts, uptime checks, status pages | Present; depth not audited for this baseline | Hosted services registered; backlog HARBORA-0030, 0031 are partial |
| Notifications and event subscriptions | Done | `Notifications/NotificationService.cs` |

## Functions and AI

| Feature | Status | Evidence, note |
|---|---|---|
| Functions: C#, JavaScript, Python | Done | `Functions/`; generated hosts were built and run in a scratch harness on 2026-08-18, and `README.md` says a `docker build` through the pipeline on a real server is not on record |
| HTTP, cron, event and queue triggers | Done | `FunctionCronScheduler`, `FunctionEventBus`, `QueueFunctionConsumerHost` |
| In-panel assistant | Done, gated by entitlement | `Agent/`; 29 tools |
| AI gateway | Partial | Built, but no route in the product creates a subscription (KI-060) |
| Web terminal | Done, off by default, local server only | `TerminalAccess.Decide`, `Features:Terminal` |

## Interface

| Feature | Status | Evidence, note |
|---|---|---|
| Persian and English, RTL and LTR | Done | `Program.cs:99-107`; localization is mostly inline (KI-071) |
| Simple and Advanced modes | Partial | `Domain/Identity/PanelMode.cs`; backlog HARBORA-0045 |
| Learning Centre | Done | `Learning/LearningLibrary.cs` reads `docs/tutorial` |

## Proven on real infrastructure

**[Verified]** from CI run 42031668273 on `7e90af0`, `deploy/launch-proof.sh` and its fixtures.

| Proven by the launch proof | Not proven anywhere |
|---|---|
| Install from nothing on a throwaway host, migrations on real PostgreSQL, a local ACME server | A public certificate through a launch |
| node-postgres app really querying through `DATABASE_URL` and the `DB_HOST` alias | `harbora launch --git` |
| A crashing app and a wrong-database-host app do not end green | A remote node in a launch |
| Python worker with Redis; two-apps folder choice; a next-day `harbora deploy` | A paying customer's wallet and charge in a launch |
| Vite, Create React App, Node build variables, Python marked variable | Browser-driven forms and polling |
| PHP with MySQL; real Laravel with its migration | Retry, and a panel restart in the middle of a launch |
| | MariaDB, MongoDB, RabbitMQ, NATS, Meilisearch in a launch |
| | .NET, Go, pnpm, yarn, real Next.js, Gatsby, Vue CLI in a launch |
| | A zip with an outer wrapper folder; MinIO upgrade over existing data |

[Assumption] The nightly live-host proof has not run: as of 2026-09-29 it stopped at its secrets check, and the secrets are the owner's to set (`.superpowers/sdd/r5/progress.md`). Its current state was not checked.

## Backlog census

**[Verified]** from `docs/product-audit/backlog.json`; `BacklogStatusTests` enforces evidence on every non-open item. 41 done, 16 partial, 9 open.

| ID | Status | Title |
|---|---|---|
| HARBORA-0011 | partial | Node Agent v1 works out of the box |
| HARBORA-0022 | partial | Live-host E2E CI lane |
| HARBORA-0030 | partial | Uptime and restart-count collection |
| HARBORA-0031 | partial | Alert incident lifecycle and timeline |
| HARBORA-0033 | partial | Volume safety |
| HARBORA-0035 | partial | Per-app grants, ownership transfer, service accounts |
| HARBORA-0038 | partial | Customer email phase 1 |
| HARBORA-0042 | partial | API v1 expansion, OpenAPI, CLI phase 2 |
| HARBORA-0046 | partial | Backup module parity and GA flip |
| HARBORA-0048 | partial | Platform admin operations pack |
| HARBORA-0050 | partial | Custom certificates and DNS-01 wildcard |
| HARBORA-0052 | partial | Dead-surface cleanup |
| HARBORA-0053 | partial | Onboarding wizard and checklist |
| HARBORA-0054 | partial | AI gateway real-provider verification |
| HARBORA-0059 | partial | Database grants and Adminer inject the local Docker engine |
| HARBORA-0061 | partial | RestoreJob has no StagingPath |
| HARBORA-0034 | open | Move an application between servers |
| HARBORA-0039 | open | Harbora-managed email relay |
| HARBORA-0040 | open | PR preview environments to GA |
| HARBORA-0041 | open | Repo-committed IaC apply |
| HARBORA-0043 | open | Marketplace growth with CI validation |
| HARBORA-0044 | open | Multi-service template live proof |
| HARBORA-0045 | open | Simple/Advanced coverage and localization consolidation |
| HARBORA-0049 | open | Metering covers services, volumes and buckets |
| HARBORA-0063 | open | Meaning of runningWorkloads and activeTunnels on the Nodes page |

[Recommendation] The defects found by this baseline (`KI-` numbers) are not yet in `backlog.json`. Decide whether that file or the known-issues document is the tracker; see OQ-09 in [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md).
