# Functions and platform services — nine sub-projects, planned 2026-08-21

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development.
> Sub-projects are ordered into waves by real file conflict, not by theme. Execute wave by wave.

**Goal:** Make the Functions feature genuinely usable (a public HTTPS door, queue triggers, custom
events, failure visibility) and close the platform-service gaps the 2026-08-21 market research
ranked highest for the Iranian market: email phase 1, bucket binding, customer DNS, and the two
templates (Telegram bot, Kavenegar) that turn those capabilities into a pitch.

**Explicitly OUT of scope, by the owner's decision:** anything payment-gateway — no ZarinPal/Zibal
integration, no wallet top-up, nothing that moves money. (A customer hosting their *own* payment
callback on a public function URL is their business; we build the door, not the gateway.) Also out:
managed Kafka — no local or global competitor treats it as a managed product.

**The market grounding, one paragraph.** Liara (the local expectation baseline) ships email, S3
storage, DNS, RabbitMQ and one-click apps beside plain hosting. Railway/Render/Fly's 2024–2026
investments went to object storage and cron/queue ergonomics. What makes functions products
*actually used* — Cloudflare, Supabase, Vercel all converge on this — is (1) a public HTTPS URL
with zero plumbing, (2) native bindings to the platform's own services, (3) trustworthy logs and
failure visibility. Iran-specific: sanctions overcompliance plus the domestic `.ir` mandate make an
email relay structurally valuable; Kavenegar is the SMS standard; Telegram bots are a historically
grounded business channel. Full sourced report is in the session record; the rankings above are
what this plan implements.

---

## Global constraints — binding for every sub-project

These are the same constraints as `docs/superpowers/plans/2026-08-20-platform-options-plan.md`,
restated so this plan stands alone.

**Standing owner instruction:** cybersecurity and vulnerability review are entirely out of scope.
No security tests, no attack scenarios, no vulnerability checks, no security tools, no
penetration-test suggestions. (Building auth/exposure *features* per this plan is in scope;
adversarially auditing them is not.)

**The law:** eighteen times in this programme a capability assumed missing already existed under
another name — three of those inside plans that warn against exactly this. **Search for what a
thing does, not for what you would have called it**, before building anything, and report what you
found.

- Zero NEW build warnings; exactly 2 pre-existing NU1903 (SSH.NET) stay.
- **Never assume a baseline.** `dotnet test Harbora.slnx` in a fresh worktree before touching
  anything; report before/after. (~5,450 passing / 0 failing / 103 skipped at planning time.)
- Frontend changes: run BOTH `dotnet build` and `npm run build` in `src/Harbora.Web`; entry bundle
  is ~126.3 kB gzip and must not grow meaningfully; new islands must be lazily imported
  (`CodeEditor.vue` is the worked example).
- Test-first; a fix's test fails before the fix. Rendered views are tested through real requests
  (`WebApplicationFactory`), structural markup with AngleSharp. **The panel renders Persian by
  default in tests** — assert on `data-` attributes, form actions, route fragments, never
  sentences. Test names read as sentences.
- Traps, all real: TempData re-types GUID/date-shaped strings (carry int unix timestamps; prove
  redirects with HTTP tests) · a route parameter named `action` receives the method's own name ·
  background/cross-workspace reads need `IgnoreQueryFilters()` + explicit `WorkspaceId ==`, tested
  in both directions · migrations: build FIRST then scaffold (`MigrationConsistencyTests` catches
  stale snapshots); never `ef migrations remove --force` · `main.ts` hand-maintains the lucide
  icon list (`IconCoverageTests`) · money is `long` minor units.
- Design system: semantic tokens only, no new hex; reuse `Views/Shared/Design/` partials;
  bilingual `isFa` + `T["…"]`; technical tokens monospace `dir="ltr"`; three states per table
  (data / empty / not-measured — never "—", never a fabricated zero); errors are in-page banners;
  destructive acts take typed-name confirmation; do-not-change item 23 (PanelMode folds, never
  removes; rejected forms force disclosures open).
