# F1 report: fix pass, server side

Commits: `6cbcae1..HEAD`, code through `dd7ff8e`, then this report, on `r8/f1`. Not pushed. One line per item: what changed, where, the test that pins it.

## 1. A launch goes green when nothing was verified: done

`LaunchAdvance.VerdictOn(checks, kind)` is the pure rule: `Succeeded` needs no `Failed`, no `Warning`, and for a long-running kind `running` to have `Passed`. A kind that runs and exits has `running` Skipped by design on every run, so it needs only the first two. `AppVerifier.RunAsync`'s catch now returns an incomplete report (one `Pending` check, `CompletedAt` null), so the launch stays `Verifying`, asks again, and the existing 15-minute limit ends it at `verify`. The timeline's `SucceededWithProblems` sentence no longer says "the app is running", which was false for both a failed `running` and a run that could not ask; it now has one sentence per case.

Pinned by `LaunchAdvanceTests.Green_needs_…`, `A_hung_daemon_is_not_an_app_that_works`, `A_page_that_answers_beside_a_log_…`, `A_kind_that_runs_and_exits_…`, `LaunchServiceTests.A_fault_in_the_panels_own_run_…`, `A_blip_…`, `A_worker_is_judged_…`, `AppVerifierTests.A_fault_in_the_panels_own_database_…`, `A_hung_daemons_whole_report_…`, `A_real_run_of_a_kind_that_runs_and_exits_…`. `HasFailures` stays, with a comment saying it is not the verdict. `A_check_still_settling_…` needed a passed `running` added.

## 2. Laravel, and PHP with a database: done, not built

`Buildpacks.Php`. What Laravel asks for, read rather than guessed: `laravel/framework` 12.x's `composer.json` requires ctype, filter, hash, mbstring, openssl, session and tokenizer, and its deployment docs add cURL, DOM, Fileinfo, PCRE, PDO and XML. None of that needs installing as far as I know (from memory, see Unverified). Neither source asks for `bcmath` or `intl`; they are in because the brief lists them and packages often do. Installed for every PHP app: `git unzip`, and `pdo_mysql pdo_pgsql mysqli pgsql zip bcmath intl`. `mysqli` and `pgsql` are beyond the brief's list: the inspector already maps `ext-mysqli` and `ext-pgsql` to a database, so a project naming them would otherwise fail its install.

Composer is fetched to a file with `curl -f` rather than piped, there is no `|| true`, and `COMPOSER_ALLOW_SUPERUSER=1` is set on that one line, because composer otherwise turns off plugins and scripts for a root in a non-interactive run, and `package:discover` is one of those scripts. With an `artisan` file: `DocumentRoot` is `public/`, `bootstrap/cache` and `storage/*` are created before the install and given to `www-data` after it. That last part is not in the brief; Laravel's docs ask for it, and without it the first request is a 500 about a log file.

`GeneratedDockerfileTests`' byte pins changed knowingly: both PHP entries, plus a new Laravel entry. An app already on the PHP image gets one rebuild, into an image with more in it and the same root. Also `Any_php_app_gets_the_drivers_…`, `A_composer_failure_stops_the_deployment_…`, `Laravel_is_served_from_public_…`, `A_plain_php_app_keeps_its_root_…`.

For P to look at, both from reading:
- A Laravel view using `@vite` needs `public/build/manifest.json`, and this image still does not run `npm run build`. The `frontend-assets-not-built` note says so, but such an app will answer 500.
- A stock `.env.example` has `DB_CONNECTION=sqlite` and `SESSION_DRIVER=database`. The release task migrates a SQLite file in its own throwaway container, so the web container has no tables and `/` may answer 500. A real database avoids this.

## 3. Slugs must be ASCII: done

`AppProvisioningService.Slugify` keeps only `[a-z0-9-]`, falling back to `app`. The person's name stays the display name, and stored slugs do not change. `UniqueSlugAsync` is now public static so `LaunchProposal.SuggestedSlug` goes through the same rule. `DatabaseProvisioningService.Slugify` was already ASCII (`[^a-z0-9]+`, fallback `svc-xxxxxx`), so only pinned. Container names come from those two. Pinned by `AppSlugTests`, `LaunchWithRealInspectorTests.A_zip_with_a_persian_name_…`, `Launching_the_same_repository_twice_…`. The form's tests needed no change.

For F2's side: `Harbora.Cli/Commands.cs:786` has its own `Slugify` still using `IsLetterOrDigit`, so `harbora init` in a Persian-named folder writes a Persian `app:`.

