# 00 — Executive Summary

**Harbora product audit · 2026-08-07 · master @ `8b1f6e9` · v0.2.0**
Full evidence in docs 01–20 + `backlog.json` (54 items). Security review explicitly out of scope (separate process).

## State of the product, honestly
Harbora is a real, unusually well-engineered self-hosted multi-tenant PaaS — not a demo. **Build: clean (0 errors/0 warnings). Tests: 3,141 pass, 0 fail** (17 honest Docker-gated skips locally; CI runs them). Live-host verification has covered install→deploy→domains→ACME→managed DBs→backup/restore→multi-server (legacy agent). The strongest assets: the deployment pipeline with a real state machine and zero-downtime cutover; the Node Agent v1 protocol (versioned contract + conformance tests + durable outbox/ledger + self-update-with-rollback — best-in-class for the category); upgrade safety (pre-migration dump that refuses to proceed on failure); genuine multi-tenancy (query filters, plans, quotas, metering, capacity scheduler); a disciplined bilingual RTL UI that never prints a fake zero; and a panel-down recovery CLI.

## The gap: truth under failure, not features
Six defects separate this from "trust it with production": **(1)** all background work runs strictly serially — one tenant's build blocks every deploy/backup platform-wide; **(2)** a crash mid-backup permanently blocks that target's future backups with no remedy; **(3)** a failed Traefik apply still reports the deployment `Succeeded`; **(4)** backups/cleanup bypass the multi-server engine seam — node-hosted volumes back up the wrong host; **(5)** Node Agent v1 cannot be enrolled on a fresh install (installer never writes the required config; referenced `node-ca` command doesn't exist); **(6)** no job timeout/retry — a hung build freezes the queue forever. Add a documentation-truth debt (four contradictory roadmap numbering schemes; README/RUNBOOK claims the code contradicts) that violates the project's own "honest software" principle.

## Top-5 problems (detail: doc 03)
1. Serial job queue + no timeout/retry (R-01/R-06) — multi-tenant credibility.
2. Backup crash-lock + missing module verification/safety-copy (R-02/R-10/R-11) — backup trust.
3. False-success on proxy failure (R-03) — deployment honesty.
4. Wrong-host backups & local-only cleanup on multi-node (R-04) — data loss class.
5. Node v1 activation chain broken out of the box (R-05) — flagship feature dead on arrival.

## Top-5 next capabilities (detail: docs 05/09/10)
1. **Notification system** (in-app center + retried/deduped/bilingual delivery on the existing durable queue) — doc 09.
2. **PR preview environments** — pieces already in the code; highest wow-per-effort.
3. **Customer email: BYO-provider + local ingest relay + Dev Inbox** (category-first) — docs 10/11.
4. **/activity job view + queue transparency/cancel** — makes every long operation observable.
5. **API/CLI expansion + OpenAPI** — full headless lifecycle.

## Recommended first phase (doc 17, Phase 1)
Deploy-engine truth & queue: worker pool with per-target locks, per-kind timeouts + selective retry, proxy-failure ⇒ `Failed` (+through-proxy probe), backup crash reconciler + DB-level uniqueness, engine-factory seam fix, cron network parity, boot-race fix, queue position/cancel, SFTP-form and dead-link/icon fixes. Exit gate: two-tenant demo (A builds 10 min, B deploys <1 min) + kill-mid-backup drill + proxy-failure honesty test — all in CI.

## Decisions taken in this audit
- **Architecture:** stay a modular monolith; no rewrite; extract only along existing seams when touched (doc 13).
- **Backups:** converge on the module architecture but only after it inherits legacy's proven safety behaviors; no default flip before live-host proof (doc 08).
- **Notifications before email; email = hybrid BYO-first with SES-style sandboxing later; mailbox hosting never in core** (Stalwart template / hosted integration instead) — docs 09/10/11.
- **Simple/Advanced:** deepen the shipped `PanelMode` fold discipline; no second panel (doc 12 §4).
- Roadmap = doc 17 (supersedes the four legacy schemes); protected behaviors = doc 19; open decisions = doc 18 (14 questions with defaults).

## Deliverables of this audit
`docs/product-audit/00…20` (21 documents), `backlog.json` (54 prioritized, machine-readable items with acceptance criteria/tests/evidence), each phase scoped for hand-off to an implementation agent.
