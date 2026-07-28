# 11 — Migration Plan (low-risk, staged)

Goal: evolve Harbora without breaking existing installs. Principles: **additive-first**,
**data-preserving**, **backward-compatible APIs**, **backup before every risky step**, and a
documented **rollback** for each change (see doc 14). No "big-bang" rewrite; small, reviewable,
atomic changes on a dedicated `overhaul` branch, each keeping Build + Test + Lint green.

## 1. Compatibility commitments

- **Data:** all schema changes are EF migrations, forward-only, with data-preserving backfills.
  Destructive column/table removals happen only after a deprecation window and a shipped backfill.
- **API:** the existing `/api/v1` shapes are preserved. New capabilities are new endpoints/fields;
  no field is repurposed. If a breaking change is unavoidable, ship `/api/v2` alongside `/v1` with a
  deprecation notice, not a silent change.
- **CLI:** existing commands keep working; new flags are additive.
- **Config/`.env`:** existing keys keep working; new keys have safe defaults (except the master key,
  which becomes required in Production — see below, with a clear migration message).
- **Routes/URLs:** existing panel routes keep resolving (add redirects if IA moves a page).

## 2. Migration workstreams

### 2.1 Database (EF Core)
- Each domain change = one additive migration. Examples anticipated by the ADRs:
  - Deployment state machine (ADR-004): add state-timestamp columns, `OwnerToken`, `Reason`;
    backfill existing rows to a terminal state (`Live`/`Failed`) from current `Status`.
  - Durable jobs (ADR-005): new `Job` table (no change to existing tables).
  - Host-port allocation (ADR-008): new `HostPortAllocation`; backfill from apps'
    `PublishedHostPort`.
  - Health probes (ADR-007): additive nullable app fields defaulted from the existing health path.
  - Audit coverage: keep `AuditLog`; add indexes; no shape change.
- **Rule:** every migration is tested on a **restored copy** of a seeded DB before merge; a
  pre-migration backup is mandatory in the release runbook (doc 14).
- **Down-migrations** provided where safe; where not (data-lossy), the rollback path is
  restore-from-backup (documented per release).

### 2.2 "Claimed-but-missing" features (honesty pass)
- Short term: **gate** Compose/Static/Template source options in the UI/README behind a feature
  flag until implemented (removes the false claim without deleting code).
- Then implement per ADR-014 and flip the flag on. No user ever sees a control that throws.

### 2.3 Security defaults
- Master key fail-closed (ADR-009): ship as a **startup check** with a precise remediation message.
  Installer users already have the key; document a one-line set for manual installs. Provide a
  migration note in the release. This is the one intentional "breaking" default — justified, small,
  and pre-announced.

### 2.4 Frontend build
- Ensure CI builds the Vue bundle; keep the manifest-missing fallback so a bad build degrades
  gracefully rather than breaking the shell. No user-facing migration.

### 2.5 Multi-server agents
- Agent API changes (if any) are versioned; the panel negotiates and tolerates an older agent for
  one minor version, prompting an agent update in the UI. Never hard-break a connected node silently.

## 3. Per-change checklist (applied to every migration)

1. Write/adjust tests first (characterization or new) — must fail before, pass after.
2. Additive migration + backfill; provide down-migration or document restore path.
3. Feature-flag risky behavior; default off until verified on a Docker host.
4. Update docs (README + relevant overhaul doc) in the same PR.
5. Verify Build + Test + Lint green; verify a real deploy smoke test where runtime is touched.
6. Record in `progress.md`: what/why/tests/result/rollback.

## 4. Breaking-change protocol (rare)

Only with explicit sign-off (per the brief's "stop and ask" rules) for: wide data/feature deletion,
a breaking change without a safe migration, a fundamental stack change, significant new external
cost, or irreversible/production-credential operations. For anything reversible and local, make the
best engineering call, record the rationale, and proceed.

## 5. Sequencing (safe order)

Baseline → tests/CI → security fail-closed → state-machine + reconciler (additive) → durable jobs →
zero-downtime cutover + artifact rollback → compose/template/static (flag-gated → on) → domains/SSL
polish → logs/history → DB provisioning verify → backups/restore hardening → monitoring depth →
multi-server port-allocation → RBAC/audit → CLI/API/OpenAPI → templates/marketplace → previews
(later). Each step is independently shippable and reversible. Detailed phases in doc 12.
