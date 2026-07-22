# 07 — Information Architecture

## 1. Principles

- **Project → App → Deployment** is the mental model. Everything hangs off a Project (a grouping of
  apps + services + domains), inside a Workspace (tenant boundary).
- **Task-oriented, not entity-oriented navigation.** Top-level items map to what users *do*
  (deploy, route, back up), not to database tables.
- **Progressive disclosure.** Two visible tiers: primary nav (always) and contextual tabs (within a
  resource). Provider/admin surfaces are separated and role-gated.
- **Reachability in ≤ 3 clicks** for every common task, aided by a command palette + global search.

## 2. Top-level navigation (redesigned)

Current nav is a flat 13-item list mixing user and provider concerns. Target grouping:

```
■ Overview            (dashboard)
■ Projects            (default working area)
   └─ Apps, Services (DBs), Domains & Routing, Deployments — scoped to the selected project
■ Servers             (nodes, capacity, health)
■ Backups             (runs, schedules, restore)
■ Monitoring          (metrics, alerts)
■ Templates           (one-click apps)
────────── (role-gated) ──────────
■ Tenants / Plans     (provider console: workspaces, plans, quotas, usage)  [Owner/Admin]
■ Settings            (platform, team, API tokens, Git providers, notifications, appearance)
```

Rationale: promotes **Projects** as the home of daily work; demotes provider-only surfaces
(Tenants/Plans) behind a divider and role gate; folds Git providers/notifications into Settings
(they're configuration, not destinations). Deployments and Routing become **project-scoped tabs**
as well as having a global recent view.

## 3. Page hierarchy

```
/                                  Overview (health strip, resources, recent deploys/errors, onboarding)
/projects                          Project list (+ create)
/projects/{id}                     Project home → tabs: Apps · Services · Domains · Deployments · Settings
/apps/{id}                         App detail → tabs: Overview · Deployments · Logs · Env · Domains · Metrics · Settings
/apps/create                       Create app (source card grid + progressive advanced)
/deployments/{id}                  Deployment detail (staged progress + unified logs)
/services/{id}                     Managed DB/cache detail (connection · metrics · backups · console[later])
/domains                           Domains (global) ; routing designer at /routes (kept)
/servers , /servers/{id}           Nodes; node detail (capacity, apps placed, health, test)
/backups                           Runs + schedules + restore
/monitoring                        Metrics + alerts
/templates                         One-click apps
/tenants , /tenants/{id}           Provider console (role-gated)
/plans                             Plans & instance sizes (role-gated)
/settings/*                        platform · team · api-tokens · git · notifications · appearance
/setup                             First-run only (self-locking)
/account/login , /account/denied   Auth
```

## 4. Navigation aids

- **Command palette (Cmd/Ctrl-K)** — fuzzy jump to any app/service/deployment/domain and run quick
  actions (Deploy, Roll back, View logs, Add domain). Replaces the currently decorative search box.
- **Global search** — backend search across apps, services, domains, deployments (by name/slug/
  host/commit). Wired to the same index as the palette.
- **Breadcrumbs** — `Project ▸ App ▸ Deployment #n` on all nested pages; the current workspace is a
  switcher in the top bar.
- **Contextual actions** — primary action per page is a single prominent button; secondary actions
  live in an overflow menu; destructive actions are visually separated and require typed confirm.
- **Persistent status affordances** — the health strip on Overview and a per-app status badge
  everywhere an app is listed; a global "activity" indicator when background jobs run.

## 5. Content priority per surface (what appears first)

- **Overview:** system health (servers/Traefik/SSL/Docker) → resource usage → recent
  deployments/errors → onboarding (only when empty).
- **App detail (Overview tab):** status + primary URL + "Deploy"/"Roll back" → current revision &
  health → recent deployments → quick links (logs, env, domains).
- **Deployment detail:** staged progress → error summary (if failed) → live/unified logs →
  metadata (commit, trigger, actor, duration).
- **Backups:** next scheduled run + last result → run-now → history (restorable) → destinations.
- **Monitoring:** current health banner → per-app/resource charts → active alerts.

## 6. Role-based visibility

| Surface | Owner | Admin | Operator | Developer | Viewer |
|---|:--:|:--:|:--:|:--:|:--:|
| Overview, Monitoring (read) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Apps: deploy/rollback/restart | ✓ | ✓ | ✓ (restart/logs) | ✓ (own) | ✗ |
| Apps: create/delete | ✓ | ✓ | ✗ | ✓ (own) | ✗ |
| Env/secrets edit | ✓ | ✓ | ✗ | ✓ (own) | ✗ |
| Backups: run/restore | ✓ | ✓ | run only | ✗ | ✗ |
| Servers, Settings | ✓ | ✓ | ✗ | ✗ | ✗ |
| Tenants / Plans (provider) | ✓ | ✓ | ✗ | ✗ | ✗ |
| Audit log | ✓ | ✓ | view | ✗ | ✗ |

Nav items and in-page actions are hidden **and** enforced server-side (see doc 10 authorization).

## 7. Empty, loading, and error states (IA-level)

Every list surface defines: an **empty state** (what it is + primary CTA), a **loading skeleton**,
and an **error state** (what failed + retry). Detailed per-page specs are in doc 08. The IA
guarantees these states exist for: Projects, Apps, Services, Domains, Deployments, Servers, Backups,
Monitoring, Templates, Tenants.
