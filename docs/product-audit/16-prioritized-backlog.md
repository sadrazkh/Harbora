# 16 — Prioritized Backlog (human view)

Machine-readable source of truth: [`backlog.json`](backlog.json) (64 items, full acceptance criteria/tests/risks/evidence per item).

This view groups by priority. Phase numbers reference 17. Type: B=bug, D=tech-debt, G=feature-gap, U=ux, X=needs-investigation.

> 0055-0064 were found while *executing* Phases 1 and 2 rather than during the audit, and were folded in on 2026-08-08 from `.superpowers/sdd/discovered-backlog.md`, which records where each came from. Two items on that list were dropped as already closed and two were narrowed; the annotations are in that file.

## P0 — reliability blockers
| ID | Title | T | Scope | Ph |
|---|---|---|---|---|
| 0001 | Parallel job execution with per-target locks | D | M | 1 |
| 0002 | Job timeouts + selective retry | D | M | 1 |
| 0003 | Backup crash reconciliation + DB-level target uniqueness | B | M | 1 |
| 0004 | Deployment must fail when proxy apply fails (+post-cutover probe) | B | S | 1 |
| 0005 | Multi-node backup/cleanup correctness via engine factory | B | M | 1 |
| 0011 | Node Agent v1 out-of-box activation chain | B | M | 2 |
| 0022 | Live-host E2E CI lane (install→node→deploy→backup→upgrade) — "R0 proof" | D | M | 2 |

## P1 — required for a dependable usable version
| ID | Title | T | Scope | Ph |
|---|---|---|---|---|
| 0006 | Fix JobWorker vs reconcilers boot race | B | S | 1 |
| 0007 | Cron joins environment network (staging DB resolution) | B | S | 1 |
| 0008 | Backups UI: SFTP fields out of schedules loop; destination toggle | B | S | 1 |
| 0009 | UX small-fix bundle (3 dead links, 13 icons, 4 empty states, dev copy, stale note) | U | S | 1 |
| 0010 | Queue transparency: position, cancel in UI + CLI | G | M | 1 |
| 0012 | Retention sweeper (deploy logs, audit, cron runs, node records, idempotency, reset tokens) | D | M | 2 |
| 0013 | Module backup verification actually runs (+button) | B | S | 2 |
| 0014 | Module restore takes safety snapshot first | G | S | 2 |
| 0015 | External DB access rotation: honor CanOpenLocally | B | S | 2 |
| 0017 | Documentation truth pass (README/RUNBOOK/contract changelog/verb counts) | D | S | 2 |
| 0018 | CI consolidation: all suites in PR workflow, shellcheck, CLI checksums | D | S | 2 |
| 0020 | Testcontainers Postgres test lane | D | M | 2 |
| 0021 | WebApplicationFactory HTTP test lane | D | M | 2 |
| 0047 | Disaster-recovery runbook + restore drill checklist | D | S | 2 |
| 0023 | Finish environment model (EnvironmentId required, single network) | D | M | 3 |
| 0024 | Managed-service provision failure surfaced + retryable | B | S | 3 |
| 0026 | /activity job list + long-op toasts link to it | G | M | 3 |
| 0040 | PR preview environments — finish lifecycle | G | L | 3 |
| 0029 | Alerts: edit/toggle, threshold validation, configurable ratios | B | S | 6 |
| 0030 | Uptime + restart-count collection and display | G | M | 6 |
| 0031 | Alert incident lifecycle (firing/resolved) + timeline UI | G | M | 6 |
| 0032 | DB logical export/import + connection snippets | G | M | 7 |
| 0033 | Volume safety: Protected flag, orphan report, deleteData in UI | G | M | 7 |
| 0046 | Backup module parity + GA (delivery channels, NoRecentBackup, Kopia maintenance/strategies, flip default) | G | L | 8 |
| 0036 | Notification system core (events, routing, in-app center, retry/dedup, fa+en templates) | G | L | 9 |
| 0038 | Customer email service phase 1 (BYO providers, ingest relay, Dev Inbox, DNS guide, injection) | G | L | 10 |
| 0042 | API v1 expansion + OpenAPI + CLI phase 2 (env/domains/rollback/list/cancel/--json) | G | L | 11 |
| 0053 | Setup wizard steps + onboarding checklist gains backup & SMTP steps | U | M | 15 |
| 0056 | Decide what a workspace operator sees in the audit log (unscoped today) | X | M | 4 |
| 0059 | Database grants + Adminer still inject the local Docker engine | B | M | 7 |

