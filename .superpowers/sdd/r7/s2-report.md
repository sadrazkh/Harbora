# S2 report: names people read for the tools, timings, and plans that stay in a reopened conversation

Branch `r7/s2` (based on master `44d38d0`), worktree `E:/Cash.Net/source/repos/sadrazkh/Harbora-r7-s2`.
Status: DONE_WITH_CONCERNS (the concerns are contract readings and one unverified lane, listed at the end).

## What I built

**Labels.** `AgentToolLabels.All` now holds all 29 tools (table below). Each label is an activity in the
present, so the pane can use the same words for "running…" and for the finished line.

**Tool events (`RunReadAsync`).** The `running` event carries `labelEn`, `labelFa`, `category` and no
duration. `done` and `failed` carry the same three plus `durationMs`, measured with
`Stopwatch.GetTimestamp` / `GetElapsedTime` around `ExecuteAsync` (the exception path included, so a tool
that times out still reports how long it took), rounded to whole ms. The fallback summaries
(`Looked up: …` / `بررسی شد: …`, `… failed` / `… ناموفق بود`) and the sentence for a tool that threw
(`<label> failed on the server.` / `<label fa> در سرور ناموفق بود.`) now name the label, not the identifier.
The exception text is still never shown or sent.

**Stored tool line.** `AgentMessage.DurationMs` (`int?`), migration `20261015031807_AgentToolDuration`
(one `AddColumn<int>` nullable, designer and snapshot regenerated). Built first, then
`dotnet ef migrations add AgentToolDuration --project src/Harbora.Data --startup-project src/Harbora.Web --no-build`,
so the snapshot is of the current model. Only reads that ran get a duration; proposals, refused calls and
plan-execution lines keep null, as do all rows from before.

**Conversation view (`GetAsync`).**
- Tool lines: `ToolLabel` (panel language), `ToolCategory`, `DurationMs`, `PlanId`. A tool with no label
  (a retired tool name in an old row) falls back to its identifier under `other`, as `AgentToolLabels.For` says.
- `PlanId` for a plan-execution line is the stored `AgentMessage.PlanId`. For a proposal line it is derived at
  read time from the plans' steps (`ToolCallId` + tool name). No column. The pending plan's proposal lines also
  carry its id.
- `Plans`: every plan of this person's conversation except the pending one, oldest first, at most
  `AgentConversationService.PlanHistorySize` (50, the newest 50 when more qualify), only plans with a proposal
  line inside the 200-message window. Each is `{ plan: plans.ViewOf(...), result, afterMessageId }`.
  `Plans` is an empty list, not null, when there are none.
- `Result` comes from `AgentPlanService.ResultOf(plan)`: it reads `ResultsJson` and goes through the same
  `ResultView(plan, status, steps)` constructor that the approve response, the auto-approved run and the
  broken-run path all use now (I replaced the four `new AgentPlanResultView(...)` sites with it), so the plan id
  and status naming cannot drift. The steps are what `FinishAsync` serialized from the very list the approve
  response returned.
- `AfterMessageId`: the last visible message written during the proposing turn (stored `PlanId` null) before the
  next user message that follows the plan's last proposal line.

Files: `src/Harbora.Domain/Agent/AgentConversation.cs`, `src/Harbora.Data/Migrations/*AgentToolDuration*` and the
snapshot, `src/Harbora.Infrastructure/Agent/{AgentToolLabels,AgentPlanService,AgentConversationService}.cs`.

### Regions of `AgentConversationService.cs` I touched (for the merge with S1)
`using System.Diagnostics;`; the new const `PlanHistorySize` after `ConversationViewMessages`; the doc and body of
`GetAsync` plus two new private statics (`ProposalLines`, `AfterTurnOf`) right after it; in `RunTurnAsync` only the
two lines inside the tool-call `AppendAsync` initializer (`SummaryFa = …,` now followed by `DurationMs = answer.DurationMs`);
the `ToolAnswer` record (new optional `DurationMs`); the whole of `RunReadAsync`. I did not touch `ListAsync`,
`SendAsync` or the token handling.

## The 29 labels

