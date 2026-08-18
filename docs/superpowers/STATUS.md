# Where this work stands — 2026-08-18

Written so the owner can put this down and pick it up later without re-deriving anything.
Everything below is merged and **deployed** to `platform.irnetfree.info` at `75b016d`.

Tests: **4,897 passing, 0 failing.** Build: 0 errors, 2 pre-existing NU1903 warnings on SSH.NET
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

### 2. Deleting a project still requires emptying it by hand — **in progress**

`ProjectsController.Delete` now **refuses with a named list** of the apps and databases in the way and
says plainly that deleting a project will not delete them for you — which is a large improvement on
the raw constraint violation it used to be. But the owner's complaint stands: there is still no way to
delete a project and its contents in one deliberate act. What is missing is a confirmation screen that
lists what would go and does it, not a change to the refusal. Being built on `proj-delete`.

### 3. Functions — **explored, three defects fixed, editor in progress**

Explored properly on 2026-08-18; the spec is
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
- **The panel lost its route to every function app on each update.** It joined each app's network
  imperatively at deploy time while compose declared only `harbora`, and the documented upgrade
  recreates the container. Cron and event calls then recorded *"Could not reach the function app."*
  for ever. It now re-attaches on boot. **Derived from four cited facts, not measured — there is no
  Docker here — and still unconfirmed against a live daemon.**
- **A rollback never cleared the unpublished flag**, so the editor showed a green "live" chip over
  code that was not running.

**One decision was made wrong and then corrected.** "Run now runs the buffer" was chosen before it was
known that running the buffer requires a full image rebuild — which made Run now a second name for
Publish and cost the only way to test a cron function without waiting for 03:00. It now runs the
published version on `apps.operate`, and the editor says beside the button which code it will reach.

**Still open:** the editor itself. A bare `<textarea>` with no id, no JS and no draft protection.
Being replaced on `fn-editor` with a lazily-imported CodeMirror island — the panel already ships Vite 6
and a Vue island registry, so it is a fifth island, not a rewrite.

### 4. The rest of the audit roadmap — phases 4, 5, 7, 8, 10–16

Listed by the audit because audits list things. **None of them is in the "a customer problem stays
silent" class** — that class was Phases 6 and 9, and both are done. Pick these up by need, not by
number.

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

- **No Docker or Postgres on the development machine.** Twelve migrations shipped this week were
  verified by snapshot diff and a fresh build, never against a real PostgreSQL. The CI Postgres lane
  is where that first happens.
- **Apps on a remote node** show unknown health and uptime: the node command allowlist has no inspect
  verb. Documented at the call site.
- **Server `91.99.205.231` is abandoned** at `9f5a9fb` (2026-08-08) by the owner's decision. Only
  `57.131.136.56` is current.
