# 08 — UI/UX Specification

Scope: the design system and per-page specs for the redesigned panel. Harbora already ships a
genuine, non-templated Tailwind UI with dark mode, RTL/LTR, and a coherent palette — this spec
**evolves** that identity rather than replacing it, and codifies the states the current UI is
missing (staged progress, tabbed app detail, command palette, consistent empty/error states).

> On mockups: Harbora's UI is server-rendered Razor + Tailwind. The authoritative "mockup" is a
> live HTML **design-system reference page** built in execution (roadmap doc 12, Phase 5), not
> AI-generated raster images — those would misrepresent the real component styling and risk the
> generic look the brief warns against. This doc is the precise spec that page implements.

---

## 1. Design language ("Harbora Calm")

- **Personality:** calm, trustworthy, technical-but-friendly. It should feel like a well-run
  control room: information-dense where it matters, quiet everywhere else. No decorative motion, no
  gradients-as-personality, no stock-admin-template feel.
- **Foundation (as-built, keep):** slate neutrals (`slate-950` canvas, `slate-900/800` surfaces),
  an indigo **brand** accent (`brand-600/500/400`), semantic emerald/amber/red for health.
- **Evolution:** formalize the palette into tokens; add clear elevation tiers; tighten typography
  scale; standardize component states. Introduce a light theme with the same token names.

## 2. Design tokens

**Color (semantic → value, dark / light):**
| Token | Dark | Light | Use |
|---|---|---|---|
| `--bg` | slate-950 | slate-50 | app canvas |
| `--surface` | slate-900/70 | white | cards, panels |
| `--surface-2` | slate-800 | slate-100 | inputs, nested |
| `--border` | slate-800 | slate-200 | hairlines |
| `--text` | slate-100 | slate-900 | primary text |
| `--text-muted` | slate-400 | slate-500 | secondary |
| `--brand` | indigo-500 | indigo-600 | primary actions, active nav |
| `--ok` | emerald-400 | emerald-600 | healthy/live |
| `--warn` | amber-300 | amber-600 | degraded/pending |
| `--err` | red-400 | red-600 | failed/critical |

**Type:** system/`Inter` for UI; a mono (`JetBrains Mono`/`ui-monospace`) for logs, IDs, commands.
Scale: 12 / 14 (base) / 16 / 20 / 24 / 30. Line-height 1.5 body, 1.25 headings. Persian text uses a
matching webfont (e.g., Vazirmatn) with correct RTL metrics.

**Spacing:** 4-pt grid (4/8/12/16/24/32/48). Card padding 16–24. Page gutters ≥ 5% each side
(mobile 16px). **Radius:** 8 (controls) / 12–16 (cards) / full (pills). **Elevation:** 1 border-only
(cards), 2 subtle shadow (popovers/menus), 3 stronger (modals/command palette).

**Motion:** ≤ 150ms ease for hover/expand; spinners for indeterminate; **no** parallax/gratuitous
animation. Respect `prefers-reduced-motion`.

**RTL/LTR:** use logical properties (`ps/pe`, `ms/me`, `border-s/e`) everywhere; icons that imply
direction mirror in RTL; numerals and code stay LTR inside RTL text.

## 3. Core components (states defined once, reused everywhere)

- **Button:** variants primary / secondary / ghost / danger; states default, hover, focus (visible
  ring), loading (spinner + disabled), disabled. Destructive = danger + confirm.
- **Status badge:** app (Running/Stopped/Failed/Crashed/Deploying) and deployment
  (Queued/Building/Deploying/Live/Failed/RolledBack) — fixed color mapping (ok/warn/err/brand/muted).
- **Staged progress bar (new):** horizontal stepper Queued → Build → Deploy → Health → Live; each
  step pending/active(animated)/done/failed; the failed step is highlighted red with the error
  surfaced above.
- **Card / stat card / list row:** consistent header + primary metric + sub-line; entire card
  clickable when it represents a resource.
