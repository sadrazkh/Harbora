# P — The proof: a real launch, on real Docker

Branch `r8/p`, from `2e03fed` (the launch API and `harbora launch`), with `r8/int` merged in up to `05f9729` (D, F1, F2). **V's route check and provisioning wait are not in it**: `r8/v` has not reached `r8/int` yet, so the `url` check here is the one without the route check.

## The result

**The proof passes on a clean GitHub runner, on `a5ca2f7`, in 17 minutes:**
https://github.com/sadrazkh/Harbora/actions/runs/41854995396

It failed for real on the nine runs before it, and each failure is listed below. The success notice, as the API returns it:

```
Launch proof passed at a5ca2f7b5469f10c9b88f5608cca5f521cd53f2b, in 17 minutes.
Names: nip.io on this host's private address resolves on the host and inside containers.
A real ACME server (Pebble) is running on this host, and this host trusts its root.
The real installer ran from nothing, at a5ca2f7…, and the launch migration applied on real Postgres.
Traefik took the panel's certificate over ACME HTTP-01, and nothing below uses curl -k.
The CLI built from this commit signs in and talks to the panel over real TLS.
The object store from the install is healthy, and the panel's helper made a bucket on it with the same image's mc.
node-postgres: exit 0, Succeeded, every check passed; the running app itself ran SELECT 1 through DATABASE_URL and through the DB_HOST alias, read a generated JWT_SECRET of 48 characters, API_KEY from --set and LOG_LEVEL from the example file.
crashes: a deployment that passed its probe, then exit 2 and SucceededWithProblems, and the restarts check says so and quotes the log.
wrong-database-host: exit 2 and SucceededWithProblems because of the logs check, which names the loopback address it could not reach, while the database it ignored answered.
python-worker-redis: a worker from a Procfile line, with no address and no url check, whose own log shows Redis answering through REDIS_URL.
two-apps: with no --folder, exit 1, naming both, nothing created; with --folder api, it built and served that folder.
harbora deploy, from the folder and the harbora.yml the launch wrote: the change is served, and the app still reaches its database both ways (node-postgres).
The launch pages render over a real finished launch with the markers they should have, and neither the generated secret nor the database password is in their HTML or the panel's log.
vite-spa: built as a frontend; / is the built page, a deep path falls back to it, a hashed asset is served, /assets/ is a 403 with no listing; VITE_GREETING is in the page and the bundle; a non-public variable is nowhere in the image or its history (and the same search does find the public one); nginx's start-up lines raised no logs warning.
A second deploy after VITE_GREETING changed, built with --cache-from on the multi-stage image: the new value is in the page and the bundle, and the old one is gone.
A frontend whose build fails, and one that builds no site, each end Failed at exit 1 with the reason on the last line of the deploy log and in the launch's message.
cra-shaped: a real react-scripts project with 'start: react-scripts start' built as a frontend and served its built page. Run directly in a container with stdin closed, that start command: it stayed up and served its page.
node-build-var: a build on the Node image read a public variable set with --set and ticked by nobody; the image's ENV does not hold it.
python-marked-var: a pip install step saw nothing until the variable was marked AvailableAtBuild, then saw its value; the image's ENV does not hold it, and on Docker 28.0.4 its docker history shows the value (a single-stage image, which Buildpacks.ArgLines warns about).
php-pdo: a plain PHP app on the generated image ran SELECT 1 against the MySQL it was given, through DB_HOST, DB_USER, DB_PASSWORD and DB_NAME, with no --set.
A real Laravel project from composer create-project: composer installed on the generated image, the release task migrated real MySQL, / is Laravel's page through public/, and the framework reads 3 migrations back.
The panel logged no error from the launch tick, the checks, an unhandled request or a failed query across all of the above.
```

## Bugs the real run found, and their fixes

**On a fresh install, as things stood, nothing worked at all.** A stranger's `curl | bash` died at the first pull. Past that, its node channel was dead, and every launch was refused. None of these could be seen from an existing server or from a test.

