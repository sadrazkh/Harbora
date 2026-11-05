# P — The proof: a real launch, on real Docker, before it reaches the server

Read `.superpowers/sdd/r8/CONSTRAINTS.md` and `docs/superpowers/specs/2026-11-04-launch-design.md` first, then the reports in `.superpowers/sdd/r8/`, especially their "unverified" sections.

Everything in this round has been tested with fakes. This machine has no Docker. The first time a launch runs for real would otherwise be the owner's production server, in front of the owner. Your job is that the first time is on a throwaway GitHub runner instead, and that if something is broken we find it there: the real installer, the real image, the real migration on real Postgres, real Traefik, real databases, real `docker build`.

The measure of your work is whether **it could fail**. A proof that cannot fail tells us nothing, and this codebase has a long history of "green" that meant "did not run".

## What to write

- `deploy/launch-proof.sh`: the proof. Runs by hand on any throwaway Linux host as well as in CI. Same discipline and same helpers as `deploy/live-host-proof.sh`, so read that first: numbered steps, every failure names its step, each step asserts a fact afterwards rather than trusting an exit code.
- `deploy/proof-fixtures/`: the apps it launches, kept small.
- `.github/workflows/launch-proof.yml`: the trigger. `push` to branches `proof/**` and to `master` when launch-related paths change, plus `workflow_dispatch`. It does not join `ci.yml`'s `complete` gate: it takes much longer than the suite, and its job is to answer a question, not to block every push. Read `ci.yml`'s `scripts` job, which syntax-checks every script, and anything else that censuses workflows (`CliReleaseWiringTests`, say). Fixtures must not break the other suites: check that nothing scans `deploy/` and trips over them, and that the panel's Docker build context is not bloated.

## How it installs

`deploy/install.sh install` with `REPO_URL` and `REPO_BRANCH` set to the ref under test. It is what a stranger runs, and it proves the migration applies. `PANEL_DOMAIN`, `ROOT_DOMAIN` and `ACME_EMAIL` as `live-host-proof.sh` sets them. Read `install.sh` for everything it asks, and what its own exit code does and does not mean.

The awkward part is names and TLS, because a GitHub runner cannot be reached from outside, so Let's Encrypt can never succeed there. Find out what really happens rather than assuming, and write down what you find:

- The installer's own no-DNS fallback is `nip.io` on the server's IP. `public_ip` on a runner gives an address nothing can reach. Using the runner's private IP in `panel.<ip>.nip.io` should resolve, including from inside containers, if the runner's DNS allows it.
- The panel's `/healthz` and the wizard can be reached with `curl -k`, or `--resolve`.
- An app's `https://` address will carry Traefik's default certificate. **What does the `url` check do with that?** Read `AppVerifier` and `IUptimeProbe`. It may report a certificate problem as `Failed`, or wait until `giveUpAt`. Either is correct behaviour on this host. If so, the proof asserts it precisely: `url` is the only check that failed, its detail is about the certificate or the address, and the launch is `SucceededWithProblems` **for that reason alone**; and the proof then fetches the app itself through Traefik with `curl -k` and asserts the body. Do not weaken a production rule to make a runner happy. If there is a real setting (`SslEnabled` on the domain, a staging CA, a local CA) that gets a clean `Succeeded` honestly, prefer that, as long as what it exercises is the same path a customer takes.

## What it proves

Everything goes through the real CLI: `dotnet publish` `src/Harbora.Cli` for linux-x64 from the ref under test, sign in as `POST /api/v1/auth/token` allows (read how `live-host-proof.sh` gets a token after the first-run wizard), then `harbora launch` as a customer runs it.

