# Where this work stands — 2026-08-18

Written so the owner can put this down and pick it up later without re-deriving anything.
Everything below is merged and **deployed** to `platform.irnetfree.info` at `678ba7a`.

Tests: **4,933 passing, 0 failing.** Build: 0 errors, 2 pre-existing NU1903 warnings on SSH.NET
(SSH.NET arrives transitively via the SFTP backup transport; security is out of scope by standing
instruction, so they are left alone deliberately).

---

## Done and live

| | What |
|---|---|
| **Phase 1** | Deploy-engine truth and the job queue |
| **Phase 2** | Node and platform confidence |
| **PAYG billing** | Hourly deduction, wallet, suspension at zero, the bill |
| **Ten panel improvements** | See below |
| **Phase 6** | Monitoring and alerting (M1–M4) |
| **Phase 9** | Notification system (N1–N5) |
| **Phase 3** | All but P8 (P1–P7) |
| **Three loose defects** | Cross-tenant container retirement · remote one-off output · database rotation |

### The ten improvements, individually

A (tab shell) · B1 (public address) · B2 (private address) · B3 (pod specifics) · C (usage window) ·
E (instant app backup) · F (rollback depth) · G (Learning Centre) · D1 (per-volume backup) ·
D4 (temporary download link).

**D2 and D3 were withdrawn — they already existed** at `apps/{id}/data` under `AppDataController`.

---

## Open, in the order worth doing

### 1. Phase 3's last piece — P8, preview environments to GA

The only remaining item with real operational risk. Exploration found **none of the three GA
conditions holds**:

- **Webhook PR events do not exist at all.** `EventName` is captured at `WebhooksController.cs:29`
  and read by nothing; a pull-request payload exits as "no ref" with HTTP 200.
- **URL surfacing is half real.** The preview's own page shows it; the parent's list does not, and
  `IGitProviderClient` can only list repositories, so there is no comment-back.
- **Teardown code is real and has zero test coverage**, and merge is not a trigger — the only signals
  are a 7-day idle sweep and a GitHub-shaped `deleted:true`.

**Why it matters:** preview environments are not being collected on merge, so they linger for a week
holding containers, volumes and ports. That is a resource leak, not a missing feature.

**Three decisions it needs first:** whether the domain gains a pull request or keeps branch keying;
whether GA includes posting the URL back to the PR; and what teardown does when a branch is deleted
while a deployment is in flight.

### 2. The rest of the audit roadmap — phases 4, 5, 7, 8, 10–16

Listed by the audit because audits list things. **None of them is in the "a customer problem stays
silent" class** — that class was Phases 6 and 9, and both are done. Pick these up by need, not by
number.

---

## Functions and project deletion — done and deployed 2026-08-18

Both were the owner's complaints on 2026-08-18: *Functions is buggy, unclear and Notepad-like, and you
cannot even delete a project.* The spec is
[`2026-08-18-functions-design.md`](specs/2026-08-18-functions-design.md).

**The old note that "no host image has ever been built" was wrong.** The generator was copied into a
scratch harness and all three hosts were built and run on this machine: health answered with the right
function count, routes resolved, an unsigned invoke got 401 and a signed one 204. **The runtime was
never the problem — the panel was**, in three places, all now fixed and on master:

- **"Run now" opened a `<form>` nested inside the save form.** HTML forbids nesting, so the parser
  dropped it and both buttons posted to Save — pressing Run now answered *"Saved. Press Publish to
  make it live."* since the feature's first commit. A comment two lines above named the exact failure
  and then committed it. No test had ever rendered the view; there is now one that parses the DOM with
  AngleSharp, because no assertion over the Razor source could catch it — the inner tag genuinely is
  in the file.
- **The panel lost its route to every function app on each update**, and this one was **confirmed on
  the live server, not derived**. It joined each app's network imperatively at deploy time while
  compose declared only `harbora`, and the documented upgrade recreates the container. Before the
  2026-08-18 deploy the panel was attached to one tenant network; the `n8n-production` network had
  existed since 2026-08-10 and the panel **was not on it** — that route had been dead for eight days
  and nothing said so. It now re-attaches on boot: `Rebound the panel to 2 of 2 tenant network(s)`.