## 4. Combined limits: done

`SettleServerAsync` asks a chosen host about everything together (`scheduler.CheckAsync(chosen, memory, cpu)`) and keeps the choice on a refusal, so a quote stays about that host. `CheckChoicesAsync` asks `quota.CanAddWorkloadsAsync` about the batch; that method already existed, so `LaunchService` gained `IQuotaService`. The batch is asked only when nothing above it has refused, so a plan that is already full says so once. An id that is no server now falls back to placement, as the create services read it. Pinned by `A_chosen_host_is_asked_about_all_of_it_…`, `A_chosen_host_that_fits_each_but_not_all_…`, `A_plan_with_room_for_each_but_not_all_…`, `A_plan_that_is_already_full_says_so_once`, `A_plan_that_sells_past_its_caps_…`.

## 5. Generic database names: done

`EnvVarClassifier` keeps the `Role`. `AliasesFor` takes what is ticked and resolves generic names against the one database engine ticked; a cache or broker beside it does not count. New: `AttachEnvNames.IsGenericDatabaseName`. One thing beyond the brief: the form sends a default's own `Aliases` for whatever stays ticked, so unticking one of two certain databases never reached the "`Aliases` is null" path. `SettledAliases` adds the generic names in that case too. Pinned by `A_plain_pdo_app_is_asked_…`, `What_the_person_typed_…_still_wins`, `With_two_engines_ticked_…`, `A_cache_ticked_beside_one_…`, `Unticking_one_of_two_certain_…`, `A_guess_unticked_for_another_engine_…`, and the inspector's half in `SourceInspectorEnvTests`.

## 6. Someone with their own database: done

`ProvidedByAttach` names are in `EnvFields`, with `Service` on those and on aliases. `Defaults.EnvVars` leaves them out. Precedence is pinned through `EffectiveEnvironmentBuilder.Compute`, the function a deploy uses: `Somebody_with_their_own_database_…`, `A_value_given_for_such_a_name_outranks_…`. `UnsetVariablesAsync` leaves out what a ticked engine's attachment sets, and also names an example file's attach name when its engine is unticked and no value was given, even if nobody posted it; nothing else would say so.

The coordinator's two additions:
- **Method name: `LaunchService.IsSecretByDefault`** (`LaunchService.Proposal.cs`, private). `ProvidedByAttach` and `AliasOfAttach` are secret unless they plainly hold a host, port, user or database name: by `Role` where there is one, else by name ending. Its answer reaches the surfaces as `LaunchEnvField.IsSecret`, so F2's copy can read that field. Pinned by `An_attach_name_is_secret_unless_…`, `An_alias_is_judged_by_…`, and `Every_name_any_attachment_sets_…`, which walks `AttachEnvNames.All`.
- **A detected service with no name of its own** gets its engine's address name as `ProvidedByAttach`: `UnnamedConnections`, about eight lines, nothing further needed. `SourceFile` is the file its evidence came from. Two engines sharing `DATABASE_URL` get one field, whose `Service` is the first. This covers guesses too, so a project with a weak Redis and no name for it now has a `REDIS_URL` field. Pinned by `A_service_with_no_example_file_…`, `With_its_engine_ticked_…`, `Two_engines_sharing_…`.

## 7. What exists, and a deleted app: done

`LaunchView.Created` comes from the rows, each with link and `Ready`. An app counts as ready at `AppStatus.Running`, a database at `ServiceStatus.Running`. `AppGone` is set when `AppId` is set and the row is gone. The databases step is derived from the rows: `Failed` with its reason, `Running` while provisioning, else `Done`. A blank posted with null is named on the timeline and nothing sets it. Pinned by `What_a_launch_made_is_read_from_the_rows_…`, `An_app_deleted_since_is_gone_…`, `A_launch_that_has_made_no_app_yet_…`, `The_databases_step_says_what_the_rows_say_…`, `A_blank_posted_with_a_null_value_…`. `A_refused_app_keeps_the_database_…` was pinning the defect (`Done` while provisioning) and now says `Running`.

## 8. Ordinary projects misread: done, one part narrowed on purpose

