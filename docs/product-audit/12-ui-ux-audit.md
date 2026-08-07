# 12 — UI/UX Audit

Scope: 84 `.cshtml` (57 feature views, 27 layout/partials), 4 Vue islands, PWA shell. The UI is deliberately server-rendered with islands — a sound choice; **nothing here recommends redesign**. Findings are keep/fix lists. Page-by-page inventory with line evidence lives in the exploration record; this file keeps the audit verdicts.

## 1. What is genuinely excellent (protect — see 19)
- **"Unmeasured ≠ zero" discipline** via `_Metric.cshtml` ("the only component permitted to print a measured number") + `AllocationReading`. Rare even in commercial products.
- **Destructive-action pages**: `Databases/ConfirmRemove` (names the orphaned volume, lists apps that break, type-the-name gate), `Apps/ConfirmRollback` (refuses with reason when artifact pruned), `Networks/ConfirmMove`, `CloneEnvironment` pre-flight.
- **Live truth checks**: per-domain DNS/TLS test buttons; architecture map drawn from real env vars; template deploy submit disabled with explanation when no version is offerable.
- **RTL craft**: logical properties everywhere, `rtl:rotate-180` on directional icons, deliberate LTR islands for code/IDs/terminal/graph, Inter+Vazirmatn.
- **App console density done right** (`Apps/Details.cshtml`, 914 lines): operations bar, env vars, volumes, domains w/ live checks, previews toggle, protection — one page, clear sections.
- Design-token system (semantic `surface/ink/line/ok/warn/danger` over CSS vars, dark mode, `panel` shadows) + shared empty-state partial + house `TempData` banner pattern + global submit-spinner guard.
- Progressive-disclosure create forms (Apps/Databases) with live summary rails.

## 2. Page-level assessment (condensed matrix)

Legend: Purpose-clear / Info-visible / Empty / Loading / Error / Confirm / Long-op-progress / Big-data-ready / Mobile* / RTL — ✓ ok, ✗ gap, — n/a. (*Mobile = responsive classes present; no dedicated audit device pass was possible in this environment — flagged in 18.)

