# 08 — Backup & Monitoring Plan

## Part A — Backup

### A1. Strategic call: one system, staged convergence
Two parallel systems exist (01 §9). Recommendation: **the module architecture is the future** (repository/policy/snapshot model, cron+timezone, tiered retention, idempotency keys) but the **legacy system currently holds the crown jewels** (real verification rehearsal, pre-restore safety copy, Telegram/email delivery, full-platform backup, live-host proof). Do **not** delete legacy until the module reaches feature parity + live proof. Convergence sequence:

1. **Phase 1 (hardening, module + legacy):** R-02 reconciler/unique-index/staging-sweeper; R-04 engine-seam fix; R-09 SFTP form fix (legacy UI).
2. **Phase 2 (module trust):** R-10 verify enqueue (auto after each snapshot + button); R-11 safety snapshot before restore (port from `BackupEngine.cs:326-341`); R-27 Kopia conflict-strategy honesty; Kopia `Maintenance` scheduling (currently never GC'd); native-engine timeout (R-35).
3. **Phase 8 (parity + GA):** module gains delivery channels (reuse `BackupDeliveryService`), full-platform target, retention-size enforcement or field removal; live-host proof of Kopia + compose overlay (never executed per `MERGE_GUIDE.md:345-381`); then flip `Features:Backup` default on, badge legacy "maintenance mode", migrate schedules with a converter.
4. **Never:** dual-write or auto-migrate artifacts between systems.

### A2. Feature work (module, Phase 8)
- Destinations: S3 for Kopia (decision: repository-config file avoids the credential-on-cmdline objection — investigate `kopia repository connect s3 --config-file`; else document native-engine-only for S3). SFTP with host-key column. Drop `WebDav/HarboraNode/Custom` enum members or return refusals (currently unhandled exceptions).
- Progress: per-snapshot byte/file progress (engines expose counts; native engine can count during tar) — replaces status-only.
- `NoRecentBackup` evaluator (the field `AlertAfterHoursWithoutSuccess` exists, dead): hourly sweep raising the already-defined notification kind. High value / S effort.
- Restore to new location UX (strategy exists, surface it), application **restore** (rebuild from `application.json` — the capture half ships; XL, needs template-like provisioning reuse).
- Backup Center UX: split Restore Center view (plan promised it), snapshot download w/ short-lived link.

### A3. DR runbook (Phase 2, docs)
Write `docs/disaster-recovery.md`: panel-host loss (restore `.env` + `harbora_pgdata` from platform backup + `restore-db`), single-app loss (volume restore drill), node loss (re-enroll + redeploy; volumes lost unless backed up — states the R-04/verb gap until fixed). Add "restore drill" checklist to onboarding docs.

## Part B — Monitoring

### B1. Keep
30 s collector → 24 h raw → 31 d/365 d rollups; "unmeasured ≠ zero" discipline (`MetricDisplay`/`AllocationReading` — exemplary); crash-loop-aware app health reconciler; real-TLS-handshake certificate watcher; per-app threshold rules with sustain windows and collection-gap honesty.

### B2. Phase 6 work (Logs/Metrics/Monitoring phase)
1. **Index `MetricRollups`** (R-34, S) — `(ServerId, Name, ResourceRef, Period, PeriodStart)`.
2. **Retention sweeper** (R-14, M) — deploy logs, audit, cron runs, node command/event records, idempotency, reset tokens; config section `Retention:*`.
3. **Uptime & restart counts** (M) — persist `StartedAt`/`RestartCount` from stats/agent into a small `AppLifecycleSample` or reuse `MonitoringMetric` names (`app.restarts`, `app.started_at`); dashboard uptime %, restart sparkline; alert on restart-rate.
4. **Alert lifecycle** (M) — add `firing/resolved` to threshold + disk + SSL alerts (state lives on `Alert` for thresholds already via `ThresholdFiredAt`; generalize to an `AlertIncident` row: opened/resolved/notified) → gives the missing incident timeline view on `/monitoring`.
5. **Alert edit/disable UI** (R-33, S) + threshold form validation (R-32, S) + bell target fix (R-17, S).
6. **Configurable thresholds** (R-44, S) — disk ratio, backup staleness into options/settings.
7. **Dedup/retry** — arrives with the Notification System (09); interim single-retry (R-13).
8. **Node self-metrics ingestion** (M, optional) — scrape agent `:9701` over tunnel; unlocks node-side view parity (cert expiry gauges, command latencies).

### B3. Explicit non-goals now
Response time / error rate / p95 (needs Traefik metrics ingestion — P3 with a "Traefik Prometheus → panel" reader, not an in-house APM); log aggregation/full-text historical search (Docker tail + download suffices until demanded); external uptime probing (pairs nicely with future status pages — P3).

### B4. Acceptance bundle
- A snapshot interrupted by `kill -9` is retaken at the next schedule without operator action; the stuck row is visible as `Failed (interrupted)`.
- Every completed module snapshot reaches `Passed`/`Failed` verification within an hour.
- `/monitoring` shows an incident list with open/resolved timestamps; the bell badge counts open incidents (and the link works).
- Deploy-log table stops growing unbounded (retention proof test with fake clock).
- Alert email for a failed backup arrives even if the first SMTP attempt 5xx'd (one retry), and the alert row shows delivery state.
