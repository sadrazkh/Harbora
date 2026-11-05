# C — The API and `harbora launch`: the same flow from a terminal

Read `.superpowers/sdd/r8/CONSTRAINTS.md` and `docs/superpowers/specs/2026-11-04-launch-design.md` first.

The owner, about the whole round: a person who wrote an app should press one thing and have it created, connected, deployed and tested. This is that one thing for someone in a terminal:

```
cd my-app
harbora launch
```

`ILaunchService` is implemented and merged (`src/Harbora.Infrastructure/Launch/`). Read `LaunchContracts.cs` and S's report (`.superpowers/sdd/r8/s-report.md`). **No new service, no second path**: the API is a thin layer over `ILaunchService`, and the CLI is a client of the API. No migration. No AI.

## 1. The API (`src/Harbora.Web/Controllers/Api/LaunchApiController.cs`)

`[ApiController]`, `[Route("api/v1/launch")]`, token scheme, in the shape of `ApiV1Controller`. `Capabilities.AppsCreate` on each endpoint that changes something, per endpoint, the way `ApiV1Controller` declares it with `AuthenticationSchemes`. Workspace and user from `ICurrentUser`.

| | |
|---|---|
| `POST /` | JSON `{ cloneUrl, gitRef, gitToken }`. Answers `{ id }` |
| `POST /archive?name=my-app` | Raw gzipped tar in the body, streamed to disk exactly as `ApiV1Controller.DeployArchive` does, same `[RequestSizeLimit]`. Written as `_uploads/{sanitised-name}-{guid:N}.tar.gz`: `LaunchSource.NameFromArchive` strips the GUID, so the name survives. Blank or unusable `name` is allowed. A refused start deletes the file |
| `GET /{id}` | `LaunchView`. 404 when `GetAsync` is null |
| `GET /{id}/proposal` | `LaunchProposal`. 404 for another workspace's id; 409 with `{ error, status }` when it is not `AwaitingConfirmation` |
| `POST /{id}/root` | `{ candidate }` → the proposal from `ChooseRootAsync` |
| `POST /{id}/quote` | `LaunchChoices` → `LaunchQuote` |
| `POST /{id}/confirm` | `LaunchChoices`. `ValidateConfirmAsync`, then `ConfirmAsync` |
| `POST /{id}/retry` · `POST /{id}/abandon` | |

- Refusals answer 400 with `{ errors: [{ field, message, messageFa }] }`. Read `ServiceRefusal`, and how other API endpoints shape errors, and follow them.
- Every route naming a launch asks `GetAsync` first, so another workspace's id is 404 whatever the route goes on to do.
- There is no global JSON enum configuration. Decide how enums go over this API once (names, not numbers, because the CLI prints them and a number is a bug waiting), and make it local to this controller: for example `new JsonResult(view, Options)` with a `JsonStringEnumConverter`. `LaunchChoices` coming in must accept names as well. Pin both.
- Confirm with a test that everything `ConfirmAsync` calls really works under the token scheme. Those services judge the signed-in user, and nothing has run them under a token yet. A token user who may not create apps gets a refusal, not a 500.
- Nothing secret in an answer: `gitToken` never comes back, and `LaunchProposal.EnvFields` carries no value but a `KeepExample`'s suggestion.
- Rate limiting and anything else `ApiV1ContractHttpTests` expects of an endpoint: read it first and add this controller's contract there or beside it.

## 2. The command (`src/Harbora.Cli/LaunchCommand.cs`, registered in `Program.cs`)

```
harbora launch [PATH]
  --git <URL>          Read this repository instead of uploading a folder
  --ref <BRANCH>       With --git: a branch. Blank means its default branch
  --git-token <TOKEN>  With --git: for a private repository. Also read from HARBORA_GIT_TOKEN
  --folder <FOLDER>    Which app, when a repository holds several
  --name <NAME>        The app's name
  --set KEY=VALUE      Answer a question up front. Repeatable
  --no-service <TYPE>  Do not create a service the inspector proposed. Repeatable
  -y|--yes             Accept the proposal without asking
  --no-follow          Return as soon as it is confirmed (CI that will look later)
  --server --token --account   As `harbora deploy`
```

