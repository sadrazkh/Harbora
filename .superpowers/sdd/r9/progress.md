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
