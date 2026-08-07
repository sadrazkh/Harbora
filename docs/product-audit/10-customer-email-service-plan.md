# 10 — Customer Email Service Plan (transactional email for tenant applications)

Distinct from 09 (Harbora's own notifications). This gives a tenant's *application* the ability to send transactional email via SMTP or HTTP API. Today **nothing** of this exists in code — greenfield subsystem; the comparison and provider-model decision live in 11.

## 1. Recommended model (summary of 11's analysis)
**Phase-1 = Bring-Your-Own-Provider (BYO SMTP + API providers) wrapped in a first-class Harbora experience. Phase-2 = optional Harbora-managed relay built on a reputable upstream (SES-class), with per-tenant sandboxing. Mailbox hosting: not in the core product (see §6).** This matches the mission's proposed sequence — confirmed sensible, with one amendment: ship the **Dev Inbox before the managed relay** (higher value, lower risk; no peer has it).

## 2. User experience (target)
App → **Email** tab:
1. Create Email Service (per environment; production/staging isolated by design).
2. Choose provider: BYO SMTP · BYO API provider (Resend/Postmark/SendGrid/Mailgun/SES/Brevo) · *Harbora relay (Phase 2)* · **Dev Inbox** (non-production default).
3. Sender domain + address; DNS guide (SPF/DKIM/DMARC records shown like the Domains page shows A records — same live-check UX, reusing `DomainInspector`-style TXT/CNAME lookups).
4. Credentials issued: SMTP host/port/user/pass (Harbora-local credential, see §3) or API key; revealed once; rotate button.
5. **Env-var injection on attach** — exactly like databases: `HARBORA_EMAIL_SMTP_HOST/PORT/USER/PASSWORD`, `HARBORA_EMAIL_FROM`, or `HARBORA_EMAIL_API_KEY/URL`. Reuse the attach pipeline; this is the differentiator vs "go paste from your provider".
6. Test email button; code snippets (.NET/Node/Python/PHP/Go) rendered beside the credentials.
7. Delivery log (per message: accepted/delivered/bounced/failed + provider reason), usage meter vs limit, suppression list view.
8. Swap provider without touching the app (credentials stay Harbora-local — §3).

## 3. Architecture
**Key decision: Harbora terminates SMTP locally even in BYO mode.** A small SMTP ingest listener (container beside the panel, like MinIO) accepts mail from tenant apps using Harbora-issued credentials, queues it, then relays via the configured provider (SMTP or API). Why: (a) apps never hold third-party creds → instant provider swap/rotation; (b) uniform delivery log + usage metering + rate limiting regardless of provider; (c) Dev Inbox is the same listener with a null relay; (d) suppression enforcement before the provider sees the message.
- Fallback mode "direct injection" (no listener): env vars carry the provider's own SMTP — zero-infrastructure option, loses log/limits; offered but not default.
- Queue/retry: the durable job queue again (`JobKind.EmailDelivery`), retry 3× with provider-aware backoff; bounce/complaint webhooks per provider adapter normalize into `EmailEvent`.
- Provider abstraction: `IEmailProvider { SendAsync, VerifyDomainAsync?, ParseWebhook }` with adapters: SmtpRelayProvider (generic), Resend, Postmark, SendGrid, Mailgun, SES, Brevo. Ship generic SMTP + 2 API adapters in MVP (Resend + SES per 11), rest follow.
- Sandbox rules (C-class, from SES's model): a new Email Service starts in **sandbox** — sends only to addresses verified by the workspace, daily cap 50 — until domain verification passes; provider-managed relay additionally requires a manual "production access" grant by the platform admin (abuse gate for multi-tenant reputation).
- Limits: per-service daily/monthly + per-workspace aggregates (plan-driven later); counted at ingest.
- **Dev Inbox (Mailtrap-class):** non-production environments default to capture-only — messages stored, viewable in panel (HTML/text/headers preview), nothing leaves. Effort: the ingest listener + a viewer page; the storage model is the same `EmailMessage`.

## 4. Data model (planning level; details merged into 14)
`EmailService` (env-scoped) · `EmailDomain` (verification records + status) · `EmailSender` (address) · `EmailCredential` (Harbora-local SMTP or provider key, encrypted, rotatable) · `ProviderConnection` (BYO provider config, encrypted) · `EmailMessage` (metadata + capped body for inbox/log; retention 30 d default) · `EmailDelivery` (attempts/status) · `EmailEvent` (bounce/complaint/open*) · `EmailSuppression` (per workspace) · `EmailUsage` (period counters). *Open/click tracking: optional, Phase 3, off by default.

## 5. Phasing (amended from mission's draft — amendment: Dev Inbox added to Phase 1; Template engine deferred to Phase 2 since apps usually render their own transactional mail)
- **Phase 1 (with roadmap phase 10) — M/L:** ingest listener + BYO SMTP relay + Resend/SES adapters + domain DNS guide with live checks + credentials/rotation + env injection + test email + snippets + delivery log + usage counters + sandbox rules + **Dev Inbox**.
- **Phase 2 (roadmap phase 14) — L:** Harbora-managed relay option (SES upstream account owned by operator; per-tenant sub-identities), domain verification automation per provider APIs, bounce/complaint webhooks + suppression enforcement, quotas wired to Plans, batch/scheduled send, message templates w/ variables (for apps that want them), webhook events to tenant apps.
- **Phase 3 (future):** dedicated IP guidance, provider failover, analytics, marketing-mail mode (separate stream + unsubscribe machinery — never mixed with transactional; Brevo latency lesson in 11), template marketplace integration.

## 6. Mailbox hosting (`user@example.com`, IMAP/webmail) — recommendation: **NO in core**
- Not aligned with the PaaS core; the operational product is *reputation + storage + abuse handling*, not software (11 §B research).
- Serve the need instead via: (a) a **curated Stalwart mail-server template** in the marketplace (one container, ARM-friendly) with the deliverability caveat surfaced in UI, backup instructions, and no Harbora SLA; (b) later, optional integration with a hosted provider's API (Migadu-class) for provisioning mailboxes — an Add-on, Phase ≥ future, only on demonstrated demand.
- Do **not** build shared multi-tenant mailbox infrastructure (combines SMTP reputation burden + storage/abuse liability). Decision criteria to revisit: ≥ N tenant requests AND PaaS core phases 1–8 stable.

## 7. Dependencies & risks
- Depends on: durable-queue parallelism (06 §1.1), Domains-style DNS checking (exists), attach/env pipeline (exists), Plans for quotas (exists), 09's template rendering approach (shared).
- Risks: SMTP listener is new attack/ops surface (rate-limit, auth-only relay, no open relay — config-audited); DKIM signing for the managed relay needs key management (Phase 2); deliverability support burden — mitigated by sandbox gates + per-tenant streams; scope creep toward marketing email — explicitly fenced.

## 8. Acceptance criteria (Phase 1)
- Create service → verify domain (live DNS check) → attach to app → app sends via injected env vars → message appears in delivery log with provider outcome; rotate credential invalidates old within 60 s without app redeploy (listener-side auth).
- Staging env captures to Dev Inbox by default; production refuses to send until domain verified (or explicit sandbox-to-verified-recipient).
- Provider outage → messages queue and retry; log shows attempts; nothing lost on panel restart.
- Suppressed address is refused at ingest with a logged reason.

## 9. Required tests
Adapter contract tests (fake providers), ingest auth + relay path, sandbox gate matrix, DNS verification parsing (SPF/DKIM record generators), rotation race, retention sweeps, quota counting, Dev Inbox capture isolation (nothing egresses), webhook normalization per provider, and an end-to-end with MailHog/`smtp4dev`-class fake upstream in CI.
