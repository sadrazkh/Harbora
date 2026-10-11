# Round six — the in-panel AI agent — progress

Design: `docs/superpowers/specs/2026-10-11-harbora-assistant-agent-design.md` (owner-approved 2026-10-11).
Owner's consent rule: safe writes may be approved once ("don't ask again"); dangerous writes always ask;
delete asks for the typed name. Everything else as the design's defaults.

Baseline: master `2331c22`, 6429 `Harbora.Tests`.

## Wave A (parallel)

| Agent | Brief | Branch / worktree | Status |
|---|---|---|---|
| A — app services (0a) | `a-brief.md` | `r6/app-services` · `../Harbora-r6-a` | **landed** `ae91497..1e304fe`, fix re-reviewed clean, 6690 tests |
| B — database services (0b) | `b-brief.md` | `r6/db-services` · `../Harbora-r6-b` | **landed** `f3bb099..1585e37`, review clean, 6636 tests after C+B |
| C — chat client + settings (1) | `c-brief.md` | `r6/chat-client` · `../Harbora-r6-c` | **landed** `038178c..504a09f`, review clean, 6574 tests |
| D — agent core (2) | `d-brief.md` | `r6/agent-core` · `../Harbora-r6-d` | **landed** via `r6/integration` (`bcc0ab8..9d06b49` + coordinator `9a99c4a`), two review rounds clean, 6828 tests |
| E — chat pane (3) | `e-brief.md` | `r6/chat-pane` · `../Harbora-r6-e` | **landed** `a038b2f..a6c4b48`, review clean, 6699 tests after A+B+C+E |

## Wave B (after A, B, D land)

Write and destructive tools over the phase-0 services; then the learning-center page and end-to-end checks.

## Review notes carried forward (minor, for the final whole-branch review)

C (client, settings, bench) — reviewer: approved, no Critical/Important.
- `error: {}` / `error: ""` beside valid choices is treated as a failure; no test for `error: null`.
- `UsageOf` invents a zero when only one of prompt/completion tokens is present.
- The bench tests the saved settings, not the unsaved form beside it.
- The bench is one POST up to 180 s: proxy cut-offs, no double-submit guard, refresh re-posts.
- `data-agent-bench-state` says `failed` when nothing was answered at all (should read `no-answer`).
- A refused settings form discards what was typed (house redirect style).
- Client error texts can carry provider/model/host details: only PlatformManage users should see them
  verbatim (sent to D as a heads-up).

B (database services) — reviewer: approved, no Critical/Important. Minor:
- `DatabasesController` builds both services itself when DI gives none (test-only fallback, keeps
  `scheduler`/`protector` in the constructor) — better: required parameters, update the 4 hand-built tests.
- Removal's "still there" check uses the same tenant filter as the engine, so it cannot catch what its
  comment says; use `IgnoreQueryFilters()` if it is meant to be real.
- `PeekEnvironmentAsync` duplicates `ResolveEnvironmentAsync`'s selection — extract a read-only finder.
- Attach's Field→404/redirect mapping has no HTTP test.
- Misnamed version-case test; stale census exemption text; `AppNotFound` English vs Persian wording;
  attach/detach audit workspace source inconsistent.
- **Wave B trap:** an `EnvironmentId` the workspace does not own silently falls back to the default
  environment (kept for parity). Tools must compare the result's environment with what they asked for.

A (app services) — reviewer: needs fixes. Important: AddVolume/AddDomain/Create run checks before the
creation lock (volume has no unique-index backstop). Sent back to A with: rebase onto master, move checks
under the lock, return all refusals on the project-cap path, AppActor.UserId, app.created target shape,
missing tests, three unlisted behaviour changes into the report.
**Owner decision 2026-10-11:** keep A's refusal of another tenant's private template on app create
(«اوکیه، نگهش دار»).

A re-review: all six findings fixed (lock before every check in create/add-domain/add-volume, released
on every path; race tests commit a row while the lock is awaited). Minor left: a-report.md:340 still says
lock order is unchanged; real advisory-lock serialisation unverified locally (Postgres lane only).