- **Tabs:** used on resource detail pages; keyboard-navigable; deep-linkable (`#deployments`).
- **Empty state:** icon + one-line "what this is" + primary CTA (e.g., "No apps yet — Create your
  first app").
- **Loading skeleton:** shape-matched placeholders (never a bare spinner for full pages).
- **Error state:** short cause + retry action + (when useful) a "view logs/details" link.
- **Toast/inline alert:** success/info/warn/error; long operations also reflect status in-page, not
  only as a toast.
- **Command palette:** modal, fuzzy search, grouped results (Apps/Services/Domains/Actions),
  keyboard-first.
- **Confirm dialog:** for destructive/irreversible actions; **typed** confirmation for delete/
  restore/wipe; shows a diff where relevant (rollback).

## 4. Global chrome

- **Sidebar:** grouped nav (doc 07), collapsible; active item uses brand tint; mobile = slide-over
  with backdrop (as-built).
- **Top bar:** workspace switcher · command-palette trigger (Cmd/Ctrl-K, replaces the dead search
  box) · theme toggle (dark/light/system) · language toggle (fa/en) · account menu.
- **Activity indicator:** shows when background jobs (deploys/backups) run; opens a mini activity
  list.

## 5. Per-page specifications

Format: **Purpose · Key components · Essential info · Primary / Secondary actions · Empty ·
Loading · Error · Permission · Mobile.**

### 5.1 Overview (dashboard)
- **Purpose:** system health + your apps at a glance.
- **Components:** health strip (servers/Traefik/SSL/Docker), 4 resource stat cards (CPU/mem/disk/
  deployments), quick actions, apps grid, recent deployments, recent errors, onboarding (empty).
- **Essential info:** is the platform healthy? what's running? what recently broke?
- **Primary:** Create App. **Secondary:** deploy, routes, backups quick links.
- **Empty:** onboarding 3-step card + "Create Application".
- **Loading:** skeleton strip + card placeholders. **Error:** if metrics source down → amber banner
  "Docker unreachable — metrics paused" (as-built; keep).
- **Permission:** all roles read. **Mobile:** cards stack 2→1; strip wraps.

### 5.2 Create App
- **Purpose:** define an app in ≤ 4 fields.
- **Components:** Name; **Source card grid** (Git · Dockerfile · Compose · Image · Static ·
  Template) — each card has icon + one-line description; source-specific field(s); **Advanced**
  disclosure (slug, branch/tag, token, port, size, domain, server); "Build & deploy now" toggle.
- **Primary:** Create & deploy. **Secondary:** Cancel.
- **Empty/Loading:** n/a (form). **Error:** inline per field; quota/capacity errors as a form-level
  banner with a link to fix. **Permission:** Owner/Admin/Developer. **Mobile:** cards 2-per-row →
  1; advanced collapses.
- *Fixes:* exposes all 6 real sources (was 2); Compose/Template selectable only when implemented.

### 5.3 App Detail (tabbed — redesign)
- **Purpose:** operate one app.
- **Tabs:** **Overview** (status, primary URL, current revision + health, Deploy/Roll back, recent
  deploys), **Deployments** (history list → detail), **Logs** (runtime, live + download), **Env**
  (vars, secret/reveal, build/runtime flag), **Domains** (add/remove + DNS guidance), **Metrics**
  (CPU/mem/requests), **Settings** (size, health path, server, delete).
- **Primary (Overview):** Deploy. **Secondary:** Roll back, Restart, Stop/Start, Open URL.
- **Empty:** never-deployed app → "Deploy for the first time" CTA on Overview.
- **Loading:** per-tab skeletons. **Error:** container/runtime unreachable → banner + retry.
- **Permission:** actions gated per role; Viewer sees read-only tabs. **Mobile:** tabs become a
  select/scrollable bar; actions in an overflow menu.

### 5.4 Deployment Detail
- **Purpose:** watch/inspect one deploy.
- **Components:** **staged progress bar**, error summary (if failed) with suggested cause, unified
  build+runtime **log stream** (mono, autoscroll, download), metadata (commit/trigger/actor/duration).
- **Primary:** Roll back / Retry (context-dependent). **Secondary:** download logs, open app.
- **Loading:** progress shows Queued; logs stream in. **Error:** failed stage highlighted; recovery
  actions inline. **Permission:** deploy actions gated. **Mobile:** progress stacks; logs full-width.

### 5.5 Domains & Routing
- **Domains:** list per app; **Add domain** shows the exact DNS record + live "resolves?" check;
  SSL status per domain (issued/pending/failed with reason).
- **Routing designer (keep):** drag to prioritize; host/path, redirect, HTTPS-redirect, headers,
  basic-auth; live Traefik-config preview; validate + apply (atomic, rollback on error).
- **Empty:** "No custom domains — your app is at {slug}.{root}". **Error:** ACME failure with the
  specific reason + retry. **Permission:** Owner/Admin/Developer(own).

### 5.6 Services (managed DBs)
- **Purpose:** provision/operate a DB/cache.
- **Components:** create (engine card grid + size), detail (connection info with reveal, metrics,
  backups, **console** [Later]), attach-to-app.
- **Primary:** Create / Attach. **Empty:** "No databases yet". **Error:** provision failed → logs +
  retry. **Permission:** Owner/Admin/Developer.

### 5.7 Backups
- **Purpose:** protect and restore.
- **Components:** next-scheduled + last-result banner, Run-now (scope + destination), history
  (restorable rows), schedules, destinations (Local/S3).
- **Primary:** Run backup. **Secondary:** Restore (typed confirm + dry-run), download, edit schedule.
- **Empty:** "No backups yet — protect your apps" + Run-now + Create-schedule. **Error:** failed
  run → reason + retry. **Permission:** Owner/Admin (Operator: run only).

### 5.8 Monitoring
- **Purpose:** resource + app health over time.
- **Components:** health banner, resource charts (host/app), per-route metrics [V1], active alerts,
  alert-channel config link. **Empty/degraded:** "Docker unreachable — metrics paused". **Mobile:**
  charts stack.

### 5.9 Servers
- **Purpose:** manage nodes/capacity.
- **Components:** node list (status, CPU/mem/disk, apps placed), Add-server wizard (agent bootstrap
  + token + optional mTLS), node detail, Test action. **Empty:** just the local node + "Add a
  server". **Error:** node Offline with failing check. **Permission:** Owner/Admin.

### 5.10 Templates
- **Purpose:** one-click stacks. **Components:** category grid, template detail (what it provisions),
  Deploy (name/size/domain). **Empty:** n/a (seeded). **Permission:** Owner/Admin/Developer.

### 5.11 Tenants / Plans (provider console, role-gated)
- **Tenants:** workspace list (plan, usage, status), create + invite, suspend/resume, per-tenant
  usage. **Plans:** tiers (limits, allowed sizes, price) + instance sizes. **Permission:** Owner/
  Admin only; hidden otherwise.

### 5.12 Settings
- Tabs: **Platform** (name/root domain/ACME), **Team** (members/roles/invite), **API tokens**
  (create/revoke, shown once), **Git providers** (connect/OAuth), **Notifications** (channels +
  test), **Appearance** (theme/language). **Permission:** Owner/Admin.

## 6. Accessibility

- Keyboard-operable everything (nav, tabs, palette, dialogs); visible focus rings; logical tab
  order. ARIA roles for tabs, dialogs, live regions (log stream announces politely). Color contrast
  ≥ WCAG AA; status never conveyed by color alone (icon/label too). Forms have labels + inline error
  text tied via `aria-describedby`. Respect `prefers-reduced-motion` and `prefers-color-scheme`.

## 7. Localization & RTL

- All strings via resource files (fa/en); no hardcoded user-facing text. Layout uses logical
  properties so RTL mirrors correctly (sidebar, breadcrumbs, progress steps). Dates/numbers
  localized; code/IDs/commands remain LTR. The language toggle persists (cookie) and re-renders
  without layout shift.

## 8. What this changes vs. today (summary)

Add: staged progress bar, tabbed app detail, command palette + real search, consistent empty/
loading/error/permission states, light theme parity, DNS-guidance component, source card grid.
Keep: palette/identity, sidebar/topbar shell, routing designer, RTL/i18n, PWA. Remove: dead search
box (replaced by palette), any control that implies an unimplemented capability.
