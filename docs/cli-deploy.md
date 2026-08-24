# Deploying to Harbora — CLI, config file and HTTP API

Everything needed to deploy to Harbora from a terminal, from CI, or from your own tool.

The HTTP API and the `harbora.yml` schema below are a **public contract**: they are what the official
CLI uses, and a third-party client that speaks them is a first-class citizen. Both are covered by
tests, so changes that break them fail the build.

---

## 1. Install the CLI

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.sh | bash

# Windows (PowerShell)
irm https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.ps1 | iex

# Or, with the .NET SDK
dotnet tool install -g Harbora.Cli
```

Then authenticate once. The token comes from **Settings → API tokens** in the panel:

```bash
harbora login --server https://panel.example.com --token hbr_cli_xxxxxxxx
harbora whoami
```

Credentials are stored in `~/.harbora/config.json`. In CI, skip `login` and pass
`--server` / `--token` to `deploy` instead.

> Installing on a machine that also **runs** a Harbora server? There, `harbora` is the admin/recovery
> command (`harbora doctor`, `harbora reset-password`). The installer detects this and installs the
> deploy CLI as **`harbora-cli`**, so the recovery tool is never replaced. Substitute `harbora-cli`
> for `harbora` in the commands below on such a machine.

---

## 2. Sign in

Either your panel account, or a token created in **Settings → API Tokens**:

```bash
harbora login                                   # asks which, then for the details
harbora login --email you@example.com           # prompts for the password
harbora login --token hbr_cli_xxxx --server https://panel.example.com   # CI
```

Signing in to a second panel does not replace the first:

```bash
harbora accounts                    # list; * marks the one in use
harbora accounts you@example.com    # switch
harbora accounts --logout you@example.com
```

When several accounts are signed in, `harbora deploy` asks which one to use — or pass
`--account you@example.com`.

## 3. Keeping the CLI current

```bash
harbora update            # replace this binary with the latest release
harbora update --check    # just say whether one exists
```

After `deploy`, `whoami`, `apps` or `status` talk to the panel, the CLI compares itself against it and
says so when it is behind:

```
! This CLI is 0.1.0; the server expects 0.2.0. Run harbora update.
```

The check is best-effort — it runs after the work has already succeeded, gives up after three
seconds, and says nothing at all if the panel is older than this endpoint or the version cannot be
read. It deliberately does **not** run from `harbora cancel`: that command promises to be safe in a
pipeline with no surprise network calls, which an extra request here would break.

## 4. Deploy

### Preflight: `harbora doctor`

`harbora deploy` runs this automatically, before uploading anything — so most people never type it
by hand. Run it directly to see the full report, including things a mid-deploy preflight
deliberately skips (see below):

```bash
harbora doctor
```

It checks, and says what it looked at either way:

- **`harbora.yml`** — parses, names an app (or one was given on the command line), names a server.
- **The build** — the context exists; the Dockerfile exists, or the project auto-detects (Node,
  .NET, Go, PHP, Python, static); every `COPY` source in the Dockerfile exists under the context;
  the build references `$PORT` (a container on the wrong port is the panel's own documented 502,
  below).
- **The upload** — packs the project exactly as `--push` would, reports what it would exclude and
  why, and cross-references every `COPY` source and every `package.json` script against that list.
  **This is the check that would have caught a real incident**: an app's `package.json` ran
  `node Scripts/build/copy-fonts.mjs` — an ordinary source file — and the upload excluded it because
  a folder in the path was named `build`, which broke the image build with no error anywhere in the
  deploy log. See *"Keeping a source folder named `build`"* below.
- **Auth** *(standalone `harbora doctor` only, not the automatic deploy preflight)* — a session
  exists for the target server and `whoami` still accepts it.

`harbora deploy` runs the manifest and build/upload checks (not auth — it already has its own login
flow) and refuses to upload when one of them would break the build outright, before touching the
network. Skip it with `--skip-doctor` if a check is wrong for your project.

```bash
harbora deploy        # deploys, then streams the build log
```

Every interactive deploy shows your apps and asks which one, the way CapRover does. The app from
`harbora.yml` (or the command line) is listed **first** and marked `(current)`, so pressing Enter
deploys where you deployed last. Pick a different one and the CLI offers to update `harbora.yml` —
rewriting only the `app:` line, so the rest of your config survives.

Pass `--yes` to skip the question; it is skipped automatically when there is no terminal, so CI
behaves exactly as before. With no `harbora.yml` and no app name, the CLI writes `harbora.yml` after
you choose, so the next run needs nothing. `harbora init` still writes the fuller commented file.

> The name is matched case-insensitively, but what gets deployed is the app's own slug. The server
> compares slugs exactly, so `harbora deploy My-App` against an app called `my-app` is a 404 — and
> one that arrives while the upload is still in flight, which used to surface as
> *"Error while copying content to a stream."*

### Every deploy mode

| Command | What happens |
|---|---|
| `harbora deploy` | Packs the current folder, uploads it, builds on the server |
| `harbora deploy --push` | Same, forced — even inside a Git repository |
| `harbora deploy --yes` | Uses the configured app without asking (implied in CI) |
| `harbora deploy --path ./web` | Deploys a different folder |
| `harbora deploy --tar dist.tar.gz` | Uploads an archive you already built |
| `harbora deploy --branch main` | Uploads a branch's **committed** content (`git archive`) |
| `harbora deploy --ref main` | The **server** pulls from the app's Git remote |
| `harbora deploy --tag v1.2.0` | Same, at a tag |
| `harbora deploy --image nginx:alpine` | Releases an existing image; nothing is built |
| `harbora deploy --no-follow` | Queues and returns instead of streaming logs |

**How the mode is chosen when you pass no flags**

1. `image:` in `harbora.yml` → release that image
2. `branch:` in `harbora.yml` → archive that branch
3. The folder **is** a Git repo → the server pulls from the app's remote
4. The folder is **not** a Git repo → the folder is packed and uploaded

Flags always beat the config file, and the CLI prints the mode and the reason it chose it.

> `--branch` uploads committed content only. Uncommitted edits are deliberately excluded — deploying
> them is how "works on my machine" reaches production.

### Stopping one

```bash
harbora cancel 0199aa11-2233-4455-6677-889900aabbcc   # the id `harbora deploy` printed
```

Works while the deployment is queued **or** already building; exits `0` when it stopped and `1` with
the server's own explanation when it did not — most often because it had already finished. Honours
`--account`, and never asks a question, so it is safe in a pipeline.

Ctrl+C while the log is streaming stops *following*, not the deployment. The CLI says so and prints
the `harbora cancel` line for it; the panel's deployment page has the same button.

### CI, with no interactive login

```bash
harbora deploy my-app \
  --server https://panel.example.com \
  --token "$HARBORA_TOKEN" \
  --push --no-follow