E (chat pane) — reviewer: approved, no Critical/Important. Accepted deviations: word-count text direction
instead of literal dir="auto"; the preview harness is committed (precedent: deployment-logs.preview.html).
Minor, to fix before/with the first deploy:
- Phone: result links land under the full-screen pane again (restore reopens it below 640px).
- A tool spinner can run forever when `done` arrives while it is still running.
- Plan expiry judged on the client clock — a fast clock hides Run; let the server decide.
- Exceptions inside the event handler are reported as "connection lost"; reader not cancelled; dead abort path.
- Composer direction flips while typing.
- No tests for drawer wiring (focus return, Escape, chunk-load retry) or the signed-out case.
- Plan card heading vs step titles differ by one weight only; "Nothing has run yet." and remaining count
  are 11px ink-faint (~3.5:1) — below 4.5:1 for small text.

D (agent core) — two reviewers (engine half, surface half): needs fixes. Sent back with:
Important — (1) a tool timeout (TaskCanceledException) or a failed save after the claim leaves the plan
stuck `executing` with no results/outcome; (2) the 4,000-char head-keeping cap cuts list_resources
(resources beyond the snapshot unreachable) and the failing tail of get_deployment logs; (3) config-group
secrets not redacted by value. Minors folded in: expiry outcome, supersede after gates, validate
change-tracker guard, read-tool timeout fed back, unique ids, get_database redact-before-clip, resolver
display-name fallback, cap/remember/antiforgery tests. get_usage stays open to members (decision).
Integration check before fixes: D replayed onto master (A+B+C+E) in ../Harbora-r6-int — 6802 + 104 node green.

E polish pass dispatched in parallel (phone links under the pane, endless spinner, client-clock expiry,
error attribution, composer direction, superseded card, contrast/heading, drawer-wiring tests).

E polish: re-review approved (all 8 items). Coordinator follow-up 631d92a: a busy (Run in flight) card is
not superseded; drawer loading line ink-muted. Landed on master with E's polish (2883708, 7a3aba7, 631d92a).

D fixes re-review: Important 1, 3 and all minors fixed; list_resources paging stable. New Important:
get_deployment redacts per line, so multi-line secrets (private keys, multi-line env values) escape —
redact the joined tail first. Minors: Clear() in the broken-run fallback (Entries() can throw on a
poisoned tracker); snapshot foreign changes before ExecuteAsync; 409 on re-validation refusal + failed
save; line counter. Sent back to D (second round).

D second re-review: approved. Coordinator follow-up 9a99c4a: orphan BEGIN/END private-key redaction (a key
whose BEGIN is older than get_deployment's 40-row window leaked body+END — pre-existing; 3 new tests fail
on the old rule, verified by mutation); broken run finished before auditing the failing step; olderRows
floored at 0. Master = 9a99c4a, 6828 Harbora.Tests + 133 node, bundle builds.

## Wave B (dispatched after D landed)

| Agent | Brief | Branch / worktree |
|---|---|---|
| F — app tools | `f-brief.md` + `wave-b-common.md` | `r6/app-tools` · `../Harbora-r6-f` |
| G — project/database tools | `g-brief.md` + `wave-b-common.md` | `r6/db-tools` · `../Harbora-r6-g` |
| H — learning-center docs | `h-brief.md` | `r6/assistant-docs` · `../Harbora-r6-h` |

## First production deploy (read-only assistant) — 2026-10-11

- CI green on 9a99c4a, every job incl. the Postgres lane (AgentCore migration and race tests on real Postgres).
- Pre-flight: checkout clean at 9a27445, disk 35%, panel healthy, 17 containers.
- Rollback image: `harbora/panel:before-9a99c4a`.
- `install.sh update` → HEAD 9a99c4a; SSH dropped mid-build but the process kept running on the server;
  log ends "Harbora updated", every verify check passed; panel healthy, restarts 0.
