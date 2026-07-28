# 14 — Release & Rollback Plan

How Harbora ships changes safely to real self-hosted installs, and how any release can be undone.
Pairs with the migration plan (doc 11) and CI gates (doc 13).

## 1. Release principles

- **Every release is reversible** — either by re-deploying the previous version (code) or by
  restoring a pre-release backup (data).
- **Backup before change** — the upgrade flow snapshots data first and aborts if the snapshot fails.
- **No forced breaking changes** — breaking changes are pre-announced, migration-guided, and gated
  (doc 11 §4).
- **Verify after deploy** — the installer already verifies Traefik↔Docker, the panel route, and SSL;
  releases extend this with a post-upgrade health check.

## 2. Versioning & branches

- **SemVer** (`MAJOR.MINOR.PATCH`). Breaking data/API changes bump MAJOR and require a migration
  guide. New capabilities bump MINOR. Fixes bump PATCH.
- Branches: `overhaul` (integration) → `master` (release) with tags `vX.Y.Z`. CI must be green to
  merge; tags trigger the release build (panel image + CLI binaries, per the existing workflow,
  extended to the solution).
- Each release has notes: what changed, migrations included, new required config (if any), and the
  rollback procedure for *this* release.

## 3. Release channels

- **stable** (tagged releases; default for `install.sh`).
- **edge** (latest `overhaul` build; opt-in via `REPO_BRANCH`) for testers.
- The installer already supports `install | update | uninstall`; add a channel selector (env) and a
  version pin so an operator can stay on / return to a known-good tag.

## 4. Upgrade flow (operator-facing)

`install.sh update`:
1. Fetch the target version (tag or branch); **verify integrity** (checksums/signature — doc 10
   §2.16).
2. **Automatic pre-upgrade backup** (full platform); abort if it fails.
3. Apply EF migrations (forward-only, tested; doc 11). Long backfills run batched.
4. Rebuild + restart the panel; keep Traefik/Postgres/Redis running (no data loss).
5. **Post-upgrade verification:** panel `/healthz` via Traefik, migration success, a synthetic
   health check; on failure, print the precise remediation and the rollback command.
6. Report success with the new version + a link to release notes.

Idempotent and safe to re-run; existing `.env`/secrets are never overwritten.

## 5. Rollback procedures

**Code rollback (no data change):**
- `install.sh update` pinned to the previous tag (`HARBORA_VERSION=vX.Y.(Z-1)`), or
  `git checkout` the previous tag in `/opt/harbora/app` + `docker compose up -d --build`. Traffic
  returns to the prior panel; app containers are unaffected.

**Data rollback (migration/data issue):**
- Restore the **pre-upgrade backup** (Backups → Restore, typed confirm; dry-run first). Because
  migrations are forward-only, undoing a schema change = restore-from-backup unless a safe
  down-migration shipped with the release (noted per release).

**Deployment rollback (app level):** the product feature — one-click **artifact rollback** to the
prior Live revision (ADR-006), instant, no rebuild.

**Config/proxy rollback:** Traefik apply is atomic with `.bak` restore; a bad routing change
auto-rolls-back the file (as-built).

**Feature-flag rollback:** risky new behavior (state machine, cutover, compose) ships behind flags;
turning a flag off reverts to prior behavior without a redeploy.

## 6. Pre-release checklist

- [ ] CI green (build + all test tiers + frontend bundle) on the release commit.
- [ ] Real E2E smoke on a Docker host: install → deploy (image+git) → domain+SSL → logs → DB →
      backup → **restore** → rollback (recorded in `progress.md`).
- [ ] Migrations tested on a **restored copy** of a realistic DB; backfills verified.
- [ ] New required config (e.g., master key) documented with a migration note.
- [ ] Release notes written incl. this release's rollback steps.
- [ ] Integrity artifacts (checksums/signature) produced for panel image + CLI.
- [ ] `docs` + README updated in lockstep; no advertised-but-unimplemented feature.

## 7. Post-release monitoring

- Watch deploy-failure and app-crash alerts across test installs after an edge/stable push.
- Provide an easy "report a problem" path (release notes link) and a documented downgrade.
- Track that no deployment can be left in a non-terminal state (reconciler metric) and that backups
  continue to run post-upgrade.

## 8. Incident response (operator)

1. Contain: roll back code to the previous tag (§5). 2. If data is affected: restore the pre-upgrade
backup (dry-run → confirm). 3. Verify via the health checks. 4. Capture logs + the failed
deployment/audit trail. 5. File the issue with the release version + steps. The product's job is to
make steps 1–3 fast, obvious, and safe — which is the whole point of the overhaul.