```

Exit code is `0` on success and non-zero on failure, so it gates a pipeline directly.

### Databases: what your app should read

Attaching a database writes its credentials into the application's environment. Which names depends
on the engine, and the application page lists them under **“How your app reads these”** — that list
is generated from the catalog, so it is never out of date.

Two of them are worth knowing before you get there:

| Variable | Shape | Who reads it |
|---|---|---|
| `DATABASE_URL` | URI — `postgresql://user:pass@host:5432/db` | Node, Python, Ruby, Go, most scripting runtimes |
| `DATABASE_DSN` | Keywords — `Host=…;Port=…;Database=…;Username=…;Password=…` | .NET and anything else on ADO.NET |
| `ConnectionStrings__DefaultConnection` | Same as `DATABASE_DSN` | .NET, with no code change at all |

**On .NET there is nothing to do.** Configuration reads environment variables and maps `__` to `:`,
so `ConnectionStrings__DefaultConnection` overrides whatever `appsettings.json` holds. If your app
spells the key differently — `ConnectionStrings:Default`, say — add one variable named
`ConnectionStrings__Default` holding the value of `DATABASE_DSN`.

> Don't hand `DATABASE_URL` to .NET. It is a URI, and every ADO.NET provider parses only
> `keyword=value`. An app given the URI starts, fails its first query, and the deployment dies at
> the health check with a stack trace about the driver — which names neither the database nor the
> attach that was supposed to supply it.