1. **The main case: `deploy/proof-fixtures/node-postgres/`.** `package.json` depending on `pg` and nothing unusual, a `server.js` with no Dockerfile, and a `.env.example` containing `DATABASE_URL=`, `DB_HOST=`, `JWT_SECRET=`, `API_KEY=`, `LOG_LEVEL=info`. Its `GET /` runs `SELECT 1` through `DATABASE_URL`, runs it again through `DB_HOST`/`PGUSER`/`PGPASSWORD`/`PGDATABASE`, and answers JSON saying which worked, plus `JWT_SECRET`'s length (never its value), `API_KEY` and `LOG_LEVEL`. `harbora launch --yes --set API_KEY=proof-answer`. Assert from the command's own output and exit code, then from `GET /api/v1/launch/{id}`, then **from the response of the running app through Traefik**: both queries succeeded, the alias works, the secret was generated at a sensible length, the answer arrived, the example's value arrived. That response is the whole point: the app itself reached its database with what it was given, which no test here has ever shown.
2. **A failure must look like a failure: `deploy/proof-fixtures/crashes/`.** A Node app that exits non-zero a few seconds after starting. It must not be reported `Succeeded`. Assert the status, the exit code, and which check says so. Also `deploy/proof-fixtures/wrong-database-host/`: an app ignoring what it was given and connecting to `localhost:5432`, whose log must produce the `logs` finding. If this reaches `Succeeded` with a warning, that is what the design says; assert it precisely. A check that reports green on a broken app is exactly what this exists to catch, so if either goes green, that is a bug to report, not a fixture to change.
3. **A Python worker: `deploy/proof-fixtures/python-worker-redis/`.** A loop using `redis` through `REDIS_URL`, and a `Procfile` with a `worker:` line. It must be a worker with no domain and no HTTP probe; assert that from the checks, and assert its log line shows Redis answered.
4. **Several apps: `deploy/proof-fixtures/two-apps/`.** With no `--folder` and `--yes`: exit 1, names both folders, created nothing. With `--folder api`: it builds from that folder.
5. **Another deploy afterwards.** From the node fixture's folder, after a small change, `harbora deploy` (the `harbora.yml` the launch wrote says which app) and assert the app answers with the change, still reaching its database. This is what a customer does the next day.
6. **The pages render over real data.** With the session cookie from the wizard, `GET /launch`, `GET /launch/{id}` for a finished launch and `GET /launch/{id}/progress`: 200, the expected markers present, and the generated secret's value, read through the API's env endpoint or the database, is nowhere in the HTML.

On a fresh install: does a launch work at all as it stands, with default sizes, default billing and no setup? That is what a customer has. If it refuses, that refusal is a finding.

## When it fails

GitHub's job logs need a login to read, and the coordinator reading this has none, only the public API. So every failure must be readable from **annotations**: `::error title=step N::...`, which `GET /repos/{owner}/{repo}/check-runs/{id}/annotations` returns without a login. On failure the script emits, as separate annotations and with newlines encoded as `%0A`: the step and fact that failed; the launch's JSON; the deployment's log tail; the panel's log tail (`docker logs harbora-panel`, errors first); and `docker ps -a`. Keep each under 4 KB. Everything on this host is throwaway, but pass values through the same care anyway: no generated secret in an annotation.

Add a final success summary as a `::notice::` annotation with the facts established, so a pass can be read the same way.

## Bugs you find

You will probably find real bugs. That is the point, and it is the best thing this round can produce.

- **Fix them**, in the product code, with a test in `tests/Harbora.Tests` that fails without the fix. Each as its own commit saying what the real run showed. Do not patch the proof to go around a product bug, and do not weaken a rule to get green. Where the correct behaviour is unclear, choose what a customer would want, and say so in your report.
- Anything too large to fix, or a decision that belongs to the owner: stop and say so in your report rather than working around it.

## Running it

You may push **one branch only**, `proof/r8`: `git push origin HEAD:proof/r8`, with `--force-with-lease` allowed on that branch alone. Never push anything else. `gh` is not installed; do not go looking for credentials, tokens or credential stores. Plain `git push` already works. Read results with `curl` on the public API, unauthenticated, which allows 60 requests an hour, so poll gently: `sleep` in a backgrounded command is fine, and read in one go.

```
GET https://api.github.com/repos/sadrazkh/Harbora/actions/runs?branch=proof/r8&per_page=3
GET https://api.github.com/repos/sadrazkh/Harbora/actions/runs/{run}/jobs
GET https://api.github.com/repos/sadrazkh/Harbora/check-runs/{job}/annotations
```

Iterate until it passes on a commit you can name, or until you can say exactly what is left and why.

## Done means

- The workflow ran on your last commit and passed. Give the run's URL and copy the success notice into your report.
- The full `Harbora.slnx` suite passed locally on that commit, with `Harbora.Tests.dll` confirmed in the output.
- The report `.superpowers/sdd/r8/p-report.md`: what the real run established, each bug found and its fix, what the proof still does not establish (a real certificate, a remote node, a real Git clone over the internet, and whatever else), and how long the run takes.

## Worktree

`E:/Cash.Net/source/repos/sadrazkh/Harbora-r8-p`, branch `r8/p`. Commit with `git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit`, with no Co-Authored-By trailer.