| # | What the run showed | Fix | Commit | Test that fails without it |
|---|---|---|---|---|
| 1 | `docker compose up` stopped at `minio Error unauthorized`: quay.io/minio/minio answers 401 to everybody. MinIO stopped publishing images and archived its repository. **Every fresh install died here**; existing servers still hold the old image | Compose, `ObjectStorageOptions.ClientImage` and the catalogue now pin one image: MinIO's own release, built from its own Dockerfile by coollabsio. See "For the owner" | `3a6eeee` | `ObjectStorageImagePinTests` |
| 2 | Traefik refused `/dynamic/harbora.yml` with `http cannot be a standalone element` when no app is routed yet, and stopped the whole file provider over it. So the node channel's router never loaded, it answered 404, and install.sh ended `WITH ERRORS` | No routes render a file of comments | `dc2a5a3` | `TraefikProxyEngineTests.No_routes_render_to_a_file_with_nothing_in_it` and one more |
| 3 | **Every launch on the defaults ended `InstanceSizeKey: Choose a size.`** straight after `Nothing is charged to this workspace`. Billing ships on and the seeded tiers ship unpriced, and both the chooser and the launch's default demanded a price from the provider's own workspace, which nothing charges. From reading the chooser, the web form would have shown every tier disabled too; that was not driven | A price is no gate in the default workspace. A customer's workspace still needs one | `e456867` | `LaunchSizeDefaultParityTests`, three new cases |
| 4 | `worker: python worker.py` was proposed as a worker, its Redis was created and charged, and the deployment then failed for having no `app.py`, though the start command is what the container runs | A start command stands in for an entry file, in the buildpack and the inspector | `65956f6` | `BuildpackTests.A_python_worker…`, `SourceInspectorParityTests.A_procfile_is_a_python_project…` |
| 5 | A failed build's last line was `Build of … failed at Step 5/11 : RUN npm run build: The command … returned a non-zero code: 3`. The sentence for a frontend with no `index.html` never reached the end of the log | What the failing step printed comes first, without npm's echo and upgrade advice, and the command is no longer quoted twice. It reaches the launch's message too | `a05ca0d` | `DockerEngineBuildFailureTests`, five new cases |
| 6 | Deploy logs had `[91m` and `[0m` around everything a step wrote to stderr: the cleaner dropped the escape character and kept the rest | A colour code goes whole | `af6665e` | `LogTextTests.A_colour_code_goes_whole…` |
| 7 | **A release task stopped at `pull access denied for harbora/laravel-real`**: the one-off always pulled first, and asked Docker Hub for an image that exists on this host and in no registry. **Every deployment with a release command on a built image failed.** Every test of a release task runs over a fake engine | It pulls only what is not already here | `2f6f2c3` | `OneOffImagePullTests` |
| 8 | With 7 fixed, Laravel's migration failed with `Connection refused`: the database read Running the moment its container started, the launch deployed on that, and MySQL was still initialising | Provisioning holds at Provisioning until the engine's own client gets an answer, up to 100 tries 3 seconds apart, and one that never answers has failed | `8ed0344` | `ManagedServiceAnswersBeforeRunningTests`, seven cases |

**That the tests fail without the fixes was checked, not assumed.** With bugs 1, 4, 5, 6, 7 and 8 all put back at once, 18 of the 21 tests for them failed. The other three guard the opposite direction (an image that is not here is still pulled, say) and passed, as they should. With the code restored and touched, 21 of 21 passed. Bugs 2 and 3 were checked the same way on their own: 2 of 2, and 2 of 11, failed.

### Bugs in the proof itself