- Process: one worktree per sub-project off `origin/master`; verify branch before every commit;
  stage by explicit path; never `git add -A`, never stash/reset --hard/clean; nothing under
  `.superpowers/` or `design_handoff/` gets committed; **commit as you go** (three agents lost
  work to dropped connections on 2026-08-20 alone); rebase + full suite before landing; **closing
  your own backlog entry is part of shipping** (`docs/product-audit/backlog.json` now carries
  evidenced statuses and `BacklogStatusTests` enforces the shape).
- **No Docker and no live PostgreSQL on the dev machine.** Fakes prove behaviour; the live server
  is the sanctioned lane for migration verification. Say plainly what was and was not proven.

---

## Verified current state — checked 2026-08-21, trust these

| Fact | Evidence |
|---|---|
| Function triggers are Http / Cron / Event; runtimes C# / JS / Python | `FunctionDefinition.cs:8-28` |
| **Every generated host route requires the panel's invoke secret — there is no public route.** The code's own comment: "The panel's own door: cron and events arrive here, never through a public route" | `FunctionProject.cs:259-263` (and the JS/Python mirrors at :489, :662) |
| The panel-side invoker enforces a 60s timeout and writes a `FunctionInvocation` row per queued call | `FunctionInvoker.cs:36` |
| Function events are exactly six platform events: `deployment.succeeded/failed`, `app.crashed`, `backup.failed`, `certificate.expiring`, `git.push` | `FunctionEvents.All` |
| A function app is an ordinary `App` (`SourceType.InlineCode`) — env vars, domains, deploys, rollback, billing all apply | established 2026-08-18 spec; the runtime builds and runs |
| **RabbitMQ and NATS already ship as managed services** with catalog entries, generated credentials and rotation | `Enums.cs:140-157`, `ServiceCatalog.cs:141,166`, `CredentialRotationPlan.cs:59` |
| **`StorageBucket` exists with encrypted credentials and a `Views/Storage` UI** — but nothing attaches a bucket to an app: no attach action in `StorageController`, no S3 env injection in `DeploymentPipeline` | grepped 2026-08-21 |
| **No SMS integration exists anywhere** (kavenegar/sms: zero hits) and **no customer KV** (only `KeyValuePair` in logging) — both were false positives in a first sweep; verified twice | grepped 2026-08-21 |
| The platform sends its own mail via `SmtpClient` inside `NotificationService.cs:790` — there is **no customer-facing email capability** | grepped 2026-08-21 |
| Outbound event subscriptions (HTTP+Telegram) shipped 2026-08-20 with `EventKind`, `EventDispatcher`, publish seams, delivery log, Attention integration | sub-project 6, commits `eb9def1..ff41fd9` |
| Custom-domain machinery: `DomainName` (app- or status-page-owned), `DomainDiagnosis`, `CertificateWatcher`, one Traefik route writer; platform Cloudflare client exists (`CloudflarePlatformService`) | sub-projects 7/8, 2026-08-20 |
| Backlog HARBORA-0038 (customer email phase 1: BYO providers + ingest relay + Dev Inbox) is open/P1; HARBORA-0039 (Harbora-managed relay) is its phase 2 | `backlog.json` |
| The marketplace/template system exists with one-click apps and a `TemplateDeploy` path; the Learning Centre (sub-project G) exists for guides | shipped earlier |

---

## Decisions taken by the planner — defaults, not laws; deviate only with a stated reason

