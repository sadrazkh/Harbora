# Detail-page shell: tabs for apps and databases, and a rail that gives its space back

**Date:** 2026-08-10 · **Status:** approved, ready for an implementation plan

This is sub-project **A** of a larger set of panel improvements. The decomposition is recorded here
because the ordering argument is part of the design; the other sub-projects get their own specs.

---

## The request, and what exploring it changed

Ten improvements were asked for at once. Three findings from reading the code changed what the work
is, and two of them changed it before any design was written:

**The app detail page is not thin — it is unstructured.** `Views/Apps/Details.cshtml` is **914 lines**
against the database page's 390. The request was "the page has too little content, fix it", but the
page has a great deal of content and no shape. Adding more to it would make the complaint worse, not
better. That is why this sub-project is a skeleton rather than a content pass.

**A maximum on retained images already exists.** `DeploymentPlanning.ImagesToPrune` takes a `keep`
parameter — "how many rollback-eligible deployments to retain images for". So "images should have a
maximum" may need no code at all: either its default is wrong or no screen shows it. That belongs to
sub-project F, and F starts by finding out which.

**The rail's space is reserved whether or not anything is in it.** `_Layout.cshtml:75` renders the
right rail as `<aside class="… 2xl:w-80 …">` — a fixed 20rem column. The two panels on the app list
(*Quick start* and *Applications overview*) already collapse, and collapsing them shrinks the panels
while the column keeps its width. That is why folding them never widened the list.

---

## Decomposition

| | Sub-project | What it delivers |
|---|---|---|
| **A** | **Detail-page shell** ← *this spec* | Tabs for apps and databases; the rail gives its space back |
| B | App identity | A guaranteed internal subdomain link per app; full pod specifics on Overview |
| C | Usage | An app usage tab level with the database one, with detail |
| D | Volumes | Browse, upload/download over the web, external access, per-volume backup |
| E | Instant backup | Back up or restore an app right now |
| F | Deployments | Deployment tab; surface and set the image maximum |
| G | Learning Centre | Guides with screenshots, a Help button on every page, routing worked examples |

**A is first because six of the ten requests are "a new tab".** Without a skeleton each one invents
its own place, and the page that is already shapeless gains six more shapes.

**G is last** because its screenshots go stale the moment A–F change the screens they document.

**The routing request (`/admin` path routing, forwarding) sits inside G**, not on its own. What was
asked for is a guide and a worked example, not a new capability — unless F finds that path-based
routing does not work, in which case it moves there.

---

## Scope

Apps and databases only. Not nodes, not every detail page in the panel. The pattern this establishes
can be carried further later; carrying it everywhere now would delay delivery for uniformity nobody
asked for.

---

## Architecture: one route per tab

Each tab is a real server-rendered route: `/apps/{id}`, `/apps/{id}/usage`, `/apps/{id}/volumes`,
`/apps/{id}/deployments`.

**Why.** Two of the requests pull against each other — "the page is too crowded" and "show much more".
They only reconcile if each tab reads **only its own data**. Today's page loads cron runs,
deployments, environment variables, domains, volumes and metrics in one request; sub-projects D and E
would add file listings and backup history to that same load. Splitting the routes is what makes
"show more" affordable. It also breaks a 914-line view into five small ones, and this codebase's
repeated lesson is that large files hide defects.

**Cost:** a page load per tab switch.

**Rejected — client-side tabs on one page.** Instant switching and almost no controller change, but
the page must fetch everything on every visit. It optimises the part that is not the problem and
worsens the part that is.

**Rejected for now — hybrid** (real routes, fragment-swapped switching, full load as the no-JS
fallback). Deep links *and* instant switching, at the cost of two rendering paths per tab — the kind
of duplication that drifts apart. If switching latency turns out to hurt, the hybrid builds **on top
of** one-route-per-tab without discarding anything. The reverse is not true.

---

## Tabs, and what moves where

**Apps — four tabs.** *(Backups is deliberately absent; see "What A is not".)*

| Tab | Route | Contents |
|---|---|---|
| Overview | `/apps/{id}` | Status, internal link and domains, pod specifics, scheduled runs (cron), environment variables |
| Usage | `/apps/{id}/usage` | Today's `CpuPercent` / `MemoryUsed`; enriching these is sub-project C |
| Volumes | `/apps/{id}/volumes` | Today's add/remove; browsing and external access are sub-project D |
| Deployments | `/apps/{id}/deployments` | Today's history and rollback; the image maximum is sub-project F |

**Databases — four tabs:** Overview · Access (the existing `Access` page) · Usage · Backups.

Databases get a Backups tab and apps do not, because databases are already a backup target today and
apps are not. The asymmetry is real, not an oversight; it disappears when sub-project E lands.

Three placement decisions, each a judgement rather than a detail:

**Scheduled runs stay on Overview.** The view's own comment gives the reason: for a cron service there
is no container to look at between runs, so "did it run, did it work, what did it say" can only be
answered from history. It leads the page; it is not a sub-tab.