- **`grep -q` under `pipefail` gave a false "not found".** It stops at the first match, whatever feeds it dies of SIGPIPE, and the pipeline reads as failed. So a secret in a large log could have been reported as nowhere. The run showed it: `docker history` "does not show" the marked value on one run, and shows it once this was fixed. Every such search now reads all its input. `c1f8dc4`
- Spectre colours redirected output under GitHub Actions, which split a `[bold]` id from the word before it. `931940e`
- Four cores are a real limit: by the Laravel step the panel refused with `3.75 of 4 allocatable vCPU`, correctly. The proof now deletes apps it has finished with. `931940e`

## Which of the fix pass's items the run saw

Before F1 and F2 merged, the run showed these exactly as their briefs say, and I left them as instructed: the green verdict over a log warning (step 8 expected exit 2 and got 0), PHP with no driver (`could not find driver`, with the launch reading `Succeeded` over a `composer install || true` that had failed), and the CLI's "It could not tell what this is built with" on a frontend. After the merge, all of those steps pass on real Docker. F1's PHP template built a real Laravel: composer ran, `public/` is the document root, and `storage` is writable.

## For the owner

1. **The MinIO replacement is a choice somebody should confirm.** `ghcr.io/coollabsio/minio`, digest `sha256:69b55a1c…`, release `RELEASE.2025-10-15T17-29-55Z`. Coolify's maintainer builds it, from MinIO's own release with the same entrypoint, `/data` and `mc`. The alternatives I looked at: `pgsty/minio` (a community fork, still releasing in 2026, whose `mc` is a symlink to `mcli`), `cgr.dev/chainguard/minio` (public, `latest` only; my read of its config failed, so I do not know whether it carries `mc`), `bitnamilegacy/minio` (frozen, with different paths). MinIO itself now only offers AIStor. **MinIO upstream is archived, so this is a stopgap**, and replacing it is a product decision.
2. **Your next `install.sh update` moves production's MinIO from 2024-10 to 2025-10, on existing data.** The proof installs from nothing, so **that upgrade has not been run.** 2024-10 was not available from coollabsio, and nothing can pull the old image any more. Try it on a copy of the data first. A server that is reinstalled today cannot get the old image at all.
3. **`react-scripts start` can serve in a container.** The assumption behind the dev-server rule was wrong: with stdin closed it stays up and serves its page. The rule still does the right thing, and a frontend gets a built site, but it is a choice rather than a necessity, and such an app that is running today moves to nginx on its next deploy. `NodeStartRule`'s comment now says so (`af28d44`). `vue-cli-service serve` and `webpack serve` were not run.
4. **A marked variable's value is in a single-stage image's `docker history`.** Confirmed on Docker 28.0.4, on the Python image. That is what `Buildpacks.ArgLines` warns about, and what D reported for apps already deployed.
5. **`--yes` has no way to tick an engine the inspector did not find.** `LaunchPlan.ChoicesFrom` starts from `defaults.services`, which holds only the certain ones, and `--no-service` can only remove. A plain PHP app with nothing naming its engine gets no database from the CLI, and only the panel's screen can tick one. So `php-pdo` names MySQL with `ext-pdo_mysql` in its `composer.json`, the one place a PHP project can say so. A `--service` flag does not exist, and I did not add one.
6. **A launch's id is not printed on success.** The proof reads the newest row from Postgres to find it. A person has no need of it, so I left it, but an API client following `harbora launch` does.
7. **The first tier is nano: a quarter of a core and 256 MB.** Everything here, MySQL included, ran on it. A 4-vCPU host holds about sixteen apps and databases at that size before the panel refuses, with a sentence that says why.

## What the proof still does not establish

