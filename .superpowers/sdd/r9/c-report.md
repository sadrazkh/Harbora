# C report: pick a repository, pushes deploy with nothing to paste, a retry keeps what was typed

Branch `r9/c`, based on `e84a288`. Four commits: `209e536` (item 3), `57f91d2` (item 1), `60dbc42` (item 2), `1683811` (a census entry for item 2's new action). No EF migration, no new `JobKind`, nothing pushed.

The round was paused by a network cut and the owner's pause after item 3 was committed and item 1 was on disk; on resuming, `git status` matched what I had written, and the work carried on from there.

## What was built

### Item 3 — a retry keeps what was typed (`209e536`)

**Why it was not kept.** `LaunchJson.WriteChoices` wrote every `LaunchEnvChoice.Value` as null, on purpose: an answer is often a third party's key (`STRIPE_SECRET_KEY`), and the launch row is no place for one in plain text. So `RetryAsync` passed an empty dictionary, the example file's values came back on their own, and a typed answer stayed unset with a sentence saying so.

**The design.** What was typed rides on the existing `ChoicesJson` column as one extra property, `sealedAnswers`: the whole answer dictionary (blanks included, so a retry is handed exactly what the confirm was) through `ISecretProtector` as one value, plus the variable names in the open. No new column.
- Written by `ConfirmAsync`; read by `RetryAsync` (`OpenAnswers`); removed with the same write that moves the launch on once the variables are on the app (`LaunchService.Confirm.cs`).
- Counted as a leftover: the tick's query finds a failed launch holding only answers, and the 24-hour sweep (`LaunchAdvance.RetryLifetime`) drops them with the token and the archive. A launch that already has an app (a retry is still open to it) gets a sentence naming what was dropped, never a value.
- A key that no longer decrypts reads as no answers; the log line names the exception's kind only, and the timeline names what is unset, as before.
- The failure sentence "What you typed for X was not kept" is gone, and so is the tutorial's «آنچه تایپ کرده بودید نگه داشته نمی‌شود».

Files: `Launch/LaunchSource.cs` (`SealedAnswers`, `LaunchJson.WriteChoices/ReadAnswers/WithoutAnswers`), `Launch/LaunchService.Confirm.cs`, `Launch/LaunchService.Advance.cs`, `Launch/LaunchService.cs`, `Launch/LaunchAdvance.cs` (a doc line), `Domain/Launch/AppLaunch.cs` (the `ChoicesJson` doc comment only), `docs/tutorial/15-launch.md`. Tests: `tests/Harbora.Tests/Launch/LaunchAnswersTests.cs` (8, with the real AES-GCM protector, because the test protector leaves its input readable in its output). The old test that asserted the loss was removed from `LaunchServiceTests`.

### Item 1 — pick a repository from a list (`57f91d2`)

- **`/launch`** draws, above the two existing panels, each connected account's repositories (`Views/Launch/Index.cshtml`). A connected account is a `GitProvider` of type GitHub, GitLab or Gitea that holds a token; `Custom` is not one.
  - It works with no script. One form for the whole list, with a submit button per repository whose value is `{account id}|{clone url}`, so a list of a thousand carries one antiforgery token.
  - The search is a plain GET (`?q=`) the server narrows; `launch.js` (`repositoryMatches`, `initRepositories`) narrows as you type, and says when nothing matches.
  - The list is a scrollable box (`max-h-80`), Persian by default, RTL with logical classes, names `dir="ltr"`, a lock on private ones.
  - A list that cannot be fetched is one sentence, with a link to the Git page, and the paste field is unchanged below. The causes are a refused token, no answer within `GitAccounts.PagePatience` (8 s; accounts are asked in parallel), or a token that no longer decrypts.
  - Nobody without `apps.create` is asked anything.
  - With nothing connected the page is exactly what it was.
- **The client** (`Git/GitProviderClient.cs`) now reads page after page, up to `IGitProviderClient.MaxRepositories` (1000). GitHub and GitLab stop on a short page; Gitea and GitLab go by the total they report (`X-Total-Count`, `X-Total`), because Gitea's page size is an install setting. GitLab now sends `Authorization: Bearer`, not `PRIVATE-TOKEN` (see the decisions).
- **`GitAccounts`** (new, `Git/GitAccounts.cs`): the workspace's connected accounts, unfiltered with the workspace applied (the inspection job has no session). It covers the picker's lists and their sentences, and `OwnerOfAsync`: the connected account whose own list holds a pasted address.
- **`GitRepositoryAddress`** (new): one key for every spelling of a repository; the host an account's repositories are cloned from (github.com for GitHub, the instance for GHE, GitLab and Gitea); `IsOn`. A connected account's token is only ever used for an address on its own host.
- **The launch.**
  - `StartAsync` takes `GitProviderId`. It refuses an account that is not this workspace's, or not connected, or an address not on the account's host, with a sentence and nothing written. It stores no token on the row.
  - The account goes onto `InspectionJson` as `StoredInspection.GitProviderId`, which this column already is: the launch's account of its source.
  - `InspectAsync` (`CredentialForAsync`) reads with: a token typed for this launch; else the picked account's own token; else, for a pasted address, the token of a connected account whose list holds it.
  - A failed read through an account says to reconnect the account, not "add a token".
- **The app** (`Apps/AppProvisioningService.cs`, the Git part, shared by the launch, the form and the agent; see `RepositorySourceAsync`/`AttachRepository`):
  - The request may name an account (`AppCreateRequest.GitProviderId`). Then the app shares that account's existing row for the repository, or gets a new row under it named by its full path.
  - With no account named and no token typed, a connected account that already has a row for the address (the Git page's import makes one) is used.
  - Otherwise the app gets the `Custom` provider of its own, exactly as before.
  - A named account that is gone is a refusal on the confirm screen, in both languages.

Tests: `Git/GitProviderClientTests.cs`, `Git/GitRepositoryAddressTests.cs`, `Git/AppRepositorySourceTests.cs`, `Launch/LaunchPickerTests.cs`, `Http/LaunchPickerHttpTests.cs`, two in `Scripts/tests/launch.test.mjs`; the chapter gets a sentence and a table row, held by two new `LaunchChapterTests` lines.

### Item 2 — pushes deploy with nothing to paste (`60dbc42`, `1683811`)

- **The client** gains `ListHooksAsync`, `CreateHookAsync` and `UpdateHookAsync`, written to each provider's documented API:
  - GitHub: `/repos/{o}/{r}/hooks`, PATCH to edit. A `web` hook on `push`, JSON, with the HMAC secret.
  - GitLab: `/api/v4/projects/{url-encoded path}/hooks`, PUT to edit. `token` is the secret, which `GitWebhookProcessor` compares with `X-Gitlab-Token`; push and tag push events. An `alert_status` other than `executable` reads as off.
  - Gitea: `/api/v1/repos/{o}/{r}/hooks`, PATCH to edit. Type `gitea`, JSON, secret.
- **`GitHookService`** (new, `Git/GitHooks.cs`):
  - The address is `https://{PANEL_DOMAIN}/webhooks/git/{repositoryId}`, and the secret is the row's `WebhookSecret`.
  - `EnsureAsync` lists the hooks and finds one at that address. If there is one, it is updated to the current secret and turned on; if not, one is created. So a retry, a second app on the repository (it shares the row, so the address) or a second press all leave one hook. A GitHub 422 on create (another request added it first) is looked up again and updated.
  - `StatusAsync` asks the provider and changes nothing. Answers are held in `GitHookStatusCache` (singleton): 5 minutes when confirmed, 1 minute otherwise. Ensure replaces the held answer at once.
  - Neither throws. A `Custom` provider, a token that no longer decrypts, an address not on the account's host, or no `PANEL_DOMAIN` is `NotPossible` with a sentence, and no provider is asked anything. A refusal or a network failure is `Failed` with a sentence by status code (401 means reconnect; 403 or 404 means admin rights and the `repo`/`admin:repo_hook` scope).
  - A created or updated hook writes the audit entry `git.webhook.created`/`updated`, with no secret or token in it.
- **Creating an app** on a connected account's repository runs `EnsureAsync` after the create has committed, outside the creation lock, and reports the result on `AppCreateResult.Webhook`. The app exists whatever the provider answers.
- **The app page** (only the webhook block of `Views/Apps/Details.cshtml`):
  - It says pushes deploy only when `StatusAsync` is `Confirmed`, that is, when the provider just listed Harbora's hook turned on. Then there is no paste instruction; the address sits in a fold, and a "set it up again" button is there for `git.manage`.
  - Otherwise the manual address and secret stay as they were, with the reason sentence above them, and «وب‌هوک را بگذار» when an account could be asked.
  - The label «استقرار خودکار (Git)» is kept.
- **`GitController`** gains `POST /git/repos/{id}/webhook` (`git.manage`; this workspace's repository only; returns to a local path). `RotateSecret` now pushes the new secret to the hook in the same press, so a rotation no longer silently breaks a working hook.
- **The census.** `AppScopeCensusTests` failed on the new action in the first full run: it asks no placement question. I added it to the census's own Exempt list beside `RotateSecret`, with that reason (commit `1683811`).

Tests: `Git/GitHookServiceTests.cs`, the hook part of `Git/GitProviderClientTests.cs`, three more in `Git/AppRepositorySourceTests.cs`, one in `Launch/LaunchPickerTests.cs` (a picked launch through a failed create, a retry and a second launch: one hook), `Http/AppWebhookHttpTests.cs` (the block in each state, the button, another workspace refused, rotation). The chapter's «بعدش» line is rewritten, and the button label is held by `LaunchChapterTests`.

## Decisions the owner might have made differently

1. **Nothing is stored about the hook.** There is no column for it and I added none. The page asks the provider while it draws: at most 5 s on a miss, then the answer is held for a few minutes. In exchange, the page can never claim a hook the provider does not have, and it notices one deleted on the provider's side. The cost is a provider call on an app page about every five minutes per repository, and a page that can wait up to 5 s when the provider is slow. The alternative was storing an outcome (a column, so a migration), or an audit row, which the retention sweeper deletes.
2. **The hook's address comes from `PANEL_DOMAIN`, over https.** I did not use the request's host, because a provider has to reach the panel from outside. With `PANEL_DOMAIN` unset, Harbora does not set the hook up and says why. The manual block still prints the request's host, as it always did.
3. **The list is drawn on the server inside `/launch`.** I did not put it on a second page or load it from a script. This is what "works with no script" needed, and the paste field is one scroll away. A slow provider delays the page by up to 8 s.
4. **What "plainly belongs to a connected account" means.**
   - For a launch, the account's own list holds the address. The inspection job asks that, which costs one list per connected account on that host.
   - For the ordinary form, the workspace already has a row for that repository under a connected account. The form makes no network call. Reading the form (it always made a `Custom` provider from the pasted address) is why I stopped there.
   - A token typed for the launch or the form always wins, and no account is asked.
5. **Apps on one repository share one repository row.** They get one webhook address and one secret, so the same hook is found rather than a second added. A push then deploys every app on that repository tracking the pushed branch: the webhook processor already worked that way for shared rows (previews, clones).
6. **Typed answers are swept after a day**, with the token, even from a failed launch whose app exists and could still be retried. The names are said when that happens.
7. **A picked repository is read from its default branch.** The picker has no branch field; the paste form keeps one.
8. **GitLab's header changed from `PRIVATE-TOKEN` to `Authorization: Bearer`.** The token "Connect with GitLab" stores is an OAuth one, and GitLab's documentation says only the bearer form carries it. GitLab documents personal access tokens as accepted the same way. This changes the Git page's existing import for GitLab too.
9. **Setting a hook up needs `git.manage`.** That is the capability that rotates the secret. A member who may only deploy sees the status, not the button.
10. **Hooks are not removed when an app or repository goes.** App deletion lives in `AppOperationsService.DeleteAsync` (not my area), a repository row outlives its apps, and no page deletes a repository or a provider. A hook left behind is answered "Queued 0 deployment(s)". I report this rather than doing it.
11. **One page of hooks is read** (100 on GitHub and GitLab, 50 on Gitea). On a repository with more webhooks than that, Harbora's could be missed and a second one added.

## Runs on the final commit `1683811`

On `1683811`, exactly the constraints' commands (`MSBUILDDISABLENODEREUSE=1`, `--no-restore`, `-p:UseSharedCompilation=false`, then `dotnet test Harbora.slnx --no-build --blame-hang --blame-hang-timeout 6m --blame-hang-dump-type none`):

- `BUILD_EXIT=0`, compiler warnings (`warning CS`): **0**. The build prints only the two `NU1903` SSH.NET warnings, which are on the base too.
- `EXIT=0`. Its summary lines:
  - `Harbora.Tests.dll`: **Passed 9532, Failed 0, Skipped 0** (baseline 9457; +75).
  - `Harbora.NodeAgent.Tests.dll`: Passed 500, Skipped 17.
  - `Harbora.NodeIngress.Tests.dll`: Passed 15.
  - `Harbora.Postgres.Tests.dll`: Skipped 117, as on this machine always.
- `grep -c "Harbora.Tests.dll"` on the log: 2.

The first full run, on `60dbc42`, failed exactly one test, `AppScopeCensusTests.Every_resource_scoped_action_asks_the_placement_question_or_is_explained`, on the new `SetUpWebhook` action. `1683811` adds the census entry, and the run above is after it.

Front end, run after the last change to `Scripts/` and never during a dotnet command:
- `npm ci` and `npm run build` in `src/Harbora.Web`: both exit 0.
- `node --test src/Harbora.Web/Scripts/tests/*.test.mjs`: 535 tests, 535 pass, 0 fail (baseline 533).

## What is unverified

**No call below has met a real provider.** Each is written to the provider's documentation and held to that shape only by fake handlers:
- GitHub: `GET /user/repos` paged; `GET`/`POST /repos/{o}/{r}/hooks`; `PATCH /repos/{o}/{r}/hooks/{id}`; the 422 "Hook already exists" answer; the `token` header for OAuth and classic tokens.
- GitLab: `GET /api/v4/projects?membership=true` paged by `X-Total`; `GET`/`POST /api/v4/projects/{path}/hooks`; `PUT .../hooks/{id}`; `alert_status`; the Bearer header for an OAuth token and for a personal access token.
- Gitea: `GET /api/v1/user/repos` paged by `X-Total-Count`; `GET`/`POST /api/v1/repos/{o}/{r}/hooks`; `PATCH .../hooks/{id}`.

**Provider behaviour I relied on but could not check:**
- Whether GitHub's OAuth `repo` scope, GitLab's `api` scope and a Gitea OAuth token are enough to manage hooks. The documentation says so for the first two; for Gitea I do not know.
- Whether a tag push reaches Harbora as a `push` event on GitHub and Gitea.
- Whether a pushed hook's delivery passes `GitWebhookProcessor`'s signature check end to end.
- A self-hosted GitLab whose `http_url_to_repo` host differs from its API host would have its picks refused as "not on this account". That case is untested.

**Pages.** I opened no page in a browser and took no screenshot. The HTTP tests render the real views; I have not looked at the layout.

**Not run here.** Postgres-backed tests do not run on this machine.

## Touched outside my area

- `src/Harbora.Infrastructure/Apps/IAppProvisioningService.cs`: `AppCreateRequest.GitProviderId`, and `AppCreateResult.Webhook` as an optional last parameter.
- `AppProvisioningService`'s constructor gains `IGitHookService`. Any other branch that constructs it by hand must pass one; tests use `FakeGitProviders.HookService(...)`.
- `src/Harbora.Infrastructure/DependencyInjection.cs`: three registrations beside the Git ones (`GitAccounts`, `GitHookStatusCache`, `IGitHookService`).
- `src/Harbora.Domain/Launch/AppLaunch.cs`: the `ChoicesJson` doc comment only.
- `src/Harbora.Web/ViewModels/LaunchViewModels.cs`: the picker's field and the model's accounts.
- Tests:
  - `AppServiceHarness.cs` and `Agent/AppToolsKit.cs`: the new constructor argument.
  - `Http/LaunchHttpTests.cs`: made `partial`.
  - `Http/LaunchPanelFixture.cs`: `PANEL_DOMAIN` and a fake provider API.
  - `LaunchChapterTests.cs` and `AppScopeCensusTests.cs`: entries added, none changed.
  - `Launch/LaunchServiceTests.cs`: the answers-lost test removed.

## Defects found and left

- **`LogicalDatabaseBackupHttpTests.Restoring_into_a_brand_new_database_creates_it_and_reaches_the_engine` fails when run alone or with its class.** It hits a `DirectoryNotFoundException` at `BackupEngine.RestoreDatabaseAsync`'s `File.Copy` into `Backups:StagingDir`, which nothing on that path creates. It passes in the full run, presumably because an earlier test made the folder. Not mine (backups); untouched.
- **The brief says the provider client is tested "the way the client is tested today", but it had no tests at all.** `GitProviderClientTests` is new. The brief's file and line references were otherwise right.
- **Sentences on the Git page that are pre-existing, out of scope and untouched:**
  - `GitController.OAuthCallback` shows `OAuth failed: {ex.Message}` in English only.
  - The Git page's Connect form accepts a Gitea account with no address, which can then never be listed.
