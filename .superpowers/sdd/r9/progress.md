# Round nine ledger: easier, safer deploys (owner request 2026-11-11)

The owner asked what, apart from sales and payment, would make deploying a project easy, safe and simple
for them and for customers. From a list of twenty they chose 1, 3, 6, 7, 8, 9, 10, 11, 12, 14, 16 and 17,
to be done now with parallel agents as far as possible, and asked which model suits each.

Not asked: merging to master, pushing master, deploying. Work lands on `r9/int` and the owner chooses.
The one push in this round is agent F's branch `proof/r9`, because the real proof only runs on CI.

Base for every agent: `r9/base`, which is master `b3fd974` plus these briefs. `r9/int` is `docs/baseline`
(master plus the unpushed baseline documents) with the agents' branches merged in, so the status documents
can be updated in the same change. The proof branch is based on master, so pushing it does not publish the
baseline documents the owner said they would push themselves.

| Agent | Items | Model | Worktree | Status |
|---|---|---|---|---|
| A | 1 env files never uploaded; 11 a CI example | Opus | ../Harbora-r9-a | dispatched |
| B | 3 a daily backup for a new database; 16 a database stuck being set up; 12 imports over 128 MB | Opus | ../Harbora-r9-b | dispatched |
| C | 8 pick a repository from a list; 7 the webhook connects itself; 12 a retry keeps answers | Opus | ../Harbora-r9-c | dispatched |
| D | 9 cron gets the app's real environment; 14 jobs and build directories are cleaned up | Opus | ../Harbora-r9-d | dispatched |
| E | 6 every deployment is checked, and a bad one offers the way back | Fable | ../Harbora-r9-e | dispatched |
| F | 10 the real proof on more stacks and databases | Fable | ../Harbora-r9-f | dispatched |
| G | 17 the CLI and API can act in a team workspace | Fable | ../Harbora-r9-g | dispatched |

Decided by the coordinator on the owner's behalf, to be confirmed in the final report:

- Item 3: daily, keep seven, as proposed when the list was shown.
- Item 6: one press, not automatic, because a rollback cannot undo a database migration.
- Item 14: settled jobs are kept 30 days.
- Item 17: no schema change this round; pinning a token to one workspace is left as a next step.
- No migration and no new job kind for anyone but E.

## Paused by the owner, 2026-11-11 (resume from here)

All seven agents were cut off by a network error (the API could not be reached) part-way through, and the
owner then asked to hold everything where it is and continue later. Nothing was resumed. Nothing was pushed:
the remote has no proof/r9 branch. No agent wrote a report yet.

Each worktree is left exactly as its agent left it, including uncommitted work. Do not clean them.

| Agent | Committed over the base (e84a288) | Uncommitted files | Where it was |
|---|---|---|---|
| A | nothing | 16 | env-file rule written in the CLI, the archive filter and the shared list; about to write the shared-rule tests |
| B | nothing | 13 | backup-on-create and the reconciler in progress, guides touched; about to write the tests for item 1 |
| C | 209e536 retry keeps typed answers | 11 | picker and webhook in progress in the Git client, provisioning and Launch |
| D | 01f5d5b cron gets the effective environment | 7 | Jobs retention written; about to write its rule tests; build directories not started |
| E | nothing | 30 | job kind, pipeline hook, planner and views in progress; was on the deployment page |
| F | 48643ed readiness and connection checks for MongoDB, RabbitMQ, NATS | 8 | seven new fixtures on disk, no run pushed yet |
| G | b59ad32 the panel honours a named workspace and checks membership | 6 | CLI side in progress |

To resume: send each agent a message to continue from its worktree, or start a fresh agent on the same
worktree and brief, telling it to read git status and git diff first because half of the work is there.

## Resumed, 2026-11-12

The owner said to continue. All seven agents were resumed from their own transcripts, each told to read
git status first and trust the disk. Worktrees were unchanged since the pause.

## The three Fable agents held, 2026-11-12

The owner said to commit the Fable tasks where they stand and do the rest later, and to keep the Opus tasks
going until told otherwise. E, F and G were stopped and their work committed. Each worktree is clean.

| Agent | Head | What is committed | What is left |
|---|---|---|---|
| E | a67abd9 | 81b7086 the checks after a deployment and the offer to go back; a67abd9 a checkpoint of refinements and a just-started HTTP test, not built or tested | finish the HTTP tests, full test run, report |
| F | 3e8effb | 48643ed readiness and connection checks for MongoDB, RabbitMQ, NATS; 3e8effb the proof extended to the new stacks and engines | 3e8effb was pushed to proof/r9 and started a run nobody has read yet; read it, fix what it shows, repeat; report |
| G | e059b1d | b59ad32 the panel side; 80f44b9 the CLI side; e059b1d a checkpoint of the guides and the token workspace lookup with tests in progress, not built or tested | finish those tests, full test run, report |

The remote branch proof/r9 exists and is left there. A, B, C and D (Opus) keep running.

## The four Opus agents are done and merged, 2026-11-12

| Agent | Range on its branch | Its own full run | Merge on r9/int |
|---|---|---|---|
| A | e84a288..19b2928 | 9544 passed | fe5b8eb |
| D | e84a288..c4c33f7 | 9483 passed | d33d103 |
| B | e84a288..6dd702a | 9513 passed, 537 front-end | 1366bf6 |
| C | e84a288..1c3f656 | 9532 passed, 535 front-end | 7d7a0ae |

All four merged with no conflict. On r9/int at 7d7a0ae: build 0 errors (the two NU1903 package warnings only);
Harbora.Tests 9701 passed, 0 failed; NodeAgent 500 passed, 17 skipped; NodeIngress 15 passed; Postgres 118
skipped here; front-end tests 539 passed; Vite build ok. Harbora.Tests.dll named twice in the log.

The coordinator read, beyond the reports: the env-file rule; the build-directory sweep and its ownership
check; how the first automatic backup is delayed (a real NextRunAt, not a stamped LastRunAt); every new
unfiltered read of a Git account (each is pinned to the caller's workspace) and the webhook action.

The status documents under docs/ were updated for this work in the same change.

Found by B and confirmed by reading, not fixed because it is outside the chosen items: a database-only
template returns before committing its creation transaction (TemplateDeploymentService, lines 79, 188, 295),
so on PostgreSQL the database, its charge and its job would roll back while the page reports success.

Still held: E, F and G. Not done: merging to master, pushing, deploying.