| Tool | English | Persian | Category |
|---|---|---|---|
| `list_resources` | Looking through your projects and resources | مرور پروژه‌ها و منابع | resources |
| `get_app` | Reading an app's details | خواندن جزئیات اپ | app |
| `get_database` | Reading a database's details | خواندن جزئیات دیتابیس | database |
| `get_catalog` | Browsing what can be created | مرور آنچه می‌توان ساخت | catalog |
| `get_deployment` | Reading the deployment's logs | خواندن لاگ‌های استقرار | deployment |
| `get_usage` | Checking your plan's usage and limits | بررسی مصرف و سقف‌های پلن | usage |
| `create_app` | Creating an app | ساخت اپ | app |
| `deploy_template` | Deploying a template as a new project | استقرار قالب به‌عنوان پروژهٔ جدید | catalog |
| `set_env_vars` | Setting environment variables | تنظیم متغیرهای محیطی | env |
| `delete_env_var` | Deleting an environment variable | حذف متغیر محیطی | env |
| `add_domain` | Adding a domain | افزودن دامنه | domain |
| `remove_domain` | Removing a domain | حذف دامنه | domain |
| `add_volume` | Adding a volume | افزودن والیوم | volume |
| `remove_volume` | Removing a volume | حذف والیوم | volume |
| `deploy_app` | Deploying an app | استقرار اپ | deployment |
| `restart_app` | Restarting an app | راه‌اندازی دوبارهٔ اپ | app |
| `stop_app` | Stopping an app | توقف اپ | app |
| `rollback_app` | Rolling an app back to an earlier deployment | بازگردانی اپ به استقرار قبلی | deployment |
| `delete_app` | Deleting an app | حذف اپ | app |
| `create_project` | Creating a project | ساخت پروژه | project |
| `add_environment` | Adding an environment to a project | افزودن محیط به پروژه | project |
| `create_database` | Creating a database | ساخت دیتابیس | database |
| `attach_database` | Attaching a database to an app | اتصال دیتابیس به اپ | database |
| `detach_database` | Detaching a database from an app | جدا کردن دیتابیس از اپ | database |
| `rotate_database_password` | Changing a database's password | تعویض رمز دیتابیس | database |
| `stop_database` | Stopping a database | توقف دیتابیس | database |
| `start_database` | Starting a database | راه‌اندازی دیتابیس | database |
| `delete_database` | Deleting a database | حذف دیتابیس | database |
| `delete_project` | Deleting a project | حذف پروژه | project |

Persian words follow what the plan cards already say (`TitleFa` in the tools: والیوم, راه‌اندازی دوبارهٔ,
تعویض رمز, اتصال/جدا کردن دیتابیس). Category choices that are judgement calls: `deploy_template` is `catalog`
(it comes from a template; it creates a project but the icon for "what can be created" fits better than a folder),
`add_environment` is `project` (`env` is for environment variables and has the key icon), and the rollback is
`deployment`.

## Tests added (23 new, all in `Harbora.Tests`)

- `tests/Harbora.Tests/Agent/AgentToolLabelsTests.cs` (5), against the registry the booted panel builds:
  29 tools, label keys equal the registry's names exactly (a stale or misspelled key fails too), no label equals
  its tool name or contains `_`, every Persian label has Persian letters, every category is one of the constants,
  the three example labels from the brief, and the unlabelled fallback.
- `tests/Harbora.Tests/Agent/AgentProcessDisplayTests.cs` (17): running has the label and no duration; done has
  label + a duration of at least 40 ms for a tool that takes 60 ms, and the stored line has the same number;
  failed (reported and thrown) are timed and named, the exception text never appears; label-based fallback
  summaries; unlabelled tool streams under its identifier and `other`; line label/category/duration in both
  languages and null duration on proposals; a pre-migration row (null duration) and a retired tool name; the
  two-reads-and-a-proposal turn then approve (one entry in `Plans`, `Result` serialized identical to the approve
  response, `AfterMessageId` = the turn's final assistant message, proposal and execution lines carry the plan id,
  reads carry none); the pending plan is not in `Plans`; rejected, superseded and lapsed-but-still-stored-pending
  plans in order with `Result` null; a plan refused at approval has `Result` null; a plan that failed midway keeps
  its result; a plan that ran without a card; call ids repeated from turn to turn pair with the right plan; a
  plan outside the 200-message window is left out and one inside is kept; 60 plans give the newest 50 oldest
  first; another user's and another workspace's plan never appear nor lend their id to a line.
- `tests/Harbora.Tests/Http/AssistantProcessHttpTests.cs` (1): through the real pipeline with the real
  `get_usage` read, asserts the camelCase wire names the pane reads (`labelEn`, `labelFa`, `category`,
  `durationMs` on tool frames; `toolLabel`, `toolCategory`, `durationMs`, `planId`, `plans[].plan/result/afterMessageId`
  on the view) and that `plans[0].result` is byte-identical JSON to the approve response.

Mutation check (done, then reverted with `git checkout`): letting plan-execution lines anchor a card, and
removing the "nothing ran" rule from `ResultOf`, made 7 of the 18 display and wire tests fail
(`Failed: 7, Passed: 11`), so the tests do bite.

## What I ran, and what it printed

All from `E:/Cash.Net/source/repos/sadrazkh/Harbora-r7-s2`, with `export MSBUILDDISABLENODEREUSE=1`.

