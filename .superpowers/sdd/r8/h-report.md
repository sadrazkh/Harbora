# H — the customer's guide: report

Branch `r8/h`, based on `0d27393`. Nothing pushed, no migration, no AI, `deploy/live-host-proof.sh` untouched.

## The test result first

**On `15d97f8` (last commit before this report) `Harbora.Tests` has 9 failures, and none come from this work.** All 9 are in `LaunchHttpTests`. They come from `e7a5d00`, which is in my base: it made the upload's name flat and added the launch link to the top bar without updating those tests. `r8/int` has already fixed them in `db8c575`, so I did not fix them here, because that would conflict on merge.

A throwaway merge of `r8/h` into `r8/int` (`c96d1e4`) applied with no conflict and ran **green**: 8719 passed, 0 failed. So the merged result is good. I did not run `0d27393` itself. The evidence for the cause is the failure messages, the diff of `db8c575`, and the green merge.

## What was built

- **`docs/tutorial/15-launch.md`**: Persian only, because the chapters are. It follows the brief's order: what it is, the steps, what it reads and what it does not, what "connected" means, reading the checks, when it says it cannot, the terminal, afterwards.
- **`HelpMap`**: `/launch` answers `15-launch`, including a launch's own page at `/launch/{id}`. `LearningLibrary` needed no change, because it lists chapters from disk.
- **`README.md`, `README.fa.md`**: a short block above Install with the page and the command, and a first bullet in the feature list. C owns the "Every command" line and `docs/cli-deploy.md`, so I touched neither.
- **`docs/tutorial/01-first-steps.md`**: one pointer at the top, the sidebar table, and one sentence about the empty dashboard's card.
- **`docs/tutorial/README.md`**: index row, a pointer above the table, and the no-images sentence now says chapters 10 to 15 (it also left out 14).

## Sentences this round had made wrong, now fixed

`/apps`' **New App** button now goes to `/launch`, so everything that sent people through it to the form was false.

| Where | Was | Now |
|---|---|---|
| `README.md` smoke test, `README.fa.md` smoke test, `deploy/RUNBOOK.md`, `docs/disaster-recovery.md` | Apps → New App | Apps → Full form |
| `README.md`, "Push code straight from your machine" | New App → source "Push from my machine" | Apps → Full form → source "Harbora CLI", plus a line about `harbora launch` |
| Both READMEs, First run | the dashboard shows apps and so on | with nothing in the workspace it offers **Get started** |
| `docs/tutorial/03-applications.md` | «اپ جدید — فرم ساخت»; first-app steps through «اپ جدید» | the button is the launch, the form is «فرم کامل» |
| `docs/tutorial/03-applications.md`, same sentence | «ورود از Git» goes to the form (already untrue) | it goes to the Git page |
| `docs/tutorial/03-applications.md` | a template is «ساده‌ترین راه برای شروع» | the simplest way for ready-made software |

I searched for "you must create the database first" and its like and found nothing.

## The runs

Commands as in CONSTRAINTS, with `MSBUILDDISABLENODEREUSE=1` and `-p:UseSharedCompilation=false`.

**Full suite on `15d97f8`:** build exit 0, test exit 1.

```
Harbora.Tests.dll             Failed: 9, Passed: 8648, Skipped: 0, Total: 8657
Harbora.NodeAgent.Tests.dll   Failed: 0, Passed: 500,  Skipped: 17, Total: 517
Harbora.NodeIngress.Tests.dll Failed: 0, Passed: 15,   Skipped: 0, Total: 15
Harbora.Postgres.Tests.dll    Failed: 0, Passed: 0,    Skipped: 117, Total: 117
grep -c Harbora.Tests.dll = 2
Warnings: NU1903 only (SSH.NET, in Harbora.Postgres.Tests); no compiler warnings.
```

The 9 failures:
- `A_refused_start_deletes_the_upload_and_its_folder_and_says_why_on_the_form`
- `An_upload_is_written_under_the_upload_directory_and_handed_to_the_launch`
- `The_first_run_card_offers_no_button_to_somebody_who_may_not_press_it`
- `The_sidebar_does_not_offer_it_to_somebody_who_may_not`
- `What_a_browser_says_a_file_is_called_cannot_write_outside_the_upload_directory` (5 cases)

**Full suite on the throwaway merge `071d747`** (`15d97f8` with `r8/int` at `c96d1e4`, not kept): build exit 0, test exit 0.

```
Harbora.Tests.dll             Failed: 0, Passed: 8719, Skipped: 0, Total: 8719
Harbora.NodeAgent.Tests.dll   Failed: 0, Passed: 500,  Skipped: 17, Total: 517
Harbora.NodeIngress.Tests.dll Failed: 0, Passed: 15,   Skipped: 0, Total: 15
Harbora.Postgres.Tests.dll    Failed: 0, Passed: 0,    Skipped: 117, Total: 117
grep -c Harbora.Tests.dll = 2
```