| Page | Verdict | Notable gaps |
|---|---|---|
| Setup | 🟡 | one long form; no steps/progress; no "what happens next" (P1 UX) |
| Dashboard | ✅ | checklist stops before backup/SMTP |
| Apps Index | ✅ | rail states persisted — good |
| Apps Create | ✅ | — |
| Apps Details | ✅ | emoji glyphs vs lucide inconsistency; env `availableAtBuild` not settable (backend accepts) |
| Apps Logs | ✅ | — |
| Deployments Index | ✗ | no filter/search/pagination — only major list without them (P1) |
| Deployment Details | ✅ | config diff only vs previous |
| Databases (5 pages) | ✅ | provision failure silent (R-15) |
| DB Access | ✅ | rotation broken server-side (R-08) |
| Projects (4) | ✅ | no sidebar entry — reachable only via dashboard tile/breadcrumbs (IA gap, P1) |
| Networks | ✅ | dead link to `/databases/details/{id}` (R-18) |
| Domains | ✅ | — |
| Routes (designer) | ✅ | dev copy shipped ("run npm run build"); island ignores design tokens (only screen whose light mode isn't token-driven); own i18n dictionary |
| Backups (legacy) | ✗ | SFTP form block in wrong loop → destination toggle dead (R-09); verify button missing (backend exists) |
| BackupCenter / SyncCenter | ✅ | modules off by default; fine |
| Storage / Objects / AppData | ✅ | — |
| Monitoring | ✅ | alert rules not editable; no incident view |
| Audit | ✅ | — |
| AI / AiAdmin | ✅ | `/v1` base URL + sample request not shown anywhere (backend-without-docs) |
| Templates (4) | ✅ | no screenshots |
| Git (2) | ✅ | — |
| Users | ✅ | no empty state |
| Tenants (2) | ✅ | stale "next phase" copy; no empty state |
| Plans | ✅ | — |
| Servers (legacy) | 🟡 | no empty state; page renders blank column at zero |
| Nodes (2) | ✅ | sidebar icon missing from bundle (R-19) |
| Settings / AdminSettings | ✅ | — |
| Terminal | ✅ | breadcrumb 404 (R-18) |
| Landing / Error pages | ✅ | — |

## 3. Cross-cutting UX findings

### 3.1 States
- Empty states: present on ~24 surfaces via shared partial; **missing on Servers, Tenants, Users, Monitoring-alerts** (S fixes).
- Loading: house pattern = submit-spinner + per-row `…`; no skeletons (fine for server-rendered).
- Long-operation progress: **only deployments have real progress.** Backup run, restore, node update, clone, template deploy, disk cleanup, measure → fire-and-forget TempData with no way to watch the job. Fix: a lightweight **/activity (job list) page** over the existing `Jobs` table + per-row status chip; link every "Started…" toast to it. (M, Phase 3 — biggest systemic UX gap.)
- Confirmations: three tiers used arbitrary — native `confirm()` (12 sites incl. delete-app), typed `prompt()` (backup restore), dedicated pages (best). Rule to adopt: data-destroying ⇒ dedicated page or typed dialog; config-destroying ⇒ styled modal; never native `confirm()` for irreversibles. Migrate delete-app first.

### 3.2 Localization — the headline problem
- `T[…]` resource usage: 282 across 32 views vs **1,447 inline `isFa ? "…" : "…"` ternaries across 58 views (~84 % of user-visible strings hard-coded)**; islands carry third/fourth dictionaries; terminology drift (Tenants called «مستأجرها» in sidebar, «فضاهای کاری» in topbar).
- Consequences: no translation workflow, no third language possible, background emails English-only (09 fixes the email half).
- Recommendation (Phase 12, L, mechanical): move ternaries into `SharedResource` (+`.fa.resx`), one PR per area; add the existing `LocalizationResourceTests`-style check asserting no new `isFa ?` in views; unify nav label tables into `NavigationMap` as the single source.
- RTL: strong (see §1); fix `offline.html` hard-coded `dir="rtl"`.

### 3.3 Navigation / IA
- Projects absent from sidebar although the model is project-centric (tutorial ch.2 teaches it as the spine). Add nav entry (respecting PanelMode).
- Command palette uses the un-augmented Advanced-only map, misses module sections, renders raw keys (`ai-admin`) for 8 labels (R-21).
- Bell 404 (R-17) → becomes notification center in 09.

### 3.4 Asset defects (S bundle, Phase 1)
13 unimported lucide icons (incl. Nodes/Sync sidebar icons); PWA icons declared but files missing (install prompt rejected); `text-success` non-token class in Terminal.vue; 3 orphan partials.

## 4. Simple Mode / Advanced Mode — assessment & design

**Already shipped:** account-level `PanelMode` (Simple/Advanced) — Simple *folds, never removes*; routes stay live; backfill migration set existing users to Advanced; sidebar/rails/dashboard sections respect it (`PanelSections`, `_AdvancedStart/_AdvancedEnd`). This matches the mission's "no two separate panels" requirement — **the mechanism is correct; finish its coverage instead of inventing a new one.**

Recommendations (Phase 12):
1. **Coverage pass** — apply the fold discipline inside pages, not just nav: App Details Simple = status/deploy/domains/env/logs; folded = volumes, previews, protection, tag pinning, resize. Databases Simple = connection + attach + backup; folded = TLS, rebuild, external access. New App Simple = Git/Image/Template sources only (Compose/CLI-upload under "More sources").
2. **Simple-mode language** — when folded sections hide, the page must still answer "is it OK / what do I do next"; attention strip already models this.
3. **Escape hatches always visible** — "Advanced settings (n)" disclosure per page rather than global-only toggle; per-page expansion remembered (RailPreferences pattern exists).
4. **Palette + search respect mode** (fix R-21 as part of this).
5. **Advanced additions stay Advanced-only:** route designer, node consoles, AI admin, template versions, terminal — already correct.
6. Per-mission mapping: the "Simple user" journey (create → source → deploy → domain → backup → status) is complete today *except backup-in-onboarding* — add checklist step 5 "protect it" (schedule a backup) + step 6 "get notified" (SMTP/Telegram) once 09 lands.

## 5. Mobile & PWA
Responsive classes present throughout; dedicated audit not executable in this environment (18 Q10). PWA: offline shell + SW caching sane; blocked by missing icons (R-20); `lang: fa` hard-coded in manifest (should follow default culture setting).

## 6. Fix bundles (feeds backlog)
- **UX-S1 (Phase 1, S):** dead links ×3, icons, empty states ×4, dev copy, stale phase note, `text-success`.
- **UX-S2 (Phase 2, S/M):** PWA icons + manifest lang, palette map unification, deployments list filters+pagination, setup wizard steps.
- **UX-M1 (Phase 3, M):** /activity job list + toast links (long-op progress).
- **UX-L1 (Phase 12, L):** localization consolidation; Simple-mode in-page coverage; confirmation-tier normalization.
