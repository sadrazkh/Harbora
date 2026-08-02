# Panel redesign — Sub-project A: design system and app shell

**Date:** 2026-08-02
**Status:** design, awaiting review
**Scope:** the token layer, the application shell, the navigation structure, and the shared
components every page will be built from. No page content is reworked here.

---

## 1. Why this is split up

The request — "make the panel look like these six mockups, add users and admin, add graphical
network configuration" — is not one piece of work. It is six, and they fail differently:

| | Sub-project | Independent because |
|---|---|---|
| **A** | Design system + app shell | Everything else renders inside it |
| **B** | Restyle the 17 existing sections | Mechanical, per page, no behaviour change |
| **C** | Real telemetry | Backend collection; the largest and riskiest piece |
| **D** | Users and admin | Roles exist; a first-class section does not |
| **E** | Graphical network configuration | Editing touches the network layer |
| **F** | Seed a test user on the server | Small, last |

Agreed build order: **A → B → D → E → C → F**.

This document specifies **A only**.

### The constraint that shaped everything

The mockups show roughly forty panels. Harbora collects four metrics: `cpu.percent`,
`disk.used`, `disk.total`, and memory. Uptime, request counts, p95 latency, error rate, network
traffic, packets/sec, open ports, internal DNS, deduplication savings, replica counts and the
incident feed do not exist as data.

Building those panels as they appear in the mockups would produce a panel that displays invented
numbers. That is the exact defect this codebase has spent its recent history removing: features
that report success while doing nothing. So the honesty rule below is a rule in code, not a
convention.

---

## 2. Tokens

### 2.1 What exists

`src/Harbora.Web/Scripts/app.css` already maps Tailwind's `slate` scale onto CSS variables, and
light mode inverts the ramp so `bg-slate-900` becomes a white surface. `tailwind.config.js` wires
this up, and `_Layout.cshtml` applies the theme before first paint. Dark, light, system and RTL all
work today.

This means the redesign is a **retune plus an additive semantic layer**, not a rewrite.

### 2.2 What changes

The light ramp is retuned from neutral slate to the mockups' warm violet-tinted neutrals, and a set
of semantic tokens is added **alongside** the existing ones. Old markup keeps working; new markup
gets honest names.

```
--surface          card background            light #FFFFFF        dark #17141F
--surface-2        raised / hover             light #FAFAFC        dark #1E1A29
--canvas           page background            light #F8F7FD        dark #0F0D17
--border           hairline                   light #EFEDF5        dark #262231
--border-strong    input / divider            light #E2DFEE        dark #332E42
--text             primary                    light #1A1523        dark #ECEAF2
--text-muted       secondary                  light #6B6880        dark #A9A4BC
--text-faint       tertiary                   light #8B8799        dark #75708A
--brand            fill, white text on it     light #6D4AFF        dark #6D4AFF
--brand-hover                                 light #5B37E8        dark #7C5BFF
--brand-text       links and icons            light #5B37E8        dark #A78BFA
--brand-soft       tinted background          light #F1EDFF        dark #241E3A
```

Two values in the first draft of this table failed the contrast test specified in §7, and were
corrected before any code was written — which is the reason that test exists:

- `--text-faint` was `#9B98AB`, giving **2.81** on white against a 3.0 floor.
- `--brand` in dark was `#8B6DFF`, giving **3.67** with white text against a 4.5 floor. The fill is
  therefore the same violet in both themes, and the lighter `#A78BFA` became `--brand-text`, used
  only for links and icons on dark surfaces where it measures 6.55.

Status colours, used by pills, chart strokes and the graph legend:

```
ok      #16A34A on #DCFCE7      warn   #D97706 on #FEF3C7
error   #DC2626 on #FEE2E2      info   #2563EB on #DBEAFE
idle    #6B7280 on #F3F4F6
```

Chart series, in assignment order: `#3B82F6`, `#8B5CF6`, `#F59E0B`, `#10B981`, `#EF4444`, `#06B6D4`.

Shape and rhythm, read off the mockups:

- card radius `12px`, control radius `10px`, pill radius full
- card shadow `0 1px 2px rgb(16 12 40 / 0.04)`; hover `0 4px 12px rgb(16 12 40 / 0.06)`
- card padding `20px`; grid gap `20px`
- type: page title `24px/700`, panel title `15px/600`, body `13–14px`, caption `11–12px`
- fonts unchanged: Inter for Latin, Vazirmatn for Persian

Light becomes the default theme. Dark remains, with the ramp above — the existing toggle keeps
working and is not removed.

---

## 3. The shell

Three regions, replacing the current two.

