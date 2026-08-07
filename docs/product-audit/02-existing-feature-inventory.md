# 02 — Existing Feature Inventory

**Status legend** (per mission vocabulary): ✅ Complete & usable · 🟡 Present but incomplete · 🔧 Backend-only · 🎨 UI-only · ⬜ Placeholder · 🏗 Architectural issue · 🎭 UX issue · ⚙️ Operational issue · ❌ Missing · ❓ Needs further investigation.
Statuses can combine (e.g. ✅⚙️ = works but has an operational hazard). Evidence: file references; see 01/03/12 for details.

---

## 1. User, Workspace & Team

| Feature | Status | Evidence / note |
|---|---|---|
| Registration (self-serve) | ❌ | By design: users are created by admins/tenant invites; first-run `/setup` creates the owner only |
| Login (+ TOTP 2FA challenge, recovery codes) | ✅ | `AccountController`, `Totp.cshtml`; admin can reset 2FA |
| Email verification | ❌ | No verification flow exists |
| Forgot / reset password | ✅ | Requires SMTP configured; anti-enumeration copy; 1-h single-use token |
| Change password / profile prefs | ✅ | Settings page: password, culture, theme, panel mode |
| Workspace (tenant boundary) | ✅ | `Workspace` + global query filters (24 filtered entities) |
| Organization above workspace | ❌ | Single level only |
| Invite members (email + temp password + role) | ✅ | `UsersController.Invite`; refuses without SMTP |
| Roles | ✅ | System: Owner/Admin/Member/Viewer/Operator; workspace: Admin/Member/Viewer; deny-by-default matrix (`RolePermissions.cs`) |
| App-level access | ❌ | Not modeled |
| Environment-level access | 🟡 | `ProjectGrant` = project + optional environment + role; intersects workspace role; no per-app granularity |
| Ownership transfer | ❌ | Only `harbora make-owner` recovery command |
| Remove / suspend member | ✅ | Suspend/restore in Users UI; tenant suspend in provider console |
| Activity history | 🟡 | `AuditLog` (append-only, filterable, CSV export) — platform-wide, not per-user timeline; unbounded growth ⚙️ |
| Personal Access Token | ✅ | `ApiToken` (hashed, prefix, scopes, expiry, revoke) |
| Service account | ❌ | Tokens are user-bound |
| Multiple workspaces per user | ✅ | Switcher in topbar |

## 2. Node & Agent

| Feature | Status | Evidence / note |
|---|---|---|
| One-line install | ✅ | `deploy/node-agent/install.sh` — checksum-verified, bilingual, ARM64+AMD64 |
| Enrollment (single-use token → mTLS) | ✅ | Token shredded after use; terminal-vs-retryable error taxonomy |
| Online/offline display, heartbeat | ✅⚙️ | 30 s heartbeat + `NodeHeartbeatMonitor`; but `ActiveDatabaseGrants`/`ActiveTunnels` always 0 (never populated — `NodeAgentWorker.cs:404-417`) |
| Agent version display + minimum-version gating | ✅ | Per-command `AgentTooOld` refusal |
| Agent upgrade (panel-triggered, SHA-256, rollback) | ✅ | Marker-before-swap; post-restart adjudication; `signatureBase64` in contract but unverified 🟡 |
| CPU/RAM/Disk/Network status | 🟡 | Host metrics via panel poll; node self-metrics on loopback :9701 Prometheus endpoint (not scraped by panel) |
| Capacity model | ✅ | Committed-vs-total with `ReservedMemoryRatio`, `CpuOvercommitFactor` |
| Labels / region / environment hints | ✅ | Enrollment flags |
| Node groups / pools | 🟡 | `Server.Pool` + `Plan.NodePool` exist; no dedicated group management UI |
| Automatic node selection | 🟡🏗 | `SchedulerService.PlaceAsync` — checked at create/resize only; **redeploy never re-checks capacity** |
| Manual node selection | ✅ | App/database scheduling onto a node |
| Maintenance / drain mode | ✅ | `DrainNode` verb: refuses mutating verbs, persisted across restarts |
| Diagnostics | 🟡 | Node detail shows commands/events; local `/healthz`; 7 declared event kinds never published 🔧 |
| Reconnect / resume | ✅ | Durable outbox (500 frames), resume tokens, full-jitter backoff |
| Delete / revoke node | ✅ | Revoke + uninstaller with typed confirmations |
| Move application between nodes | ❌ | No migration flow |
| AMD64 / ARM64 | ✅ | Enforced at install, enrollment, and manifest levels |
| Agent↔CP compatibility | ✅ | Protocol negotiation + capability advertisement + conformance tests |
| **Out-of-box activation** | ⚙️ | `install.sh` never writes `NodeAgent__PublicUrl`; Traefik template hardcodes `panel.example.com`; referenced `node-ca` command doesn't exist |
| Legacy HTTP agent | 🟡🏗 | Works; not deprecated; zero tests; RUNBOOK still teaches it |

