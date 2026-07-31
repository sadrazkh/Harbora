# Harbora Overhaul — Progress Log

Newest entries on top. Every entry records: what was done · files changed · tests/checks run ·
result (success/fail) · decisions · next step.

---

## 2026-07-31 — Run now, and the start of Phase 3 (database hardening)

**Run now** — a nightly job nobody can try until tomorrow is a nightly job nobody trusts.

- The execution moved out of `CronRunner` into `CronJobRunner`, so the schedule and the button take
  exactly the same path: what someone tests by hand is what happens at 03:00.
- Queued through the existing durable job queue (`JobKind.CronRun`) rather than run inline — a job
  can take minutes, and the request that started it must not be what keeps it alive.
- A run started by hand does **not** move the schedule. Testing tonight's backup must not quietly
  cancel the one that mattered.
- Runs are marked as started by hand, because "why did this run at 14:32 when it is scheduled for
  03:00?" is otherwise unanswerable.
- One at a time: a held-down button would otherwise start a container per press.
- **Interrupted runs are now settled at startup.** A row with no finish time is shown as still
  running, so a job killed mid-run was reported as running for ever — and with the guard above it
  could never have started again.
- The queue path checks the kind itself, not only the button: a request can be claimed long after it
  was made, by which time the service may have been deleted or changed.

**Phase 3 — database hardening, first slice: deleting a database tells the truth**

Two linked defects, both found by reading what the code actually did rather than what it claimed:

1. **The architecture view never showed a database connection.** It searched each app's stored
   environment values for the database's container name — but attaching a database always stores
   those variables **encrypted**, so it was searching ciphertext. An app with a database attached
   was drawn with no connections at all, which is precisely the case the view exists for.
2. **Removing a database asked "Remove service?" in a browser dialog and nothing else.** It never
   checked which apps were using it, and it deleted the row while leaving the volume on the node —
   the code comment said the data was kept, but nothing recorded where, so "kept" meant unreachable
   and untracked.

Both need the same answer — who is using this database — so it is built once (`ServiceUsage`,
`ServiceUsageService`) and used by both. A deliberate asymmetry: a value that will not decrypt is
still read rather than skipped, because the two mistakes are not equal. Missing a user is what lets
a database be deleted out from under a running app; a spurious one only makes a warning too
cautious. (The first version skipped it — mutation testing showed the test could not tell the
difference, and thinking about why showed the choice was wrong.)

The delete button now leads to a page that says what will happen to the data, names the volume if it
is kept, lists the apps that will stop working, and asks for the name to be typed — but only when
the data goes too, since asking for it on a reversible action teaches people to type it without
reading.

**Tests / checks**

- Suite **712 passing**, 0 errors / 0 warnings.
- Mutation testing: **15 mutations, 15 caught** (6 on "run now", 9 on the database work).
- Two weak tests found and replaced along the way: the usage tests used a passthrough protector
  whose "ciphertext" still contained the plain text, so they would have passed without anything
  being decrypted — the one thing they exist to check. They use the real protector now.

**Not yet done in Phase 3**

- Storage usage per database (how big is it, is the disk about to fill).
- Version pinning surfaced in the UI: the catalog offers explicit versions, but the entity still
  defaults to `latest`, and nothing shows the version actually running versus the one configured.
- Rotation of credentials; per-engine restore beyond the existing dump/restore.

**Verified live on the server**

- Pressed "run now" on a job scheduled for 03:00: the run appeared marked as done by hand, with the
  job's own output, and `NextRunAt` was still 2026-08-01 03:00 — the schedule was not disturbed.
- Created a Postgres database, attached it to a service, and opened the remove page: it listed the
  attached service by name (the encrypted-value bug, proven fixed on real data) and named the volume.
- Delete protection: refused with no typed name, refused with the wrong name, and only removed the
  database — and its volume — on an exact match.
- Migration applied with an automatic restore point first (`pre-upgrade-20260731-100653.sql.gz`).
- One thing the live run caught that the tests could not: the removal sentence was English-only while
  the page around it rendered Persian. The wording moved into the view, which is bilingual; the plan
  now carries only the decision.

Test state removed afterwards: no test apps, no managed services, no cron runs, and
`CpuOvercommitFactor` back to `1.0`.

## 2026-07-31 — Scheduled jobs and release tasks (Cron + Release Task)

Two things a real PaaS is expected to have and Harbora did not: a command that runs *before* a new
version goes live (database migrations, almost always), and services that run on a schedule instead
of staying up.

**What was built**

- `CronSchedule` — a five-field cron parser answering one question: when next? Written rather than
  taken from a library because the surface needed is one method, and a scheduler that drifts or
  double-fires is invisible until a nightly job has been dead for a month. Includes cron’s own
  or-rule when both day fields are restricted, Sunday-as-7, and a bounded search so an impossible
  schedule (31 February) answers "never" instead of spinning.
- `CronRunner` — a one-minute tick. Each run is a short-lived container; the `CronRun` row it leaves
  is the feature. First sight of a job schedules it rather than firing it; the schedule advances
  *before* the run so a slow job is not started twice; missed runs are not replayed.
- `App.Command` + migration — what a scheduled job actually runs, deliberately separate from
  `BuildCommand`: for a job built from a repository those are two different commands.
- `RunReleaseTaskAsync` — runs from the **new** image, with the app’s environment and network,
  before the new container starts. If it fails the deployment fails and the version already serving
  keeps serving. That ordering is the entire reason it does not live in the container’s own
  start-up, where a failed migration takes the site down with it.
- Cron run history and the job’s command on the app page; schedule and command validated server-side.

**Five defects, every one found by running it on the live server rather than by review**

1. **A release task could not run at all on most real images.** The command was sent as the
   container’s *command*, which for any image with an `ENTRYPOINT` (`dotnet App.dll` — nearly every
   application image) makes it *arguments* the image ignores. Proven on the server: with an
   entrypoint that ignores its arguments the run returned **exit 0** — a migration that never ran,
   reported as a successful release. With one that does not, the deployment hung indefinitely.
   Fixed in `OneOffLaunch`: the command replaces the entrypoint. Affected scheduled jobs and every
   other one-off container too.
2. **A release task had no time limit,** so a command waiting for input left the deployment "in
   progress" for ever with nothing to click. Now bounded (`ReleaseTaskTimeoutMinutes`, default 30),
   and a shell-less image is reported as "this image has no shell" rather than Docker’s complaint
   about `sh`, which reads like a typo in the command.
3. **Deploying a cron service always reported Failed.** `ServicePlan.IsLongRunning` existed but was
   referenced nowhere — the rule was on paper only. The pipeline started the job’s image like a web
   service, the container exited as a scheduled job’s container must, and the health gate called
   that a broken deploy. The panel said "Failed" while the job ran successfully every minute
   underneath. Non-long-running kinds now finish at the image, with a matching
   `Deploying → Succeeded` transition added to the state machine — passing through `HealthChecking`
   would record a check that never ran.
4. **Captured output could not be stored at all.** Docker frames the output of a container with no
   TTY: eight bytes per chunk, NUL bytes included. PostgreSQL cannot hold a NUL in a text column,
   so the write threw — and because it threw *inside* `SaveChanges`, the pipeline could not even
   record that it had failed. The deployment sat "in progress" indefinitely, which is worse than a
   plain failure. Fixed in `LogText`, applied to deployment logs, the stored failure message and
   cron run output. The stored failure message was also being saved **unredacted** while only the
   log line was redacted — a build error echoing a secret kept it in the database. Now both.
5. **A scheduled job could not be given a command,** and nothing said so. The runner read a field
   the create form never captured, so a job fired exactly on time, ran the image’s default
   entrypoint, exited 0 and did nothing — a history full of successful runs that accomplished
   nothing. Now a required field for cron services, refused at the form.

Also fixed along the way: a cron run did not join the workspace network (a job got the environment
variables naming its database and no route to reach it — indistinguishable from wrong credentials),
and `Progress<T>` was used to capture output it hands over asynchronously, so the last lines were
lost — a run recorded the image pull and nothing the job printed, and a failure message said "It
produced no output" about a command that printed plenty. `InlineProgress` reports on the calling
thread.

**Tests / checks**

- Suite **690 passing**, 0 errors / 0 warnings (from 663 at the start of this work).
- `CronRunner`: 13 tests, previously **none** — it had only ever been checked by watching production.
- `CronSchedule` 24 · release task 11 · `LogText` 8 · `OneOffLaunch` 4.
- Mutation testing: **23 mutations, 23 caught.** One (removing the release-task timeout) was caught
  by the suite hanging, which is the bug it reintroduces.
- Live on the server, after deploying each fix:
  - a release task that succeeds — its real output (`MIGRATION_STARTED`/`MIGRATION_DONE`) captured
    from an image with an `ENTRYPOINT`, finishing **before** the container starts;
  - a release task that fails — deployment **Failed** with a readable reason quoting the command’s
    own output, version 1 still **Up** and answering **HTTP 200**, app still Running;
  - a cron service — deployment **Succeeded**, then three runs exactly one minute apart, exit 0,
    with `NIGHTLY_JOB_RAN` and its timestamp in the history;
  - a cron service with no command — refused at the form;
  - the migration applied with an automatic restore point taken first
    (`pre-upgrade-20260731-092101.sql.gz`, 537 KB).

**Server left clean**

- Test apps (`nightly`, `nightly2`, `nightly3`, `reltest`, `reltest2`) deleted; 0 cron runs left.
- `CpuOvercommitFactor` back to `1.0` — raised to `2.0` with the owner’s approval only to fit a test
  app on a two-core node whose entire CPU is committed to the one real app.
- The `test` app is Running and serving; its 404 at `/` is the application’s own routing — Traefik
  reaches the container.

**Next step**

- A "Run now" button for scheduled jobs: without it a nightly job cannot be checked until tomorrow.
- Editing the schedule, command and release command after creation — they are set at create time only.
- A notification when a scheduled job fails; nothing currently tells anyone.

## 2026-07-31 — Backups that arrive where you can see them: Telegram, email, S3

Scheduled backups already existed — every N hours, with retention, to a local directory or any
S3-compatible bucket. What was missing is the part that makes people actually trust them: a copy that
lands somewhere you look every day.

**Delivery is not storage, and the model says so.** A destination is where the artifact *lives*:
restore reads from it, retention deletes from it. A chat or a mailbox can do neither — nothing fetches
a file back out of a sent email. So `BackupDelivery` sends a *copy*, and the stored artifact remains
the one a restore can read. Choosing the other design would have quietly broken restore for anyone who
picked Telegram.

**The size check is the point.** Telegram refuses documents over 50 MB and mail servers refuse
attachments long before that, so a channel that simply tries and fails is a channel that looks
configured and protects nothing. The artifact is measured first and refused with both numbers and
somewhere to go: *"The backup is 180 MB, and Telegram accepts at most 50 MB. Keep using a storage
destination for artifacts this size."* Never "make your backup smaller".

**Failures are visible where the channel is offered.** Delivery cannot fail a backup — the artifact is
already stored by then, and an unreachable chat is not a reason to record a successful backup as failed
— but the outcome is written onto the channel row, so a delivery that quietly stopped working shows on
the backups page with the reason. Same treatment the alert rules got.

**The Telegram step people get stuck on is handled.** A bot cannot open a conversation, so the
recipient must message it first, and Telegram then reports the numeric chat id only through
`getUpdates`. Rather than sending someone to a third-party "what is my id" bot, the panel asks for
them: paste the token, press Start in Telegram, click Find, and the chat id fills itself in. An empty
answer says exactly what to do rather than looking broken.

**Verified on the live server**, with a deliberately invalid token so the chain could be proven without
anyone's real bot:

| Step | Result |
|---|---|
| Add a Telegram channel, press test | uploaded, and recorded `Telegram returned 401 Unauthorized — {"ok":false,…}` |
| Run a real backup | completed, and the channel's last attempt moved — delivery fires on the real artifact |
| The backup itself | unaffected by the delivery failure, as designed |

**Tests:** 560 → 575. Nine mutations, all caught — the last only after a real one: removing the
engine's delivery call was *rejected by the compiler* rather than caught by a test, which proves
nothing about behaviour. An end-to-end test now runs a genuine backup through the engine and asserts
the channel received it.

---

## 2026-07-30 — Deploying a real project: the port it listens on, and what "healthy" means

Pushing an actual ASP.NET Core project with `harbora deploy` got all the way through: packed, uploaded,
built on the server, container started, *"Application started"* in the log — and then failed. Twice, for
two different reasons, both of them ours.

**1. Harbora probed a port nothing was on.** The app had been created with port 80. .NET 8 listens on
8080, and the image says so (`EXPOSE 8080`). The image knows where it listens and the configured number
was simply wrong, so the deploy spent its whole health window talking to a closed port.

The image is now asked. When it declares ports and the configured one is not among them, the declared
one is used and the app row is corrected, so the panel stops advertising a port that cannot work. An
image that declares nothing, or that agrees, changes nothing — overriding on no information would be a
guess rather than a fix. Among several exposed ports a recognisable web port wins over a debug or
metrics one, and among equals the lowest, so the answer is deterministic instead of an artefact of list
order.

**2. Then it refused a working app for answering 404.** With the port fixed, the probe reached the app —
which returned `404` for `/`, because it is an API with no root route. The gate demanded a status below
400 and failed the deploy on a service that was serving perfectly.

A configured health path and the default root are different questions. A path someone chose is an
assertion about health and is still held to it. The root is only ever asking "is anything serving
here?", and a 404 answers that as clearly as a 200 — while a 5xx does not, because that is the app
failing rather than a route being absent. When a 404 is accepted the log says why, so the leniency is
visible rather than silent.

**Also, from the same session:** `harbora deploy` in a folder whose `harbora.yml` named an app that does
not exist just refused. The list was already in hand, so it now offers it — the same treatment as no
name at all.

**Verified with the user's own project, on the live server:**