- **A rollback never cleared the unpublished flag**, so the editor showed a green "live" chip over
  code that was not running.

**One decision was made wrong and then corrected.** "Run now runs the buffer" was chosen before it was
known that running the buffer requires a full image rebuild — which made Run now a second name for
Publish and cost the only way to test a cron function without waiting for 03:00. It now runs the
published version on `apps.operate`, and the editor says beside the button which code it will reach.

### The editor

A lazily-imported CodeMirror island over the textarea, which **still works alone** — the Vue mount
only happens after the dynamic import resolves, so with JavaScript off or broken the real
`<textarea name="Code">` is untouched and the form still posts. Highlighting, line numbers, bracket
matching, auto-indent, a real undo history, find-and-replace, and tab indenting instead of escaping
the field. Plus draft protection on leaving unsaved, and code history kept 20 deep and restorable.

**Measured on this machine, not estimated:** the entry bundle went 129.00 → 129.78 kB gzip — **+0.78 kB
for every page**, which is only the registration glue. The editor's own chunk is 112.46 kB gzip and the
grammar splits per runtime (C# 120.23 · Python 140.46 · JavaScript 154.79 kB total), all of it on that
one page.

### Deleting a project

A confirmation screen that **names every app, database, domain and scheduled function** that would go,
gated on typing the project name — the convention `ServiceRemovalPlan` already set here. The screen and
the deletion read **one** `ProjectRemovalPlan`, and a test holds them together, because two independent
queries drift and then the screen becomes a lie. The old refusal is untouched: an unconfirmed POST
still gets the same named list.

**What does not get deleted, stated rather than hidden:** legacy `Backup`/`BackupSchedule` rows and the
backup module's policies reference apps by a loose string, not a foreign key, so they are orphaned
rather than removed. That was already true of deleting a single app.

**A real bug surfaced on the way:** `AppOperationsService.DeleteAsync` dropped an app's routes with
`ExecuteDeleteAsync`, which the test provider cannot run. `HostPortAllocator.RemoveAsync` three lines
below already carried the fix and said why — the routes line had simply never been reached, because no
test had ever driven app deletion through HTTP.

---

## Two things to fix about the process before continuing

**`docs/product-audit/backlog.json` has no status field.** Sixty-six items, none marked done. Every
phase therefore starts with exploratory archaeology, which is most of why this took as long as it did.
Turning it into a real tracker is a small job that pays for itself immediately.

**Nineteen git worktrees are active on this repository**, with several sessions committing at once.
Today one agent's staged file was swept into another's commit, and `dotnet ef migrations remove`
deleted a pre-existing migration. Nothing was lost — every case was caught — but the arrangement is
not stable.

---

## The finding worth carrying into whatever comes next

**Twelve times, a capability assumed missing already existed.** Three of those, the person who assumed
was the author of the spec. The twelfth was the Functions runtime: a note in memory said no host image
had ever been built, and all three build and run fine.

The one that taught the method: a spec claimed volume browsing and upload did not exist. They had
shipped for months. The search had been for `BrowseVolume` and `FileBrowser` — **the names the feature
would have had.** It lives under `AppData`.

**Search for what a thing does, not for what you would have called it.** Before building anything in
this codebase, check it is not already there.

---

## Known gaps in verification, stated so nobody assumes otherwise

- **No Docker or Postgres on the development machine.** Migrations shipped this week were verified by
  snapshot diff and a fresh build, not against a real PostgreSQL. **One exception:**
  `FunctionCodeRevisions` was confirmed applied on the live server's PostgreSQL on 2026-08-18, table
  and all. Deploying to server 57 is a genuine verification lane and was not being used as one.
- **"No Docker here" is weaker than it was taken to be.** The Functions runtime was written off as
  unproven for that reason; copying the generator's output into a scratch directory built and ran all
  three hosts. Before declaring something unverifiable, check whether the artefact under test is
  really the container or merely reached through one.
- **Apps on a remote node** show unknown health and uptime: the node command allowlist has no inspect
  verb. Documented at the call site.
- **Server `91.99.205.231` is abandoned** at `9f5a9fb` (2026-08-08) by the owner's decision. Only
  `57.131.136.56` is current.