```
┌──────────┬────────────────────────────────────────┬──────────────┐
│          │  topbar: env ▾   search ⌘K   🔔 ☀ 👤   │              │
│ sidebar  ├────────────────────────────────────────┤  right rail  │
│ 240px    │  page header: title · pill · actions   │  320px       │
│          │                                        │  (optional)  │
│ nav      │  content grid                          │  contextual  │
│          │                                        │  panels      │
│ server   │                                        │              │
│ health   │                                        │              │
│ user     │                                        │              │
└──────────┴────────────────────────────────────────┴──────────────┘
```

**Sidebar** — logo, grouped navigation, a server health card (CPU / memory / disk / network bars,
from real samples), the signed-in user, and a Collapse control that reduces it to a 64px icon rail.
The existing mobile off-canvas behaviour is kept.

**Topbar** — environment switcher (real: reads `Environments`), global search with `⌘K`,
notification bell with unread count, theme toggle, avatar menu.

**Right rail** — a stack of contextual panels, opt-in per page via a `RightRail` section. Below
`1280px` it moves underneath the main content rather than disappearing, because on the mockups it
carries real information, not decoration.

**RTL** — every offset uses logical properties (`ms-`/`me-`/`start`/`end`), which is already the
codebase's pattern. In Persian the sidebar is on the right and the rail on the left, mirrored by the
existing `dir` attribute. Numerals inside charts and log tables stay LTR.

---

## 4. Navigation

The mockups' visual language, with every section Harbora actually has. Nothing functional is
dropped, and nothing is invented — there is no Billing section, because there is no billing.

```
Overview        Dashboard

Deploy          Applications · Services · Databases · Deployments
Connect         Networks · Domains & SSL · Routing
Data            Storage · Backups
Insight         Monitoring · Audit log
Build           Templates · Git
Platform        Servers · Users & Team · Plans · Tenants · Settings
```

Mapping to what exists: **Services** → managed services; **Storage** → volumes;
**Users & Team** → sub-project D; **Networks** → sub-project E; **Domains & SSL** → the existing
Domains section, absorbing certificate state.

Group headers render as small uppercase labels. Items the signed-in user has no capability for are
hidden, not disabled — the sidebar should not advertise doors that are locked.

---

## 5. Shared components

Razor partials in `Views/Shared/Design/`. No new frontend framework; the existing Vue islands
(deploy logs) keep working unchanged.

| Partial | Purpose |
|---|---|
| `_StatCard` | icon tile, label, value, delta line |
| `_Panel` | bordered card, header with optional "View all", divided body |
| `_StatusPill` | status → colour, absorbing today's `_StatusBadge` |
| `_DataTable` | header, rows, end-aligned actions, overflow menu |
| `_EmptyState` | icon, one sentence, one action |
| `_Sparkline` | inline SVG from a real series |
| `_MiniChart` | area and bar charts for the metric panels |
| `_Metric` | **the honesty gate — see below** |

### 5.1 The honesty gate

`_Metric` is the only component allowed to print a measured number. It takes a nullable series or
value and:

- renders the value when one exists;
- renders "not collected yet" — in the page's language — when the source does not exist;
- **never** renders `0`, `—`, or a flat line in place of an unknown.

Every panel the mockups show that Harbora cannot yet populate is built now, in its correct place,
in this state. In sub-project C the collector starts filling the same series and the panels light up
with no markup change.

This is the load-bearing rule of the whole redesign: unknown is not zero.

---

## 6. Out of scope for A

Page content rework (B), users admin (D), network graph (E), telemetry collection (C), the seeded
test user (F). Billing, at all.

---

## 7. Testing

| What | How |
|---|---|
| Contrast | a test asserting every text/surface token pair meets 4.5:1 in both themes |
| The honesty gate | `_Metric` given a null or empty series must not emit a digit — the test with teeth |
| Nav capability filter | a user without a capability does not see that item |
| RTL | rendering a page under `fa-IR` produces `dir="rtl"` and no physical-direction utility classes |
| Routes | every route returns 200 in both cultures and both themes |

Mutation testing on the honesty gate and the capability filter, since both fail silently and both
are the kind of thing that looks right while being wrong.

Verified on the real server at desktop, tablet and mobile widths before the phase is called done.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Retuning the shared ramp shifts all 45 views at once | the new light values are chosen so existing markup lands close to the target; B corrects the rest |
| A page looks half-migrated during B | accepted, and the reason B follows immediately |
| Tailwind purges the new semantic classes | the content globs already cover `Views/**` and `Scripts/**`; a build check asserts the new utilities survive |
| The deploy engine's pages break | those views are touched in B, one at a time, with the route smoke test after each |
| "not collected yet" reads as broken | the phrase names the reason and C is scheduled; better an honest gap than a fabricated number |