| Deployment | Outcome |
|---|---|
| #7 (before) | built and started, then failed probing `:80` |
| #8 (port fix) | probed `:8080` correctly, failed on the 404 at `/` |
| #9 (both fixes) | **succeeded**; `server: Kestrel` behind the domain, so traffic reaches the app |

**Tests:** 543 → 560. Eleven mutations across the port choice and the probe rule. The first sweep left
three alive, and all three were tests that proved nothing: the pipeline harness already defaulted to
port 8080, so asserting "the port is 8080" asserted the default back to itself, and no case
distinguished a preferred web port from the lowest one. Fixed by making the configured port disagree
with the image, which is the entire situation being tested.

---

## 2026-07-30 — A CLI that can update itself, and says when it needs to

Two things were missing once `harbora deploy` worked: there was no way to update the CLI except
finding the install script again, and nothing ever told anyone their CLI was old. A stale CLI does
not announce itself — it fails in ways that look like server bugs.

**One version for the product.** The panel and the CLI now take their version from a single
`Directory.Build.props`. Without that, "your CLI is older than this server" compares two numbers from
different places and means nothing — the panel's assembly was reporting the .NET default of `1.0.0`,
which would have told every user, forever, that they were behind. The Dockerfile has to copy that
file into the build context or the published panel falls back to `1.0.0` again; found by checking the
live endpoint rather than by assuming.

**`GET /api/v1/version`** (anonymous, so it works before signing in) reports what the panel is and
which CLI matches it. **`harbora update`** downloads the matching asset from the project's GitHub
releases and replaces the running binary — renaming the old one aside first, because Windows will not
overwrite a running executable, and clearing that leftover on the next start.

**The notice** appears after a deploy has already been queued, gives up after three seconds, and stays
silent when the panel is too old to answer or the version cannot be read. A check that cries wolf is
worse than no check.

**Verified from Windows against the live panel:**

| Step | Result |
|---|---|
| `GET /api/v1/version` | `{"server":"0.2.0","cli":"0.2.0"}` |
| `harbora update --check` on 0.2.0 | `Already up to date (v0.2.0)` |
| a 0.1.0 build deploying | deploy succeeded, then `! This CLI is 0.1.0; the server expects 0.2.0` |
| `harbora update` on that 0.1.0 build | fetched v0.2.0, replaced itself, `--version` → 0.2.0 |
| leftover `.old` binary | cleared on the next run |

**Tests:** 511 → 533. Seven mutations across version comparison and asset naming, all caught. One
test earned its place immediately: `"2026-07-30"` was being cut at the dash and read as version
**2026**, so any date-versioned panel would have told every user they were years behind. A version
string with no dot is no longer treated as a version at all.

---

## 2026-07-30 — `harbora deploy` from a developer's machine, actually working

Reported from a real terminal: `harbora deploy` answered *"404 Not Found: App not found."*, and
`harbora deploy test` queued a deployment that failed.

**Why the deploy failed.** The CLI chose its mode from the local folder: a `.git` directory meant
"let the server pull from its remote". But whether the server has anything to pull is a fact about
the **app**, not about the folder. The app had been created for pushed source — the CapRover-style
flow this CLI exists for — so it had no remote, and the server correctly reported *"no source archive
was uploaded"*. Any user working from a git checkout hit this. The CLI now asks the server
(`canServerPull` on `GET /apps`) and uploads when there is nothing to pull; explicit flags still win.

**Why the first command said nothing useful.** With no `harbora.yml`, the CLI had no app name and
returned the server's 404 verbatim — naming neither the problem nor a way out, while the list of apps
was one request away. Now it lists them and asks; in CI, where there is nobody to ask, it prints the
available slugs instead of blocking.

**Then it remembers.** Whichever way the app was resolved, if the folder has no config one is
written, so the next deploy is just `harbora deploy`. It never overwrites an existing file.

**Signing in no longer requires the browser.** `POST /api/v1/auth/token` exchanges a panel account for
a CLI token, so `harbora login --email you@example.com` works from a terminal — previously the only
way in was to open the panel, create a token by hand and paste it. The endpoint is held to the same
rules as the web login: same per-IP limiter, the password verified even for unknown addresses so
timing cannot confirm who has an account, one audit entry either way, and identical wording for both
kinds of failure.

**And several accounts can be signed in at once.** The config held one server and one token, so a
second `harbora login` silently replaced the first. It now keeps a profile per account, asks which to
use when more than one is signed in, and adds `harbora accounts` to list, switch and log out.

Testing this on a real machine immediately produced a bug of its own: a migrated config is named
after its server (the old file did not record who it belonged to), so the first login afterwards filed
the *same* account a second time and every command started asking which of two identical accounts to
use. Signing in now adopts that placeholder instead.

**Verified against the live server, from Windows:**

| Step | Result |
|---|---|
| `harbora login --email …` | signed in, no token created by hand |
| `harbora deploy` with no config, no TTY | lists `test` instead of a bare 404 |
| `harbora deploy test` in a git folder | `PushFolder (this app has no Git remote on the server)` → built and deployed |
| the app itself | `hello from harbora deploy` over HTTPS, `200` |
| `harbora deploy` with no arguments afterwards | deploys, using the config it wrote |

**Tests:** 496 → 511. Seven mutations across the mode decision and the account store, all caught —
one only after the test stopped asserting through a fallback that hid a stale pointer.

---

## 2026-07-30 — Notifications that admit when they fail — and have never once worked (P11/P14)

Last phase made the SSL and crash alerts fire. This phase asked the obvious next question: does
anything actually receive them?

**Three things were wrong before the answer even arrived.** The HTTP response from a webhook, Discord
or Telegram was discarded, so a 404 was indistinguishable from success. There was no timeout, so one
unresponsive endpoint held the caller for the handler's 100-second default — per alert rule — while a
failed deploy waited to report that it had failed. And the panel's Test button set *"Test notification
sent."* unconditionally: before the delivery was judged, and even for a rule belonging to another
workspace. A test that cannot fail is worse than no test button; it is an assurance issued without
looking.

**Then checking the response found the real one.** The first live test came back:

> Refusing to call webhook URL: not an absolute URL.

`AlertsController` serialises the channel target from an anonymous object, so it is stored as
`{"url": "..."}`, `{"botToken": "..."}`. The service reads it into `UrlTarget`/`TelegramTarget`, whose
properties are PascalCase — and `System.Text.Json` matches **case-sensitively** by default. Every
field came back null, every channel failed at the SSRF guard, and **no notification of any kind has
ever been delivered** — not a deploy failure, not a crash, not a backup failure. It stayed invisible
for exactly the reason this phase existed: the failure was swallowed and the button reported success.

Fixed by reading targets case-insensitively, which also repairs every target already stored. The
regression test uses the exact JSON the controller writes, so the two halves cannot drift apart again.

**Live, on the server, both directions:**

| Rule | Reported |
|---|---|
| webhook → a receiver deployed on the platform itself | `delivered ok` — the first notification Harbora has ever delivered |
| webhook → a URL that 404s | `The webhook returned 404 Not Found` |

The failing rule now says so on the alerts page, with the reason, instead of only in the panel logs.
The receiver was hosted on the platform, so no test payload left the machine.

Along the way the first receiver image (`kennethreitz/httpbin`) crash-looped, and the health diagnosis
from two phases ago named it immediately: *"exec /usr/local/bin/gunicorn: exec format error"* — an
amd64-only image on an ARM server. That is the message that used to read "Container failed its health
check."

**Tests:** 479 → 496. Ten mutations, all caught, but only after two rounds: a stale-error test built a
second context and so never watched a rule recover, and the timeout test passed while taking 100
seconds because it asserted the wording rather than that the attempt ended promptly. Both were tests
that did not test what their names claimed. The delivery timeout is configurable for the same reason
the health-gate timings are — a real ten-second wait belongs in production, not in a test suite.

---

## 2026-07-30 — Host ports are reserved, not guessed; multi-server verified for the first time (P12)

Remote nodes have no shared overlay, so the proxy reaches an app at `node-host:published-port`. That
port came from `20000 + sha256(slug#number) % 10000` — deterministic, and blind to everything already
running.

**Ten thousand slots picked at random collide far sooner than the number suggests.** A coin flip at
about 119 deployments on one node, and every redeploy draws again, so it is ordinary usage rather than
a distant edge case: `app78` and `app138` both land on 22585 at their first deployment.

The consequence was worse than a failed deploy. Routes store host **and** port, so a port belonging to
a retired deployment that is later handed to another app quietly points the first app's traffic at the
second app's container.

Now a tracked reservation: `HostPortAllocation`, unique on (server, port). The unique index — not a
check-then-insert — is what actually stops two concurrent deploys agreeing on the same number; the
allocator retries when it loses the race. Ports are released after the cutover (never before: an
early release would offer another app a port still carrying live traffic), when a deploy fails, and
when an app is deleted. The migration **backfills** reservations for apps already serving on a remote
node — without it the allocator would have considered exactly those ports free.

**Multi-server had never been verified.** The README has advertised helper nodes since the beginning.
So this phase ran a real one: the agent built and joined as a node — bound to the Docker bridge
address only, never exposed publicly — and reported genuine host info (2 cores, 4 GB).

| Check | Result |
|---|---|
| Two apps deployed to the node | ports **20000** and **20001** — sequential, not two dice rolls |
| Both through Traefik | `200` on each domain, routed to `node-host:port` |
| Redeploy of the first | took **20002** while 20000 was still serving; 20000 released only after cutover; the other app's 20001 untouched |
| Both apps deleted | every reservation freed (the FK hangs off the server, so nothing cascaded them away — the app-delete path releases them explicitly) |

**The previous phase proved itself in production along the way.** This migration made the panel's own
restart a genuine upgrade, and the automatic restore point fired unprompted: *"Upgrade detected: 1
pending migrations. Taking a restore point first."* → 415 KB written, then migrated.

**Tests:** 466 → 479. Seven mutations; the first sweep left two alive — a failed deploy keeping its
port, and superseded ports never being released — both were pipeline-level rather than allocator-level,
and now have pipeline tests. `ExecuteDelete` gave way to load-and-remove so the reservation lifecycle
is exercisable by the suite's provider at all; the row counts are per-app and tiny.

---

## 2026-07-30 — An update you can undo: restore point before every migration (P10)

`harbora update` pulled new code, rebuilt, and the panel applied migrations on boot — with nothing
captured beforehand. Additive migrations are harmless, but a destructive one, or a new version that
turns out to be broken, left no route back to the data as it was. This project has already had one
update that took the panel down; the missing piece was never the diagnosis, it was the way back.

**The panel now takes a restore point before it migrates, and refuses to migrate if it cannot.**
Refusing is the deliberate choice: migrating anyway and logging a warning spends the one moment the
restore point can still be taken. A panel that declines to start is recoverable — the previous image
and the data are both still there — while a schema migrated with no way back is not.
`HARBORA_SKIP_UPGRADE_BACKUP=1` exists for a host where the dump genuinely cannot run.

It fires only where it matters: an existing install with pending migrations. A fresh install has
everything pending and nothing to lose; an ordinary restart changes nothing.

Two details that decide whether the artifact is worth having:

- `set -o pipefail` in the dump. `pg_dump | gzip` reports **gzip's** exit code, so without it a dump
  that died halfway still succeeds and leaves a valid gzip of a truncated dump — the worst kind of
  restore point, because it looks fine until the moment it is needed.
- The password goes in the helper's environment, never on the command line, which is visible in
  `docker inspect` and in the host's process list.

`DockerOneOffRequest` gained `Env` and `NetworkMode` for this. The dump helper runs with
`container:harbora-panel`, sharing the panel's network namespace — so whatever host the panel's own
connection string uses resolves identically, with no second copy of the network configuration to drift.

**A restore point nobody can restore is theatre**, so the break-glass tool grew the other half:
`harbora backups`, `harbora backup-db`, `harbora restore-db <file>`. These are host-side, like the
rest of the recovery commands, so they still work while the panel is crash-looping — which is exactly
when you want a copy before surgery. `restore-db` saves the current database as `pre-restore-*` before
replacing it, and asks you to type the database name.

**Rehearsed on the live server, on scratch copies rather than production data:**

| Step | Result |
|---|---|
| Copy the database, rewind it one migration | 7 applied, 1 pending, real data |
| Start the panel against it — a genuine upgrade | "Upgrade detected: 1 pending migrations. Taking a restore point first." |
| Dump produced | 415 KB, visible to the panel, then migrated to 8 |
| Restore it into a fresh database | 1 user preserved, 7 migrations, and **no** post-migration column — it captured the state *before* the upgrade |
| `harbora restore-db` against a scratch database | marker row gone, users kept, `pre-restore-*` safety copy written, production database untouched |

The redirection used for that last test was proven with a non-destructive `backup-db` first — a
`DROP DATABASE` that silently targeted the wrong name would have been an unacceptable way to find out.

**Tests:** 450 → 466. Nine mutations across the plan (back up on a fresh install, back up on every
restart, drop `pipefail`, password on the command line, no shell quoting, prune everything, prune
newest first, drop `--no-owner`, local calendar in the file name) — all nine caught. The file name is
pinned to the invariant calendar by a test that runs under `fa-IR`, because retention orders by name
and a Jalali year would sort a brand-new restore point as the oldest and delete it.

---

## 2026-07-30 — Monitoring that tells the truth about app health (P11)

Started by looking at what the live server actually holds rather than at the roadmap. Metrics
collection turned out to be healthy — 690 samples in the last hour, host and per-container. Three
other things were not.

**1. A crash-looping app kept its green Running badge.** Crash detection watched only for containers
in state `exited`. App containers run under `unless-stopped`, so Docker revives one that dies on
startup and it reports `restarting` — the same root cause as the health-gate bug in the previous
phase, in a second place. Proven on the server before touching any code: a container left
crash-looping for two collector passes, and the panel still said the app was running, with no alert.

**2. Nothing ever cleared `Crashed`.** Once marked, an app that recovered on its own stayed marked
until someone deployed again. Both directions were wrong, so the reconciliation now goes both ways.

