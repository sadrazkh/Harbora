# C report: the API and `harbora launch`

Branch `r8/c`, based on `e7a5d00`. `harbora launch` works from a folder or a repository, as a client of a thin API over `ILaunchService`. There is no new service, no migration and no model call.

**Read this first: `e7a5d00`, the commit this branch starts from, fails 9 tests in `LaunchHttpTests`.** It changed `UploadedArchive` and `_Topbar.cshtml` without changing the tests. I ran that class on `e7a5d00` itself: `Failed: 9, Passed: 103`. Commit `c909697` fixes them. It is outside my brief and stands alone, so see item 1 under "Where I left the brief".

## What was built

- **The API, `src/Harbora.Web/Controllers/Api/LaunchApiController.cs`.** Every route in the brief's table is there. Each route naming a launch asks `GetAsync` first. Refusals answer 400 with `{ errors: [{ field, message, messageFa }] }`.
- **Enums go by name, in both directions, local to this controller.** Answers use `new JsonResult(value, WireOptions)`. `LaunchChoices` is read by hand with the same options, because `[FromBody]` would use the global ones. A number coming in is refused.
- **Secrets stay off the answers.** `LaunchProposal.Inspection.EnvVars` carries the example file's value for every disposition, including a `NeedsAnswer` line, so the brief's rule about `EnvFields` was not enough on its own. The controller strips `ExampleValue` from everything but a `KeepExample`.
- **The command, `src/Harbora.Cli/LaunchCommand.cs`, registered in `Program.cs`.** It follows the nine steps in the brief's order.
- **The pure parts, `src/Harbora.Cli/LaunchPlan.cs`.** Parsing `--set` and `--no-service`, the choices, the questions, the sentences, the exit code, and what a step or check prints as.
- **Smaller CLI changes.**
  - `ApiClient` gains `ApiRefusedException`, a subclass of `HttpRequestException` with the same message, so existing catches still work.
  - `PostFileAsync` gains a token parameter.
  - `DeployCommand.ReportExcluded` becomes internal.
  - `RememberApp`'s header comment no longer claims `harbora deploy` wrote the file.
- **Docs.** The "Every command" lines in `README.md` and one section in `docs/cli-deploy.md`, which also holds the API table. `DocumentationDriftTests` made me update the command count in `docs/product-audit/01-current-system-map.md`. `README.fa.md` has no such list, so nothing went there.

**Ctrl-C needed real wiring.** Spectre.Console.Cli 0.55 has no `CancelKeyPress` in its DLL, and `Program.cs` passes no token, so the token a command receives never fires. The launch installs its own handler, but only around its waits: a prompt blocked on the keyboard would otherwise ignore the key.

`UploadedArchive` did not need changing. `PathForLaunch(workDir, $"{name}.tar.gz")` is already public and goes through the private sanitising.

## Tests

| File | What it covers |
|---|---|
| `tests/Harbora.Tests/Http/LaunchApiHttpTests.cs` | The API's contract over a scripted service, in the real pipeline |
| `tests/Harbora.Tests/Http/LaunchApiTokenHttpTests.cs` | The **real** `LaunchService` driven by a bearer token: real packer, real inspector, real creates on EF InMemory |
| `tests/Harbora.Tests/CliLaunchPlanTests.cs` | The pure parts, with JSON serialised from the contracts themselves |
| `tests/Harbora.Tests/CliLaunchEndToEndTests.cs` | The real command over the real controller over a scripted service, through `Panel.Server.CreateHandler()` |
| `tests/Harbora.Tests/Http/LaunchScript.cs`, `LaunchPanelFixture.cs` | Shared script, plus additive helpers on the fixture |

**The token question from the brief.** `ConfirmAsync` and everything under it works for a token user. The owner's run ends `WaitingForServices` with the app, the database, the attachment and the variables all present. It stops there because no tick runs in that test. I found no defect here.

A token user who may not create apps gets a refusal, in three cases:
- A viewer gets 403.
- A scoped member with no grant gets 400 at the start, and the upload is deleted.
- The same member confirming an owner's launch gets 400, with nothing created.

The end-to-end tests have their own collection with `DisableParallelization = true`. They capture `AnsiConsole`, which belongs to `cli-console`, and they boot a panel, whose setup-guard flag is process-wide and belongs to `harbora-http`. A class can only be in one collection.

## The runs

On `fd2a035`, the last commit with code in it. The only commit after it adds this file.

```
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false
BUILD_EXIT=0, 0 Error(s), no "warning CS" lines. The only warning code is NU1903 (4 lines).

dotnet test Harbora.slnx --no-build --nologo -v q
EXIT=0
Passed!  - Failed: 0, Passed:  500, Skipped: 17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:   15, Skipped:  0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed: 8772, Skipped:  0, Total: 8772 - Harbora.Tests.dll
grep -c "Harbora.Tests.dll" = 2

node --test src/Harbora.Web/Scripts/tests/*.test.mjs
NODE_EXIT=0, # tests 521, # pass 521, # fail 0

dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~CliLaunch|FullyQualifiedName~LaunchApi"
Passed: 179, Failed: 0   (this work's own tests)
```

**Do the tests fail when the code is wrong?** Almost everything passed first time, so I broke the code on purpose, ran the tests, and restored it with `git checkout`.

| Broken on purpose | What failed |
|---|---|
| Proposal sent with example values | `A_proposal_sends_no_value…`, `Every_step_of_a_launch…` |
| `.env` and `.env.local` removed from the packer's excludes | `The_upload_is_the_folder…`, `A_folder_holding_only…` |
| A skipped check given a tick | `Only_a_check_that_passed…`, `A_check_that_was_not_answered…`, `Succeeded_with_problems…`, others |
| `Retry` no longer asks `GetAsync` | The two 404 theories, on `/retry` |
| Numbers accepted for enums | `An_enum_sent_as_a_number…` |
| Confirm prompt skipped | Both tests I ran for it failed |