Redis and the brokers have no connection string; they are reached by URL (`REDIS_URL`, `AMQP_URL`,
`NATS_URL`). Redeploy after attaching — variables are applied at release, not while a container runs.

---

## 5. `harbora.yml`

Written by `harbora init`, read by `harbora deploy`. Every field is optional.

```yaml
app: my-api                          # app slug on the server
server: https://panel.example.com    # optional; otherwise the logged-in server

build:
  dockerfile: Dockerfile             # path inside the context
  context: .                         # build context

ignore:                              # on top of .dockerignore / .gitignore
  - coverage
  - "*.log"

dockerfileLines:                     # define the build inline, no committed Dockerfile
  - FROM node:20-alpine
  - WORKDIR /app
  - COPY . .
  - RUN npm ci --omit=dev
  - CMD ["npm", "start"]

image: nginx:alpine                  # release this image instead of building
branch: main                         # archive this branch instead of the folder
```

**Schema notes**

- `app`/`name` and `server`/`url` are accepted as aliases.
- `dockerfile` and `context` are read whether they sit at the top level or under `build:`.
- Lists accept both `- item` lines and inline `[a, b]`.
- Values may be quoted; a `#` inside quotes is content, not a comment.
- **Unknown keys are ignored**, so a file written by a newer CLI still works with an older one.

### What is never uploaded

`.dockerignore` is honoured first (it is what the build actually reads), then `.gitignore`, then
`ignore:` from this file. On top of that, two built-in rules apply everywhere, with no ignore file
needed:

Matched at **any depth**, because nobody puts real source inside a directory with these names, no
matter how deep it sits:

`.git` · `.hg` · `.svn` · `node_modules` · `bower_components` · `bin` · `obj` · `.next` · `.nuxt` ·
`.venv` · `venv` · `__pycache__` · `.pytest_cache` · `.idea` · `.vs` · `.vscode` · `.DS_Store` ·
`Thumbs.db` · **`.env`** · `.env.local` · `.terraform` · `.gradle`