**Environment variables stay on Overview.** A tab of their own is tempting, but someone who edits a
variable almost always deploys immediately afterwards, and Deploy is in the header. Separating them
adds a round trip to the most common sequence.

**Overview gets a place for the internal link and leaves it empty.** Building that link is
sub-project B. The empty slot is what makes B visible rather than forgotten.

**No Settings tab.** Everything considered for one either belonged on Overview or in its own tab. A
Settings tab becomes the bucket for whatever had no home.

---

## The rail gives its space back

**When no rail panel is open, the rail is not rendered and `<main>` takes the full width.** No empty
column remains. This is the actual answer to "free that space so the list shows to the end".

**The way back must not disappear with it.** `Views/Apps/Index.cshtml`'s own comment states the rule —
"never removed: a setting that makes a feature disappear entirely is a setting nobody finds their way
back from" — and do-not-change item 23 is the same rule. So a small control in the list's toolbar,
beside the search box, reopens *Quick start* and *Applications overview*. The feature folds; it is
not removed.

**Persistence is already correct and is not changing.** `Rails.IsOpenAsync` and `Account/SetRail`
store the choice server-side per user, which beats `localStorage` — the same person gets the same
layout on their laptop and their phone.

**Defaults:** the code default becomes closed. A user who has already opened a panel has that choice
stored and keeps it; only someone who never touched it sees the full-width list from now on.

**This applies to every page with a rail**, because the rule lives in `_Layout.cshtml`. That is
broader than apps and databases and is called out here so it is a decision rather than a surprise.

---

## Code shape

`AppsController` is already **1,516 lines**. Four more actions each loading their own data would fix
one problem by worsening another.

**Tabs are a nested Razor layout, not a partial repeated five times.**

```
Views/Apps/_Shell.cshtml      header + tab strip + @RenderBody()
Views/Apps/Details.cshtml     Overview
Views/Apps/Usage.cshtml
Views/Apps/Volumes.cshtml
Views/Apps/Deployments.cshtml
```

Each tab view sets `Layout = "_Shell"` and renders only its own content. The header — title, status,
Deploy / Restart / Stop / Start / Logs — is written **once**. As a partial it would be invoked five
times, and one day one of those invocations would be missed.

**The shell's model is a base class.** `AppTabViewModel` carries the header fields; each tab's view
model inherits it; `_Shell` is typed to the base. The compiler then guarantees every tab supplies
what the header needs — rather than a `ViewBag` that comes back empty at run time.

**The controller** gets one private method that loads the header (app, status, ownership check), and
each action adds only its own tab's data. That is the saving the whole architecture choice was for.

**Placement:** the tab actions live in `AppsController` as a `partial class` in
`AppsController.Tabs.cs`. Routing and filters stay in one place; the files stay small. A separate
controller was rejected: the URLs are all under `/apps/{id}/…`, and splitting them across two
controllers sends the next reader hunting for an action.

Databases get the same treatment: `Views/Databases/_Shell.cshtml` and `DatabasesController.Tabs.cs`.

---

## Testing

A tab strip is exactly the thing that can look right and not be. A link pointing at an action that
does not exist gives a 404 nobody tried; an action with no link is a page nobody finds. Both pass
silently.

- **Tab census.** Every link in the shell resolves to an action that exists, and every action marked
  as a tab appears in the shell. The test reads the source rather than a hand-maintained list — a
  list would become the very thing it was meant to protect. This follows `StartPathCensusTests`,
  which caught a real gap when it was written.
- **Content preservation.** Before the split, a test asserts what today's page contains. Step 1 is a
  pure refactor, and a pure refactor is where things vanish without any test failing, because no test
  claimed they were there.
- **Rail.** With every panel closed the rail is not rendered and `<main>` is full width; with the rail
  closed the reopen control is present (do-not-change item 23).
- **Tenancy.** Every tab returns 404 for an app in another workspace, not content.

---

## Order of work

Each step is test-first.

1. **Shell and tabs for apps, moving existing content only.** No new content. Success criterion:
   **nothing is lost.** This is the riskiest step — it breaks the 914-line view — and everything else
   rests on it.
2. The same for databases.
3. Rail: not rendered when empty, plus the reopen control.
4. Tab census tests. Listed last only because there is nothing to count until the tabs exist; the
   content-preservation test is written with step 1.

---

## What A is not

The internal subdomain link (B) · usage charts (C) · volume browsing, FTP, upload (D) · instant
backup (E) · the image maximum (F) · the Learning Centre and routing guide (G).

**No Backups tab for apps.** Nothing ties a backup to an app today — `BackupTargetType` has no app
member — so the tab would exist and do nothing. A tab that says "coming soon" is the panel promising
a capability it has not got, which is the defect this codebase has just spent two phases removing.
It arrives with sub-project E.

---

## Risk

Step 1 is a pure refactor of a 914-line view. The failure mode is that a section is not carried into
any tab and **no test fails**, because nothing ever asserted it was there. The content-preservation
test exists for exactly that, and it must be written before the file is broken up, not after.