1. **Function exposure is per-function, default Protected.** A new function is exactly as closed
   as today; a `Public` toggle opens it, with honest copy ("anyone on the internet can call this
   URL"). Supabase's per-function `verify_jwt` toggle is the precedent. No third mode in v1.
2. **Queue triggers are a panel-side bridge, not per-runtime SDKs.** A background consumer
   subscribes to the RabbitMQ queue and invokes the function through the existing signed door
   (`FunctionInvoker`), so all three runtimes work unchanged, every delivery is a
   `FunctionInvocation` row, and the 60s timeout applies. Baking amqp clients into three generated
   hosts would triple the surface for no v1 gain. Throughput ceiling stated honestly in the UI.
3. **Custom events ride the existing event plumbing.** Keys are namespaced `custom.*`; ingest is
   an authenticated panel endpoint using the app's existing invoke-secret idiom. No new event bus.
4. **Email is phase 1 of HARBORA-0038 exactly:** BYO SMTP providers per workspace, injected into
   apps as env vars; a Dev Inbox (catch-all viewer) for non-production; **no Harbora-run MTA** —
   that is HARBORA-0039, deferred. The backlog item's own acceptance criteria govern.
5. **DNS v1 is BYO Cloudflare token per workspace**, managing records for domains the workspace
   already uses, reusing the existing Cloudflare client shape. Running authoritative DNS
   (PowerDNS) is deferred and stated as such — it is an ops commitment, not a feature.
6. **Templates are products, not docs.** The Telegram-bot and Kavenegar items ship as marketplace
   templates plus a Learning Centre guide each — a deployable starting point, with the guide
   explaining the wiring. The Telegram template's default mode is **long-polling** (works with no
   public URL at all); webhook mode is documented once F1 exists.

---

## Wave A — no shared files between these four

### F1 — A public HTTPS door for HTTP functions

*The single highest-value change in this plan. An HTTP trigger without a public URL is not an HTTP
trigger; payment callbacks, SMS delivery callbacks and webhook bots are all locked behind this.*

**Files:** `FunctionDefinition` (+`IsPublic`, migration) · `FunctionProject.cs` — all three
generated hosts render an unauthenticated route **only** for public functions, beside the
unchanged secret door · `FunctionsController`/`EditFunction.cshtml` — the toggle, the function's
own URL displayed copy-ready (the app already has an address; show `https://{host}/fn/{slug}`) ·
recording: public calls write `FunctionInvocation` rows from inside the host? **No — decide
honestly:** the host has no DB. Record instead at the panel edge only what the panel can see, and
state plainly in the UI that public-call history is the app's own logs (the Logs tab already
searches them). Do not fabricate an invocation row the panel never observed.
**Acceptance:** a public function answers an unsigned request; a protected one still 401s; the
secret door still works for cron/events; the generated-host text tests (`FunctionProjectTests`
idiom) pin all three runtimes; the toggle renders with `data-` attributes and honest bilingual
copy; flipping it marks the app unpublished (the existing `HasUnpublishedChanges` idiom — exposure
changes ship on publish, like every other function change).

### F4 — Function failures become visible

**Files:** `EventKind` (+`function.failed`) published by `FunctionInvoker` where it already writes
the failed row · the dashboard Attention block gains repeated-function-failure entries via the
existing broken-channel/`AttentionService` path · the function's own page already lists runs —
check it says "failed" loudly, not grey.
**Acceptance:** a failed invocation publishes exactly one event; repeated failures surface on the
dashboard; a subscription checkbox exists for the new kind (the 2026-08-20 lesson: an event that
fires but cannot be subscribed to is half-connected — check `EventKind.Publishable`).

### F5 — Buckets attach to apps

**Files:** `StorageController`/`Views/Storage` (+attach/detach) · `DeploymentPipeline` env
assembly — inject `S3_ENDPOINT/S3_ACCESS_KEY/S3_SECRET_KEY/S3_BUCKET` (match whatever names the
bucket UI already documents; check before inventing) on attach, following exactly how database
attach injects connection env · provenance on the app's env page ("from bucket X"), following the
config-groups provenance idiom shipped 2026-08-20.
**Acceptance:** attach → next deploy carries the env (proven at the same seam config-groups tested:
`RunRequests[...].Env`) · detach removes it · secret masked · attached-bucket delete refuses with
the named list (the established refusal idiom) · functions get it free via app env — one test
proves the generated host env includes it.

### F9 — Customer DNS, BYO Cloudflare token