- Migration `20261011104149_AgentCore` applied (120 total). Provider plan has the `assistant` grant Enabled.
- 17 containers before and after (same names). `/assistant/status` anonymous → 302 to login.
- Next for the owner: Settings → assistant (OpenRouter base `https://openrouter.ai/api`, key, agent model),
  run the agent-model test, then open the drawer. Write tools arrive with wave B.

## Paused by the owner — 2026-10-11 (resume from here)

Owner: «هرجا هستی نگه دار بهت بگم ادامه بدی». Every running agent was stopped; every worktree is clean
(work committed). Master = d20cd0c (contracts for streaming/in-progress + u-brief), deployed = 9a99c4a.

| Work | Branch (worktree) | Head | State |
|---|---|---|---|
| F app tools | r6/app-tools (Harbora-r6-f) | dd6c408 | stopped mid-task (was replacing slug prediction with exact-slug refusals); no report yet — resume F |
| G db tools | r6/db-tools (Harbora-r6-g) | f839ede | DONE, 6947 tests; review was stopped — re-dispatch review (package /tmp/r6-review-g.diff, base d41247d) |
| H docs | r6/assistant-docs (Harbora-r6-h) | e919153 | DONE + coordinator correction; re-check against F/G tools before merge |
| U-C streaming client | r6/chat-client (Harbora-r6-c) | 1cfbd2f | DONE, 6874 tests; review was stopped — re-dispatch (package /tmp/r6-review-uc.diff, base d20cd0c) |
| U-D resilient turns | r6/agent-core (Harbora-r6-d) | 108bd36 | stopped mid-task (was writing turn tests); resume D |
| U-E typing/resume | r6/chat-pane (Harbora-r6-e) | e10dec6 | stopped before its full-suite run; resume E |

Owner's model settings on prod: model nvidia/nemotron-3-super-120b-a12b:free (bench 6/6); advised
fallbacks google/gemma-4-31b-it:free, google/gemma-4-26b-a4b-it:free.
Found by G, not changed (for the task table): ManagedServiceEngine.StartAsync marks a database Running
when it found no container; DatabaseProvisioningService.CreateAsync clears the shared change tracker on a
billing refusal. Pane has no UI to revoke "don't ask again" (API only).

## After the pause — 2026-10-12

Owner: «ادامه بده تمومش کن و دیپلوی کن». Then: secrets as black boxes before the deploy (lock button →
handle; value never reaches the model) — owner chose to build it in this round.

| Work | Outcome |
|---|---|
| G db tools | review approved; minor fix pass (rotate failure reporting + new-password redaction, delete card names users); merged 5dfb2e1, 6951 tests |
| U-C streaming | review: 2 Important (callback faults misreported as provider failures; "stream" heuristic) → fixed, re-review approved; merged 707f896, 7032 |
| U-D resilient turns | review approved; minors + secrets vault/endpoint; vault review approved; hardening pass (scrub by value in history/cards, expiry window, fail-closed DI, masked DTO, size limit); merged 3f1d0c9, 7077 |
| U-E typing/resume/lock | review: 3 Important (stuck dots, words lost during create, silent failed read) → fixed; lock button; re-review: Important (password-manager save) → text field + CSS mask + opt-outs; coordinator follow-ups af30c2a; merged, 7077 + node 217 |
| F app tools | review: 3 Important (secrets via model args, remembered set_env_vars downgrading a secret, rollback target drift) → handles/generate, kept flags, pinned rollback; re-review Important (template plain-variable handle not atomic) → refused; merged 5737b07, 7301 |
| H docs | guide + READMEs brought to what was built; merged e4c467c, 7304 |
| Final review (server) | ready to deploy; no Critical/Important; deferred notes triaged |
| Final review (pane↔server) | every call matches; Important: revalidation_refused shown as "already ran" → sent to E with minors |

A transient network error (SSL verification) killed three agents once; all resumed from their worktrees
with nothing lost.
