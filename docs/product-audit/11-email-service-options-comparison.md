# 11 — Email Service Options Comparison (+ product landscape)

Research method: web-verified 2026-08-06/07 (source domains noted per section by the research pass); items not re-verifiable marked *knowledge-based*. Full raw notes retained in the audit working record; this file keeps decision-relevant conclusions.

## Part 1 — Delivery-model options for Harbora's customer email service

### Option A — Thin layer over existing providers (API adapters)
Harbora UI/SDK wraps Resend/Postmark/SendGrid/Mailgun/SES/Brevo accounts owned by the tenant.
- ✅ Zero deliverability liability; fastest to ship; each provider's strength inherited.
- ✅ Provider diversity = resilience story.
- ❌ Tenant still needs a provider account (signup friction); free tiers shrinking (SendGrid free tier retired 2025; Fly/Render-style pricing churn everywhere).
- ❌ Per-provider webhook/format drift = adapter maintenance.

### Option B — Bring Your Own SMTP
Tenant pastes any SMTP credentials; Harbora manages secret, injection, log.
- ✅ Cheapest; works with corporate/self-hosted servers; no account coupling.
- ✅ With the local-ingest-relay design (10 §3) we still get logs/limits/rotation.
- ❌ No delivery events (bounce/complaint invisible over bare SMTP); quality varies wildly.

### Option C — Harbora-managed relay (own infrastructure)
Harbora operates outbound MTA + IPs.
- ❌ Operationally the hardest thing in this document: dual IP+domain reputation warming over weeks; Gmail (since Nov 2025) and Microsoft (May 2025) hard-reject non-compliant senders; <0.3 % complaint requirement; FBL registration; blocklist monitoring; cloud IP ranges pre-poisoned for port 25 (sources: powerdmarc.com, smtp2go.com, suped.com, securityboulevard.com — accessed 2026-08-07).
- ❌ One abusive tenant burns the shared IP for everyone → mandatory sandbox/production-gate machinery.
- ✅ Only option giving "email included, zero signup" UX and margin.
- Realistic form: **relay through an upstream you pay** (SES at $0.10/1k) rather than raw port-25 MTA — this is exactly what Resend proved viable (Resend ≈ SES × ~4 price, redesigned DX; resend.com, tiergauge.com).

### Option D — Hybrid (recommended)
Phase 1 = A + B behind one abstraction with Harbora-local credentials + ingest listener + Dev Inbox; Phase 2 = C-lite (operator-owned SES-class upstream as "Harbora relay" option with SES-style tenant sandboxing). Matches 10 §1/§5. **Mission's proposed phase order confirmed** with two amendments: Dev Inbox promoted to Phase 1; template engine demoted to Phase 2.

## Part 2 — What to copy from each email product (accessed 2026-08-06/07)

| Product | Copy | Avoid |
|---|---|---|
| **Resend** | Domain-verification UX (provider-detected DNS instructions, live re-check); two-level API keys (full vs send-only) | 100/day cap shape on a monthly tier (breaks password-reset storms); pricing opacity above mid-tier |
| **Postmark** | Message Streams (transactional ≠ broadcast, first-class); 45-day full-content searchable activity log = the delivery-log gold standard | silent auto plan migrations; strictness without tenant-facing diagnostics |
| **SendGrid** | Subuser model as the reference for per-tenant reputation/quota partitioning | free-tier rug-pull; paywalling deliverability diagnostics; shared-IP decay (61–77 % inbox placement reports) |
| **Mailgun** | Event-level logs w/ structured filtering; message tagging → per-tenant analytics | selling deliverability visibility as a separate SKU |
| **Amazon SES** | Sandbox → production-access-request lifecycle (the abuse gate a multi-tenant PaaS must have); cheapest upstream ($0.10/1k) | exposing raw SES complexity (config sets/SNS/IAM) to end users — wrap, never proxy |
| **Brevo** | Forever-free daily allowance as adoption engine; guided SPF/DKIM/DMARC setup | mixing marketing+transactional on shared infra (5–15 s latency vs Postmark 1–3 s) |
| **Mailtrap** | **Dev Inbox as a product category** — capture staging email, preview HTML/headers/spam score; whitelisted-recipient escape hatch | splitting testing and sending into separately-priced products |

**Mailbox-hosting research conclusion** (sumguy.com, selfhosting.sh, privacyguides.org, hostmap.io — 2026-08-07): Stalwart (Rust, single binary, 512 MB, IMAP+JMAP+SMTP; Enterprise ed. early 2026) = best embeddable candidate as a *template*, pair with Roundcube; Mailcow too heavy per-tenant (15+ containers, 2–4 GB); Migadu-class hosted API = resale option without infrastructure. Unanimous 2025-26 conclusion: *the MTA software is the easy part; reputation is the product* → grounds the "no mailbox hosting in core" call (10 §6).

## Part 3 — PaaS landscape takeaways (for docs 05/12/17; full per-product notes from research pass)

| Product | Single best idea for Harbora | Explicitly do not copy |
|---|---|---|
| Railway (railway.com, 2026-08-06) | Project canvas + `${{Postgres.DATABASE_URL}}` reference variables | metered billing complexity; free-tier whiplash |
| Render (render.com) | `render.yaml` Blueprints → repo-committed IaC; PR previews | two-layer pricing; bandwidth-cut reprice (Apr 2026) |
| Heroku (devcenter.heroku.com) | attach-resource ⇒ env vars appear (Harbora already has it — market it) | generation splits (Cedar/Fir) breaking Docker/monorepos; sustaining-mode drift (Jun 2026: no new Enterprise) |
| Fly.io (fly.io) | `fly launch` config scaffolding; health-gated deploys | GPU adventure ("We Were Wrong About GPUs", shutdown Jul 2026); region pricing matrices |
| DO App Platform | clear component taxonomy (web/worker/job/static) | per-component billing sprawl; no-volumes/no-SSH limits are Harbora's ready-made comparison ad |
| Coolify (coolify.io; v4 stable Apr 2026) | $5/mo hosted-control-plane monetization shape; notification breadth | 280-template treadmill; dual-proxy (Traefik+Caddy) config surface |
| CapRover | "remove us and your apps keep running" no-lock-in messaging (true for Harbora too — say it) | Swarm-only architecture bet |
| Dokploy (34k★ May 2026) | dashboard-driven node join; build-on-manager/run-on-workers pattern | control-plane-entangled-with-workloads default |
| Vercel | PR preview URLs as the collaboration primitive; instant rollback | SKU explosion; serverless runtime in a container PaaS |
| Portainer | escape hatch: one click to raw container inspect/exec from any managed app | licensing churn; multi-orchestrator QA matrix |

**Macro:** 2025-26 cloud-PaaS pricing/trust erosion (Heroku sustaining mode, Render/Fly repricing, SendGrid free-tier kill) is a tailwind for self-hosted; Coolify/Dokploy own mindshare but not multi-tenancy — **Harbora's structural differentiators are real multi-tenancy + provider console + the honesty discipline** ("unmeasured ≠ zero", refusal-with-reason patterns). Universal anti-pattern: metered multi-SKU billing and free-tier rug-pulls.