**3. `SslExpiring` could never fire.** The event existed in the enum, the checkbox existed in the
alert-rule UI, and the notification router had a branch for it — but nothing in the codebase ever
raised it. Ticking "tell me when SSL is expiring" promised something that could not happen.
`CertificateWatcher` now checks each SSL domain daily and raises it, reusing the domain inspector
built two phases ago. The 14-day threshold is meaningful rather than arbitrary: Let's Encrypt issues
for 90 days and Traefik renews at 30 remaining, so a certificate still inside that window is evidence
that renewal is *failing*, not pending — a healthy one never gets there, which is what keeps the
alert from becoming noise.

Also fixed: the disk warning throttled through a `static` field on a scoped service — one timestamp
shared by every server and workspace, so the first node to fill up silenced the warning for all the
others for an hour. Now keyed per node.

**Verified live, all three:**

| Check | Before | After |
|---|---|---|
| Container left crash-looping | app status `Running` (2) indefinitely | `Crashed` (5) within one pass |
| Container healthy again, no deploy | stayed `Crashed` | back to `Running`, logged "App p11app recovered" |
| `expired.badssl.com` added as a domain | nothing, ever | `Certificate for expired.badssl.com expires 2015-04-12` |

The expiry check was proven against a real expired certificate rather than a mocked date, and it
stayed silent about the healthy `nip.io` domain (89 days) in the same pass — both halves of the
decision, on live infrastructure.

**Tests:** 429 → 450. Eleven mutations across the three decisions; ten caught, one rejected by the
compiler. Dates in alerts are pinned to the invariant calendar with a test that runs under `fa-IR` —
these go out to webhooks and email, where a Jalali date is unreadable to everything downstream. The
same fix was applied to the domain checker's expiry line for consistency.

---

## 2026-07-30 — Managed databases proven end to end, and a failed deploy that says why (P9)

P9 asked for one thing that had never been done: **verify the managed-database path on a real host**.
Attaching a database was, until yesterday, a 500 — it used the add-to-a-loaded-parent pattern fixed
in the previous phase — so nothing downstream of it had ever run.

**The whole chain, on the live server:**

| Step | Result |
|---|---|
| Provision PostgreSQL 16 | container `harbora-svc-p9db` up on the tenant network `harbora-ws-default` |
| The connection string the panel reveals | `psql` connected with it verbatim, from that network |
| Attach to an app | six env rows written, every one encrypted and marked secret |
| Redeploy | all six present in the running container |
| DNS from the app container | `harbora-svc-p9db` → `172.19.0.3` |
| TCP + auth from the app container's own credentials | authenticated as `harbora` |

So "attach wires env with no copy-paste" is now a demonstrated fact rather than a claim. No secret
values were printed at any point — the checks assert on lengths, names, and a live authentication.

**What the verification turned up: a failed deploy could not be diagnosed.** Deploying
`postgres:16-alpine` without `POSTGRES_PASSWORD` — an ordinary mistake — produced exactly one
sentence: *"Container failed its health check."* True, and useless. The reason was sitting in the
container's own log, in the one place the user cannot look, because the failed container is removed
moments later.

Four distinct failures had collapsed into that sentence: the container exited, it never started, it
was removed by something else, or it was running and never answered. Each needs a different next
step, so each now gets its own verdict, the runtime's status line (which carries the exit code), and
the container's last output — collected *before* cleanup removes it.

**Then the real host corrected me.** My first version watched for state `exited`. App containers run
under `unless-stopped`, so Docker revives a crashing container within moments and it reports
`restarting` — `exited` is almost unreachable in production. The live deploy came back as *"running
but never returned a success response"*, which is the opposite of what was happening, and only after
burning the full health-check timeout. Added `CrashLooping`; the same deploy now fails in **8
seconds** with:

> The container keeps crashing and being restarted (Restarting (1) …). It is failing during startup —
> usually a missing environment variable or a service it cannot reach. Its last output was: … Error:
> Database is uninitialized and superuser password is not specified.

The log window was sized from that same failure, not guessed: at 600 characters it kept Postgres's
*advice* and cut the error line above it. It is 1500 now, and a test pins the error surviving.

**Tests:** 426 → 429. Nine mutations across the diagnosis and the gate; the first sweep left two
alive — a container that dies *during* probing, and a vanished container reported as never-started —
both now covered by tests written against the fake Docker engine. The last three (restarting isn't a
crash, crash-loop worded as an unanswered probe, log window shrunk back) were caught.

Unchanged and worth stating: a crash-looping container never took traffic, and the previous
deployment stayed live throughout.

---

## 2026-07-30 — Domain readiness: what the browser actually gets (P8)

The Domains panel showed an "SSL" badge whenever the auto-SSL checkbox was ticked. That is the
*intent*, not reality: a domain whose DNS had never been pointed here displayed "SSL" while every
browser showed a certificate error, and nothing in the panel said why.

**Now it probes.** `DomainInspector` resolves the name and completes a real TLS handshake with SNI,
then `DomainDiagnosis` turns those facts into one verdict and one concrete next step. Validation is
deliberately not enforced during the handshake — an expired or untrusted certificate is precisely
the condition worth reporting, so it has to be inspected rather than rejected. Traefik's self-signed
default is read as "no certificate for this host yet", because otherwise a host with no certificate
reports one valid for three years.

DNS is diagnosed **before** the certificate. With DNS wrong, "waiting for a certificate" is a
symptom, and following it costs an afternoon waiting for a certificate that can never be issued.

The check runs from the browser after the page renders, not inside `Details` — it is a live network
call per domain, and the app page should not wait on the slowest one.

**Verified on the live server**, three domains, three verdicts:

| Domain | Verdict |
|---|---|
| `domcheck.91.99.205.231.nip.io` | Ready — "Certificate valid for 89 more days (YR2)", matching `openssl s_client` exactly |
| `harbora-does-not-exist-9f3a.example.com` | DnsMissing — "Add a DNS A record … pointing to 91.99.205.231" |
| `www.wikipedia.org` | DnsNotPointingHere — names the addresses it *does* resolve to, and that no certificate can be issued until it changes |

**Four bugs found, three of them mine.**