- **A certificate from a public CA.** The whole ACME path is real (Traefik asks, Pebble connects to the name over port 80), but Let's Encrypt itself needs a public address.
- **`harbora launch --git`.** The installer's own clone is real and the launch's is not exercised. All launches here are folder uploads.
- **A remote node:** `RemoteDockerEngine`, `X-Build-Args`, a launch landing on a node.
- **A paying customer's workspace.** Everything ran as the owner, in the exempt workspace, so a wallet, a charge and a real quote were never touched. The refusal when a customer's tiers are unpriced has a unit test only.
- **The browser.** The pages were fetched and read. The upload form, the confirm form and the poll script were not driven, and the CLI took every launch.
- **Retry, and a panel restart in the middle of a launch.**
- **Other engines.** Postgres, MySQL and Redis ran. MariaDB, MongoDB, RabbitMQ, NATS and Meilisearch did not, and the new readiness wait does not cover the ones with no shell client (Mongo, RabbitMQ, NATS), which still read Running as soon as they start.
- **Other stacks.** A Next.js stand-in, not Next.js. No pnpm or yarn lockfile, no .NET or Go, no zip wrapper, no frontend on a port other than 80. **Gatsby and Vue CLI were left out, as instructed.**
- **An upgrade over existing data**, which item 2 above is the sharp end of.
- **V's route check and provisioning wait**, which are not merged yet. The readiness wait in bug 8 is separate from V's: V holds a deployment while a service is Provisioning, and this makes Provisioning last until the database answers, so the two should compose. That has not been run.

## What was built

- `deploy/launch-proof.sh`: numbered steps, each asserting a fact afterwards. Steps from 3b on fail the run without stopping it, so one run finds every bug. Failures are annotations with what the command said, the launch, the deployment's log tail, and the panel's and Traefik's errors.
- `deploy/proof-fixtures/`: 34 files, 47 KB, 12 fixtures. None is built into the panel's image, since the Dockerfile copies only `src/` and `docs/tutorial`. No test scans them, and `ci.yml`'s script sweep skips them, having no shebang.
- `.github/workflows/launch-proof.yml`: every push to `proof/**`, `master` only when something a launch depends on changed (its `decide` job, because `on.push.paths` cannot say both), and `workflow_dispatch`. It is not in `complete`. `ci.yml` now names the script in its required sweep.
- **The certificate decision:** a real ACME server on the runner, because the CLI has no way to skip verification, rightly, and the `url` check would have waited five minutes per app to fail for a reason that says nothing about the app. No product rule was changed for it. Only `ACME_CA_SERVER`, a compose override and the host's trust store differ.
- `README.md`, `README.fa.md`: the "Known limitations" bullet now says what is proven and what is not.

## The runs

| Run | Commit | Result |
|---|---|---|
| 1, 2 | `54bf262`, `793f48b` | Failed: the MinIO pull |
| 3, 4 | `3a6eeee`, `2c2763c` | Install up; node channel 404 |
| 5 | `c214da8` | Every launch refused; the Traefik error found |
| 6 | `a733e3e` | Node channel fine; `Choose a size.` |
| 7 | `e456867` | **First real launches: node-postgres passes**; 8 steps fail |
| 8 | `931940e` | Only steps 8, 19, 20 fail, all F1's or blocked on it |
| 9 | `b4e7afb` | F1 merged; only Laravel fails, at `Connection refused` |
| 10 | `8ed0344` | **Passed**, 18 minutes |
| 11 | `a5ca2f7` | **Passed**, 17 minutes, with the searches fixed |

A failure before the launches takes about 5 minutes, and the panel's build is about 3 of them.

**Local, on `a5ca2f7`:**

```
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false
BUILD_EXIT=0, 0 Error(s), 0 "warning CS". Only NU1903 (SSH.NET, in Harbora.Postgres.Tests).

dotnet test Harbora.slnx --no-build --nologo -v q
EXIT=0
Passed!  - Failed: 0, Passed:  500, Skipped: 17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:   15, Skipped:  0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed: 9428, Skipped:  0, Total: 9428 - Harbora.Tests.dll
grep -c "Harbora.Tests.dll" = 2
```

The Postgres lane was not run, since there is no Docker on this machine. Nothing was added to it. Everything in the proof ran on real Postgres instead, including the launch migration and every launch query. Front-end node tests and `npm run build` were not run: no script or view changed here, and the panel's image, which runs the Vite build, built in every run above.