- **Procfile: narrowed, please check you agree.** The brief says "wherever a `web:` line exists". I keep it on Node, Python and PHP, and leave Go, .NET, static and Dockerfile stacks to their image, because those images hold one compiled output or their own `CMD`, and a Procfile written for Heroku names paths that exist only there (`bin/server`). Taking it would break apps that work today. `heroku-php-*` is also dropped on PHP, since the image has its own Apache. Widening is one line, `KindDetector.ImageCanRun`. Pinned by `A_python_apps_real_server_…`, `A_launcher_that_exists_only_on_heroku_…`, `An_image_that_already_knows_how_it_starts_…`. Two tests that pinned the old behaviour changed.
- **JS monorepos.** `RootLocator` goes past a root that is only tooling: a `package.json` that cannot start, with no other marker, no `index.html`, no Procfile, and not a `build` beside its own `src`/`app`/`pages`. Direct children count if they could run by themselves, so `public/index.html` does not. `apps/*` and `services/*` are read one level in, and `packages/*` only if nothing else was found. A lone .NET project below tooling stays at the root, because the pipeline already builds it from there. Pinned by the new "monorepo's root" section of `SourceInspectorTests` and `A_javascript_monorepo_asks_which_app_…`.
- **Nuxt.** `nuxt` with a `build`, no way to start, and not `nuxt generate` is proposed as Node with `node .output/server/index.mjs`. Parity tests hold: `Detect` honours the start command, and `What_a_nuxt_project_is_proposed_as_…` runs both sides.
- **SvelteKit's sentence.** It was not what they saw: the daemon quotes the whole `RUN` step, so a launch read a screen of shell with the sentence inside an `echo`. `Buildpacks.NotASite` is now a name, and `LaunchAdvance.DeploymentSays` lifts it out, with Persian. Pinned by `A_build_that_wrote_no_site_…`.
- **Toolchains.** New public `Toolchains` in `Harbora.Shared`. .NET takes the major of `TargetFramework` for both stages, from the csproj then `Directory.Build.props` (the props file is beyond the brief), XML-parsed so a commented-out target does not count. Go takes the `go` line, never below 1.22, since a newer toolchain builds an older module. The one `cmd/*/main.go` is built when the top has no `main.go`. The default-version pins did not change a byte. Pinned by "The version a project asks for" in `GeneratedDockerfileTests`.

## 9. Zips: done, one reading different

- **Executable bits: applied differently from the wording.** The brief says apply the mode from `ExternalAttributes`. I apply only the owner's executable bit, giving 0755, and otherwise leave the file as created, which is exactly what git keeps. Copying the raw mode would make a file stored 0600 a 403 from Apache running as `www-data`, and the comparison the brief makes is to the same repo by URL. The tar path gets the same treatment, since it had the same defect. Pinned by `Only_the_owners_executable_bit_…`, `An_entry_with_its_executable_bit_set_…`, `A_mode_that_would_shut_out_the_web_server_…`.
- **Wrapper.** `SoleTopLevelFolder` takes off one level, when nothing else is beside it. What is left out of an upload does not count as beside it, and `__MACOSX`, `.DS_Store`, `Thumbs.db` and `desktop.ini` are now left out, or every zip a Mac makes would keep its wrapper. The escape check still runs on the original name, and again after the level comes off. `A_zip_that_wraps_everything_…` changed to the null root as the brief said. `Things_merely_named_like_them_are_kept` gained a sibling file, since a lone entry would now lose its folder.
- **The refusal in Persian.** `SourceArchiveRefusedException` carries `MessageFa` and is still an `InvalidOperationException`. Pinned by `Being_too_large_is_said_…`, `A_zip_refused_for_what_is_in_it_…`, `Too_large_a_zip_…`, `Anything_else_gets_the_sentence_…`.

## 10. Smaller

