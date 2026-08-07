# 04 — User Personas & Journeys

Grounded in the shipped product (code + tutorial), not aspiration. The earlier vision doc (`docs/overhaul/04-product-vision.md`) named the same four personas; this file re-validates them against what exists and maps each journey to real screens, noting friction found in 12.

## Personas

### P1 — Solo developer / indie hacker ("Sara")
Owns one VPS. Wants Heroku-feel on her own hardware. Uses Simple mode, CLI push, nip.io first, real domain later.
- **Cares about:** first deploy < 10 min, honest status, rollback that works, not thinking about Docker/Traefik.
- **Today's fit:** strong — install → `/setup` → dashboard checklist → New App → deploy → domain. CLI `login`/`init`/`deploy` covers her loop.
- **Top friction:** password reset/invites dead until SMTP configured (no nudge); backups not part of onboarding; deployments page unfiltered; no build timeout means a stuck build looks like "forever deploying" until she finds the log.

### P2 — Agency operator ("Rahim", 2–10 devs, 5–30 client apps)
Runs staging+production per client, several nodes, needs roles and audit.
- **Cares about:** projects/environments, per-project grants, clone environment, per-client isolation, backup proof.
- **Today's fit:** good — projects/environments/clone/grants/audit exist; Operator role exists.
- **Top friction:** node v1 activation chain (R-05); volume backups of node-hosted apps silently wrong (R-04); no per-app access; alert rules per-workspace w/o edit; no digest/quiet hours so alert email fatigue.

### P3 — Small business internal platform ("Fatemeh", IT-of-one)
Hosts n8n/WordPress-class ready apps + one or two custom services.
- **Cares about:** one-click templates with versions, backup/restore she can trust, "is everything OK" dashboard.
- **Today's fit:** ready-app catalogue with pinned digests + attention strip serve her; Adminer for quick DB looks.
- **Top friction:** template catalogue breadth (8 apps); multi-service template deploy-as-unit ambiguity; restore preview only in the off-by-default module; no screenshots/backup notes per template.

### P4 — Provider / reseller ("Hosting shop")
Sells quota-limited workspaces on own fleet — Harbora's differentiator vs Coolify/Dokploy.
- **Cares about:** plans/quotas/metering, tenant isolation, suspend/resume, per-tenant usage exports, capacity view.
- **Today's fit:** genuinely differentiated — plans, scheduler, per-tenant networks, GB-h/vCPU-h metering + CSV, provider console.
- **Top friction:** serial job queue = cross-tenant head-of-line blocking (R-01) — the single biggest threat to selling this; no invoicing/credit ledger (accepted for now); usage meters ignore managed services/volumes/buckets; no per-tenant email/notification branding.

### P5 — Platform operator (any of the above wearing the ops hat)
Runs updates, reads `harbora doctor`, restores from `pre-upgrade-*`.
- **Today's fit:** the strongest part of the product (see 19 — do-not-change list).
- **Friction:** RUNBOOK drift; recovery paths hardcode a volume path (R-42).

## Key journeys (as shipped, with friction points)

### J1 First-run → first app live (P1)
`install.sh` (DNS test, SSL verify) → `/setup` (single long form — 🎭 no steps/progress) → dashboard checklist (4 steps, persisted) → New App (source cards, progressive disclosure) → deploy watch (5-step bar + live logs) → open domain.
**Verdict:** competitive with Railway/Coolify onboarding. Gaps: checklist stops before backups; no "test deployment" button; SMTP not in checklist though two flows depend on it.

### J2 Code change → production (P1/P2)
Git push → HMAC webhook → auto deploy → health gate → cutover; or `harbora deploy` from a laptop.
**Gaps:** no PR preview URL journey (preview envs half-built); no deploy cancel from CLI; no queue position feedback when another tenant's build holds the serial worker (R-01 perception issue: "Queued" with no explanation).

### J3 Attach a database (P1/P3)
Databases → engine card → version/size/environment → create (job) → attach to app → prefixed env vars appear → redeploy.
**Gaps:** provisioning failure is silent (R-15); rotation doesn't roll the app (R-29); no connection snippets per language.

### J4 Add a custom domain (P1)
App → Domains → add → live DNS check row → cert on first hit → status dot.
**Verdict:** best-in-class honesty (real handshake, per-row tests). Gap: Cloudflare-proxied guidance absent.

### J5 "Is everything OK?" morning check (P2/P3)
Dashboard attention strip → Monitoring (host cards, disk banner, app health, SSL list, alert delivery status).
**Gaps:** bell 404 (R-17); no incident timeline; no uptime/restart metrics; alerts not editable.

### J6 Restore after a bad deploy (P1)
Deployments → Details → Rollback → confirm page (shows target commit/image, refuses when pruned).
**Verdict:** solid and honest. Gap: rollback reach = 5 images and not surfaced until refusal.

### J7 Disaster drill (P5)
`harbora backups` → `restore-db` (typed DB name, auto pre-restore dump) — works panel-down.
**Gap:** app-data (volume) restore drill has no equivalent one-liner; module restore lacks safety copy (R-11).

### J8 Onboard a customer (P4)
Plans → create plan → Tenants → new tenant + plan → invite user (email/temp password) → customer sees quota'd workspace.
**Gaps:** stale "next phase" copy on Tenants (R-listed); invite requires SMTP with no inline warning until submit; no welcome email branding.

## Journey coverage summary

| Journey | Screens exist | Happy path works | Honest failure states | Molasses points |
|---|---|---|---|---|
| J1 install→live | ✅ | ✅ (verified live per progress.md) | ✅ | setup form UX, SMTP nudge |
| J2 change→prod | ✅ | ✅ | ✅ | queue opacity, no previews |
| J3 database | ✅ | ✅ | 🟡 silent provision failure | snippets |
| J4 domain | ✅ | ✅ | ✅ | CF guidance |
| J5 health check | ✅ | ✅ | 🟡 bell 404 | no timeline |
| J6 rollback | ✅ | ✅ | ✅ | retention opacity |
| J7 DR drill | 🟡 | ✅ DB / 🟡 volumes | 🟡 | module safety copy |
| J8 tenant onboarding | ✅ | ✅ | 🟡 | copy drift, SMTP dependency |
