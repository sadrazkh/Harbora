# 17 — Implementation Roadmap

## 0. Amendments to the mission's proposed order (with reasons)
The proposed 16-step order is adopted with three changes:
1. **"Domain و SSL" loses its dedicated phase** — it is the most production-proven area (live ACME verified; watcher; per-row diagnostics). Its remaining items are small (gRPC toggle, real-IP docs → Phase 7; custom certs/DNS-01 → Phase 16). A whole phase there would idle.
2. **Phase 2 absorbs test-infrastructure and docs-truth work** (Testcontainers, WebApplicationFactory, live-host E2E "R0", CI consolidation, doc drift) — node out-of-box activation can only be *proven* by the live-host lane, so they belong together; and every later phase depends on those lanes.
3. **Phase 5 becomes "Node fleet growth & app mobility"** (freed by #1) — app-move-between-servers builds directly on Phase 1/2's engine-seam and snapshot-verb work while it is fresh.
Also: previous roadmap corpus (`docs/overhaul` docs 12/15/16/17) contains four incompatible numbering schemes — **this file supersedes them for planning**; keep doc 17 (R0–R5) only as historical context.

Sizing: S/M/L as in 05. A phase is *done* only when its acceptance bundle passes in CI — "No phase is marked done because its UI exists" (adopting the repo's own delivery rule).

---

## Phase 1 — Deploy-engine truth & queue (P0) — items 0001-0010
- **Goal:** the platform never lies about a deployment and one tenant can't freeze another.
- **Solves:** R-01..R-03 (partially R-04..R-07), R-09, R-12, UX-S1.
- **Scope:** worker pool + per-target locks; per-kind timeout + retry classes; proxy-failure=Failed + optional through-proxy probe; boot-race fix; backup crash reconciler + unique active index; engine-factory in BackupEngine/DiskCleanup/TcpGateway; cron network parity; queue position + cancel (UI + one API endpoint + CLI verb); SFTP form fix; small-UX bundle.
- **Out:** any new feature surface; node changes beyond seam fix.
- **Changes:** Backend: Jobs/, Deployments/, Backups/, module reconciler. Frontend: queued-card, cancel, /backups form, link/icon fixes. Agent: none. DB: 2 migrations (unique partial index; optional MaxAttempts). Infra: none.
- **Tests:** concurrency matrix, timeout, boot-race, kill-mid-backup, proxy-failure, remote-engine resolution (15 §3).
- **Risks:** hidden shared-state under parallelism → ship behind `Jobs:MaxConcurrency` (rollback = 1); per-target lock deadlocks → lock ordering test.
- **Acceptance:** 15 §3 bundles green; two-tenant demo: A builds 10 min while B deploys in <1 min.
- **Rollback:** config to serial; migrations additive-only.
- **Docs:** README limitations updated (serial claim removed only when true).

## Phase 2 — Node & platform confidence — 0011-0022, 0047, 0051, 0054, 0016, 0019
- **Goal:** a stranger can install Harbora + one node from README alone, and CI proves it nightly.
- **Solves:** R-05, R-08, R-10, R-11, R-14, R-16, R-25, R-34, R-41, R-42; test holes L2/L3/L5.
- **Scope:** installer templates node mTLS config + CA export + env backfill + `node-ca` verb + enroll preflight; live-host E2E lane; Testcontainers + WebApplicationFactory lanes; CI consolidation + shellcheck + checksums; retention sweeper; module verify enqueue + safety snapshot; rotation local fix; docs truth pass; DR runbook; legacy-agent deprecation notice; heartbeat gauges + node events; MetricRollups index; AI-gateway single-provider verification (or "unverified" banner).
- **Out:** app-move; new verbs; module GA flip.
- **Changes:** Backend: Nodes/, Backups/, Maintenance/. Frontend: minor (node page badges). Agent: heartbeat fields, event publishing (additive, no contract bump needed — fields exist). DB: index migration. Infra: install.sh, workflows, runbook.
- **Tests:** artifact tests extended; heartbeat assertion; L2/L3/L5 lanes themselves.
- **Risks:** live-host lane cost/flakiness → nightly not per-PR, ACME staging CA; installer templating errors → idempotent `backfill_env` pattern + verify step.
- **Acceptance:** fresh two-VPS script: panel + node Online + nginx deploy through Traefik + backup + restore + upgrade-from-previous — fully unattended, green twice consecutively.
- **Rollback:** none needed (additive); installer keeps old behavior when vars pre-exist.
- **Docs:** node quickstart rewritten from the working path; RUNBOOK regenerated.

## Phase 3 — Application & environment management — 0023-0028, 0040
- **Goal:** the project/environment model is finished, and long operations are observable.
- **Scope:** EnvironmentId required + single-network migration sweep; provision-failure surfacing + retry; rotation batch-redeploy prompt; /activity job page (+ toasts link); deploy retry action; build args; scheduler/port/disk pre-flight; **PR preview environments to GA** (webhook PR events, URL surfacing, teardown proof).
- **Out:** per-app RBAC (next phase); IaC apply.
- **Changes:** DB: the EnvironmentId-required migration (biggest of the roadmap — staged: backfill, enforce, drop dual-attach). Agent: none.
- **Tests:** isolation/wiring suites must stay green through the network change; preview lifecycle e2e in L5.
- **Risks:** env migration on live data → ship with a dry-run report command; previews depend on provider webhook payloads (verify Gitea/GitLab PR events — open Q5).
- **Acceptance:** new workload inspects to exactly one network; PR open→URL comment ready→merge→gone; stuck-job page shows every running/queued job.

## Phase 4 — Users, workspaces & roles — 0035 (light)
Per-app grants (extend `ProjectGrant`), ownership transfer UI, service-account tokens. Out: SSO/SCIM (P3). Tests: capability matrix in L3. Acceptance: a Developer scoped to app X cannot see app Y (HTTP-level proof).

## Phase 5 — Node fleet growth & app mobility — 0034
App move between servers (snapshot→restore verbs→route/port rewire), node pools UI, panel-side dispatch of volume snapshot verbs (completes R-04 story for v1 nodes). Risks: data-consistency during move → stop-first strategy only (no live migration). Acceptance: move an app with a volume between two nodes with byte-identical volume content and <60 s downtime; refusal on legacy-agent source.

## Phase 6 — Monitoring & alerting — 0029-0031
Alert edit/toggle/validation + configurable thresholds; uptime/restart metrics; incident lifecycle + timeline; bell → real target. Acceptance: 08 §B4.

## Phase 7 — Database, networking & storage — 0032-0033 (+small: gRPC/h2c toggle, real-IP guidance, connection snippets, TLS policy R-31)
Acceptance: 07 §5 bundle.

## Phase 8 — Backup convergence & GA — 0046
Module parity (delivery channels, full-platform target, NoRecentBackup evaluator, Kopia maintenance + strategy honesty + S3 decision, progress) → live-host proof → `Features:Backup` default on → legacy marked maintenance. Acceptance: a restore drill of DB+volume from the module passes on the L5 lane; NotVerified count on fresh snapshots reaches 0 within an hour.

## Phase 9 — Notification system — 0036, 0037
Per 09. Acceptance: 09 §6. Depends: Phase 1 queue.

## Phase 10 — Customer email (BYO + Dev Inbox) — 0038
Per 10 §5 Phase-1. Depends: 9 (template rendering shared), 1 (queue). Acceptance: 10 §8.

## Phase 11 — Marketplace, API & CLI — 0041-0044, 0042
Template growth + validation harness + screenshots; multi-service proof + brokers; API v1 expansion + OpenAPI; CLI phase 2 (`--json`, env/domains/rollback/list/cancel, completion); repo IaC apply. Acceptance: every published template deploy-tested in CI; CLI can run a full app lifecycle headless.

## Phase 12 — Simple/Advanced & localization — 0045
In-page fold coverage; resx consolidation (no new `isFa?` test); confirmation-tier normalization; palette/nav unification. Acceptance: string-catalog coverage >90 % of views; Simple-mode walkthrough of J1–J7 without seeing an Advanced concept.

## Phase 13 — Platform admin, plans & quota ops — 0048, 0049
Maintenance announcements + banner, feature-flag UI, failed-job/delivery overviews (reads /activity + notification tables), metering completeness, support/incident basics. Manual credit ledger only if billing decision made (18 Q11).

## Phase 14 — Managed email relay — 0039 (per 10 §5 Phase-2)

## Phase 15 — Onboarding & documentation — 0053
Setup wizard steps; checklist gains backup + SMTP/notification steps; contextual doc links; tutorial refresh; public docs site decision.

## Phase 16 — Future — 0050, HA register (13 §4), Traefik-metrics/response-time, status pages, autoscaling/multi-region/billing/mailbox integration per demand.

---

## Sequencing rationale & dependencies (summary)
Phases 1–2 are strictly first (everything else queues jobs or ships features on the node story). 3 unlocks 4/5/7. 6 before 9 (incidents feed notifications). 8 before 10 only in that both use the queue; they are independent otherwise — 8 can slide later without blocking 9/10. 9 before 10 (shared templates/delivery). 11 anytime after 3; 12 anytime (pure UI); 13 after 9 (delivery overviews). Suggested tempo: 1–2 ≈ one milestone each; 3 the largest single milestone; then value-driven order per demand.
