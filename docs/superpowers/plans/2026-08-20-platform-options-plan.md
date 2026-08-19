# Platform options — eleven sub-projects, planned 2026-08-20

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Sub-projects are ordered; each is independently shippable. Execute in order unless a
> dependency note says otherwise.

**Goal:** Ship the eleven capabilities the owner selected on 2026-08-20 — six customer-facing
(outbound event notifications, maintenance mode, public status page on subdomain + custom domain,
shared env-var groups, self-serve database export/import, SSO), five operator-facing (backlog
status tracking, platform revenue view, support impersonation, platform announcements, DR runbook
with restore drill). **Online wallet top-up / payment gateways are explicitly OUT of scope** — the
owner excluded them.

**Architecture:** Harbora is a modular-monolith PaaS — ASP.NET Core MVC (.NET 10), EF Core +
PostgreSQL, Docker + Traefik, Razor views with Tailwind semantic tokens and lazily-imported Vue
islands, Persian-first RTL bilingual. Every sub-project below extends an existing subsystem;
none of them founds a new one. The single most important law of this codebase follows.

**The law: search for what a thing does, not for what you would have called it.** Fifteen times in
this programme, a capability assumed missing already existed under another name (volume browsing
lived under `AppData` while the search was for `BrowseVolume`; single-app log search lived under
`LogFilter`; the billing burn rate lived inside the low-balance warning). Three of those times the
wrong assumption was written by the author of a spec. **Before building anything in this plan,
search the codebase by behaviour and report what you found** — a sub-project shrinking because
half of it exists is a good outcome and must be reported, not hidden.

---

## Global constraints — binding for every sub-project

**Standing owner instruction (verbatim scope rule):** cybersecurity and vulnerability review are
entirely out of scope and belong to a separate later process. Run no security tests, examine no
attack scenarios, perform no vulnerability checks, run no security tools, offer no
penetration-testing suggestions. (Building auth/impersonation *features* per this plan is in
scope; *auditing them for vulnerabilities* is not.)

**Build & test discipline**
- Zero NEW build warnings. Exactly **2 pre-existing NU1903** on SSH.NET in
  `Harbora.Postgres.Tests` — leave them.