What it does, in order, and all of it in plain sentences. The CLI's language is English, as the other commands are.

1. **Source.** With no `--git`: pack the folder with `SourcePacker` (the same excludes as `harbora deploy`; `.env` must not leave the machine, so check that it does not and pin it) and `POST /archive?name=<folder name>`. A folder with nothing in it says so. With `--git`: `POST /`.
2. **Reading.** A status line while `Inspecting`, polling every second or two. `InspectFailed`: the server's message, exit 1, and say nothing was created.
3. **Which folder.** `RootCandidates` with no `RootDirectory`: `--folder`, otherwise a selection prompt, otherwise (no terminal, or `--yes`) exit 1 naming the candidates and `--folder`.
4. **The proposal, as sentences.** What it found, and every note. What will be created, each service with why. The names of secrets it will generate, never a value. Then the cost, one sentence from `/quote`: a refusal is printed and is exit 1 before anything is created, naming `/billing`.
5. **Questions.** Only `NeedsAnswer`. `--set` answers first; a person is asked for the rest, with a blank accepted and hidden input when `IsSecret`. With `--yes`/no terminal, an unanswered one is a warning naming it, and it goes on. Anything can be blank: the web form allows it too.
6. **Confirm.** A person gets one yes/no. No terminal and no `--yes`: exit 1 saying to pass `--yes`; never hang waiting, never create anything without being told to. Validation refusals are printed with their fields and exit 1.
7. **Follow.** Print each step as it changes state, with its detail. When the deployment exists, give the panel URL of its log. Poll every 2-3 seconds. Ctrl-C stops watching and says the launch carries on, and how to look at it. Do not stream the build log by default; `harbora logs` exists.
8. **The end.** `Succeeded`: the address, large, and each check. `SucceededWithProblems`: the address, the failed checks first with their fixes, and exit 2. `Failed`: the step, the message, what exists so far, exit 1. Whether retrying could help: `CanRetry` printed as the panel page to press it on, or offer it interactively with one yes/no, whichever is less code. A `Skipped` check is never printed as a pass.
9. **Next time.** On success from a folder, `Interactive.RememberApp` writes `harbora.yml`, so `harbora deploy` works from then on, and the last line says so. Never overwrite an existing config.

The pure parts (parsing `--set`, turning a proposal plus answers into the choices, what the exit code is, what a state prints as) go in a static class with tests. Use the existing `CliConsoleCollection` and fake handler patterns in `tests/Harbora.Tests/Cli*Tests.cs`.

**Do not use `SourceInspector` in the CLI**, even though it can reach it: the server's reading is the proposal, and two readings is how they come to disagree.

A test with an end-to-end shape: the real `LaunchCommand` over the real `LaunchApiController` over a fake `ILaunchService`, through `HarboraWebFactory`'s in-memory handler if `ApiClient(server, token, handler)` lets it. Otherwise separately, and say so. At least: happy path with `--yes`; a question answered by `--set`; a question with no terminal and no `--yes` creates nothing; a quote refusal creates nothing; several folders with no `--folder`; `.env` not in the upload; each exit code; a value given to `--set` never printed; `--git-token` never printed.

## 3. Documentation the tests demand

`DocumentationDriftTests` requires every CLI command as `harbora <cmd>` in `README.md`'s "Every command". Add the `harbora launch` lines there and the same in `README.fa.md` if it has that list, and one section in `docs/cli-deploy.md`. H writes the rest of the docs afterwards. Follow every census that complains; do not exempt anything without saying why.

## Worktree

`E:/Cash.Net/source/repos/sadrazkh/Harbora-r8-c`, branch `r8/c`, from the commit given in your first message. Report: `.superpowers/sdd/r8/c-report.md`: what was built, the exact commands and their numbers, what is unverified, and anywhere you left the brief. The full `Harbora.slnx` suite must have run on your last commit, with `Harbora.Tests.dll` confirmed in the output. Commit with `git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit`, with no Co-Authored-By trailer.