**Files:** per-workspace Cloudflare token (encrypted, `ISecretProtector`) in workspace settings ·
a Domains-page section listing/managing records (A/AAAA/CNAME/TXT/MX) for zones the token can
see, reusing the `CloudflarePlatformService` client shape (extend, don't fork) · honest failure
surfaces: a token that cannot list zones says so.
**Acceptance:** no token → the section says what it needs, no fabricated state · with a (faked)
client, list/add/delete round-trip · another workspace's token is unreachable (tenancy both
directions) · real Cloudflare round-trip is stated unproven locally.

## Wave B — after A lands (F2 touches the invoker; F3 touches events; F6 is disjoint but heavy)

### F2 — Queue-triggered functions (RabbitMQ bridge)

**Files:** `FunctionTrigger.Queue` (+queue name, +which attached broker service) · a panel-side
`BackgroundService` consumer per enabled queue function: consume → `FunctionInvoker.QueueAsync`
(or invoke synchronously through the same door — decide with the invoker's own semantics; the
delivery must become a `FunctionInvocation` row either way) → ack on success, nack-requeue once,
then park in a dead-letter table row surfaced on the function's page · editor UI for the trigger.
**Constraints:** background work — tenant-filter trap, both-direction tests · the consumer must
survive panel restarts (it is on the panel, which redeploys often — reconnect like
`PanelNetworkRebinder` re-attaches) · broker down ≠ silent: surface through Attention.
**Acceptance:** a message on the queue produces an invocation row and an ack (proven against a
faked consumer seam — no live broker here) · failure paths park and surface · disabling the
function stops consumption.

### F3 — Custom events from customer apps

**Files:** an ingest endpoint (`POST /events/ingest`, app-authenticated with the existing invoke
secret idiom, key forced into `custom.` namespace) · `FunctionEvents` accepts `custom.*`
subscriptions · optionally the outbound `EventSubscription` page lists seen custom keys.
**Acceptance:** an app emits, a subscribed function runs (through the existing event path — no new
bus) · a foreign workspace's secret cannot emit into this one · unknown keys are accepted but
listed, not dropped silently.

### F6 — Email phase 1: BYO providers, injection, Dev Inbox (HARBORA-0038)

**Read the backlog item's own `acceptanceCriteria` first — they govern.** Shape: per-workspace
SMTP provider credentials (encrypted); attach-to-app injects `SMTP_*` env vars exactly as F5
injects buckets; a **Dev Inbox** — a catch-all viewer for non-production environments so a
developer sees what their app tried to send without a real provider (check first whether a
Mailpit-style one-click template is the cheaper honest v1 for the inbox half; the platform
already runs one-click apps). **No Harbora-run MTA** (that is 0039). Update the backlog entry on
completion — that is part of shipping.
**Acceptance:** per the item's own criteria, plus: injection proven at the env seam; Dev Inbox
never receives production mail; provider test-send button reports the provider's real answer,
never "sent" for a refusal.

## Wave C — templates, after F1 lands

### F7 — Telegram bot template + guide

A marketplace template (bot skeleton in one supported runtime, long-polling by default so it
works with zero public exposure) + a Learning Centre guide covering: token via env var, polling vs
webhook, and — once F1 exists — switching to webhook mode on a public function URL. Follow the
existing template validation and the marketplace's own conventions.

### F8 — Kavenegar SMS template + guide

A template/guide pairing: sending OTP/SMS via Kavenegar's API from an app (SDK usage, key in env
var), and receiving delivery-status callbacks on a public function URL (F1). **No platform-side
SMS service is built** — this is an integration story, and the market research says that is all it
needs to be.

---

## Landing order and conflicts

A: F1 · F4 · F5 · F9 in parallel (disjoint). B: F2 and F3 after F1 (both touch
invoker/event plumbing — sequence F2 then F3 or accept a rebase), F6 parallel to both. C: F7 · F8
parallel. Every migration lands one at a time (the snapshot regenerates each time); waves keep
them apart. Deploy after each wave, not at the end; the live server verifies each migration
against real PostgreSQL — the lane that existed all along and went unused until 2026-08-18.