**Documentation censuses**, run on `9115e39` before the last one-row chapter edit: `LaunchChapterTests`, `LearningCensusTests`, `LearningLibraryTests`, `DocumentationDriftTests`, `LearnHttpTests`, `AssistantGuideTests`, `TelegramBotTemplateTests`, `LearningCentreDockerImageTests`, `BacklogStatusTests`: 150 passed, 0 failed. All of them also ran in both full suites.

**The new tests can fail.** Three deliberate breaks, read from disk at test time, gave three failures, each naming its fault. All three were reverted before the runs above:
- `MONGO_URI` misspelt in the chapter
- `go.mod` renamed in the chapter
- a label changed in `Root.cshtml`

**The two code examples:** `node --check` exit 0 and `py_compile` exit 0, each wrapped with the imports it needs. Their projects also go through the real inspector in `LaunchChapterTests`.

**Not run:** the front-end node tests and `npm run build`. No script or view was changed.

## `DocumentationDriftTests` and C

It demands no line of me. No command is registered here yet, so nothing is required, and my mentions of `harbora launch` do not trip it. Once C registers the command, its own "Every command" line satisfies the test and I have added no second one. `README.fa.md` has no such list.

## The brief asked for something I could not deliver: the shortest page

**It is not the shortest page in the library.** It is 5,191 characters, 890 words, 95 lines and 7,950 bytes: 7th of 15 by characters, 2nd by lines. `06-networking` is 2,236 characters. The first draft was 15.9 KB, and I cut it to half by:
- pointing at the app page's own variable list instead of repeating it
- giving a rule and examples instead of the full alias table
- showing two lines of code per language instead of a whole server

Getting shorter means dropping sections the brief requires, so that is the owner's call. What a person actually needs is the first 11 lines, and line 11 says so.

## Added beyond the brief

- **`tests/Harbora.Tests/LaunchChapterTests.cs`**, 63 tests. It holds the chapter to the real inspector over real folders, to the attach names, and to each quoted label in the file that draws it.
- One test each in `LearningCensusTests` and `LearnHttpTests` for the Help control.
- **A "Known limitations" bullet in both READMEs**: a launch has not been recorded end to end on a real server, plus three limits of the checks. **If the live-host proof lands, this bullet must be updated.**
- `docs/tutorial/12-cli-deploy.md`: one line pointing at `harbora launch`.
- **A row worded for `r8/int`.** That branch moved after my base with the mixed-stack rule, so a frontend's `package.json` no longer always wins. The chapter's row points at the screen's note, which is true on both.

## Unverified

- **No launch was run.** No panel, Docker, Traefik or database was involved, and the chapter was written from the code. It was not looked at in a browser, so Persian beside code spans in the tables is unseen. A test confirms the three tables render as tables.
- **The terminal section comes from C's brief, not C's code.** `Each_flag_the_chapter_names_is_one_the_launch_command_takes` returns early until `AddCommand<LaunchCommand>` is registered, so it **currently proves nothing**. After the merge it reads the CLI source for `--yes` and `--set`. Nothing pins the exit codes 0, 1 and 2, or `harbora launch --help`: **check those by hand at merge.**
- **"Even after a password change"** for an alias rests on S's simulation; `RotatePasswordAsync` itself was not run.
- **The two examples** were syntax-checked, not run against a database.
- **The advice to put table-building in the start command** is my reading of the code (see below). It has not been run.

## Seen in passing, not changed

1. **A release command cannot be set after an app exists.** Only creation and environment cloning write it, and the app page shows it read-only. Yet the log check's fix says "Set a release command", which leads nowhere on a launched app, and that is exactly who sees it. The chapter says so and sends them to **دستور راه‌اندازی**. A small form on the app page would be the real fix. The same thing also has two names: «دستور پیش از اجرا» on the confirm screen and «دستور پیش از انتشار» elsewhere.
2. **`PORT` is not set for an app's own Dockerfile.** I found it set only in the generated builds and function hosts, by reading the code. `src/Harbora.Cli/Doctor.cs` (lines 206-208) and chapter 12 both say Harbora sets it for the container. Chapter 15 says what the code does, so **chapter 12 and chapter 15 now disagree** until someone corrects those two.
3. **A launched Git app does not auto-deploy until a person pastes the webhook into their repository.** `AutoDeployOnPush` is on and the app page says "auto-deploys ✓", but nothing calls it until then. The chapter says so.
4. **Three tutorial screenshots predate this round:** `01-dashboard`, `02-apps`, `03-app-create`. The `02-apps` entry in `annotations.json` still says «اپ جدید: از ایمیج آماده، Git یا قالب». They need recapturing on a live panel, so I left the file and the images alone.
5. **`docs/tutorial/04-databases-and-brokers.md` says «هفت موتور»** and omits Meilisearch, while the catalogue has eight. It was already wrong before this round.