1. **Adding a domain to an existing app returned a 500** (pre-existing, and the reason this phase
   couldn't be tested at first). `BaseEntity` assigns its own Id, so every new entity arrives with a
   populated key; EF's default for a Guid key is "the store generates it", and under that assumption
   a key that already has a value can only mean the row exists. A child added to a loaded parent was
   tracked as Modified and saved as an `UPDATE` matching no row —
   `DbUpdateConcurrencyException: expected to affect 1 row(s), but actually affected 0`. Creating an
   app hid it, because `db.Apps.Add` cascades Added through the whole graph.
   Fixed in the model (`DeclareApplicationGeneratedKeys`) rather than at the call sites, because the
   same pattern appears in five places and the next collection someone appends to would bring it
   back. Annotation-only: `has-pending-model-changes` reports none, so no migration.
   **This also broke config restore** whenever a backup contained an env var the app no longer had —
   `RestoreAppConfigAsync` inserts exactly that way.
2. **Every TLS probe threw.** I supplied the certificate callback to both the `SslStream`
   constructor and the authenticate options; .NET rejects that. My catch-all then reported it as
   "nothing answered on HTTPS" — a broken probe reading as a broken deployment. `ProbeFailures` now
   separates "the far end didn't answer" (a verdict) from "our code faulted" (an error, reported as
   Unknown). Found only because I compared the verdict against `curl` from inside the container.
3. **The Persian failure message rendered as `&#x628;&#x631;…`.** Razor HTML-encodes it either way,
   and entities are decoded in attributes but not inside a `<script>` body. Moved to a data
   attribute.
4. A test passed alone and failed in the suite: my mutation script restored sources with their
   original timestamps, so MSBuild kept the mutated build output. The source was innocent.

**Tests:** 405 → 416. Five mutations applied to the diagnosis (DNS-after-certificate,
dropped unknown-server guard, Traefik default treated as real, all-addresses-must-match, expired
reported ready) — all five caught. The certificate reading and the failure classifier are covered
because both were wrong-answer risks the pure tests could not see.

**Left running on the server**: the panel is serving this build. The repo checkout there is back to
`c23f535` and clean, so `harbora update` after the push rebuilds the same thing from source.

---

## 2026-07-30 — Docker Compose deploys, working on real Docker

The last "advertised but missing" feature. The README promised docker-compose; the pipeline threw
"not supported yet".

**Wired into the pipeline.** A Compose app is resolved *before* anything is built or started:
the source is materialised, the file parsed and validated, and an unsupported directive rejects the
deployment cleanly rather than leaving half a stack running. The stack then gets the same guarantees
as a single container — every service starts under a versioned name alongside whatever is currently
running, the web service is health-checked, and only then does traffic switch and the old stack
retire.

`ContainersToRetire` gained a multi-container form. Retiring per-service would have torn down half
the stack it had just built.

**Service-to-service DNS — the thing that would have made this nominal.** My first pass routed to
versioned container names, which means a service written to connect to `db:5432` cannot find `db`.
Almost every real compose file does exactly that, so the feature would have "worked" in a demo and
failed for users. `DockerRunRequest` now carries `NetworkAliases`, and each service is registered
under both its bare compose name and a per-deployment name — the bare name resolves within a stack,
the versioned one stays unambiguous while two stacks overlap during a cutover.

**Verified end to end on the server**, pushed with the released CLI:

*A valid two-service stack* (built web + `redis:7-alpine`, named volume, `depends_on`):
```
Stack: web, cache (web = web:3000)
containers  harbora-stack…-web-1     harbora/stack…-web:build-1
            harbora-stack…-cache-1   redis:7-alpine
https://stack….nip.io/  →  200  "compose-works + cache reachable"
docker inspect cache     →  aliases [cache cache-1]
```
The response text is the proof: the web service opened a socket to `cache:6379` **by service name**.

*An unsafe stack* (`privileged: true` plus a `/etc:/host-etc` bind mount) → **Failed**, with both
refusals named and explained, and **zero containers started**.

**Checks:** `dotnet build` → 0 warnings / 0 errors · `dotnet test` → **387/387**.

**Honest notes**
- `depends_on` orders startup but does not wait for readiness — neither does compose. The health gate
  on the web service is what actually decides whether the stack works.
- Compose volume names are namespaced per app (`harbora-{slug}-{volume}`), so two tenants both using
  `pgdata` don't share one volume.
- Only the web service is health-checked. A background worker that crash-loops will not fail the
  deployment; that needs per-service checks, which compose's own `healthcheck:` key would supply.
- A wasted 16 minutes during testing: the test script called a CLI binary I had deleted in an earlier
  cleanup, so the pushes silently did nothing and the poll loop ran to its timeout. The script now
  fails fast when a push produces no deployment.

---

## 2026-07-30 — CLI v0.2.0 released; the installer was deleting the recovery tool

Tagged `v0.2.0` and pushed. The release workflow published six single-file binaries
(linux/win/macOS × x64/arm64) plus `Harbora.Cli.0.2.0.nupkg`.

**A bug found by running the published installer — on the production server.**
Both tools are called `harbora`: on a server it is the break-glass admin script (`doctor`,
`reset-password`, `fix-key`), on a developer machine it is the deploy CLI. Running
`install-cli.sh` on the server **silently overwrote the recovery command**. `harbora doctor` simply
stopped existing — on a live host, and precisely the tool you reach for when the panel is down.

I caused this by running the installer on the production server to verify the release. The admin
command was restored immediately from the server's own checkout, and the installer now detects a
Harbora server install (or an existing admin script at the target path) and installs the CLI as
**`harbora-cli`**, saying so. Verified: `harbora doctor` and `harbora-cli --version` now coexist.

The README previously described the two tools as "separate tools for separate machines". That was
advice, not a guarantee — and the guarantee is what was needed. Both the README and
`docs/cli-deploy.md` now describe the actual behaviour.

*(The first verification run appeared to fail because `raw.githubusercontent.com` was still serving
the pre-push script; running the installer from the server's updated checkout confirmed the fix.)*

---

## 2026-07-30 — CLI 0.2.0: every deploy mode, a config schema, and a compatibility spec

Completing the CapRover-equivalent experience and preparing the CLI release.

**Deploy modes** — `DeployPlan` decides the source and prints *why*, so "it deployed the wrong thing"
is never a mystery:

| Invocation | Source |
|---|---|
| `harbora deploy` | Packs the folder and uploads it |
| `--push` | Same, forced (even inside a Git repo) |
| `--tar dist.tar.gz` | An archive the caller already built |
| `--branch main` | `git archive` of **committed** content |
| `--ref` / `--tag` | The server pulls from the app's remote |
| `--image nginx:alpine` | Releases the image; builds nothing |
| `--server` / `--token` | CI, with no interactive login |
| `--no-follow` | Queue and return |

Precedence: explicit flags → `harbora.yml` → folder shape (no `.git` ⇒ push, `.git` ⇒ server pulls).
Pinned by `DeployPlanTests`.

**`harbora.yml`** got a real schema and a hand-written parser (`ProjectConfig`) — `app`, `server`,
`build.dockerfile`, `build.context`, `ignore`, `dockerfileLines`, `image`, `branch`. `harbora init`
now writes the full commented template. Unknown keys are ignored on purpose, so a file from a newer
CLI still works with an older one. `dockerfileLines` is the CapRover `captain-definition` equivalent:
the CLI writes it into the upload and the server prefers it over stack detection.

**Server**: `DeploymentRequest.ImageOverride` + `POST /apps/{slug}/deploy` accepting `{ "image": … }`
— an explicit image is released as-is, with nothing built.

**`docs/cli-deploy.md`** — the compatibility spec the user asked for: install, every mode, the full
`harbora.yml` schema, every API endpoint with request/response shapes and status codes, a working
~20-line Python client, and the stability rules this project holds itself to (fields are added not
repurposed; unknown fields ignored; status names stable).

**Release prep**: `--version` now works (it didn't), version bumped to **0.2.0**, package metadata
added, and the unused `YamlDotNet` dependency dropped — `ProjectConfig` parses the small schema by
hand, which keeps the self-contained binary lean. Both release artifacts were built and run:
single-file binary and the `dotnet tool` nupkg.

**Two bugs found by actually running the released binary**
1. **`--version` was unsupported** — Spectre needs `SetApplicationVersion`. First thing any bug
   report needs, and it printed "Unexpected option".
2. **The CLI crashed on server text containing brackets**: `Could not find color or style '60vh'`.
   Server messages went straight into Spectre markup, so an HTML error page — containing
   `min-h-[60vh]` from our own error view — took the CLI down instead of being displayed. Server
   strings are now `Markup.Escape`d.

**Verified end to end with the real published binary on the live server**
```
harbora --version           → 0.2.0
harbora login / whoami      → ok
harbora init                → harbora.yml written
harbora deploy              → packed, uploaded, built, health-checked, live
uploaded contents           → Dockerfile.harbora, harbora.yml, package.json, src
                              (no .env, no node_modules)
https://clidemo…/           → 200  "CLI-RELEASE-WORKS"
harbora deploy --image      → pulled and released nginx:alpine
```
The ARM64 artifact is what ran — the server is aarch64, which also exercised that release target.

**Zero-downtime confirmed under real failure.** Two deployments failed during this session (a broken
app, then nginx health-checked on the wrong port) and the app kept serving the previous build
throughout, 200 the whole time. The failed containers were removed and traffic never moved.

**Checks:** `dotnet build` → 0 warnings / 0 errors · `dotnet test` → **363/363**.

**To publish the release**
```bash
git tag v0.2.0 && git push origin v0.2.0
```
That runs `.github/workflows/release-cli.yml`, which builds six single-file binaries
(linux/win/macOS × x64/arm64) plus the nupkg and attaches them to a GitHub release. The installers in
`deploy/install-cli.{sh,ps1}` download from the latest release, so they start working the moment the
tag lands.

**Uncommitted:** `ProjectConfig.cs`, `DeployPlan.cs`, CLI command/packer/csproj/Program changes, the
image-override path (Application/Infrastructure/Web), `docs/cli-deploy.md`, and two test files.

---

## 2026-07-30 — Push-to-deploy from a developer's machine (CapRover-style)

Until now the server always *pulled* the source: a Git remote it could reach, or a prebuilt image.
That misses the common case the user raised — create the app in the panel, then deploy a folder from
your own machine, with no Git in between (how CapRover works).

**What landed**
- `AppSourceType.Upload` (appended value 6) — an app can now be created with **no Git URL at all**
  and simply wait for its first push.
- `Deployment.SourceArchivePath` — per-deployment, because every push carries its own snapshot of
  the working directory. Migration `UploadedSourceDeploys`.
- `POST /api/v1/apps/{slug}/deploy/archive` — streams a gzipped tar straight to disk (a source tree
  never has to fit in memory), then queues a normal deployment. A rejected push (coalesced onto an
  in-flight deploy) deletes its upload rather than leaking a file per attempt. 512 MB ceiling.
- `SourceArchive` — the unpacker. This is the only place a user's bytes are written to the panel's
  filesystem, so it is deliberately paranoid: entry paths are resolved and must stay inside the
  destination, links are refused outright, and entry count and uncompressed size are capped.
- **Pipeline refactor**: `BuildFromGitAsync` was split into "materialise the source" + a shared
  `BuildFromSourceAsync`. Git checkout and archive extraction now feed *identical* build behaviour —
  stack detection, generated Dockerfile, cutover, rollback — instead of a parallel implementation.
  A pushed archive also wins over the app's configured source: the user just sent that exact code.
- `SourcePacker` in the CLI + `harbora deploy --push`. Pushing is automatic when the folder has no
  `.git`; `--ref`/`--tag` still mean "deploy from Git".

**Exclusions are a security concern, not just size.** `.dockerignore` is honoured first (it is what
the build reads), then `.gitignore`, then a built-in list. `.env` is always excluded — it routinely
holds local database URLs and API keys, and uploading it would ship them to the server.

**Verified live, end to end**
```
app created with SourceType=Upload and no Git URL
token issued, folder packed (471 bytes), POST /deploy/archive → 200
deployment                → Succeeded
build dir on the server   → Dockerfile.harbora, package.json, src
                            (no .env, no node_modules, no .git)
container                 → harbora-pushed…-1  running the built image
https://pushed….nip.io/   → 200  "PUSHED-FROM-MY-MACHINE"
```

**Tests:** `SourceArchiveTests` (traversal refused for `../..`, deep traversal, absolute paths and
sibling directories sharing a prefix; symlinks skipped; empty/corrupt archives get a readable message
instead of `EndOfStreamException`) and `SourcePackerTests` (built-in and ignore-file exclusions,
`.dockerignore` beating `.gitignore`, and a round trip through the server's own extractor asserting
the `.env` never made it). **341/341 passing**, 0 warnings.

**README** gained a "Push code straight from your machine" section covering the flow, the flags and
exactly what is excluded.

**Uncommitted:** the domain/migration/pipeline/API/CLI changes above, the create-form source card,
two new test files, and the README section.

**Next step**
- The CLI binary on the server is still the old release; `harbora deploy --push` was verified through
  the API it calls, not through a rebuilt CLI. Worth publishing a new CLI release.
- Rotate the server's root password (shared in chat).

---

## 2026-07-29 — Restore is now extract-then-swap (closes Phase E)

The last open item. The restore shell was one line:

```sh
rm -rf /data/* && tar xzf /backup/ARCHIVE -C /data
```

The wipe ran **unconditionally and first**. Anything that went wrong afterwards — a truncated
archive, a full disk, the wrong file — left an empty volume and nothing to put back. The gates added
earlier (checksum, archive probe) make reaching that state unlikely, but "unlikely" is the wrong
safety property for the most destructive operation in the product.

**`RestoreScript`** replaces it with four ordered steps:
1. Extract into a staging directory **inside the same volume** — a failure here cannot touch the
   live data, and staying on one filesystem keeps the later moves cheap renames.
2. Move the current contents **aside**, not delete.
3. Move the extracted tree into place.
4. Only now discard the set-aside copy.

If a move fails mid-swap the script puts the original contents back and exits `90`, which the engine
reports as *"the volume's original contents were put back. Nothing was lost."* — the first thing an
operator wants to know after a failed restore.

Cost: peak disk is roughly double the volume while a restore runs. That is the price of not being
able to lose the data, and it is paid only during a restore.

**Tests** (`RestoreScriptTests`) pin the properties rather than the text: extraction precedes any
move or delete, the swap path renames instead of `rm`, the fallback copy is discarded only after the
new tree is in place, a failed swap restores the original, staging lives inside the volume, the
script never sweeps up its own working directories, and the archive name is single-quoted with
embedded quotes escaped.

One test of mine was wrong first: it asserted the injected `; rm -rf /data` text was *absent*, but
correctly-escaped text still appears — inside quotes. Rewritten to assert the exact safe encoding,
which is the property that actually matters.

**Verified live**
```
data            : GOOD-V1
backup          : Completed
data changed to : BAD-V2
restore         : GOOD-V1          ← recovered
staging residue : none (no .harbora-* left in the volume)
```
PostgreSQL restarted cleanly on the swapped volume — worth noting, since PGDATA is unforgiving about
stray files.

**Checks:** `dotnet build` → 0 warnings / 0 errors · `dotnet test` → **313/313**. Test service,
container, volume, backup rows and artifacts removed; staging volume empty; panel 200;
`harbora doctor` clean.

**Phase E is complete.** Every claim it made — encryption at rest, dry-run verification, the checksum
gate, the pre-restore snapshot, a real restore, and now a restore that cannot destroy data on
failure — is verified against real Docker and a real database.

**Uncommitted:** `RestoreScript.cs`, `BackupEngine.cs`, `RestoreScriptTests.cs`.

**Next step**
- Rotate the server's root password (shared in chat during this work).
- Doc 15 has no open items left; the remaining roadmap is doc 12's later phases (design system, app
  detail redesign, Compose, PR previews, in-browser DB client).

---

## 2026-07-29 — Backup → restore round trip: three bugs, then a real recovery

Tested the last unverified Phase E claim on the server: a real managed PostgreSQL, a canary row, a
backup, then destroy the data and restore it.

**Bug 1 (pre-existing, CRITICAL) — the staging volume was two different volumes.**
The backup ran `tar` in a helper container that mounts the staging volume **by name**
(`harbora_backups`), while the panel reads it through a Compose mount. Compose prefixes volume names
with the project directory, so the panel had `deploy_harbora_backups` and Docker silently
auto-created a *separate* `harbora_backups` for the helper. tar exited 0, the 6.6 MB archive landed
in a volume the panel can never read, and the backup failed with "Could not find file".

So **volume and database backups have never worked**. Worse, restore has the same mismatch: it runs
`rm -rf /data/* && tar xzf …` in that helper — it would have wiped the target volume and then failed
to find the archive. Fixed by giving the volume an explicit `name:` so Compose doesn't prefix it,
plus a guard that checks the archive is visible after tar and names the volume mismatch instead of
failing later with a bare "file not found".

**Bug 2 (mine, from Phase E) — archives were encrypted with an unreproducible key.**
`ArchiveKey()` was `SHA256(protector.Protect("harbora-archive-key"))`. `Protect` uses a **fresh nonce
per call**, so the "derived" key was different every time: the archive was sealed with a key nothing
could ever reproduce. Verification failed with *"the computed authentication tag did not match"*.

The unit tests missed it because the test double returned its input unchanged — deterministic where
the real thing is not. Fixed with a proper `ISecretProtector.DeriveKey(purpose)` (HKDF-SHA256 over
the master key), and the test double is now **randomised like the real protector** so this class of
bug can't hide again. `KeyDerivationTests` pins the determinism contract against the real
implementation and states plainly that `Protect` is *not* deterministic.

**Bug 3 (minor) — Jalali dates in artifact filenames.**
The pre-restore snapshot came out as `pre-restore-…-14050507-184916.tgz`. Restore runs inside a web
request, where the culture is Persian, so the ambient calendar leaked into a machine-facing filename
— while backups from the background job used Gregorian. Same directory, two calendars, names that no
longer sort. Filenames now use `InvariantCulture`; `ArtifactNamingTests` demonstrates the hazard is
real and that every culture now yields the same name.

**Round trip, verified end to end**
```
before backup      : ORIGINAL-DATA
backup             : Completed
artifact           : …tgz.enc   (magic "HRBENC")
plaintext leak     : 0 occurrences of the canary in the archive
verify (dry run)   : "Backup verified — restorable (4 checks passed)"
data destroyed     : CORRUPTED
restore            : ORIGINAL-DATA          ← the actual recovery
artifact corrupted : restore REFUSED — "does not match its recorded checksum … your current data
                     has NOT been touched"
data after refusal : ORIGINAL-DATA          ← the gate protected live data
```
The pre-restore safety snapshot was also produced, confirming that part of Phase E works too.

Every Phase E claim — encryption at rest, dry-run verification, the checksum gate, the pre-restore
snapshot, and an actual restore — is now confirmed against real Docker and a real database rather
than a fake engine.

**Checks:** `dotnet build` → 0 warnings / 0 errors · `dotnet test` → **303/303**. Test services,
containers, volumes, backup rows and artifacts removed; staging volume empty; panel 200,
`harbora doctor` clean.

**Uncommitted:** `SecurityAbstractions.cs`, `AesGcmSecretProtector.cs`, `BackupEngine.cs`,
`AuditController.cs`, `docker-compose.yml`, and three test files.

**Next step**
- Harden the restore shell command (extract-then-swap) — the last item on the Phase E list.
- Note for existing installs: any archive written before this fix cannot be decrypted. Since volume
  backups never completed successfully, there is nothing real to lose.

---

## 2026-07-29 — Git deploys were broken since day one; cutover + rollback verified live

Tested a real Git deployment on the server (`heroku/node-js-getting-started` — Node, **no
Dockerfile**, so Harbora's own buildpack detection had to do the work). It failed:

> No Dockerfile found and the stack couldn't be auto-detected.

But the clone succeeded and `package.json` was plainly there on disk.

**Root cause — pre-existing, not from this overhaul.** `LibGit2GitService` returned the checkout path
as `Path.GetDirectoryName(Repository.Clone(...))`. `Repository.Clone` returns the path of the **.git
directory with a trailing separator**, and `GetDirectoryName` only strips that empty trailing segment
— so it yielded `….../.git`. The pipeline then looked for a Dockerfile, a `package.json`, a `go.mod`
inside the metadata folder and of course found nothing.

**Every Git-sourced deployment has always failed** — with a Dockerfile or without one. It survived
because no test could see it: the bug only exists once a real clone has happened, and until now there
was no Docker host to run one on. Fixed by asking libgit2 for `repo.Info.WorkingDirectory` instead of
deriving a path by string surgery.

`GitCheckoutTests` clones a genuine local repository (real libgit2, no network) and asserts the
checkout path is the working tree, that source files are visible there, and that `Buildpacks.Detect`
recognises the stack from it. Restoring the old line fails 3 of the 4 tests.

**After the fix — full Git deploy, live:** buildpack detected Node → generated Dockerfile built
(`ENV PORT=3000`) → image `harbora/gitdemo-…:build-1` → container `harbora-gitdemo-…-1` → HTTP health
check passed → proxy wired → **app served at its own subdomain over HTTPS with real content**.

**Zero-downtime cutover and artifact rollback, verified in production for the first time:**
- Deploy #2 → live container `…-2` on `build-2`. Versioned naming works against real Docker.
- Rollback to #1 → confirm screen showed the target and "no rebuild"; deployment #3 succeeded on
  image **`build-1`**, and its logs contain **zero** `Successfully built` lines — the artifact was
  re-released, not rebuilt (ADR-006, Phase C).
- Deployment #2 moved to status **7 = RolledBack** — the Phase B fix that marks the displaced
  deployment behaves as designed outside of tests.
- The app answered 200 throughout.

Every claim Phases B and C made about cutover and rollback is now confirmed against real Docker
rather than a fake engine.

**Checks:** `dotnet build` → 0 warnings / 0 errors · `dotnet test` → **291/291**.

**Uncommitted:** `src/Harbora.Infrastructure/Git/LibGit2GitService.cs` + `tests/…/GitCheckoutTests.cs`.

**Next step**
- Backup → restore round trip (the last unverified Phase E claim).
- Harden the restore shell command (extract-then-swap).

---

## 2026-07-29 — Clean `update` path verified on the live server

Re-ran the real command users run (`install.sh update`) after the fixes were pushed, to prove the
recovery wasn't only a hand-patched server.

**Result:** server moved to `b047639` from git alone, panel `healthy`, restarts 0, `/` 200,
`/account/login` 200, `/nope` 404 themed, SSL certificate valid. No manual steps.

**One more bug, found by watching that run.** The installer reported
*"Traefik returns 404 for the panel — it didn't pick up the panel's labels"* on a completely healthy
install. `wait_panel` waited for the container to be `running` — which happens in about a second —
then slept 5s, while migrations and seeding take closer to a minute. `verify_install` then probed
once and failed. For a user who has just been through an outage, a false "your install is broken"
message immediately after a good update is close to the worst possible output.

Fixed both ends:
- `wait_panel` now waits for the container to report **healthy** (the healthcheck added earlier),
  falling back to running-plus-grace for images built before it. On failure it prints the panel's
  last 15 log lines and points at `harbora doctor` instead of a bare "check the logs".
- `verify_install` retries the panel route for up to a minute before declaring failure.

Second run output: `✓ Panel is ready` → `✓ Panel route via Traefik: OK` → `✓ Valid SSL certificate issued`.

**Data Protection fix confirmed in the field.** The keyring file present before the rebuild
(`key-a1fca5ca-…`) was still there afterwards — sessions now survive an update, which was the whole
point of moving the keyring onto a volume.

**Still uncommitted:** `deploy/install.sh` (the readiness fix above). Everything else is on master.

---

## 2026-07-29 — Live server: outage diagnosed and fixed, first real end-to-end deploy, landing page

Worked directly on the production host (91.99.205.231, Ubuntu 22.04). The panel was returning **502**.

**Five bugs found — four of them mine, introduced by this overhaul.**

1. **CRITICAL — panel could not start.** `PendingModelChangesWarning`: I hand-edited the
   `DeploymentWorkspaceScope` migration to add an index while *also* adding `HasIndex` to
   `OnModelCreating`, so the model and the snapshot disagreed. `MigrateAsync` refuses to run in that
   state, so the app died on boot, before serving anything. The database was still two migrations
   behind. Fixed by regenerating the migration (EF now emits the index itself) and re-applying the
   backfill SQL. **My earlier diagnosis — a missing master key — was wrong for this server;** its
   `.env` had one all along.
2. **HIGH — a dead panel looked healthy.** The process didn't exit on that fatal exception: it sat
   at 99% CPU for 39 minutes while Docker still reported `running`, `restarts=0`. The restart policy
   never fired. Now startup failures log and `return 1`, and the panel has a healthcheck.
3. **HIGH — every user was signed out by every update.** Data Protection keys lived in the
   container's filesystem, so rebuilding the image destroyed the keyring: cookies invalid,
   antiforgery failures mid-session. Now persisted to a `harbora_keys` volume with a stable
   application name.
4. **CRITICAL — login could not resolve a user's workspace.** The global query filter I added in the
   previous session also applied to `WorkspaceMember` — but the query that *decides* the caller's
   workspace runs before they have one, so it matched nothing. Every user signed in scoped to
   `Guid.Empty`: blank dashboard, and any app they created was stamped `Guid.Empty` and could never
   deploy. This is what made the E2E test fail with "Sequence contains no elements". Same trap fixed
   in `TokenAuthenticationHandler` (API/CLI auth) and three `TenantsController` admin queries.
5. **MEDIUM — logs were unusable.** EF logged every SQL statement at Information and the job worker
   polls on a timer: **1028 lines in 7 minutes**, burying real errors. EF command logging set to
   Warning → 40 lines. (First attempt at this broke startup: a `_comment` key inside `LogLevel` is
   parsed as a log level. Caught on the server, fixed.)

**First real end-to-end deployment.** Every phase so far recorded "blocked without a Docker host".
With one available, the full path was exercised: sign in → create app → deploy → verify. Result:
`Succeeded`, container `harbora-e2e-nginx-…-1` running `nginx:alpine`, job row `Succeeded` after one
attempt. The versioned-container naming (Phase B) and durable job queue (Phase D) are confirmed
working against real Docker, not a fake.

**Guard added.** `MigrationConsistencyTests` compares the EF model against the migration snapshot at
test time — the same check the runtime does, but before it can reach production. Verified by
re-introducing the exact mistake (an index in `OnModelCreating` with no migration): the test fails.

**Public landing page.** The root URL served a login form to anonymous visitors, which says nothing
about the product. `/` now renders a marketing page for signed-out visitors and the dashboard for
signed-in users, on its own layout: hero with a live-looking deploy terminal, feature grid, three-step
flow, plans **read from the database** so the page reflects what this installation actually offers,
FAQ and CTA. Bilingual and RTL-aware like the rest of the panel.

**Verified on the live server**
- `harbora doctor` → no problems; panel `healthy`, restarts 0.
- `/` → 200 (landing), `/account/login` → 200, `/nope` → **404 with the themed page**.
- Login verified end to end with a fresh password set via `harbora reset-password`.
- Both pending migrations applied; keyring file present in the volume.
- `dotnet build` → 0 warnings / 0 errors · `dotnet test` → **287/287**.

**Honest note**
The server currently runs these fixes applied directly to its checkout, so its working tree is dirty
and one migration file differs from `origin/master`. The same changes are staged locally and must be
committed and pushed, or the next `install.sh update` (`git reset --hard`) will revert the server to
the broken state.

**Next step**
- Commit + push these fixes, then re-run `update` on the server to confirm the clean path.
- Rotate the server's root password — it was shared in chat.
- Still open from Phase E: harden the restore shell command (extract-then-swap).

---

## 2026-07-29 — Lockout recovery (`harbora` command), upgrade repair, themed error pages

**Reported:** after running `update`, the panel would no longer come up at all.

**Root cause — the upgrade path, not the domain or the password.**
`install.sh`'s `write_env()` keeps an existing `.env` untouched, and `cmd_update` never touched it
either. A `.env` written by an installer older than PR #1 has **no `HARBORA_MASTER_KEY`** — and PR #1
made the panel *fail closed* without one (correctly: an unset key makes every stored secret trivially
decryptable). So the update was working as designed and the platform was refusing to boot for a good
reason it had no way to communicate. Nothing in the product could tell the operator that.

**Fixes**
1. **Upgrade repair.** `backfill_env`/`repair_env` add any *missing* key to an existing `.env`
   (existing values never overwritten). `cmd_update` runs it before starting containers.
2. **`harbora` server command** (`deploy/harbora`, installed to `/usr/local/bin` on install *and*
   update). Split deliberately by what still works when things are broken:
   - Host-side — `doctor`, `env`, `set-domain`, `fix-key`, `status`, `logs`, `restart` — pure shell,
     so they work when the panel container will not start at all. `doctor` checks the master key,
     domains, DB password, container states and ports, prints the panel's last 20 log lines when it
     is down, and names the fix for each problem.
   - Database-side — `info`, `users`, `reset-password`, `make-owner`, `unlock` — run through
     `docker compose run --rm panel admin …`. The image's entrypoint is `dotnet Harbora.Web.dll`, so
     these start a one-off container that never starts a web server.
3. **`AdminCommands`** in the Web host, dispatched from `Program.cs` **before** any service
   registration. This is the key design point: it never calls `AddHarboraInfrastructure`, because the
   situations it exists for are exactly the ones where that throws. A recovery tool that needs a
   healthy app is useless precisely when it is needed.
4. **Themed error pages.** `HomeController.Error()` rendered `Views/Home/Error.cshtml` — **which did
   not exist**, so handling an error raised a second error. And nothing handled 404 at all: a mistyped
   URL gave a blank page with a bare status code. Added the view (site shell, bilingual, per-status
   copy for 404/403/401/429/503/5xx, request id, and a pointer to `harbora doctor` on 5xx) plus
   `UseStatusCodePagesWithReExecute`, which keeps the real status code on the response.
5. **README** — new "🆘 `harbora` — server administration & recovery" section, the lockout row added
   to Troubleshooting, and the update section now explains the backfill and why it exists.

**Files changed**
- New: `deploy/harbora`, `Web/Infrastructure/AdminCommands.cs`, `Web/Infrastructure/AdminDiagnostics.cs`,
  `Web/Views/Home/Error.cshtml`, `tests/…/AdminDiagnosticsTests.cs`, `tests/…/ErrorPageTests.cs`.
- Edited: `install.sh`, `docker-compose.yml` (panel now sees `PANEL_DOMAIN`/`ROOT_DOMAIN` so
  `harbora info` can report them), `Program.cs`, `HomeController.cs`, `ErrorViewModel.cs`, `README.md`.

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **283/283 passed** (was 259).
- `bash -n` on both shell scripts.
- Ran the real command: `dotnet Harbora.Web.dll admin help` and `admin info` — the latter correctly
  reported the insecure dev key, redacted the DB password and exited non-zero on an unreachable
  database.
- Deleted `Error.cshtml` and confirmed `The_error_view_exists_on_disk` fails — the guard actually
  catches the original defect.

**Honest notes**
- The error pages were **not** verified in a running browser: booting the app needs a reachable
  Postgres and there is no Docker or usable local DB in this environment. Covered instead by
  controller-level tests (status code preserved, correct view name and model) plus the on-disk view
  guard. A real HTTP check is still worth doing on the server.
- `AdminDiagnostics` (master-key description, connection-string redaction) is unit-tested because that
  output is what a locked-out operator pastes into a bug report; a leaked DB password there would be a
  new problem created by the recovery tool.
- `harbora fix-key` refuses to replace a working key without typing `REPLACE`, since replacing one
  makes every stored secret permanently unreadable.

**Next step**
- On the affected server: `harbora doctor`, then `harbora fix-key` if it reports the missing key.
- Still outstanding from Phase E: harden the restore shell command (extract-then-swap).

---

## 2026-07-28 — Global query filters: workspace scoping (closes P13)

**What was done**
- New `IWorkspaceScope` decides whether the current unit of work belongs to one tenant or spans all
  of them. `HttpWorkspaceScope` keys that on **request vs. system**, not authenticated vs. anonymous:
  no `HttpContext` → background work (deploy pipeline, job worker, schedulers, seeding) runs
  unscoped; a request with no workspace claim scopes to `Guid.Empty` and therefore matches nothing.
  Deny by default — a request must never fall back to seeing everything.
- `HarboraDbContext` applies global query filters to every tenant-owned entity: `App`, `Route`,
  `ManagedService`, `Backup`, `BackupDestination`, `BackupSchedule`, `Alert`, `GitProvider`,
  `WorkspaceMember`, `UsageRecord`, `Deployment`. The existing single-argument constructor still
  builds a system-scoped context, so every background call site keeps working unchanged.
- The two places that legitimately span tenants now say so explicitly with `IgnoreQueryFilters()`:
  the tenants admin page, and the "is this server still in use?" check before removing a node —
  which must be blocked by *any* tenant's workload, not just the admin's own.

**A design decision the tests forced**
Filtering `Deployment` through its `App` navigation looked natural and was wrong. Because `AppId` is
non-nullable, EF treats the relationship as required and emits an **INNER JOIN** — so a deployment
whose app row is missing disappears from *every* query, including the crash reconciler whose entire
purpose is to find stranded deployments. `IgnoreWorkspaceFilter ||` cannot rescue rows the join has
already dropped. Found because seven existing tests started failing.

Fixed by denormalising `WorkspaceId` onto `Deployment` (migration `DeploymentWorkspaceScope`, with a
backfill from the owning app — without it every existing deployment would keep the empty default and
vanish from its own tenant's history on upgrade) and filtering on a direct comparison. No join, no
hazard, and an index to match.

`EnvironmentVariable`, `Volume`, `DomainName` and `DeploymentLog` are deliberately left unfiltered:
they are only ever reached through a parent that *is* filtered, so a navigation filter would add a
join to every read — and the same inner-join hazard — for no extra protection. Stated explicitly in
a test rather than left implied.

**Files changed**
- New: `Application/Abstractions/IWorkspaceScope.cs`, `Web/Infrastructure/HttpWorkspaceScope.cs`,
  `Migrations/…_DeploymentWorkspaceScope.cs`, `tests/…/WorkspaceQueryFilterTests.cs`.
- Edited: `HarboraDbContext.cs`, `Deployment.cs`, `DeploymentEngine.cs`, `Program.cs`,
  `TenantsController.cs`, `ServersController.cs`.

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **259/259 passed** (was 244).
- **Mutation-tested:**
  1. treat "no workspace" as unscoped (the anonymous-sees-everything bug) → 1 test fails ✅
  2. drop the `App` filter → 4 tests fail ✅
  3. forget to stamp `WorkspaceId` on a new deployment → **survived** ❌. This one matters: the
     deployment would still build and release (background work is unscoped) but never appear in the
     UI of the tenant who triggered it — it would look like the deploy silently vanished. Added
     `A_newly_queued_deployment_is_visible_to_the_tenant_that_triggered_it`; now caught ✅.

**Honest notes**
- The filters are defence in depth, not a fix for a live leak: every controller was already scoping
  its queries by hand. What changes is the failure mode of a *future* mistake — "missing" instead of
  "another tenant's data".
- Denormalised `WorkspaceId` can drift if an app is ever moved between workspaces. Nothing supports
  that today; if it is added, the move must update its deployments too.

**Next step**
- Harden the restore shell command (extract-then-swap) — still outstanding from Phase E.
- Still blocked without a Docker host: real backup→restore round trip and end-to-end deploy.

---

## 2026-07-28 — Phase E: data-safety hardening + audit trail UI

**The bug this phase exists for**
`Backup.Checksum` has been in the schema since the first migration and is written on every backup —
and **nothing ever read it**. Meanwhile the volume restore path runs
`rm -rf /data/* && tar xzf …` as a single shell command: the wipe happens *first*. Restoring a
corrupt or truncated archive therefore destroyed the live data and had nothing to put back. That is
the worst failure mode in the product, and it was reachable through a normal, confirmed user action.

**What was done**
- **Integrity gate before restore.** The stored artifact's checksum is recomputed and compared with
  the one recorded at backup time; a mismatch aborts with an explicit "your current data has NOT
  been touched". Backups predating checksums still restore (refusing would strand the oldest
  backups) but log a warning.
- **Archive probe before restore.** A second, distinct check — a checksum only proves the bytes are
  the ones we stored, not that they form a usable archive. A backup that was garbage *when written*
  has a perfectly valid checksum. Found by a test that failed for exactly this reason.
- **Dry-run verification** — `IBackupEngine.VerifyAsync` fetches, checksums, decrypts and reads the
  archive without touching live data, returning per-check results. Wired to a "Verify" button.
  A backup nobody has ever verified is a promise, not a safety net.
- **Archive encryption at rest** — new `ArchiveCipher`: streaming, chunked AES-GCM. Chunked
  deliberately (database dumps don't fit in memory); each chunk carries its own nonce and tag, and
  the chunk index is bound into the associated data so chunks can't be reordered, duplicated or
  dropped. Key derived from the platform master key, so there is no second secret to lose. Format
  is detected per file, so pre-encryption artifacts keep restoring.
- **Pre-restore snapshot** — the current volume is tarred aside before it is overwritten, so even a
  verified-but-wrong restore is recoverable. Best-effort: it never blocks a confirmed restore.
- **Audit log UI + CSV export** (owed from P13). Entries had been written since the overhaul but
  nothing could read them. Admin-only (the trail spans workspaces and holds actor emails and IPs),
  filterable by action/actor, paged, with export capped at 50k rows.
- **Cross-tenant isolation tests** (owed from P13) — apps, backups, deployments, routes, proxy
  config, container retirement and image retention.

**CSV formula injection**
Audit fields carry attacker-influenced text (actor emails, target ids) and the export is opened in
Excel by an administrator investigating an incident. `CsvWriter` prefixes values starting with
`=`, `+`, `-`, `@` with an apostrophe so they are read as text rather than executed.

**Files changed**
- New: `Backups/ArchiveCipher.cs`, `Web/Controllers/AuditController.cs`,
  `Web/Infrastructure/CsvWriter.cs`, `Web/Views/Audit/Index.cshtml`, and four test files
  (`ArchiveCipherTests`, `BackupSafetyTests`, `AuditExportTests`, `CrossTenantIsolationTests`)
  plus `Fakes/BackupHarness.cs`.
- Edited: `PlatformAbstractions.cs` (VerifyAsync + result types), `BackupEngine.cs`,
  `BackupOptions.cs`, `BackupsController.cs`, `_Layout.cshtml`, `ViewModels.cs`,
  `appsettings.json`, `Fakes/FakeDockerEngine.cs` (one-off commands now recorded).

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **244/244 passed** (was 197).
- **Mutation-tested** — this phase can destroy data, so a weak test here is the most dangerous
  kind:
  1. remove the checksum gate before restore → 3 tests fail ✅
  2. remove the archive probe before the wipe → 1 test fails ✅
  3. unbind the chunk index from the AES-GCM tag (chunks become reorderable) → 1 test fails ✅

**Honest notes**
- One test I wrote (`Verification_reads_the_archive_not_just_its_checksum`) did not initially test
  what its name claimed — it asserted on the wrong backup. Rewritten around a genuinely
  intact-but-unusable artifact, which is what then exposed the missing probe on the restore path.
- The restore path still shells out `rm -rf /data/* && tar xzf …` as one command. The two gates in
  front of it make reaching that command with a bad archive very unlikely, but extracting to a
  temporary directory and swapping would remove the window entirely. Recorded as follow-up.
- Centralized workspace scoping (a query-filter refactor) is **not** done — the cross-tenant tests
  pin the predicates the controllers use today, but nothing yet prevents a future controller from
  forgetting one. That is the remaining P13 item.

**Next step**
- Global query filters for workspace scoping, so isolation is structural rather than per-query.
- Harden the restore shell command (extract-then-swap).
- Still blocked without a Docker host: an actual backup→restore round trip against a live volume.
  The verification path is exercised end-to-end against real archives on disk, but the tar/untar
  legs run through the fake engine.

---

## 2026-07-28 — Phase D: durable job queue (completes P3)

**What was done**
- Replaced the in-memory `Channel` queue with a persisted **`Job` table**. The old queue held
  `Func<IServiceProvider, CancellationToken, Task>` delegates — a delegate cannot be written to a
  database, which is why "crash-safe deploys" previously meant *the reconciler re-queued work into
  another equally volatile channel*. Persisting a **description** of the work (kind + target id)
  instead makes the row itself the queue.
- New: `Job`/`JobKind`/`JobStatus` (Domain), `IJobQueue` + `IJobCancellationRegistry`
  (Application), `DatabaseJobQueue`, `JobWorker`, `JobDispatcher`, `JobReconciler`,
  `JobCancellationRegistry`, `JobSignal` (Infrastructure). EF migration `DurableJobQueue`.
- Deleted `ChannelBackgroundJobQueue`, `BackgroundJobWorker` and `IBackgroundJobQueue`. All three
  producers migrated: deployments, backups, managed-service provisioning.
- **Real cancellation.** `IJobCancellationRegistry` maps running job → its `CancellationTokenSource`,
  so `DeploymentEngine.CancelAsync` now stops the work as well as updating the record. Previously
  cancelling a Building deployment only rewrote a column while the build carried on.
- `JobSignal` wakes the worker instantly on an in-process enqueue, so durability costs no latency;
  a 5s poll is the backstop that also catches rows written by the reconciler.
- `JobReconciler` runs **before** `DeploymentReconciler` and settles jobs left `Running` by a crash.
  Deliberately does not retry: deployments/backups/provisioning have side effects that a blind
  re-run could compound.

**A duplicate-deploy bug this introduced, and fixed**
`DeploymentReconciler` used to re-queue every `Queued` deployment on startup. With a durable queue
the job row survives the restart too, so that would deploy the same thing **twice**. It now
re-queues only when no live job covers the deployment — heal the gap, don't duplicate the work.
Covered by `A_queued_deployment_that_still_has_its_job_is_not_queued_twice`.

**Semantics worth stating**
- Host shutdown returns a claimed job to `Pending` with its claim released — the work never
  happened, so it must resume, not be recorded as cancelled or failed.
- A user cancel settles a `Pending` job outright; if the worker claimed it in the meantime, the
  concurrency stamp turns that into a caught conflict and the running path interrupts it instead.
- `ClaimStamp` is an EF concurrency token, so two workers racing for one job means a lost update
  for one of them rather than a double execution. Enforced on Postgres; the InMemory provider used
  in tests does not check it, so that guarantee is not test-covered — noted honestly.

**Files changed**
- New: `Domain/Jobs/Job.cs`, `Application/Abstractions/IJobQueue.cs`, four files under
  `Infrastructure/Jobs/`, `Migrations/…_DurableJobQueue.cs`, `tests/…/DurableJobQueueTests.cs`,
  `tests/…/Fakes/JobHarness.cs`.
- Deleted: `Jobs/ChannelBackgroundJobQueue.cs`, `Jobs/BackgroundJobWorker.cs`.
- Edited: `PlatformAbstractions.cs`, `HarboraDbContext.cs`, `DependencyInjection.cs`,
  `DeploymentEngine.cs`, `DeploymentReconciler.cs`, `BackupEngine.cs`, `ManagedServiceEngine.cs`,
  and two test files.

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **197/197 passed** (was 179).
- **Mutation-tested:**
  1. ignore `CancelRequested` on a pending job → **survived** ❌. The queue settles a pending cancel
     itself, so the worker's guard only matters after *cancel-then-restart* — a path I hadn't
     tested. Added that test; mutation now caught ✅. Also hardened the cancel/claim race the
     investigation exposed.
  2. treat host shutdown as cancellation (losing the work) → 1 test fails ✅
  3. remove the cancellation registration → **rejected by the compiler** (unused parameter is an
     error here); the compiling variant — registering a decoy token — fails 1 test ✅. That run
     also showed the blocking stub could hang the suite instead of failing, so its wait is now
     bounded: the test fails in ~11s rather than never.

**Next step**
- Phase E (data-safety hardening): backup→restore round-trip verification, archive encryption,
  dry-run restore; then the audit-log UI/export, centralized workspace scoping and cross-tenant
  tests still owed from P13.
- Known limitation: a cancel for a job running on **another** instance persists the flag but cannot
  interrupt it — the registry is process-local. Single-instance today; worth revisiting if the
  platform ever runs more than one panel.

---

## 2026-07-28 — Phase C: image retention + resilient rollback

**What was done**
- **Image operations added to the runtime seam.** `IDockerEngine` had no way to list, check or
  delete images at all — retention was impossible to implement, and "instant rollback" could not
  even be verified. Added `ListImagesAsync` / `ImageExistsAsync` / `RemoveImageAsync`, implemented
  across all four engines: `DockerEngine` (Docker.DotNet), `RemoteDockerEngine` (HTTP), the
  `Harbora.Agent` endpoints (`GET /agent/images`, `GET /agent/images/exists`,
  `POST /agent/images/remove`), and `FakeDockerEngine`.
- **Retention policy** as a pure function, `DeploymentPlanning.ImagesToPrune`. Keeps the active
  image plus the newest N *rollback-eligible* (Succeeded/RolledBack) images; prunes the rest after
  a successful cutover. Configurable via `Runtime:ImageRetentionCount` (default 5; 0 disables).
  Closes **R1** from doc 15 — previously every deploy leaked an image forever, and artifact
  rollback only worked because nothing cleaned up.
- **Rollback pre-flight.** New `IRollbackPlanner` checks up front that the target exists, belongs to
  the app, succeeded, has a retained image, and that the image is *still on the node*. The pipeline
  also re-checks before starting anything, so a pruned artifact fails cleanly instead of part-way
  through a deploy.
- **Rollback confirmation screen** (`Apps/ConfirmRollback`): shows the live version vs. the target
  with commit sha/message/author, deploy time and the exact image being re-released — or explains
  why the rollback is blocked. The Details page now links here instead of posting straight through.
  Closes P4's owed "pre-confirm rollback diff". The POST re-runs the plan, since retention could
  prune between rendering and submitting.

**Safety properties deliberately encoded**
- User-supplied images (`nginx:1.27`, template images) are **never** prunable — only tags matching
  `{prefix}/{slug}:build-`. Deleting a shared base image would break unrelated apps.
- Failed deployments do not consume the retention window; otherwise a burst of broken builds would
  silently push every working version out of rollback range.
- Retention dedupes by **image tag, not deployment** — a rollback re-releases an existing tag, so
  counting deployments would spend the window on one artifact.
- Pruning runs only after the deployment is recorded `Succeeded`, and any failure is swallowed:
  housekeeping must never turn a live, working deployment into a failure.
- `RemoveImageAsync` uses `Force = false`, so an image a container still references survives even if
  our bookkeeping thinks otherwise.

**Files changed**
- New: `Application/Abstractions/IRollbackPlanner.cs`, `Infrastructure/Deployments/RollbackPlanner.cs`,
  `Web/Views/Apps/ConfirmRollback.cshtml`, `tests/…/ImageRetentionTests.cs`,
  `tests/…/RollbackResilienceTests.cs`.
- Edited: `IDockerEngine.cs`, `DockerEngine.cs`, `RemoteDockerEngine.cs`, `Agent/Program.cs`,
  `DeploymentPlanning.cs`, `DeploymentPipeline.cs`, `HarboraRuntimeOptions.cs`,
  `DependencyInjection.cs`, `AppsController.cs`, `ViewModels.cs`, `Apps/Details.cshtml`,
  `appsettings.json`, and the test fakes.

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **179/179 passed** (was 154).
- **Mutation-tested** — retention deletes data, so a weak test here is actively dangerous:
  1. drop active-image protection → 1 test fails ✅
  2. drop the build-prefix guard (would delete `nginx:1.27`) → 2 tests fail ✅
  3. let failed deployments consume the window → 1 test fails ✅
  4. dedupe by deployment instead of by tag → **survived** ❌ → the test used a case that was
     immune (rollback to a non-adjacent version). Rewrote it around the common case — rolling back
     to the immediately previous version, where the two newest deployments share a tag — and the
     mutation is now caught ✅.

**Next step**
- Phase D (durable job queue) — completes P3. The in-memory `Channel` still means a `Queued`
  deployment only survives a restart because the reconciler re-queues it into another volatile
  channel; `CancelAsync` still cannot stop work already in progress.
- Note for a future phase: retention is per-app and runs on deploy, so an app that is never
  deployed again keeps its images indefinitely. A platform-wide sweep is the natural follow-up.

---

## 2026-07-28 — Phase B: pipeline integration harness (fake Docker engine)

**What was done**
- Built `FakeDockerEngine` — an in-memory container runtime that **records every call in order** and
  simulates a small container world (containers exist, have state, can be removed, can refuse
  removal). Ordering is the point: "zero-downtime" is a claim about *sequence*, so a fake returning
  canned values could never falsify it.
- Added `PipelineHarness`, which wires a **real** `DeploymentPipeline` (real state machine, real EF
  context, real cutover logic) over fake Docker/git/proxy/HTTP, plus recording fakes for the log
  stream, proxy and notifications, and a stub `IHttpClientFactory` for the health probe.
- **20 end-to-end tests** over `DeploymentPipeline.ExecuteAsync`, which previously had **zero**
  behavioural coverage: start-before-retire ordering, traffic switches only after health passes,
  failed deploy removes only its own container, container that never reaches `running`, failed
  build never starts a container, rollback re-releases the artifact without building or checking
  out source, rollback marks the deployment it displaced, imageless rollback target, remote-node
  host-port uniqueness, local vs remote proxy targets, unremovable old container, health probe
  targets the same address the proxy will use.

**Bug found and fixed — DbContext race on build logs**
The harness immediately failed with *"Collection was modified; enumeration operation may not
execute"*. Cause: `new Progress<string>(l => _ = Log(...))` — `IProgress` dispatches through the
thread pool (ASP.NET Core has no `SynchronizationContext`), so build/pull log lines were calling
`db.DeploymentLogs.Add(...)` **on a thread-pool thread while the pipeline thread was inside
`SaveChangesAsync`**. `DbContext` is not thread-safe. In production this hits every build that
emits log lines — the more verbose the build, the likelier the corruption.
Fix: engine-thread lines enqueue to a `ConcurrentQueue` and are drained onto the DbContext by the
pipeline thread; live SignalR streaming still happens immediately and never touches the context.

**Health-gate timings made configurable**
`Task.Delay(2s)` was hardcoded (up to 16s to reach `running`, then 20s of probing), which made the
suite unusable and gave operators no way to accommodate a slow-booting app. Now
`HealthPollIntervalSeconds` / `HealthRunningAttempts` / `HealthHttpAttempts` /
`HealthHttpTimeoutSeconds` on `HarboraRuntimeOptions`, defaulting to exactly the previous
behaviour. This also closes the "no probe fields" gap doc 12 left owed from P4.

**Files changed**
- New: `tests/Harbora.Tests/Fakes/{FakeDockerEngine,PipelineFakes,PipelineHarness}.cs`,
  `tests/Harbora.Tests/DeploymentPipelineCutoverTests.cs`.
- Edited: `DeploymentPipeline.cs` (log threading + configurable timings),
  `HarboraRuntimeOptions.cs` (health-gate knobs).

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **154/154 passed** (was 134), suite still ~1s.
- **Mutation-tested the new tests** — a green ordering test that cannot fail is worthless:
  1. retire old containers *before* the health gate → 3 tests fail ✅
  2. wire the proxy *before* the health gate passes → 1 test fails ✅
  3. rollback rebuilds instead of re-releasing → 2 tests fail ✅
  Pipeline restored and re-verified green after each.

**Decisions**
- `ListContainersAsync` is deliberately **not** recorded by the fake: the health loop polls it
  repeatedly and it would drown the ordering assertions in noise.
- Cross-fake ordering (proxy vs docker) is asserted through resulting state plus `ApplyCount`,
  not a shared clock — a shared call log across unrelated fakes would couple them for little gain.

**Next step**
- Phase C (image retention + resilient rollback). Note the harness makes the retention work
  testable: "prune everything except the last k images and the active one" is exactly the kind of
  ordering/selection claim `FakeDockerEngine` can now verify.
- Still blocked without a Docker host: the real E2E run. These assertions are the precise
  specification to execute against once a host exists.

---

## 2026-07-28 — PR #1 merged; Phase A (post-merge review fixes)

**What was done**
- Merged PR #1 into `master` (`a18b217`, `--no-ff`, 12 atomic commits preserved). Activated CI by
  moving the workflow to `.github/workflows/ci.yml` and dropping the now-merged `overhaul` trigger.
- Wrote `docs/overhaul/15-phase-plan.md` — actual-vs-claimed state per doc-12 phase, plus a
  re-sequenced plan for the constraint doc 12 assumed away: **no Docker host is available**.
- **Phase A — four defects found in post-merge review:**
  1. **Forwarded headers were never configured** while the shipped topology puts the panel behind
     Traefik, so every request carried the proxy's IP. The per-IP rate limits added in this overhaul
     were therefore one platform-wide bucket (10 logins/min for *everyone*), and every audit row
     recorded the proxy. New `TrustedProxySetup` trusts one hop from configured proxy networks only
     (`Harbora:TrustedProxyNetworks`, default = Docker's private ranges); `UseForwardedHeaders()`
     runs before the rate limiter.
  2. **The single-active-deploy guard swallowed rollbacks.** A rollback requested while a deploy was
     in flight returned the forward deploy's id and redirected to it — indistinguishable from
     success, though nothing was queued. Coalescing now applies only when both requests share the
     same intent; a mismatch throws with a clear message (surfaced as `TempData["Error"]` in the UI,
     `409 Conflict` on the API, skip-and-continue for webhooks).
  3. **`Succeeded → RolledBack` was allowed by the state machine but never applied**, so history
     never showed which version a rollback abandoned. The pipeline now marks the displaced
     deployment after cutover; the decision is a pure `DeploymentPlanning.ShouldMarkRolledBack`.
  4. **`CancelAsync` bypassed the state machine** via `ExecuteUpdateAsync`. It now transitions
     through it, making an already-terminal deployment a no-op instead of a raw column write.

**Files changed**
- New: `Web/Infrastructure/TrustedProxySetup.cs`, `docs/overhaul/15-phase-plan.md`,
  `tests/Harbora.Tests/TrustedProxySetupTests.cs`.
- Edited: `Program.cs`, `appsettings.json`, `DeploymentEngine.cs`, `DeploymentPipeline.cs`,
  `DeploymentPlanning.cs`, `AppsController.cs`, `ApiV1Controller.cs`, `GitWebhookProcessor.cs`.
- Tests: +38 → **134 total**, incl. the real `ForwardedHeadersMiddleware` exercised end-to-end
  (trusted hop adopted, untrusted peer ignored, client-prepended entry not believed).

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → 134/134 passed.

**Decisions**
- `IPNetwork.TryParse` accepts a non-canonical base (`10.1.2.3/8`) and masks it to the prefix.
  Rejecting such entries would silently drop proxy trust and reintroduce defect 1, so they are
  accepted and documented as equivalent to the canonical form rather than treated as errors.
- Used `KnownIPNetworks` (not the obsolete `KnownNetworks`) to keep the build at 0 warnings.

**Next step**
- Phase B (`docs/overhaul/15-phase-plan.md`): a recording `FakeDockerEngine` and integration tests
  over `DeploymentPipeline.ExecuteAsync`, which today has **zero** behavioural coverage — the
  cutover ordering that this overhaul's headline claim rests on is currently unverified.

---

## 2026-07-23 — Action-level RBAC + Operator role (H2 / threat 2.12)

**What was done**
- Added the **Operator** role (enum value 4, appended) — day-2 ops only.
- Introduced a capability-based authorization model (deny-by-default): `Capabilities` (16 named
  action policies) + pure `RolePermissions` matrix (Domain) + `CapabilityRequirement` /
  `CapabilityAuthorizationHandler` (Web) evaluating the caller's role claim. Registered one policy
  per capability via `AddCapabilityAuthorization()` (replaced the bare `AddAuthorization()`).
- Applied `[Authorize(Policy = …)]` to **every privileged action** across all controllers **and**
  the token-authenticated API:
  - Apps: create / deploy+rollback / operate (restart·stop·start) / delete / env·domains.
  - Databases, Routes(save), Git(connect·import·oauth·rotate), Alerts, Backups(run/restore/manage),
    Servers(add/remove), Plans(create), Settings(platform), Tenants (whole controller).
  - API `POST /api/v1/apps/{slug}/deploy` → `apps.deploy` (same matrix as the UI).
- Role→capability matrix: Owner/Admin = all; Member (developer) = app lifecycle + databases/routes/
  git; Operator = operate + backups.run; Viewer = read-only.

**Files changed**
- New: `Domain/Authorization/Capabilities.cs`, `Domain/Authorization/RolePermissions.cs`,
  `Web/Infrastructure/CapabilityAuthorization.cs`.
- Edited: `Enums.cs` (Operator), `Program.cs` (policy registration), and 11 controllers.
- Tests: `RolePermissionsTests.cs` (full matrix) + `CapabilityAuthorizationHandlerTests.cs`
  (adapter, incl. missing/unknown role) → **96 tests total**. Test project now references
  `Harbora.Web` to test the handler directly.

**Tests / checks run**
- Build 0/0; `dotnet test` → **96 passed** (+10).
- **Live enforcement (real Postgres):** as Owner, `GET /apps/create` → 200. After switching the
  user's role to Viewer and re-logging in: `GET /apps/create` and `POST /servers/add` both →
  **302 → /account/denied** (denied). Role restored to Owner afterward.

**Decisions**
- Deny-by-default: unknown/missing role claim → denied (verified by test). Cookie users get a 302
  to `/account/denied`; API/token users get 403 — both driven by the same policy + matrix.
- `RolePermissions` lives in Domain (pure, framework-free) so the matrix is the single source of
  truth and is exhaustively unit-tested; the Web handler is a thin adapter.
- Converted the pre-existing `[Authorize(Roles="Owner,Admin")]` on Tenants to the capability policy
  for one consistent model.

**Next step**
- Push this to GitHub `overhaul` / PR #1. Remaining: Operator/role selection in the member-invite
  UI, resource-level "own apps" scoping for Member, audit UI/export.

---

## 2026-07-23 — Pushed to GitHub: branch `overhaul` + PR #1

**What was done**
- Pushed the entire overhaul branch (10 commits, original messages preserved) to
  `github.com/sadrazkh/Harbora` on branch **`overhaul`** via the GitHub integration
  (`create_branch` + per-commit `push_files`, replayed in order on top of `master@84603e0`).
- Opened **Pull Request #1** (`overhaul` → `master`) with a full summary of the phase:
  https://github.com/sadrazkh/Harbora/pull/1

**Verification**
- `origin/master` was still exactly the local baseline `84603e0` (no drift) before branching.
- After the push: fetched `origin/overhaul` and diffed against local — **only** the planned CI-file
  relocation differs (see below); all other 57 files byte-identical; all 10 commit messages intact.
- One transient GitHub 500 on commit 8 (ref not moved) — verified via `list_branches`, retried
  safely, succeeded.

**Decision / known limitation**
- The integration token lacks the GitHub `workflows` permission, so `.github/workflows/ci.yml`
  could not be pushed (403 on tree containing workflow files). The workflow was shipped at
  **`docs/overhaul/ci-workflow.yml`** with a relocation note. **Resolved at merge time:** the file
  was moved to `.github/workflows/ci.yml` (note dropped, stale `overhaul` branch trigger removed)
  in the merge commit for PR #1.
- Local branch was reset to `origin/overhaul` so local == remote lineage from here on.

**Next step**
- Review/merge PR #1; move the CI file; then continue with the roadmap phases (Docker-host E2E
  verification, per-action RBAC, monitoring depth, previews).

---

## 2026-07-23 — Audit logging for privileged actions (threat 2.13)

**What was done**
- Added `IAuditLogger` (Application) + `AuditLogger` (Infrastructure): append-only audit rows,
  actor/workspace default to the current user, request IP passed by the caller (no web coupling),
  best-effort (an audit failure never breaks the audited action).
- Wired it into the highest-value actions: **login success**, **login failure**, **app deploy**,
  **app rollback**, **app delete** — each records actor, target, IP, and metadata.

**Files changed**
- `SecurityAbstractions.cs` (`IAuditLogger`), `Auditing/AuditLogger.cs` (new), `DependencyInjection.cs`
  (register), `AccountController.cs` (login ±), `AppsController.cs` (deploy/rollback/delete).
- Added `tests/Harbora.Tests/AuditLoggerTests.cs` (+2) → **86 tests total**.

**Tests / checks run**
- Build 0/0; `dotnet test` → **86 passed**.
- Runtime (real Postgres): a wrong-password then correct-password login produced two audit rows —
  `user.login_failed` and `user.login` — each with the actor email and client IP (127.0.0.1).

**Result**
- SUCCESS. Security-relevant actions are now audited (the entity existed but was previously written
  only by the webhook path). Audit UI + CSV/webhook export remain a follow-up (R-AUD-1).

**Next step**
- Remaining items are broad refactors or Docker-dependent (per-action RBAC across all controllers,
  per-app/route monitoring, PR previews, in-browser DB client, multi-server port table, OpenAPI).
  These are documented in the roadmap; the critical/verifiable overhaul work is complete.

---

## 2026-07-23 — Staged deploy-progress UI + live reconciler verification

**What was done**
- Added a **staged deploy-progress bar** (`_DeployProgress` partial) to the deployment details
  page: Queued → Build → Deploy → Health → Live, server-rendered from the current status, with a
  clear failed-state message ("previous version is still serving — retry or roll back"). Matches
  docs/overhaul/08.
- **Live-verified the P3 crash reconciler against real PostgreSQL:** inserted a deployment in the
  `Building` state, restarted the app, and confirmed the reconciler transitioned it to `Failed`
  with *"Interrupted by a platform restart before completion. Please redeploy."* and set the app
  status to Failed — exactly the C2 behavior, now proven end-to-end (not only in the unit test).

**Files changed**
- Added `src/Harbora.Web/Views/Shared/_DeployProgress.cshtml`; included it in
  `Views/Deployments/Details.cshtml`.

**Tests / checks run**
- `dotnet build` (web) → 0/0 (Razor precompiles, partial valid).
- Runtime render check (real Postgres, seeded deployments): details page renders 200 for Building/
  Failed/Succeeded; Succeeded shows all five steps complete (5 ✓); Failed shows the ✕ + recovery
  message. Reconciler DB fingerprint confirmed.

**Result**
- SUCCESS. A signature UX gap from the spec is closed, and the crash-recovery fix is now verified
  live against PostgreSQL.

**Next step**
- Audit logging for privileged actions (login/deploy/rollback), then the deeper Docker-dependent
  and broad-refactor items (per-action RBAC, monitoring depth, previews).

---

## 2026-07-23 — Security & reliability hardening (H3 + threats 2.8 / 2.18)

**What was done**
- **Concurrency guard (H3):** `DeploymentEngine.QueueDeploymentAsync` now coalesces concurrent
  triggers (double-clicks, webhook storms) onto the existing in-flight deployment instead of racing
  a second build — at most one active deployment per app.
- **SSRF guard (threat 2.8):** new pure `UrlSafety.IsAllowedOutboundUrl` rejects non-http(s)
  schemes, localhost/metadata hostnames, and loopback/link-local/private/unique-local IP literals.
  Applied to the outbound Discord + generic webhook notification channels (blocked → logged, never
  sent; never breaks a deploy).
- **Rate limiting (threat 2.18):** per-IP fixed-window limiters — login `auth` (10/min) and inbound
  git `webhook` (60/min); 429 on exceed. Middleware added; policies applied via
  `[EnableRateLimiting]` on the login POST and the webhooks controller.

**Files changed**
- `DeploymentEngine.cs` (concurrency guard), `Security/UrlSafety.cs` (new),
  `Notifications/NotificationService.cs` (SSRF guard on webhook/Discord), `Program.cs` (rate
  limiter registration + middleware), `AccountController.cs` + `WebhooksController.cs`
  (`EnableRateLimiting`). Added `UrlSafetyTests.cs` (+11) and `DeploymentEngineConcurrencyTests.cs`
  (+2), plus others → **84 tests total**.

**Tests / checks run**
- Build 0/0; `dotnet test` → **84 passed**.
- Runtime: `/healthz` 200; login hammered 14× → first 10 = 200, then **429 429 429 429** (limiter
  works); app boots with the limiter active.

**Result**
- SUCCESS. Three targeted security/reliability gaps closed, all verifiable without Docker.

**Next step**
- Deeper items (per-action RBAC, audit coverage/export, per-app monitoring, previews, in-browser DB
  client) and the live Docker-host end-to-end run remain — larger and/or Docker-dependent.

---

## 2026-07-23 — Phase 7 (C3): Static-site + Template deploys + honest Compose gating

**What was done**
- Implemented **Static-site** deploys (git checkout → forced Nginx build) — previously threw
  `NotSupported`. Exposed as a source card in the create form; wired through the controller
  (validation, repo creation, deployability).
- Implemented **Template** deploys via a pure `TemplateResolver`: image-based templates deploy
  one-click (pull image), git-based templates build from the app's repo, managed-service and
  multi-service (`requires`) templates return an **honest, specific message** instead of a raw
  crash.
- **Docker Compose** now fails with a clear "not yet supported / planned" message (still gated, not
  selectable) instead of `NotSupportedException`.
- Refactored the git build path into a reusable `BuildFromGitAsync(forceStatic)` helper.
- **README** corrected: Compose is "planned, not shipped"; Static/Template status stated honestly.

**Files changed**
- `DeploymentPipeline.cs` (StaticSite/Template/Compose cases + BuildFromGitAsync),
  `TemplateResolver.cs` (new, pure), `Buildpacks.cs` (public `ForStaticSite`),
  `Apps/Create.cshtml` (Static card + multi-source panels), `AppsController.cs` (StaticSite),
  `README.md`. Added `tests/Harbora.Tests/TemplateResolverTests.cs` (+5).

**Tests / checks run**
- `dotnet build Harbora.slnx -c Release` → 0/0. `dotnet test` → **64 passed**.
- Runtime: `/apps/create` renders all three source cards (Git, Image, **Static site**); auth + form
  load verified (HTTP 200).

**Result**
- SUCCESS. C3 resolved honestly: advertised single-container sources now work or fail with a helpful
  message; Compose is truthfully marked as planned. No control implies an unimplemented capability.

**Decisions**
- Scoped Template to single-container (image/git); multi-service templates (WordPress+DB) return a
  clear "not one-click yet" message and remain a documented roadmap item rather than shipping a
  half-working multi-service orchestration I can't verify without Docker.

**Next step**
- Remaining backend hardening (webhook de-dup/rate-limit, RBAC per-action, audit) and the
  Docker-host end-to-end verification (P2 live step) are the natural continuations.

---

## 2026-07-23 — Phase 4: Zero-downtime cutover + artifact rollback (C4)

**What was done**
- **Zero-downtime cutover (ADR-007):** the new container now starts under a **versioned name**
  (`harbora-{slug}-{n}`) ALONGSIDE the currently-serving one; the old container is retired only
  AFTER the new one passes health checks and traffic has been switched. A failed deploy now leaves
  the previous version serving (was: old container removed before the new one even started →
  downtime + outage on failure).
- **True artifact rollback (ADR-006):** rollback now **re-releases the prior deployment's image**
  with no rebuild (instant + exact). Fixed a real correctness bug — the previous "rollback" ignored
  `RolledBackFromId` and rebuilt from current source, which could produce a *different* image.
- Remote nodes get a **per-deployment host port** so old+new can coexist during cutover.
- Container lookup for restart/stop/logs/delete is now **label-based** (was exact-name), matching
  the versioned naming.

**Files changed**
- Added `src/Harbora.Infrastructure/Deployments/DeploymentPlanning.cs` (pure helpers: versioned
  naming, retirement selection, per-deployment port, rollback-image resolution).
- `DeploymentPipeline.cs`: rollback short-circuit (skip build), start-new-before-retire-old cutover,
  failed-container cleanup on error, retire-after-cutover.
- `AppOperationsService.cs`: label-based current-container lookup.
- Added `tests/Harbora.Tests/DeploymentPlanningTests.cs` (+6).

**Tests / checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **59 passed** (+6).

**Result**
- SUCCESS at build + unit level. Fixes C4 and a rollback correctness bug.
- Live cutover/rollback still needs a **Docker host** to verify end-to-end (P2 Docker step); the
  pure planning logic is fully unit-tested.

**Decisions**
- Versioned container names + retire-after-cutover chosen over the old remove-then-start, and the
  fix applies to remote nodes too via per-deployment ports (strictly better than the prior stable-
  port remove-first behavior). Legacy unversioned containers are retired automatically on first
  redeploy (safe migration).

**Next step**
- C3 honesty pass: implement Static-site + single-container Template deploys (currently throw),
  expose them in the create form, gate Compose until implemented, and correct the README.

---

## 2026-07-22 — Phase 2 (partial): Master key fail-closed (critical security fix)

**What was done**
- Implemented ADR-009 / threat 2.2: the master encryption key is now resolved **fail-closed**.
  Previously it silently fell back to a public default — in code *and* hardcoded in
  `appsettings.json` — so with `HARBORA_MASTER_KEY` unset, all "encrypted" secrets were trivially
  decryptable. Fixed both instances.

**Files changed**
- Added `src/Harbora.Infrastructure/Security/MasterKeyResolver.cs` (pure policy: Production must
  have a secure key; rejects known-insecure placeholders; Development uses a dev key with a loud
  warning).
- `DependencyInjection.cs`: use the resolver; coalesce a blank appsettings value through to the env
  var; print a warning when the dev fallback is used.
- `appsettings.json`: removed the insecure `Harbora:MasterKey` default (now blank).
- `appsettings.Development.json`: added a dev-only key for local convenience.
- Added `tests/Harbora.Tests/MasterKeyResolverTests.cs` (8 tests).

**Tests / checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **31 passed** (was 24; +7 net).
- Runtime (built DLL, real Postgres): Production **without** a key → aborts with the precise
  message; Production **with** an env key → `/healthz` 200; Development (no env key) → boots and
  prints the INSECURE-key warning.

**Result**
- SUCCESS. The platform's most serious "insecure default" is closed and covered by tests.

**Decisions**
- Marked BREAKING (semver-major): existing Production installs that never set `HARBORA_MASTER_KEY`
  will now refuse to boot. Justified (it's a real vulnerability), low blast radius (the installer
  already generates the key in `deploy/.env`), and documented as a migration note (doc 11 §2.3).
  This is the one intentional breaking default in the overhaul; per the escalation rules it is
  reversible (unset the check) and non-destructive, so proceeded and recorded.

**Next step**
- P2 remainder needs a Docker host: reproduce install + one real end-to-end deploy (image + git),
  recorded here. Then P3 — deployment state machine + crash reconciler (ADR-004/005).

---

## 2026-07-22 — Phase 0–1: Guardrails & protective tests (execution begins)

**What was done**
- Created branch `overhaul` (off `84603e0`).
- **P0 — guardrails:** added a solution-wide CI workflow (`.github/workflows/ci.yml`: restore →
  build → test, plus a frontend job that runs `npm ci && npm run build` so a broken Vue bundle
  fails CI); added `.editorconfig` (promotes the unused-parameter warning CS9113 to an **error** so
  dead ctor params can't return); fixed the 3 pre-existing warnings.
- **P1 — protective tests:** added the first-ever test project `tests/Harbora.Tests` (xUnit +
  FluentAssertions) with 24 characterization tests over the highest-risk pure logic:
  secret protector (round-trip, non-determinism, wrong-key + tamper rejection), PBKDF2 hasher
  (verify + salting), secret redactor, buildpack detection (per-stack + precedence + no-match),
  and the Traefik renderer/validator (router/service YAML, cert resolver, priority ordering,
  and the validation gate: missing host, bad port, redirect-without-target, duplicate warning).

**Files changed**
- Added: `.github/workflows/ci.yml`, `.editorconfig`,
  `tests/Harbora.Tests/{Harbora.Tests.csproj,SecurityTests.cs,BuildpackTests.cs,TraefikProxyEngineTests.cs}`.
- Edited (warning fixes, removed unused `clock` param): `GitWebhookProcessor.cs`,
  `ManagedServiceEngine.cs`, `AppsController.cs`.
- Solution: `Harbora.slnx` (added test project).

**Tests / checks run**
- `dotnet build Harbora.slnx -c Release` → **Build succeeded, 0 warnings, 0 errors** (was 3
  warnings).
- `dotnet test Harbora.slnx -c Release` → **24 passed, 0 failed**.

**Result**
- SUCCESS. The protective net is live and green; the build is warning-clean; CI will gate future
  PRs on both backend tests and a successful frontend bundle build.

**Decisions**
- Removed the 3 unused `clock` primary-constructor parameters rather than suppressing the warning
  (cleaner; DI unaffected). Recorded because it slightly changes 3 constructor signatures (no
  behavior change).
- Started tests at the pure-logic tier (no Docker/DB needed) so the net exists before any core
  refactor; integration/E2E tiers (Testcontainers) come with the phases that need them (doc 13).

**Next step**
- P2: on a Docker-capable host, reproduce install + run one real end-to-end deploy (image + git)
  and record it here; implement the master-key **fail-closed in Production** check (ADR-009) with a
  unit test. Then P3 (deployment state machine + crash reconciler).

---

## 2026-07-22 — Phase 0: Discovery, market research, and design (baseline)

**What was done**
- Cloned `github.com/sadrazkh/Harbora` @ `84603e0` (branch `master`).
- Read the full repository (Domain/Application/Infrastructure/Data/Web/Agent/Cli/Shared, installer,
  compose, Traefik config, CLI, Vue islands, all controllers/views).
- Installed .NET 10 SDK (10.0.107) and PostgreSQL 15 in the workspace.
- Established the **build baseline** and a **runtime baseline** of the panel.
- Ran deep competitor research across 25 products (5 parallel research agents).
- Wrote the first design documents (see below).

**Files changed**
- Added `docs/overhaul/01-current-state-assessment.md`, `02-competitor-research.md`, `progress.md`
  (more docs landing in this phase).
- No source files changed yet (discovery only).

**Tests / checks run**
- `dotnet restore Harbora.slnx` → success.
- `dotnet build Harbora.slnx -c Release` → **Build succeeded, 0 errors, 3 warnings** (unread
  `clock` primary-constructor parameters in `GitWebhookProcessor`, `ManagedServiceEngine`,
  `AppsController`).
- `dotnet run --project src/Harbora.Web` against PostgreSQL 15 → boots, applies **5 migrations**,
  seeds **7 templates / 5 instance sizes / 3 plans / 1 local server**.
- Authenticated UI walk (cookie session): **16/16 routes → HTTP 200** (`/`, `/apps`,
  `/apps/create`, `/deployments`, `/git`, `/domains`, `/routes`, `/databases`,
  `/databases/create`, `/backups`, `/monitoring`, `/servers`, `/plans`, `/tenants`, `/templates`,
  `/settings`).
- `npm run build` (Vue islands) → **blocked** by the sandbox package-registry firewall
  (`registry.npmjs.org` 403 on transitive `ws`/`vite` tarballs). Environmental, not a project
  defect; fallback CSS keeps the shell usable.

**Result**
- SUCCESS for build + backend runtime baseline. Frontend bundle build deferred to a normal network.
- Docker is unavailable in this sandbox → container/deploy/metrics runtime paths were verified by
  **code reading only**. They must be validated on a Docker host as execution step 0.

**Key findings / decisions**
- The codebase is a genuine, well-structured modular monolith — **keep the foundation, don't
  rewrite.**
- Critical gaps identified: **no tests / no CI gate (C1)**; **deploy lifecycle not crash-safe
  (C2)**; **compose/static/template deploy sources advertised but throw `NotSupported` (C3)**;
  **health/cutover not zero-downtime (C4)**. Full list in doc 01 §5.
- Decision: overhaul order = stabilize (tests+CI+real deploy smoke) → fix domain/state-machine core
  → close claimed-but-missing gaps → layer differentiators. Recorded in doc 12.

**Next step**
- Finish the remaining design docs (03–14).
- Create the `overhaul` branch, add a solution build+test CI workflow, and stand up the first test
  project (characterization tests around Traefik rendering, buildpack detection, slug/host logic,
  secret protector) — the protective net required before any refactor.
- On a Docker-capable host: reproduce the baseline and run one real end-to-end deploy (prebuilt
  image + git repo); record the outcome here.

---

### Baseline reference (do not edit — pin for comparison)
- Commit: `84603e0`
- Build: 0 errors / 3 warnings (Release)
- Migrations: 5 · Seed: 7 templates, 5 sizes, 3 plans, 1 server
- UI: 16/16 routes 200 · Tests: 0