Matched **only at the project root** — these are exactly the build-output names an unconfigured
project uses by default (npm's `./build` or `./dist`, Cargo's `./target`, a PHP/Go `./vendor`,
Nuxt's `./.output`), but every one of them is equally an ordinary *source* directory name anywhere
deeper in a tree:

`build` · `dist` · `target` · `vendor` · `.output`

**Keeping a source folder that happens to be named `build`.** A real incident: an app's own source
tree had `Scripts/build/copy-fonts.mjs` — an ordinary helper script, nested under a folder literally
named `build` two levels down — and an older version of this packer matched `build` at any depth,
silently dropping the file. `npm run prebuild` then failed *inside the image*, with nothing in the
upload or the build log saying why. The rule above is the fix: `build`/`dist`/`target`/`vendor`/
`.output` only count as build output at the project root now, so a nested folder with the same name
is packed like any other source directory.

That fix does not cover every path to the same mistake, though. **`ignore:` in `harbora.yml`, and
any line in your own `.dockerignore`/`.gitignore`, still match at any depth** — that is what makes
`ignore: [coverage]` drop `coverage/` wherever it appears, which is the whole point of that feature.
If your `.gitignore` happens to carry a bare `build` or `dist` line (extremely common in JavaScript
projects), a nested folder with that exact name is excluded again, the same way, just via a
different rule. Two ways to avoid it:

- Run `harbora doctor` (or just `harbora deploy`, which runs it for you) before the first deploy of
  a new app — it reads `package.json`'s `scripts` and the Dockerfile's `COPY` sources and fails
  loudly if either references a path the upload would drop.
- Scope your own ignore entries to the root when that is what you mean: `/build` instead of `build`.

`.env` is excluded on purpose: it usually holds local database URLs and API keys. Put production
values in the app's **Environment Variables**.

### How the build is chosen, in order

1. `dockerfileLines` from `harbora.yml`, if present
2. The `dockerfile` path, if that file exists in the context
3. Stack auto-detection — Node (`package.json`), .NET (`*.csproj`), Go (`go.mod`), PHP
   (`composer.json`/`index.php`), Python (`requirements.txt`/`pyproject.toml`/`Pipfile`), static
   (`index.html`)
4. Otherwise the deployment fails with a message saying exactly this

Auto-detected builds set `ENV PORT` to the app's container port, so `process.env.PORT` and the
equivalents work without configuration.

---

## 6. HTTP API

Base URL `https://<panel>/api/v1`. Authenticate with a bearer token:

```
Authorization: Bearer hbr_cli_xxxxxxxx
```

### `GET /version`
Unauthenticated, so a client can check compatibility before signing in.
```json
{ "server": "0.2.0", "cli": "0.2.0" }
```
`cli` is the newest CLI known to match this panel. Compare it with your own version and tell the user
when they are behind — but treat anything you cannot parse as "no opinion" rather than as out of date.

### `POST /auth/token`
Exchanges a panel account for a CLI token, so a client never has to ask a user to create one by hand.
Unauthenticated; rate-limited per IP like the panel's own login.

```json
{ "email": "you@example.com", "password": "…", "name": "my-tool on laptop" }
```
```json
{ "token": "hbr_cli_xxxxxxxx", "email": "you@example.com", "name": "my-tool on laptop" }
```
`401 {"error":"Invalid email or password."}` — deliberately the same for a wrong password and an
unknown address, so this cannot be used to discover who has an account.

### `GET /whoami`
```json
{ "email": "you@example.com", "workspaceId": "0199…" }
```

### `GET /apps`
```json
[ { "id": "0199…", "name": "My API", "slug": "my-api", "status": "Running",
    "source": "Upload", "canServerPull": false } ]
```

`canServerPull` says whether the app has a Git repository the server could pull from. **Decide how to
deploy from this, not from whether the client's folder happens to be a git checkout.** An app created
without a repository — the flow this CLI exists for — will accept a plain deploy request and then fail
with *"no source archive was uploaded"*, because there was nothing for the server to fetch. Older
panels omit the field; treat a missing value as "upload", which works for every app type.

### `POST /apps/{slug}/deploy`
Server-side deploy. Body optional:

```json
{ "gitRef": "main", "image": "nginx:alpine" }
```

| Field | Meaning |
|---|---|
| `gitRef` | Branch or tag for the server to pull. Defaults to the app's configured ref |
| `image` | Release this image and build nothing. Wins over `gitRef` |

→ `200 { "deploymentId": "0199…" }`

### `POST /apps/{slug}/deploy/archive`
Push source. The body is the **raw bytes of a gzipped tar** — not multipart.

```
Content-Type: application/gzip
```

```bash
tar czf - . | curl -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/gzip" \
  --data-binary @- \
  https://panel.example.com/api/v1/apps/my-api/deploy/archive
```

→ `200 { "deploymentId": "0199…" }`

Limits: **512 MB** compressed, 2 GB uncompressed, 200,000 entries. Archive paths must stay inside the
archive root — entries containing `..` or absolute paths are rejected — and symlinks are skipped.

### `GET /deployments/{id}`
```json
{ "id": "0199…", "number": 42, "status": "Building", "commitSha": "a1b2c3d", "errorMessage": null }
```

`status` is one of `Queued`, `Building`, `Pushing`, `Deploying`, `HealthChecking`, `Succeeded`,
`Failed`, `Cancelled`, `RolledBack`. The first five are in flight; the rest are terminal.

### `GET /deployments/{id}/logs?after={seq}`
```json
[ { "seq": 0, "stream": "System", "message": "Deployment #42 started (Upload)." } ]
```

Poll with the highest `seq` you have seen to follow a build. `stream` is `System` or `Build`.

### `POST /deployments/{id}/cancel`

Stops a deployment that is queued or in flight. Needs `apps.deploy`, the same capability as starting
one. No body.

→ `200 { "deploymentId": "0199…", "status": "Cancelled" }`

A deployment that reached a terminal state first — including between your last status read and this
call — is a `409` naming the state it ended in, never a `200` for a cancellation that did not happen:

→ `409 { "error": "Deployment #42 had already ended (Succeeded), so there was nothing to cancel." }`

Cancelling settles the deployment **and** stops the work: a queued build is settled before it starts,
and one already running is interrupted through its cancellation token.

### Status codes

| Code | Meaning |
|---|---|
| `200` | Accepted |
| `400` | Empty or malformed body |
| `401` | Missing or invalid token |
| `403` | The token's role lacks `apps.deploy` |
| `404` | No such app or deployment **in your workspace** |
| `409` | A conflicting deployment is in flight (e.g. a rollback is running), or the deployment has already ended |
| `413` | Archive above the size limit |

---

## 7. Writing your own client

A minimal integration is three calls:

1. `POST /apps/{slug}/deploy/archive` with a gzipped tar → take `deploymentId`
2. Poll `GET /deployments/{id}` until `status` is terminal
3. Optionally stream `GET /deployments/{id}/logs?after={seq}`

```python
import subprocess, requests, time

TOKEN, BASE, APP = "hbr_cli_…", "https://panel.example.com/api/v1", "my-api"
headers = {"Authorization": f"Bearer {TOKEN}"}

tar = subprocess.run(
    ["tar", "czf", "-", "--exclude=.git", "--exclude=node_modules", "--exclude=.env", "."],
    capture_output=True, check=True).stdout

r = requests.post(f"{BASE}/apps/{APP}/deploy/archive", data=tar,
                  headers={**headers, "Content-Type": "application/gzip"})
r.raise_for_status()
deployment = r.json()["deploymentId"]

seq = -1
while True:
    for line in requests.get(f"{BASE}/deployments/{deployment}/logs?after={seq}", headers=headers).json():
        print(line["message"]); seq = line["seq"]
    status = requests.get(f"{BASE}/deployments/{deployment}", headers=headers).json()["status"]
    if status in ("Succeeded", "Failed", "Cancelled", "RolledBack"):
        raise SystemExit(0 if status == "Succeeded" else 1)
    time.sleep(2)
```

**Compatibility rules this project holds itself to**

- Fields are added, not removed or repurposed.
- Unknown fields in a request body are ignored, so you may send more than an older server knows.
- Status names are stable strings; treat anything unrecognised as "in flight" and keep polling.
- `harbora.yml` keys are additive for the same reason.

---

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `No app specified` | No `app:` in `harbora.yml` and none passed | `harbora init`, or `harbora deploy <slug>` |
| `the stack couldn't be auto-detected` | No Dockerfile and no recognised stack marker | Add a `Dockerfile`, or `dockerfileLines:` to `harbora.yml` |
| `409 Conflict` | Another deployment or a rollback is in flight | Wait for it, or cancel it in the panel |
| Deploy succeeds but the app 502s | The app isn't listening on the configured container port | Bind to `process.env.PORT` (auto-detected builds set it) |
| Upload is huge / slow | `node_modules` or build output is being packed | Add a `.dockerignore`, or `ignore:` entries |
| `--branch` deploys stale code | It archives **committed** content | Commit, or use `--push` for the working folder |
| `Upload … was cut off by the server` | The server refused before reading the body — usually an app name it doesn't recognise, or a token that can't deploy | Run `harbora deploy` and pick from the list; check the token's permissions in the panel |
| Build succeeds, health check times out, driver stack trace in the log | The app is reaching the database in `appsettings.json` (`localhost`), not the attached one | Attach the database, then read `ConnectionStrings__DefaultConnection` or copy `DATABASE_DSN` under your own key — see *Databases: what your app should read* |