## P2 — product completion
| ID | Title | T | Scope | Ph |
|---|---|---|---|---|
| 0016 | MetricRollups composite index | D | S | 2 |
| 0019 | Populate heartbeat gauges; publish 7 declared node events | B | S | 2 |
| 0051 | Legacy HTTP agent: deprecation notice + freeze + migration note | D | S | 2 |
| 0054 | AI gateway: verify against one real provider or gate the UI with "unverified" notice | X | S | 2 |
| 0025 | DB password rotation offers batch redeploy of attached apps | B | S | 3 |
| 0027 | Deploy retry action + build args | G | S | 3 |
| 0028 | Scheduler re-check on redeploy; port-burn handling; disk pre-flight | D | M | 3 |
| 0035 | Per-app grants, ownership transfer, service accounts | G | M | 4 |
| 0034 | App move between servers (snapshot/restore verbs) | G | L | 5 |
| 0041 | Repo-committed IaC apply (harbora.yml services) | G | L | 11 |
| 0043 | Marketplace: screenshots, backup notes, curated growth + CI validation harness | G | L | 11 |
| 0044 | Multi-service template live proof + broker requirements | G | M | 11 |
| 0045 | Simple/Advanced in-page coverage + localization consolidation (resx) | U | L | 12 |
| 0037 | Notification preferences UI, digest, quiet hours, weekly report | G | M | 9 |
| 0048 | Platform admin: announcements, maintenance banner, feature-flag UI, support tooling | G | L | 13 |
| 0049 | Metering covers services/volumes/buckets | G | M | 13 |
| 0039 | Harbora-managed email relay (SES-class upstream, tenant sandboxing, quotas) | G | L | 14 |
| 0057 | Deployment coalescing is an unguarded read-then-insert (partial unique index) | D | S | 3 |
| 0058 | Reserved-host guard missing on the template and preview host paths | B | S | 3 |
| 0060 | No startup re-apply of the proxy configuration | B | M | 3 |
| 0055 | MonitoringMetrics index mis-shaped for its own query | D | S | 6 |
| 0062 | Retention sweep result has no consumer; a clean sweep logs nothing | D | S | 6 |
| 0063 | Decide what runningWorkloads and activeTunnels mean on the Nodes page | X | S | 6 |
| 0061 | RestoreJob has no StagingPath, so a staged dump has no retry pointer | D | S | 8 |
| 0064 | SftpTransfer.Download's local-name substitution is unproven | D | S | 8 |
| 0050 | Custom certificates + DNS-01 wildcard (Cloudflare first) | G | L | 16 |

## P3 — future
| ID | Title | T | Scope | Ph |
|---|---|---|---|---|
| 0052 | Dead-surface cleanup bundle (03 §P3 list — rides along area PRs) | D | S | 16 |
| — | HA/multi-instance control plane (constraints register 13 §4) | G | XL | 16 |
| — | Traefik-metrics ingestion (response time/error rate), status pages | G | L | 16 |
| — | Autoscaling, multi-region, marketplace-public, billing engine, mailbox hosting | G | XL | 16+ |

**Rejected (do not schedule):** Kubernetes core, microservice split, in-house APM, GPU orchestration, scale-to-zero runtime, per-second metered billing, 280-template catalog chase, generic plugin framework (13 §1), shared multi-tenant mailbox infrastructure (10 §6).