- **`Deploying` with no id: done.** `LaunchAdvance.QueueingGiveUpAfter`, 20 minutes, ends at `deploy`. Pinned by `Deploying_with_no_deployment_after_twenty_minutes_…`, `A_queue_that_keeps_throwing_…`.
- **Duplicate on retry: done, over the limit.** `AdoptServicesAsync` and `OrphanedAppAsync` come to about 25 lines against "about twenty". The excess is the check that skips what another launch already owns, without which a retry could attach a sibling launch's database. I built it rather than stopping; say if you would rather drop it. Adoption runs only on a retry, and a numbered name counts (`-2`). Pinned by `A_retry_after_a_crash_adopts_…`, `What_a_crash_left_under_a_numbered_name_…`, `What_somebody_made_before_…`, `What_another_launch_made_…`, `The_first_confirm_adopts_nothing_…`.
- **Money refusals: done.** `RefusalIsAboutMoney` comes from `CreationPaymentRequiredException.MoreCreditWouldHelp`, set only by too little balance; not an unpriced tier, a suspension or a spend limit. `DefaultSizeKeyAsync` holds the platform default to the chooser's rules on any host, then falls through. That is stricter than the price check asked for: a default the plan excludes or that does not exist is passed over too. Pinned in `LaunchQuoteTests` and `A_platform_default_…`.
- **A withdrawn engine: wrong, left.** Nothing can withdraw an engine today. `ManagedServiceEngine.Catalog` is every `ServiceCatalog.All`, `ServiceCatalogReader.EffectiveAsync` maps over all of them, and `ServiceVersions.Resolve` falls back to the shipped list when an operator's is empty (`TemplateVersionsController` says so to the operator). So a certain engine is always in `ServiceOptions`, and a note would be dead code. I corrected the comment in `BuildProposalAsync` that implied otherwise. If withdrawal is added, this defect becomes real.
- **`ServiceEvidence.ReasonFa`: done.** Every reason, pinned by `Every_reason_comes_in_persian_too_…`; the English pins read through `InEnglish`.
- **Singular: done.** `LaunchAdvance`, `LaunchService.*`, `LaunchSource`, `AppVerifier`, `AppLogDiagnosis`, `ConnectionProbe.InPersian`; inspection notes were already singular. A census, `LaunchPersianVoiceTests`, keeps it. Refusals from the shared create services (`AppRefusals`, `ResourceCreationBilling`) still say `کنید` and are not in the list you gave; a launch does show them.

## Contracts

None wrong. Additive only: `CreationQuote.MoreCreditWouldHelp`, `AttachEnvNames.IsGenericDatabaseName`, `Toolchains`, `SourceArchiveRefusedException`, `Buildpacks.NotASite`, `AppVerifier.RunningKey` (internal). Doc comments updated in `SourceInspection.cs`, `AppVerificationContracts.cs` and `AppLaunch.cs` (`Harbora.Domain`, outside the two folders I own, comments only). No migration.

No page test in `tests/Harbora.Tests/Http/` broke, and no view changed. `Apps/Details.cshtml:267` still draws `verificationState` as `done` when every check is Skipped; that is F2's page.

## Runs

On `dd7ff8e`, the last commit with code in it. The commit after it adds only this report.

```
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false   BUILD_EXIT=0, 0 errors, 2 warnings (both NU1903 on SSH.NET, as before this work)
dotnet test Harbora.slnx --no-build --nologo -v q                                    EXIT=0
Passed!  - Failed: 0, Passed:  500, Skipped: 17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:   15, Skipped:  0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed: 9327, Skipped:  0, Total: 9327 - Harbora.Tests.dll
Skipped! - Failed: 0, Passed:    0, Skipped: 117, Total:  117 - Harbora.Postgres.Tests.dll (no Docker here, so none of these ran)
grep -c "Harbora.Tests.dll" in the log: 2
```

The constraints' baseline is 7429 in `Harbora.Tests.dll`; I did not measure the base commit itself, so I cannot say how much of the difference is mine. NodeAgent and NodeIngress match their baselines exactly.

Filtered runs during the work were all on `Harbora.Tests`. One commit, `a4c025a`, went in with one PHP pin failing, because my command gated on a `grep | head` rather than the test's exit code; `d2a96f1` fixed it straight after, and later commits gated on the real exit code.

## Unverified

- **Nothing generated was built.** There is no Docker here. The PHP template, the .NET and Go version images, `node .output/server/index.mjs` and the Laravel root are text checked against text. What the PHP base image compiles in is from memory, not `php -m`. The composer root behaviour, the `-j"$(nproc)"` line and the `apt` package names are from memory too. P runs them.
- **The executable bit itself.** This machine is Windows, so `An_entry_with_its_executable_bit_set_…` and `A_mode_that_would_shut_out_…` return before their real assertion here, as `ChildProcessTests` does. Only the pure bit test really ran. They need the Linux CI lane.
- **The daemon's failure text.** That a failed `RUN` quotes the step is from `DockerEngine.BuildFailureMessage` and memory of the classic builder. If a daemon does not quote it, the launch shows the raw message rather than the sentence, which is no worse than before.
- **The Postgres lane did not run.** `OrphanedAppAsync`'s subquery and the rest of this ran only on EF InMemory.
- **Not run:** `node --test`, `npm run build` (no front-end change here).