The first time, that last mutation **hung the run** instead of failing, because a launch that never ends is followed for ever. I killed that run, restored the code, and gave each end-to-end run a 30-second limit. It then failed properly, in a filtered run of those two tests.

**The real binary, by hand, over a real socket.** I ran `harbora.dll` against a throwaway Python stand-in for the panel, which is not committed. That covers the binary and its console. It does not cover the panel.

| Run | Result |
|---|---|
| Success | Exit 0; the proposal, steps, address and `harbora.yml` read correctly |
| Problems | Exit 2, failed check first, skipped check as "not checked" |
| Failed | Exit 1, step, message, what exists, panel page to retry |
| No terminal, no `--yes` | Exit 1, abandon called |
| A real Ctrl-C and Ctrl-Break on a real Windows console | "Stopped watching", exit 1, no abandon |
| `HARBORA_GIT_TOKEN` | Reached the body, never printed; `--git-token` wins over it |
| Upload | `Expect: 100-continue` seen on the wire |

That hand run is where the truncated panel header turned up, and I fixed it.

## Unverified

- **A real launch.** No Docker, no Postgres, no build. The real-service test stops at `WaitingForServices`. The CLI has never talked to a real panel, only to the in-memory host and the Python stand-in. Nothing here has followed the real tick through deploy and verify.
- **Postgres.** Everything ran on EF InMemory. I added nothing to the Postgres lane.
- **The three real prompts.** The folder list, the hidden input and the yes/no in `TerminalPrompts` have never been pressed. Tests go through a scripted `ILaunchPrompts`, so hidden input is only checked as "asked with `secret: true`".
- **Ctrl-C in the suite.** The committed tests cancel a token and pin the handler's source line; the real key press was by hand, once, on Windows. Linux and macOS are untried. If the parent process ignores Ctrl-C the handler never runs: my first attempt did exactly that, because the parent had the ignore flag set. Ctrl-Break still works there.
- **413.** `TestServer` does not enforce it, so only the attribute is pinned, as for `DeployArchive`.
- **A clone with a token.** The API test stops at `StartAsync`, so a private repository over HTTPS was not exercised.
- **Traefik and TLS.** `ApiClient` sends JSON chunked, as `harbora deploy` does. I saw that on the wire but never through Traefik.

## Where I left the brief

1. **Commit `c909697` is outside the brief.** It updates seven tests to the flat archive name. It also puts one `@if` in `_Topbar.cshtml`, so the menu stops offering a launch to a viewer, which is what the two failing tests expect of it. The commit stands alone, so you can take a different view of the menu. Without some fix, those 9 tests stay red.
2. **`/quote` answers a superset of `LaunchQuote`.** It adds `firstHour`, `perHour` and `balance` as written figures, because `MinorUnits` lives in the web project and the CLI would otherwise hold a second copy of the rule.
3. **Numbers are refused coming in, not just names accepted.** I read "accept names as well" as "as well as going out". If numbers should also be accepted, flip `allowIntegerValues`, and one test goes with it.
4. **Stopping before anything is created calls `/abandon`,** so the upload goes now rather than after the 24-hour sweep. The exception is a validation refusal at confirm, which leaves the launch alive and prints its panel page, because that screen can change things no flag reaches, such as the size. There is no `--size`, so "Choose a size" can only be settled there.
5. **Choices the brief left open:**
   - A person answering no is exit 1.
   - Ctrl-C while following is exit 1, as `StreamLogs` does.
   - `harbora.yml` is also written on `SucceededWithProblems`: the app exists, and deploying again is how the problem gets fixed.
   - `--no-follow` does not write it.
6. **Additions:**
   - `--no-service` accepts any unambiguous start of a type (`postgres`), and a name matching nothing is exit 1 rather than a database the person thinks they turned down.
   - `--folder` on something read as one app is refused, because that folder was never read.
   - A `--set` key nothing asked for is warned about by name, sent anyway, and stored as a secret.
   - A folder and `--git` together, or `--ref`/`--git-token` without `--git`, stop before anything is sent.
   - A poll may fail four times in a row without comment; the fifth gives up and says so.
7. **`CanRetry` is printed as the panel page,** the lesser-code option the brief offered. The CLI never calls `/retry`, though the route exists and is tested.
8. **No rate limiter.** `ApiV1ContractHttpTests` expects none beyond `auth/token`, and `DeployArchive` has none, so these routes match.

## Contracts and things worth a second look

- **`ProposeAsync` returns the example values in `Inspection`.** I strip them in the API. A future caller of the service gets them again, so the service may be the better place to strip them. I left the contract alone.
- **`LaunchView` has no slug.** `harbora.yml` needs one, so the CLI makes one `GET /apps` and matches on id.
- **`errors[].field` is `ServiceRefusal.Field` as written,** so it is `InstanceSizeKey` while the JSON says `instanceSizeKey`. It is unchanged so the quote's own `refusal.field` agrees with it.
- **A retry loses `--set` answers,** as S reported for the web form. The failure message says so.
- **`.env.production` and `.env.staging` do leave the machine.** The built-in excludes name only `.env` and `.env.local`, for `harbora deploy` and `harbora launch` alike. I kept "the same excludes as deploy" and pinned those two, at the root and nested.
- **`harbora deploy`'s "Stopped following" message cannot appear in a real run,** because nothing cancels its token, so Ctrl-C just kills the process. That is from reading the code, not from running it. `CliCancelTests` pass because they cancel a token themselves. I did not change it: a global handler would make `harbora login`'s prompts ignore the key, so it needs the same "only around a wait" treatment.