## 3. Application & Environment

| Feature | Status |
|---|---|
| Create application (5 sources + template) | ✅ — progressive-disclosure form |
| Projects / environments (prod auto-created) | ✅ — but `App.EnvironmentId` still nullable mid-transition 🏗 |
| Clone environment (with pre-flight plan) | ✅ — deliberately skips domains/volume contents/DB passwords |
| Preview environments (per-branch) | 🟡 — pieces exist, lifecycle incomplete (progress.md's own admission) |
| Duplicate application | ❌ |
| Archive application | ❌ (delete only, with typed confirm + volume choice) |
| Env vars + secrets (encrypted, build-time flag) | ✅ — but `availableAtBuild` not settable from UI form 🔧 |
| Variable groups / import-export | ❌ |
| App settings (limits, restart policy, health path) | ✅ |
| App activity | 🟡 — deployment history yes; unified activity feed no |
| Maintenance mode (app-level) | ❌ |
| Resource limits (instance sizes nano→large) | ✅ — copied onto app at creation |
| Restart policy | ✅ (`unless-stopped` default; contract supports all modes) |
| Transfer app between workspaces | ❌ |
| Web terminal | ✅ — feature-flagged off by default; local apps only; audited; idle/absolute timeouts |
| Volume file browser + editor | ✅ (≤512 KB edit, 32 MB upload) |

## 4. Source & Build

| Feature | Status |
|---|---|
| GitHub / GitLab / Gitea (token + OAuth) | ✅ · Bitbucket: enum value exists, no OAuth/UI path ❓ |
| Private repos | ✅ (encrypted credentials) |
| Docker image (+ custom tag pinned to digest) | ✅ |
| Private registry | 🟡 — image pull credentials not modeled as first-class registries ❓ |
| Dockerfile / build context | ✅ |
| Buildpacks (Node/.NET/Go/PHP/Python/static) | ✅ |
| Nixpacks-class detection | 🟡 — own generator, narrower ecosystem |
| Monorepo support | 🟡 — Dockerfile path + context; no per-app subdir watch filters |
| Build args | ❌ (not exposed) |
| Cache | 🟡 — Docker layer cache only; buildkit cache never pruned ⚙️ |
| Branch / commit deploys, Auto-deploy on push/tag | ✅ — HMAC webhooks (GitHub/Gitea) + token header (GitLab) |
| Deploy hook URL | ✅ (per-repo webhook + secret rotate) |
| Build log (live) | ✅ SignalR + fallback |
| Build timeout | ❌ — **no job-level timeout**; a hung build blocks the serial queue ⚙️ |
| Cancel build | 🟡 — works in-process; cross-instance cancellation unimplemented; CLI cannot cancel |
| Build history + artifact retention | ✅ (retention = 5 images) |
| Multi-arch build | ❌ — builds happen on the panel host's arch only; v1 nodes can't build at all |
| CLI push-from-machine | ✅ — one of the strongest flows |

## 5. Deployment

| Feature | Status |
|---|---|
| History, statuses, progress bar (5 steps) | ✅ — deployments list has no filters/pagination 🎭 |
| Cancel | 🟡 (see build cancel) |
| Retry | ❌ — no retry of failed deployments (manual redeploy only) |
| Redeploy / Rollback | ✅ — artifact rollback, double pre-flight, pruned-artifact refusal explained in UI |
| Queue | ✅⚙️ — durable but strictly serial platform-wide |
| Deployment lock / conflicting-deploy prevention | ✅ — coalescing + serial worker (TOCTOU window exists, benign) |
| Rolling / multi-replica strategies | ❌ — single container per app (replica count absent) |
| Recreate + zero-downtime cutover | ✅ — versioned names, cutover-then-retire |
| Health & readiness gates | ✅ |
| Release info (commit, author, config diff) | ✅ — diff limited to consecutive deployments |
| Failure diagnostics | ✅ — taxonomy + 30-line tail + optional AI explain (preview-then-send) |
| Cleanup / retention | ✅ images ·  ❌ deployment-log rows (unbounded) ⚙️ |

## 6. Domain & SSL (operational view)

| Feature | Status |
|---|---|
| Add domain, multiple domains per app | ✅ |
| DNS status detection + guidance | ✅ — live per-row A-record check, required-record rail |
| Root domain / auto subdomain (`{slug}.{root}`, nip.io default) | ✅ |
| Wildcard domains | 🟡 — listed in UI rail; certs would need DNS-01 which isn't implemented |
| Redirect / WWW redirect / Force HTTPS | ✅ (route middlewares) |
| SSL issue + renew | ✅ — fully delegated to Traefik ACME HTTP-01 |
| Cert status display + expiry warning | ✅ — daily real-handshake watcher, 14-day `SslExpiring` alert |
| Custom certificate upload | ❌ |
| Cloudflare compatibility guidance | ❌ (no docs/detection for orange-cloud + HTTP-01 interplay) ❓ |
| WebSocket | ✅ (Traefik default + explicit flushInterval for node channel) |
| gRPC | ❓ — no h2c/scheme option surfaced |
| Real client IP | 🟡 — `UseForwardedHeaders` + trusted-proxy setup for the panel; per-app guidance absent |
| DNS/SSL self-troubleshooting for users | ✅ — test buttons + failure copy |

## 7. Networking

| Feature | Status |
|---|---|
| Internal network (per environment + per workspace) | ✅ — dual-attach transition still present 🏗 |
| Service discovery / internal hostname | ✅ — container-name DNS + internal-address table UI |
| Public/internal ports | ✅ (remote nodes: tracked host ports) |
| App↔App, App↔DB connectivity | ✅ — cross-environment connection rules explained in UI |
| Temporary public access for an app | ❌ (apps get domains; no ephemeral URL) |
| Temporary DB access | ✅ — TTL grants; **credential rotation broken (Fake client)** ⚙️; cross-node path unbuilt (contract only) |
| IP allowlist | ✅ (per app protection + per grant) |
| TCP service exposure (generic) | ❌ — only DB gateway + node tunnels |
| Custom proxy rules (visual designer) | ✅ — drag-drop, preview, validate, save |
| Basic auth on routes | ✅ |
| Network usage metrics | 🟡 — rx/tx per container; no per-network aggregation |
| Architecture/connection diagram | ✅ — real-state graph with accessible fallback |

## 8. Database Service

| Feature | Status |
|---|---|
| PG / MySQL / MariaDB / Redis / MongoDB (+ RabbitMQ, NATS brokers) | ✅ |
| Version selection (curated, admin-editable) | ✅ |
| Internal connection + connection string + reveal | ✅ |
| Credential management + rotation | ✅ internal rotation (apps not auto-redeployed 🟡) · external-grant rotation broken ⚙️ |
| Temporary external connection (TTL, IP allowlist) | ✅ local · ❌ node-hosted (TCP gateway not built) |
| Host/port display | ✅ |
| Backup / restore | ✅ legacy path (PG/MySQL/MariaDB verified via rehearsal) · Redis/Mongo dump refused with reasons 🟡 |
| Clone / import / export | ❌ (module snapshot browse ≠ logical export) |
| Storage usage, metrics | ✅ (measured, honest "not measured") |
| Upgrade version | ❌ — no engine-version upgrade flow |
| Restart / rebuild | ✅ |
| Database log | 🟡 — container logs only via app-style viewer? (DB details lacks log tab) ❓ |
| Admin tool | ✅ — throwaway Adminer (1-h TTL, PG/MySQL/MariaDB only) |
| Connection templates per language | ❌ |

## 9. Volume & Storage

| Feature | Status |
|---|---|
| Persistent volume + mount path (validated) | ✅ |
| Storage usage | ✅ (du-measured) |
| Resize | 🟡 — `SizeLimitBytes` exists; no resize UX w/ enforcement semantics ❓ |
| Backup / restore / snapshot | ✅ legacy engine (local-server only 🏗) · node `SnapshotVolume`/`RestoreVolume` verbs exist but panel never dispatches them 🔧 |
| Read-only mount | ✅ |
| Volume transfer between apps | ❌ |
| Orphan volume detection | ❌ ⚙️ |
| Accidental-deletion protection | 🟡 — `removeVolumes` defaults false; UI never sends `deleteData` on unmount 🔧; no protection flag |
| Attached-apps display | ✅ (via app page; DB removal names orphaned volume) |
| Retention after app delete | 🟡 — volumes survive unless checked; then invisible (no orphan list) |
| S3 buckets (MinIO) + object browser + quotas | ✅ — absent from any roadmap doc ❓ |

## 10. Backup & Restore

| Feature | Status |
|---|---|
| Manual + scheduled backups, retention | ✅ legacy (interval-based) · ✅ module (cron + timezone + tiered retention) |
| DB / volume / app-config / full-platform | ✅ legacy |
| Compression + encryption | ✅ (tar+gzip+AES-GCM; module DB dumps uncompressed for dedup) |
| Local / S3-compatible | ✅ · SFTP: backend yes, **UI form unreachable (template bug)** 🎨⚙️ · module: S3 only via native engine |
| MinIO / R2 / B2 | ✅ (any S3-compatible endpoint) |
| Telegram / Email copies | ✅ — size ceilings enforced with explicit refusal |
| Status, history, download | ✅ |
| Progress | ❌ backups (status only) · 🟡 restores (4 hard-coded checkpoints) |
| Retry | ❌ — no retry anywhere in backup paths |
| Restore preview (browse) | ✅ module (decrypts whole archive per level ⚙️) · ❌ legacy |
| Restore in place / to new app | 🟡 — in place only; app restore is capture-without-rebuild ❌ |
| Integrity verification | ✅ legacy (hourly, real rehearsal, "restorable?" column) · ⬜ module (handler never enqueued) |
| Failure notification | ✅ `BackupFailed` alert (11 module kinds collapse to 1) |
| Crash-consistency | ⚙️ **P0**: interrupted snapshot/restore rows never reconciled → target permanently blocked |
| Disaster recovery plan | 🟡 — pre-upgrade dumps + restore-db are solid; no documented full-DR runbook |

## 11. Logs, Metrics & Monitoring

| Feature | Status |
|---|---|
| Live deploy logs | ✅ SignalR + polling fallback |
| Runtime log search/filter/download | ✅ (substring + problems-only) |
| Historical log retention / size limits | ❌ deploy logs unbounded ⚙️ · runtime = Docker's own |
| CPU/RAM/Disk/Net + rollups (31 d / 365 d) | ✅ — `MetricRollups` table has **no index** ⚙️ |
| Restart count / response time / error rate / uptime | ❌ — explicitly not collected |
| Health status | ✅ (probe + reconciler) |
| Node metrics | ✅ host-level · agent Prometheus endpoint unscraped 🔧 |
| App / DB metrics | ✅ (vs own allocation; unmeasured ≠ zero) |
| Alert rules | 🟡 — 5 event toggles + per-app CPU/mem thresholds; **no edit/disable UI**, create+test+delete only |
| Incident timeline / firing-resolved lifecycle | ❌ — fire-and-forget messages, no state |
| Summary dashboard | ✅ (attention strip, stat tiles) |
| Time windows | ✅ 1h→30d |
| Simple vs advanced views | ✅ (PanelMode folds platform detail) |

## 12. App Marketplace & Templates

| Feature | Status |
|---|---|
| Real icons | ✅ 22 local SVGs with license provenance |
| Description / topology / trust panel | ✅ |
| Screenshots | ❌ |
| Categories + search + scope filters | ✅ |
| Version selection + recommended lifecycle | ✅ digest-pinned; deploy-time re-check |
| Required resources / variables / volumes | ✅ manifest-driven |
| Required database | ✅ single-service · **multi-service templates cannot require brokers** 🟡; multi-service deploy-as-unit contradicted between docs ❓ (code supports dependency provisioning per tests — verify on live host) |
| Domain requirement | ✅ |
| Default config | ✅ |
| Update path | 🟡 — template version update w/ migration warning on app page; no fleet-wide update flow |
| Template versioning/validation | ✅ + refusal re-check |
| Verified vs community | ✅ (platform-shipped vs workspace-owned + review queue) |
| Registry discovery | ✅ off-by-default drafts-only job |
| Backup instructions | ❌ per-template |
| Catalogue breadth | 🟡 8 ready apps vs Coolify's 280+ (deliberate curation opportunity) |

## 13. CLI & API

| Feature | Status |
|---|---|
| login / accounts (multi-panel) / whoami / status | ✅ |
| init (scaffold yml) / deploy (6 modes) / apps / logs | ✅ |
| Env vars, domains, restart, rollback, backup, restore, node, db via CLI | ❌ — API v1 has only 8 endpoints; ~35 node/backup/sync REST endpoints exist with no CLI consumer 🔧 |
| Readable output | ✅ Spectre tables |
| JSON output | ❌ |
| Non-interactive / CI mode | ✅ (`--server --token --no-follow`; all prompts TTY-gated) |
| Exit codes | 🟡 — deploy=1 on failure, but network errors in `apps`/`logs` escape as -1 |
| Progress | 🟡 — staged lines; no upload progress |
| Cancel | ❌ — Ctrl+C not honored in log-follow loop; no server-side cancel |
| API versioning | ✅ `/api/v1` + `/version` endpoint + update nudge |
| OpenAPI | ❌ panel API (node contract has openapi.yaml; module blocked on a tooling advisory) |
| Pagination | ❌ (list endpoints return all) |
| Error contract | ✅ documented status-code table (`docs/cli-deploy.md:281`) |
| Idempotency | ✅ `Idempotency-Key` on backup POSTs; ❌ on deploy endpoints |
| Docs + examples | ✅ `docs/cli-deploy.md` — but `examples/harbora.yml` advertises unsupported keys 🎭 |

## 14. Onboarding & Install

| Feature | Status |
|---|---|
| One-command install (interactive, bilingual) | ✅ — idempotent, DNS test, Traefik/SSL verification, nip.io zero-DNS default |
| First admin via `/setup` | ✅ — single long form, no steps 🎭 |
| First node connect | ✅ copy-paste enrollment · legacy path still documented ⚙️ |
| Docker / port checks | ✅ / 🟡 (ports warn-only) |
| Root domain + DNS + SSL guidance | ✅ |
| First app / first deploy | ✅ 60-second nginx smoke test documented |
| First backup nudge | ❌ |
| Setup checklist (resumable) | ✅ dashboard 4-step checklist w/ progress ring |
| Test deployment button | ❌ (manual smoke test only) |
| System health check | ✅ `harbora doctor` + install verification |
| Interactive troubleshooting | ✅ doctor names fixes; README symptom table |
| Contextual docs | 🟡 — tutorial exists (Persian, screenshots) but not linked contextually from pages |
| RUNBOOK accuracy | ⚙️ — drifted: missing S3/MinIO env vars, legacy agent, pre-projects flow |

## 15. Platform administration

| Feature | Status |
|---|---|
| Provider console (tenants, plans, quotas, sizes) | ✅ — incl. monthly usage + CSV export |
| Manual credit / billing | ❌ — metering basis only (GB-h, vCPU-h); no ledger |
| User management (roles, suspend, invite, 2FA reset) | ✅ |
| Feature flags | 🟡 — config-file flags (`Features:*`), not runtime toggles |
| Support tooling / incident mgmt / announcements / changelog | ❌ |
| Update check | ✅ opt-in, read-only notice |
| AI gateway admin (providers, models, plans, margin) | ✅ — never exercised against a real provider ❓ |
| Platform settings (SMTP + test, defaults, featured apps) | ✅ |

---

### Verified-live vs verified-by-tests caveat

Per `docs/overhaul/16-paas-strategy.md` and `progress.md`, real-host verification (on a VPS) covered: install, Git/static/compose deploys, domains+ACME, managed DB attach, backup/restore, multi-server with legacy agent. Node Agent v1, ingress tunnel, backup **module**, sync module, AI gateway and registry client are verified by tests/harnesses only — the docs themselves flag Sync/Kopia/compose-overlay as **never executed** (`docs/backup-sync/MERGE_GUIDE.md:345-381`). Treat those as "needs live proof" (roadmap R0 in 17).