- **Never assume a baseline.** Run `dotnet test Harbora.slnx` in a fresh worktree before changing
  anything; report before/after. (At planning time: 5,070 passing / 0 failing / 100 skipped —
  83 Postgres-gated, 17 Docker-gated. Verify, don't trust.)
- After any frontend change run BOTH `dotnet build` and `npm run build` in `src/Harbora.Web`.
  Entry bundle is **126.23 kB gzip** at planning time; report before/after — it must not grow
  meaningfully. Any new Vue island must be **lazily imported** (`Scripts/islands/CodeEditor.vue`
  and `RouteDesigner.vue` are the worked examples; the other three islands are static imports —
  do not copy those).
- Work test-first. A fix's test must fail before the fix. For anything rendered, test through a
  real request (`WebApplicationFactory`); for structural markup assertions parse the DOM with
  AngleSharp (`tests/Harbora.Tests/Http/FunctionEditorHttpTests.cs` is the worked example — a
  nested `<form>` survived for months because no test ever rendered the view).
- **The panel renders Persian by default in tests.** Assert on `data-` attributes, form actions
  and route fragments — never on a UI sentence.
- Test names read as sentences. Narrative commit messages (read `git log --oneline -15` first).
  End every commit with `Co-Authored-By:` naming the implementing model.

**Codebase traps (each has bitten this programme; all are verified real)**
- **TempData re-types values**: `CookieTempDataProvider` re-infers JSON types, so a GUID-shaped
  string comes back as `System.Guid` and `as string` reads null across a redirect. Carry an
  `int` unix timestamp or prefixed string, and prove every redirect with an HTTP test.
- **A route parameter named `action` receives the method's own name** from MVC route-value
  binding, and the page looks empty rather than broken. Never name a parameter `action`.
- **Global query filters kill sessionless work**: background jobs and cross-workspace admin reads
  see an empty database unless they use `IgnoreQueryFilters()` (and then scope explicitly by
  `WorkspaceId`). `Project`/`Environment` carry NO global filter — explicit `WorkspaceId ==`
  checks are the only tenant protection there. Test tenancy in both directions: finds its own,
  cannot reach another's.
- **Migrations**: build FIRST, then scaffold — a migration scaffolded against a stale assembly
  captures the old model; `MigrationConsistencyTests` catches it. Never `dotnet ef migrations
  remove --force` (it once deleted an unrelated migration).
- **`main.ts` carries a hand-maintained lucide icon list**; `IconCoverageTests` fails on any icon
  used in a view but missing from `usedIcons`.
- **Money is `long` minor units** (`AmountMinor` on `BillingLedgerEntry`), never `double`/`float`.
  Follow the ledger's own rounding exactly.
- **No Docker and no live PostgreSQL on the dev machine.** Container/proxy behaviour is proven
  against `FakeDockerEngine`/`IProxyEngine` fakes; Postgres-lane tests skip locally and run in CI.
  Say plainly in reports what was and was not proven. The live server (57.131.136.56, deploy via
  `git pull` + `docker compose up -d --build` in `/opt/harbora/app/deploy`) is a legitimate
  verification lane for migrations — one was verified there for the first time on 2026-08-18.

**Design-system rules (from the 2026-08 redesign, binding)**
- **No new hex anywhere.** Semantic tokens only, via the Tailwind names in `tailwind.config.js`
  (`text-ink`, `bg-surface`, `border-line`, `bg-warn-soft`, `text-warn`, `bg-ok-soft`, …) mapping
  onto the CSS variables in `Scripts/app.css`. Hardcoded palette classes cannot follow `html.dark`.
- Reuse the partials in `Views/Shared/Design/`: `_PageHeader`, `_StatCard`, `_Sparkline`,
  `_StatusPill`, `_Metric`, `_EmptyState`, `_PanelStart`/`_PanelEnd`, `_AdvancedStart`/`_AdvancedEnd`.
- **Bilingual** via `isFa` ternary + `T["…"]` + `Resources/SharedResource.fa.resx`. Layout with
  logical properties. **Every technical token** (host, path, id, number, YAML) in monospace with
  `dir="ltr"`, even in Persian.
- **Three states for every table**: has data · empty (`_EmptyState`) · not-measured ("not
  collected yet", never "—", never a fabricated 0). **The defining defect class of this codebase
  is a surface reporting success/health for work it never did** — `MonitoringController` rendered
  "CPU: 0%" for a metric never collected until 2026-08-20. Do not add a single instance of it.
- Errors are in-page banners (`alert-warning`), not toasts. Destructive acts get typed-name
  confirmation (`ServiceRemovalPlan` / `ProjectRemovalPlan` idiom) — do-not-change item 19.
- **Do-not-change item 23 — PanelMode fold-never-remove**: Simple mode folds advanced material
  into `_AdvancedStart`/`_AdvancedEnd`; never removes it; routes stay live in both modes; a
  rejected form forces its disclosure open. Read `docs/product-audit/19-do-not-change-list.md`
  fully before each sub-project and report which items applied.

**Process rules**
- One worktree per sub-project: `git worktree add ../Harbora-<slug> -b <branch> origin/master`.
  Verify `git rev-parse --abbrev-ref HEAD` before every commit. Stage by explicit path; never
  `git add -A`/`-.`; never stash/reset --hard/clean anywhere in this repository (several sessions
  and ~16 worktrees are typically active). Commit as you go — two agents have lost large
  uncommitted work here.
- Do not commit anything under `.superpowers/` or `design_handoff/`.
- Rebase onto `origin/master` and re-run the full suite before landing. Deploy after each landed
  sub-project, not in one batch at the end.

---

## Decisions already taken by the owner (2026-08-20) — do not re-litigate

1. **SSO providers: Google AND GitHub AND generic OIDC** — all three.
2. **Outbound notifications: HTTP webhooks + Telegram** (not Slack/Discord/email in v1).
3. **Impersonation: visible banner + two-sided audit** — the customer sees a banner while a
   support session is active, and the session is written to both the platform audit log and the
   tenant's own audit view.
4. **Status page: platform subdomain AND customer custom domain** — both phases are in scope,
   shipped in that order.

Decisions taken by the planner (documented per item below) are defaults, not laws — if
implementation proves one wrong, say so in the report and propose the correction; do not silently
deviate.

---

## Verified current state — trust these, they were checked on 2026-08-20

| Fact | Evidence |
|---|---|
| Telegram is already a workspace alert-channel type | `src/Harbora.Domain/Common/Enums.cs:214` (`Telegram = 1`); channel target stored encrypted per `src/Harbora.Domain/Monitoring/Alert.cs:13` |
| Personal notification channels are InApp + Email only, deliberately | `Enums.cs:382-393` — "Telegram/Discord/Webhook remain workspace-level integrations" (doc 09 §3) |
| Channel sending lives in | `src/Harbora.Infrastructure/Notifications/NotificationService.cs` |
| Money enters wallets ONLY via admin credit + vouchers | `WalletService.cs:254` (`LedgerKind.Credit` with `ByUserId`), `VoucherService.cs` — no gateway exists (and none is to be built) |
| Burn rate / runway / forecast are one shared implementation | `BurnRate`, `CostForecast`, `WalletService.ForecastAsync` (built 2026-08-18; forecast must never be computed a second way) |
| Auth is cookie-only; no external providers, no ExternalLogin table | `src/Harbora.Web/Program.cs:102` |
| 2FA/TOTP exists; password reset exists; member invitations exist | verified by behaviour-grep across `AccountController`/`UsersController`/`WorkspacesController` |
| Impersonation does NOT exist | the only matches for "impersonat" are security comments in SFTP/node-CA files |
| Announcements do NOT exist | no matches outside unrelated text |
| Cross-workspace revenue view does NOT exist | no admin surface reads the ledger across workspaces |
| Custom-domain machinery exists per app | `src/Harbora.Domain/Networking/DomainName.cs` (Host, SslEnabled, CertificateId), `DomainDiagnosis.cs`, `ReservedHosts.cs`, Cloudflare/cert infra in `Infrastructure/Networking` |
| Reserved-host guard exists but is MISSING on template + preview host paths | backlog HARBORA-0058 — do not add a third unguarded path |
| Per-app 30-day uptime/restart history exists | `LifecycleHistory` (Phase 6 M3), surfaced on Apps/Details and Monitoring |
| Temporary signed download links exist | `src/Harbora.Domain/Storage/VolumeDownloadToken.cs` (D4; expiry carried as int unix because of the TempData trap) |
| pg_dump/restore machinery exists inside the backup engine | `Infrastructure/Backups/BackupEngine.cs`, `DatabaseDumpPlan.cs`, `UpgradeSafetyPlan/Service.cs` (safety-snapshot-before-restore idiom) |
| Secrets-at-rest idiom | `ISecretProtector` (`SecurityAbstractions.cs`); `EnvironmentVariable.IsSecret` + ciphertext `Value` |
| Audit idiom | `IAuditLogger.LogAsync(action, targetType, targetId)` (`SecurityAbstractions.cs:58`); workspace-visible audit question is backlog HARBORA-0056 |
| Time-boxed borrowed access idiom | `AdminerSession.Lifetime` = 1 hour — the platform's vocabulary for temporary access |
| Deferred-config honesty idiom | `HasUnpublishedChanges` flag + "applies on next deploy" convention (Functions, SetReplicas follow it) |
| Job queue for async work | `JobKind` + durable queue; parallel workers with per-target locks exist (`Jobs:MaxConcurrency`) |
| Backlog has 66 items and NO status field | `docs/product-audit/backlog.json` — three times work was spent on already-done items because of this |
| The census-test idiom | `DetailTabCensusTests`, `AppAddressCensusTests`, `NotificationTemplateCensusTests` read source rather than hand-kept lists |

---

## Sub-project 1 — Backlog gets a status field, and every entry gets audited

*Why first: it is tiny, and it has already cost three wasted efforts (HARBORA-0008 was fixed
2026-08-07 in `995ebe7` while its backlog entry still read as open).*

**Files:** `docs/product-audit/backlog.json` · new `tests/Harbora.Tests/BacklogStatusTests.cs`

**Tasks**
1. Add to every item: `"status": "done" | "open" | "partial" | "withdrawn"`, plus for non-open
   items `"evidence"`: a commit sha, file:line, or one-line justification. No item may carry a
   bare status without evidence.
2. **Audit all 66 items against the code** using the search-by-behaviour law. Known at planning
   time (verify each anyway): 0001/0065 done (`Jobs:MaxConcurrency` exists), 0008 done
   (`995ebe7`), 0020/0021 done (Postgres + HTTP lanes exist), 0023 done (EnvironmentRequired
   migration), 0026 done (`/activity`), 0029–0031 done (Phase 6), 0033 done (volume-safety,
   2026-08-19), 0036/0037 done (Phase 9 + digest/quiet hours), 0040 open (P8), 0038/0047/0048/
   0053 open. Everything else: establish honestly.
3. A test that fails when any item lacks `status`, or has a non-open status without `evidence` —
   the census-test idiom, pointed at process data.
4. Note in `docs/superpowers/STATUS.md` that the tracker is now real.

**Acceptance:** the JSON parses; every item carries an evidenced status; the test is red without
the field and green with it.

---

## Sub-project 2 — Platform revenue view (operator)

**Problem:** the operator has no answer to "what is the platform earning, who burns most, whose
wallet dies next" although every number already sits in `BillingLedger`.

**Files:** new `src/Harbora.Web/Controllers/AdminRevenueController.cs` (or an action set inside
the existing admin area — follow whatever `AdminSettingsController`'s authorization does; check
first) · new view(s) under `Views/AdminRevenue/` · new
`src/Harbora.Infrastructure/Billing/RevenueReport.cs` · tests.

**Design (planner decisions)**
- Read-only report, platform-admin-only, built like `EnvironmentPlacementReport`: a Build method
  answering fixed questions and a Render/view that **names every zero explicitly** — a month with
  no ledger rows says "no rows", never a silent 0.
- Questions v1: charged total per calendar month (last 6); per-workspace: current wallet balance,
  last-30-days burn, **runway date via the existing `BurnRate`** (never recomputed a second way),
  suspended-or-not; top-10 workspaces by burn; credits issued (admin + voucher) per month.
- All queries `IgnoreQueryFilters()` (this is the cross-workspace admin read the tenant-filter
  trap exists for) with explicit grouping by `WorkspaceId`.
- Money formatted the way `BillingController` and the bill already format it — find and reuse,
  do not invent a second money formatter.
- Projections (runway) labelled as projections, in the same voice `CostForecast` established.

**Acceptance:** page renders for a platform admin, 404/denied for a workspace owner (test both);
totals reconcile against a seeded ledger in an HTTP test; a workspace with <24h of billed history
shows the same "too little history" honesty `WalletService.MinimumHistoryHours` established.

---

## Sub-project 3 — Support impersonation with a visible banner and two-sided audit (operator)

**Owner decision: visible + two-sided.** Silent support access was explicitly rejected.

**Files:** new `src/Harbora.Domain/Identity/SupportSession.cs` + migration · new
`Infrastructure/Identity/SupportSessionService.cs` · `TenantsController` (start point — the
tenants admin page already exists) · `AccountController`/auth claims ·
`Views/Shared/_Layout.cshtml` (banner partial) · tenant audit view · tests.

**Design (planner decisions)**
- A `SupportSession` row: platform-admin user id, target user id, target workspace id, reason
  (required, free text), started/expires/ended. **Lifetime 1 hour** — the `AdminerSession`
  vocabulary — with an explicit "end now" button on the banner.
- Sign-in-as issues the normal cookie **plus claims** carrying the original admin id and the
  session id. Every request under those claims: `IAuditLogger` writes with BOTH ids; action
  string prefix `support.` so the tenant's audit view can label the rows "پشتیبانی / Support".
- **The banner renders on every page** while the claims are present — customer-visible wording:
  who (platform support), since when, why, and that everything is being recorded. Assert its
  presence with a `data-support-session` attribute, not a sentence.
- **Blocked while impersonating** (server-side, not hidden buttons): changing the target's
  password/email/2FA, ending other sessions, creating API tokens, and any wallet credit. These
  return the standard capability-refusal path. Everything else is allowed — support usually needs
  to *do* the thing to see it fail.
- Tenant-side visibility: the workspace audit page shows support-session entries (this partially
  answers backlog HARBORA-0056 — note that in the report).
- Expiry enforced server-side on each request (claims carry the session id; the row is checked),
  so a stolen banner-cookie after expiry is inert.

**Acceptance (all HTTP tests):** starting a session requires platform admin + a reason; the
banner appears on `/` under impersonation and not otherwise; a blocked action refuses and audits;
both audit logs carry both ids; the session dies at expiry and by the end button; a workspace
owner cannot start one.

---

## Sub-project 4 — Platform announcements (operator)

**Files:** new `Domain/Platform/Announcement.cs` + `AnnouncementDismissal.cs` + migration ·
admin CRUD (inside the existing admin area) · a banner partial in `_Layout` · optional fan-out
via the existing `UserNotification` (N3) machinery · tests.

**Design (planner decisions)**
- Fields: title/body **in both languages** (fa + en, both required — the panel is bilingual and
  an announcement half its users cannot read is not an announcement), severity (info/warn),
  optional start/end window, created-by.
- Shows as a dismissible banner on every panel page while active; dismissal is per-user,
  persisted (`AnnouncementDismissal`), and a **new announcement is a new banner** — dismissing
  one must not dismiss the next.
- Severity `warn` additionally writes `UserNotification` rows through the existing N3 path
  (reuse `NotificationService`; do not build a second fan-out). Info-level stays banner-only.
- Simple mode still shows announcements — operational information is never folded (this is not
  the item-23 material; note the distinction in code comments).
- Banner asserts via `data-announcement` attribute.

**Acceptance:** active window respected (before/after windows show nothing); dismissal survives
navigation and does not leak across users or announcements; warn-level produces notification
rows; a workspace owner cannot reach the admin CRUD.

---

## Sub-project 5 — Maintenance mode per app (customer)

*Pairs with sub-project 4; ship after it so a platform announcement can accompany planned
maintenance.*

**Files:** `Domain/Apps/App.cs` (a `MaintenanceMode` flag + optional bilingual message + since) +
migration · the Traefik config writer (find it: the same path `RoutesController.Save`/apply uses)
· a panel-served maintenance endpoint · Apps Details/Overview toggle UI · status-page integration
(sub-project 7 reads it) · tests against the `IProxyEngine` fakes.

**Design (planner decisions)**
- When ON: the generated router for the app's hosts points to a panel endpoint serving a themed
  **503** with `Retry-After`, the customer's optional message (bilingual fallback), and the app's
  name — the same semantic tokens, dark-mode capable, no new hex. The app's containers keep
  running (stopping is already a separate existing action; do not conflate them).
- Applying the flag flows through the SAME proxy-apply path routes use — no second way to write
  Traefik config. It is an immediate operational act (no draft step), audited, and surfaced in
  the dashboard Activity feed.
- The toggle states its effect before confirming ("visitors will see a maintenance page; the app
  keeps running") — plain confirmation, not typed-name (nothing is destroyed).
- Honesty: if the proxy apply fails, the flag must NOT read as on — follow the pattern the route
  designer's apply-result banner established; never "Maintenance enabled" for an apply that
  failed.
- Capability: `apps.operate` (same as start/stop).

**Acceptance:** toggling writes the router change through the fake proxy engine (assert the
rendered config); a failed apply leaves the flag off and says so; the 503 page renders in both
languages with `dir` correctness; Activity shows the act; an Operator can toggle it, a Viewer
cannot.

---

## Sub-project 6 — Outbound event notifications: HTTP webhooks + Telegram (customer)

**Owner decision: HTTP + Telegram, nothing else in v1.** The transports already exist at
workspace level (alerts) — **this sub-project adds event subscriptions, not channels.** Reuse
`NotificationService` / the alert-channel sending code and the encrypted-target storage; report
exactly what was reused.

**Files:** new `Domain/Notifications/EventSubscription.cs` + `EventDelivery.cs` + migration ·
`Infrastructure/Notifications/EventDispatcher.cs` (job-queue backed) · publish call sites at the
existing lifecycle seams (deployment finished, app crashed, backup finished, service failed,
maintenance toggled) · a workspace settings page (`/notifications/webhooks` or beside the alert
channels — follow the existing IA) · tests.

**Design (planner decisions)**
- Events v1: `deployment.succeeded`, `deployment.failed`, `app.crashed`, `backup.succeeded`,
  `backup.failed`, `service.failed`, `maintenance.on/off`. A subscription = target (webhook URL
  or Telegram chat, encrypted via `ISecretProtector`) + event mask + enabled flag.
- **HTTP payloads are signed** — reuse the Functions host's HMAC signing idiom (the platform
  already signs invocations; find it, reuse the shape, per-subscription secret shown once at
  creation). JSON payload: event, workspace, resource ids/names, timestamp, and a stable `id`
  for consumer-side dedup.
- Delivery through the job queue with bounded retries/backoff; every attempt writes an
  `EventDelivery` row (status, HTTP code, error). **A delivery log with honest failures** — and a
  subscription whose deliveries keep failing surfaces through the existing broken-channel path in
  the dashboard Attention block (`ChannelKind` — extend it, don't fork it).
- Dispatch is background work: `IgnoreQueryFilters` + explicit `WorkspaceId` — and a tenancy test
  in both directions.
- The publish seams must not slow or fail the acts they observe: enqueue only, never inline HTTP.

**Acceptance:** each v1 event enqueues exactly one delivery per matching subscription (test the
mask); the signature verifies against the shared secret in a test consumer; a failing endpoint
shows a red delivery row and eventually the Attention entry; Telegram target reuses the existing
send code (assert via the fake/recorded sender); no event crosses workspaces.

---

## Sub-project 7 — Public status page on a platform subdomain (customer)

**Files:** new `Domain/Status/StatusPage.cs` (+ selected components + manual incident notes) +
migration · new anonymous-route controller · public view (its own minimal layout — the panel
chrome must not leak) · workspace settings UI to enable/select apps · `ReservedHosts` guard ·
tests.

**Design (planner decisions)**
- Address: `status-{workspaceSlug}.<platform domain>` — **register the prefix in
  `ReservedHosts`** so no app can squat it (HARBORA-0058 warns the guard is already missing on
  two paths; do not add a third — and note in the report if you can close those two cheaply).
- **Opt-in only.** Nothing is public until the customer enables the page and picks which apps
  appear. Each app shows: state (up / degraded / maintenance / unknown), 30-day uptime from
  `LifecycleHistory`, and nothing else — no metrics numbers, no hostnames beyond what the
  customer chose to name the component.
- Manual incident notes: the customer can post/resolve a short bilingual note (this is the
  "we know, we're on it" line every status page exists for). Auto-states come from the same
  health source the panel's own Apps list reads — one source, never a second derivation.
- **Honesty rules bind hardest here**: unknown health says unknown; an app never deployed says
  so; uptime with no history says "no history yet". This page makes claims to the customer's own
  users — a fabricated green here is the defect class at its worst.
- Anonymous route resolves the workspace by host, reads with an unscoped context bound explicitly
  to that workspace id, and must be provably unable to serve any other workspace's data (tenancy
  test both directions). Rate/robots decisions: `noindex` off by default (a status page wants to
  be findable), no auth, no cookies.

**Acceptance:** disabled page 404s; enabled page renders anonymously with only the chosen apps;
maintenance mode (sub-project 5) shows as maintenance; unknown shows unknown; another workspace's
slug shows nothing of the first's; the reserved prefix cannot be claimed as an app host.

---

## Sub-project 8 — Status page on the customer's own domain

*Depends on 7.*

**Files:** extend `StatusPage` with a domain reference · reuse the `DomainName` attach flow
(diagnosis, cert issuance, Traefik route) pointed at the public status endpoint · settings UI ·
tests against the proxy fakes.

**Design (planner decisions)**
- The customer adds `status.their.com`; the panel walks the SAME flow an app domain walks —
  `DomainDiagnosis` for "is DNS pointed here", the existing cert resolver path for TLS, a Traefik
  router whose backend is the public status endpoint with the host bound to the workspace. **Do
  not fork the domain-attach machinery** — if it needs a "target = status page" variant, extend
  the one flow.
- The platform subdomain keeps working after a custom domain is attached (both answer).
- Removal detaches cleanly: route gone, cert record handled the way app-domain removal already
  handles it.

**Acceptance:** attach renders the router + cert request through the fakes; diagnosis honestly
reports unpointed DNS; both hosts serve the page; removal leaves no route behind (assert the
rendered config, not just the DB row).

---

## Sub-project 9 — Shared environment-variable groups (customer)

**Files:** new `Domain/Apps/ConfigGroup.cs` + `ConfigGroupEntry.cs` + `AppConfigGroup.cs` (join)
+ migration · the deploy pipeline's env assembly point (find where `EnvironmentVariable` rows
become container env — extend there) · UI: a groups page + an attach control on the app env-vars
page · tests.

**Design (planner decisions)**
- Workspace-level groups; entries mirror `EnvironmentVariable` exactly (name, value, `IsSecret`
  with ciphertext via `ISecretProtector`) — same editor affordances, same masking.
- **Precedence: the app's own variable wins over any group; groups attached later win over
  earlier (attachment order is explicit and visible).** The effective set is shown on the app's
  env page ("from group X" provenance per row) — a merged value whose origin is invisible is a
  debugging trap.
- Editing a group does NOT restart anything: attached apps flip the existing
  "applies on next deploy" convention (the `HasUnpublishedChanges` idiom — reuse the same visual
  language the Functions chip uses). The group page lists which apps will pick the change up.
- Deleting a group that is attached anywhere refuses with the named list (the
  `ProjectsController.Delete` refusal idiom) unless detached first.

**Acceptance:** deploy assembles app-over-group precedence (unit test at the assembly seam, and
one pipeline test through the fake engine asserting the container's env); provenance renders;
secret entries mask; group edit marks attached apps stale; attached-group delete refuses with
names.

---

## Sub-project 10 — Self-serve database export & import (customer)

**Files:** database Details UI · new job kinds wired into the existing queue · reuse
`DatabaseDumpPlan`/`BackupEngine` dump machinery and the `VolumeDownloadToken` download idiom ·
import path reusing the safety-snapshot-first idiom (`UpgradeSafetyService`) · tests.

**Design (planner decisions)**
- **Export:** a button queues a dump job (existing pg_dump machinery — reuse, do not shell out a
  second way); on completion the customer gets a time-limited signed download link (the
  `VolumeDownloadToken` shape: token row + **int unix expiry** because of the TempData trap).
  Artifacts land where backup staging already lands, and an expiry sweeper removes them —
  check whether the existing retention sweep can own this before writing a new one.
- **Import:** upload a dump → **safety snapshot first, always** (the platform already knows how —
  `UpgradeSafetyPlan`) → restore through the existing restore path → report. Import is
  destructive: **typed-name confirmation** (do-not-change item 19) naming the database and
  stating the safety snapshot that will be taken.
- Both acts audited; both appear in Activity; a failed import reports what the safety snapshot is
  and how to restore it — never "Import failed" with no way back.
- Capability: exports under `backups.run`-equivalent read; import under the heavier database
  capability — check `RolePermissions` and follow the existing split.

**Acceptance:** export produces a token-gated download that 404s after expiry (HTTP test crossing
the redirect); import refuses without the typed name; the safety snapshot exists before restore
touches anything (assert ordering through the fakes); an Operator can export but not import.

---

## Sub-project 11 — SSO: Google + GitHub + generic OIDC (customer)

**Owner decision: all three providers.**

**Files:** `Program.cs` auth registration · new `Domain/Identity/ExternalLogin.cs` (provider,
subject, userId, linkedAt) + migration · `AccountController` (challenge/callback/link/unlink) ·
login view buttons + account-settings linking UI · admin settings section for provider
config (client id/secret via `ISecretProtector`, per-provider enable) · tests.

**Design (planner decisions)**
- Providers register from DB-held admin settings at startup (or via options monitor — check how
  other admin-settable infrastructure config is loaded here and follow it). A provider with no
  config simply shows no button.
- Google via `AddGoogle`; GitHub via the OAuth handler with GitHub endpoints; generic OIDC via
  `AddOpenIdConnect` with admin-entered authority/client — three registrations, one shared
  callback→account flow.
- **Linking rules:** a signed-in user can link/unlink providers in account settings (unlink
  refuses if it would leave no sign-in method — no password AND no other provider). An external
  sign-in matching an existing verified email **links only after the user proves the password**
  (sign-in-then-link), not silently by email match. An external sign-in with no matching account
  follows whatever the platform's existing self-registration behaviour is — **check by behaviour
  whether open registration exists** (no `AllowRegistration` setting was found; the register
  action's own guards are the truth) and mirror it; if registration is closed, say "no account —
  ask your workspace owner for an invitation" (invitations exist).
- **Local 2FA still applies after an external sign-in** (conservative; the provider's MFA is not
  ours to assume).
- What cannot be proven here: a real round-trip against Google/GitHub. Use a test authentication
  handler inside `WebApplicationFactory` to drive the callback path; state plainly in the report
  that live-provider round-trips remain unverified until the owner configures real credentials
  on the server.

**Acceptance:** buttons render only for configured providers; the full link/unlink matrix is
HTTP-tested (link, unlink-last-method refusal, email-match-requires-password, closed-registration
refusal if applicable); 2FA challenge still fires after external sign-in for a 2FA user;
`ExternalLogin` rows are unique per (provider, subject).

---

## Sub-project 12 — DR runbook + restore drill (operator)

**Files:** new `deploy/DR-RUNBOOK.md` · new `deploy/restore-drill.sh` · a "last drill" surface in
the admin area · (no migration unless the drill result needs a row — planner default: one
`Setting` key, no new table).

**Design (planner decisions)**
- **The runbook is written against the real install**, not an imagined one: `/opt/harbora/app`,
  compose in `deploy/`, the untracked production compose override (it exists — retention is
  overridden in production; the runbook must tell the operator to preserve it), `.env`, Postgres,
  MinIO, Traefik certs, per-app volumes. Full-loss order: infra → compose → DB restore → volume
  restores → cert recovery → verification checklist. Read `deploy/RUNBOOK.md` first; DR extends
  it, does not duplicate it.
- **The drill script restores the latest database backup into a scratch Postgres container**
  (the server has Docker; the dev machine does not — the drill runs on the server), runs sanity
  queries (migrations table count, workspaces count, newest ledger row age), prints a dated
  verdict, and **fails loudly when there is no backup to drill** — a drill that silently passes
  on nothing is the defect class again.
- The admin area shows the last drill date + verdict (written by the script via a CLI/admin
  command — check what `harbora admin` verbs exist; `volume-orphan-report` was added 2026-08-19,
  follow that shape). A drill older than 30 days shows as a warning, honestly worded.
- What CI can prove: the script's parsing/verdict logic against a fixture. What only the server
  can prove: the real drill — the implementer runs it once on 57 as part of acceptance, with the
  owner's standing deploy access.

**Acceptance:** script fails on missing backup; sanity checks catch a truncated dump (fixture
test); the admin surface shows date + verdict and warns past 30 days; the runbook names the
production compose override and the pinned-host-key access idiom.

---

## Suggested landing order and why

1 (backlog) unblocks honest scoping of everything else. 2–4 (revenue, impersonation,
announcements) are operator tools with no cross-dependencies. 5 (maintenance) wants 4's banner
machinery conceptually but not literally. 6 (events) before 7 so the status page can later feed
it. 7→8 (status page phases) in order. 9–11 (env groups, DB export, SSO) are independent. 12
(DR) any time — but before, not after, the next infrastructure incident.

Sub-projects touch disjoint files except: 4+5 (both add a `_Layout` banner — land 4 first, 5
rebases), 7+8 (sequential by design), and anything touching `Scripts/app.css` (append-only at the
file end, per the parallel-work convention).