```
dotnet restore Harbora.slnx --ignore-failed-sources            -> RESTORE_EXIT=0
dotnet build src/Harbora.Web/Harbora.Web.csproj --nologo -v q --no-restore -p:UseSharedCompilation=false
                                                               -> BUILD_EXIT=0, 5 warnings (all NU1900), 0 errors
dotnet ef migrations add AgentToolDuration --project src/Harbora.Data --startup-project src/Harbora.Web --no-build
                                                               -> "Done. To undo this action, use 'ef migrations remove'"
dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~AgentProcessDisplayTests|FullyQualifiedName~AgentToolLabelsTests"
                                                               -> Passed! Failed: 0, Passed: 22, Total: 22
dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~Agent|~Assistant|~MigrationConsistency|~ToolsHttp|~ModelSnapshot"
                                                               -> Passed! Failed: 0, Passed: 844, Total: 844
dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~MigrationConsistency"
                                                               -> Passed! Passed: 2 (model has no pending changes; every migration has its designer)
dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~AssistantProcessHttpTests"
                                                               -> first run FAILED (I had expected an English label; the panel's default
                                                                  culture is Persian, so the view returned the Persian one - the test was
                                                                  wrong, not the code); after fixing the expectation: Passed: 1
```

Full suite once at the end, recipe from CONSTRAINTS (with `--no-restore`), log `/tmp/test-r7-s2.log`:

```
dotnet build Harbora.slnx --nologo -v q --no-restore -p:UseSharedCompilation=false > /tmp/test-r7-s2.log.build 2>&1
BUILD_EXIT=0   (14 warnings: NU1900 unreachable private feed and NU1903 SSH.NET advisory in Harbora.Postgres.Tests; zero CS compiler warnings; 0 errors)
dotnet test Harbora.slnx --nologo -v q --no-build > /tmp/test-r7-s2.log 2>&1
EXIT=0
Passed!  - Failed: 0, Passed:  500, Skipped: 17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:   15, Skipped:  0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed: 7327, Skipped:  0, Total: 7327 - Harbora.Tests.dll
grep -c "Harbora.Tests.dll" /tmp/test-r7-s2.log  ->  2   (present)
```

`Harbora.Tests.dll`: 7327 = baseline 7304 + my 23. `Harbora.NodeAgent.Tests` has 17 skipped before and after (not mine).

## Unverified

- `tests/Harbora.Postgres.Tests` was not run (no Postgres here). The migration is a single nullable `integer`
  column add, `MigrationConsistencyTests` passes against the model, but I have not applied it to a real Postgres.
- No pane code was touched or run; the JSON shapes are checked only from the server side.
- The front end (`npm run build`, node tests) was not part of this task and was not run.

## Concerns and decisions the pane agents and the merge should know

1. **`Result` is null for a plan that was refused when checked again at approval** (status `failed`,
   `ExecutedAt` empty, the approval was answered with a 4xx refusal and no result). This follows the contract
   text on `AgentPlanHistoryView` and the brief's "null when nothing ran", but it means a `failed` plan can have
   no result. The reason is only in the execution lines the refusal wrote (they carry the plan id, state
   "refused"/"skipped"). So the pane must not hide a plan's lines when `result` is null. If you would rather
   give the pane the refused step's reason, `ResultOf` is the one place to change (drop the `ExecutedAt` rule).
2. **`AfterMessageId` can be a proposal line.** It is the last visible message the turn itself wrote. Normally
   that is the closing assistant text. If the turn ends right after the proposal (model failed or hit the cap),
   it is the proposal line, which carries the plan id; if the pane hides lines that carry a plan id, it should
   still draw the card after that position. Lines written by a plan's execution never anchor a card (they come
   after the turn's closing words, and the brief's own test needs the closing text).
3. **Call ids are only unique within one reply**, and `NormalizeCalls` gives a blank id `call_r1_1` in every
   turn, so a proposal line is matched to its plan by call id + tool name, paired newest-to-newest when an id
   repeats. This is exact for unique ids and for ids repeating across turns in order; with the same id in two
   plans created in the same millisecond the order is by `CreatedAt` then `Id`, which could be wrong.
4. **Fallback summary wording.** `Looked up: Reading the deployment's logs` is awkward English, because a label is
   an activity, not a noun. It only shows when a read tool returns no summary of its own (all six built-in reads
   do), so I kept the brief's format with the label swapped in rather than invent a new one.
5. **`Plans` cap**: when more than 50 qualify I return the newest 50 (oldest first among those).
6. **Plan-step durations** are not stored: the brief only asks for reads. The pane's activity block can show
   durations for reads only; plan execution lines have `durationMs` null.
7. The panel's culture decides the language of `toolLabel`/summary (`IsFa` in `AssistantController`), as the summary
   already did; the stream events carry both languages.
8. Contracts on master (`AgentViews.cs`, `AgentCoreModels.cs`, `AgentToolLabels.cs`) were used as they are; I did
   not change any shape. The only edit inside `AgentToolLabels.cs` is filling `All`.
