# Harbora

A self-hosted, **multi-tenant PaaS** — install it on a VPS with one command, open the web UI, and
deploy/manage all your apps. Then resell it: give customers their own quota-limited, network-isolated
workspaces across your primary and helper servers. Bilingual (فارسی/English, RTL/LTR), PWA, with a CLI.

> **Status:** feature-complete against its spec — app deployment (Git / Dockerfile / image / templates),
> visual routing designer, managed databases, backups (local + S3), monitoring + alerts, Git webhooks +
> OAuth, multi-server agents, and a full multi-tenant layer (plans, quotas, capacity scheduler, provider
> console, per-tenant network isolation, usage metering). Builds clean; run it on a VPS to use it live.

---

## 🚀 Install (one command)

On a fresh **Linux VPS** (Ubuntu/Debian/Fedora/Alpine), as root:

```bash
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install.sh | bash
```

That's it. The installer is **fully self-contained and interactive (فارسی/English)** — it:

1. checks OS/arch compatibility,
2. **installs every prerequisite itself** — `curl`, `git`, `openssl`, and **Docker** (with Compose) if missing,
3. **asks whether you have a real domain** (and derives `panel.` + `apps.` from it) or falls back to zero-DNS `nip.io`,
4. **tests DNS** for the panel + apps wildcard — warns clearly if records don't point at the server (you can still continue and fix DNS later),
5. asks for the **Let's Encrypt email** (blank → sensible default),
6. generates `/opt/harbora/app/deploy/.env` with freshly-random secrets,
7. **builds the platform from source** and starts it (Traefik v3.6 + PostgreSQL + Redis + the panel),
8. **verifies the install**: Traefik↔Docker API compatibility, the panel route through Traefik (a 404 prints a clear bilingual fix), and SSL issuance (on failure it prints the ACME log lines and likely causes).

It is **idempotent** — safe to re-run; an existing `.env` (your secrets) is never overwritten, and a running stack is reused.

### Zero-DNS default (just works)

If you don't pass any domains, Harbora defaults to **`nip.io`** wildcard DNS based on your server's
public IP — e.g. `panel.203.0.113.10.nip.io` — which resolves automatically with **no DNS setup**, so
you get a working HTTPS panel immediately. Great for trying it out.

### Custom domains (production)

Point DNS at your server first:

- `panel.example.com` → your VPS IP
- `*.apps.example.com` → your VPS IP (wildcard for deployed apps)

Then install non-interactively with your domains:

```bash
PANEL_DOMAIN=panel.example.com \
ROOT_DOMAIN=apps.example.com \
ACME_EMAIL=you@example.com \
  curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install.sh | bash
```

Run it in a terminal (not piped) and it will **prompt** for these, showing the defaults.

### How SSL works

Traefik obtains certificates from **Let's Encrypt via the HTTP-01 challenge**: the panel domain and
**each app domain get their own certificate automatically** on first HTTPS hit — no wildcard needed,
because every route Harbora generates carries `certresolver: letsencrypt`. Requirements: the domain's
DNS must point at the server, and **port 80 must be reachable from the internet** (the challenge runs
over it). A **wildcard certificate** (`*.apps.example.com`) is only possible with a **DNS-01 challenge**
(provider API credentials); it's not needed for the default per-subdomain design.

### Prerequisites (all auto-installed)

You don't need to install anything by hand. For reference, the installer ensures: Docker + Compose v2,
`git`, `curl`, `openssl`. Recommended VPS: **2 GB+ RAM**, x86_64 or arm64, ports **80** and **443** open.

---

## 🔄 Update & 🗑 Uninstall

```bash
# Update to the latest source and rebuild:
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install.sh | bash -s -- update

# Uninstall (prompts before deleting data volumes):
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install.sh | bash -s -- uninstall
```

Or from the checkout: `cd /opt/harbora/app/deploy && docker compose up -d --build` (update),
`docker compose down` (stop), `docker compose down -v` (also wipe data).

### An update can always be undone

Before an update applies any database migration, Harbora dumps the database first and **refuses to
migrate if that dump fails**. A schema changed with no way back is the one step of an update that
cannot be reversed, so the moment the restore point can still be taken is the moment to insist on it.
Nothing is dumped on a fresh install (there is nothing to lose) or on an ordinary restart (nothing
changes).

```bash
harbora backups                     # list restore points; pre-upgrade-* are automatic
harbora backup-db                   # take one now, before doing something risky yourself
harbora restore-db <file>           # put the database back (asks you to type the database name)
```

`restore-db` saves the current database as `pre-restore-*` before replacing it, so restoring from the
wrong file is survivable too. If a host genuinely cannot run the dump, `HARBORA_SKIP_UPGRADE_BACKUP=1`
lets the update proceed without one.

Updating never overwrites `deploy/.env`, but it **does** add settings that newer versions require and
your file predates — most importantly `HARBORA_MASTER_KEY`. Without that backfill the panel would
refuse to start after an update (it fails closed rather than leaving secrets decryptable), which looks
like the update broke everything. If an update ever leaves the panel down, run:

```bash
harbora doctor
```

---

## 🆘 `harbora` — server administration & recovery

The installer puts a **`harbora`** command on the server (`/usr/local/bin/harbora`). It exists for the
moment you can't get into the panel: it shows what the platform is configured with, explains what is
wrong, and can reset the admin password.

The recovery commands deliberately **do not need a healthy panel** — they run the app as a one-off
container that never starts the web server, so they still work while the panel is crash-looping.

```bash
harbora doctor      # ← start here: checks config, containers, ports, and prints the panel's last errors
```

### Diagnose

| Command | What it does |
|---|---|
| `harbora doctor` | Checks the master key, domains, DB password, container states and ports 80/443. Prints the panel's recent log lines when it isn't running, and names the fix for each problem found |
| `harbora status` | Container status (`docker compose ps`) |
| `harbora logs [panel\|traefik\|postgres]` | Follow logs; defaults to `panel` |
| `harbora info` | Configuration as the **app** sees it: master key state, domains, database reachability, pending migrations, user/workspace counts, and the owner's email |
| `harbora env` | Everything in `deploy/.env`. Secrets are shown as `(set, hidden)` — safe to paste into an issue |

### Get back in

| Command | What it does |
|---|---|
| `harbora users` | List accounts, roles and whether they're active |
| `harbora reset-password` | Reset the **owner's** password (prompts for the new one) |
| `harbora reset-password --email you@example.com --password 'new-pass'` | Non-interactive form |
| `harbora make-owner --email you@example.com` | Promote an account to Owner — for when the only owner was deleted or demoted |
| `harbora unlock --email you@example.com` | Re-enable a deactivated account without changing its password |

Passwords must be at least 8 characters. A reset also re-activates the account, because a locked-out
admin usually needs both.

### Change settings

| Command | What it does |
|---|---|
| `harbora set-domain <panel-domain> <apps-domain>` | Change both domains and restart. Point DNS at the server first |
| `harbora fix-key` | Generate a `HARBORA_MASTER_KEY` when it's missing or still the insecure development default, then restart |
| `harbora restart` / `harbora stop` | Lifecycle |
| `harbora backups` / `harbora backup-db` / `harbora restore-db <file>` | Database restore points — see [An update can always be undone](#an-update-can-always-be-undone) |

> ⚠️ `harbora fix-key` **replaces** an existing key only if you type `REPLACE` when prompted. Replacing a
> working key makes every stored secret — env vars, git tokens, agent tokens — permanently unreadable.
> It is meant for the case where no usable key exists at all.

Installed somewhere other than `/opt/harbora`? Set `HARBORA_DIR=/your/path harbora doctor`.

> **Two tools share the name `harbora`.** On a server it is this admin/recovery command (installed by
> `install.sh`); on a developer machine it is the deploy CLI (installed by `install-cli.sh`).
> Running the CLI installer *on a server* would overwrite the recovery command, so it detects that
> case and installs itself as **`harbora-cli`** instead, leaving `harbora doctor` intact.

---

## ▶️ First run

1. Open **`https://<panel-domain>/setup`** and create your **owner** account (that's you, the provider).
2. You're in. The dashboard shows apps, deployments and host resources.

### Deploy your first app (60-second smoke test)

**Apps → New App** → source *Prebuilt image* → image `nginx:alpine`, port `80`, size `nano`, domain
`test.<root-domain>` → **Save** → **Deploy**. Watch the live logs; then open the domain — nginx, with a
valid cert. You can also deploy from a **Git repo / Dockerfile / docker-compose / static site / template**.

A detailed, copy-paste walkthrough (including adding worker nodes) is in **[deploy/RUNBOOK.md](deploy/RUNBOOK.md)**.

---

## 🏢 Run it as a PaaS for customers

Harbora is multi-tenant. As the provider you:

1. **Plans** — define tiers (max apps/services, CPU/RAM/disk caps, allowed instance sizes, price).
   Built-ins are seeded: *Provider* (unlimited, yours), *Starter*, *Pro*.
2. **Tenants** (provider console) — create a customer **workspace**, assign a plan, **invite a user**
   (email + temp password + workspace role). Suspend/resume anytime.
3. The customer logs in and sees **only their workspace**. Their apps are **quota-checked**, the
   **scheduler** places them on a node with capacity (never overcommitting your servers), each tenant
   runs on its **own isolated docker network**, and usage is **metered** (GB-hours / vCPU-hours) as a
   billing basis — all visible to you per tenant.

Instance sizes (nano → large) map to real CPU/memory limits, so customers only consume what their plan allows.

---

## 🖥 CLI — one-command install

Install the `harbora` CLI (self-contained binary, **no .NET runtime needed**):

**Linux / macOS** — in a terminal:

```bash
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.sh | bash
```

**Windows — PowerShell:**

```powershell
irm https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.ps1 | iex
```

**Windows — cmd.exe** (either one):

```bat
:: invoke the PowerShell installer from cmd:
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.ps1 | iex"

:: …or just download the exe directly (Windows 10+ ships curl):
curl -L -o "%USERPROFILE%\harbora.exe" https://github.com/sadrazkh/Harbora/releases/latest/download/harbora-win-x64.exe
```

> These download the right binary for your OS/arch (x64/arm64) from the latest GitHub release and put
> `harbora` on your PATH.

**Alternative (any OS, if you have the .NET SDK)** — install as a global tool:

```bash
dotnet tool install --global Harbora.Cli
```

### Push code straight from your machine (no Git needed)

If your project isn't in a Git repository the server can reach — a private machine, a work laptop, a
folder you haven't pushed anywhere — create the app once and send the code yourself:

```bash
# 1. In the panel: New App → source "Push from my machine" → Save
# 2. On your machine, once — your panel account is enough, no token to create first:
harbora login --email you@example.com --server https://panel.example.com

# 3. From the project folder:
harbora deploy            # asks which app, then remembers it in harbora.yml
```

The folder is packed, uploaded, and built **on the server** — the same build path a Git deploy uses,
so stack auto-detection, zero-downtime cutover and rollback all behave identically.

`harbora deploy` pushes automatically when the folder has no `.git`. Force it either way:

| Flag | Effect |
|---|---|
| `--push` | Always upload this folder, even in a Git repo |
| `--path <dir>` | Push a different folder than the current one |
| `--ref <branch>` / `--tag <v1.0>` | Deploy from Git instead (never uploads) |

**What is not uploaded.** `.dockerignore` is honoured first (it is what the build actually reads),
then `.gitignore`. On top of that these are always skipped, even with no ignore file:

`.git` · `node_modules` · `vendor` · `bin` · `obj` · `dist` · `build` · `.next` · `.venv` ·
`__pycache__` · `.idea` · `.vs` · `.vscode` · **`.env`** · `.env.local`

`.env` is excluded deliberately: it usually holds local database URLs and API keys. Set production
values in the app's **Environment Variables**, not by uploading a file.


### Deploy directly

```bash
harbora login                                  # asks: email + password, or an API token
harbora login --token hbr_cli_xxx --server https://panel.example.com   # non-interactive (CI)

# Signed in to more than one panel? They all stay signed in:
harbora accounts                               # list them; deploy asks which to use

# Keep the CLI current — it also tells you when it is behind the panel:
harbora update

# In ANY project folder, scaffold the config in one command (slug = folder name):
harbora init                                   # writes ./harbora.yml (uses the folder name; detects Dockerfile)

harbora deploy                                 # deploys this project (reads ./harbora.yml) and follows live logs
# …or without a config file:
harbora deploy my-app --ref main
harbora deploy my-app --tag v1.0.0             # deploy a specific tag
harbora apps
harbora logs <deploymentId>
harbora status
```

`harbora init` creates a ready-to-edit `harbora.yml`, so `harbora deploy` needs no arguments — the same
file also drives CI. To reuse a different name: `harbora init --app my-name`.

> First release not tagged yet? Build from source once: `dotnet publish src/Harbora.Cli -c Release`
> (output in `src/Harbora.Cli/bin/Release/net10.0/publish/harbora`), or `dotnet run --project src/Harbora.Cli -- <args>`.

---

## 🧩 Add helper servers (multi-server)

On each worker VPS:

```bash
git clone https://github.com/sadrazkh/Harbora /opt/harbora/app && cd /opt/harbora/app/deploy
docker build -f Dockerfile.agent -t harbora/agent:latest ..
export HARBORA_AGENT_TOKEN=$(openssl rand -hex 24); echo "$HARBORA_AGENT_TOKEN"
docker compose -f agent.compose.yml up -d
```

Then **Servers → Add a server** → `http://<worker-ip>:9700` + that token. New apps auto-schedule onto
whichever node has room. Optional **mTLS** (client certificate) hardens the panel↔agent link.

---

## ✨ Features

- **Deploy** from a Git repo (with a Dockerfile **or none** — automatic buildpacks detect Node / .NET / Go / PHP / Python / static and generate the build), a Dockerfile, a **Docker Compose stack**, a static site (Git repo served by Nginx), a prebuilt image, a CLI upload, or a one-click **template**. Templates can provision complete multi-service projects (for example WordPress + MariaDB or Redis Commander + Redis), private networking, generated secrets, reference variables and persistent volumes. Compose imports use the same private-network, health-check and safe cutover pipeline as ordinary apps.
- **Git integration**: connect GitHub/GitLab/Gitea by token **or OAuth**; deploy-on-push/tag via
  HMAC-verified webhooks; commit metadata, deploy history, rollback.
- **Visual routing designer**: drag-and-drop rules, host/path routing, SSL toggle, HTTP→HTTPS,
  WebSocket, basic-auth, custom headers, live Traefik-config preview, validate + apply with rollback.
- **Managed databases**: PostgreSQL, MySQL, MariaDB, Redis, MongoDB — provisioned with encrypted
  credentials, safe connection info, one-click attach to an app.
- **Backups**: app config, volume/database, full platform; local + S3-compatible; scheduled; retention;
  a copy of every backup can also be **sent to Telegram or email** (Backups → *Send backups to*) — the
  panel finds your Telegram chat id for you, and refuses an artifact too large for the channel instead
  of failing silently;
  download; restore (with a typed confirm).
- **Monitoring + alerts**: host/container metrics, live CPU chart, app health, disk/backup/crash
  warnings; notify via email / Telegram / Discord / custom webhook.
- **Multi-tenant PaaS**: plans, instance sizes, quotas, capacity-aware scheduler, provider console,
  per-tenant network isolation, usage metering.
- **UI/UX**: premium Tailwind dashboard, dark mode, RTL/LTR, PWA (installable + offline shell), bilingual.
- **Security**: first-run setup, PBKDF2 hashing, RBAC, API/CLI tokens (hashed), AES-GCM secrets at rest,
  CSRF, secure cookies, webhook HMAC, audit log, secret redaction in logs.

## 🏛 Architecture

Clean, modular .NET solution (`Harbora.slnx`):

```
Domain → Application (ports) → Infrastructure / Data → Web
                                             + Agent (remote nodes) · Cli · Shared
```

- **Reverse proxy: Traefik** — hot-reloads dynamic config (no restart), built-in Let's Encrypt, discovers
  containers by label. The visual designer emits routes → Harbora renders/validates/applies Traefik config.
- **CSS: Tailwind** — original, premium look (not a stock admin template), first-class dark mode + RTL/LTR.
- **Frontend: Vue islands via Vite** — compiled into `wwwroot/build`; Razor hydrates only interactive
  nodes. **No separate SPA server.**
- **Containers: Docker.DotNet** — one `IDockerEngine` seam (local in-process, or a remote agent over HTTP);
  no shell-string commands.
- **Live logs: SignalR.** **Jobs: background worker + Redis.** **DB: PostgreSQL + EF Core.**

## 🛠 Local development

Prereqs: **.NET 10 SDK**, **Node 22**, **PostgreSQL** (Docker easiest).

```bash
docker run -d --name harbora-pg -e POSTGRES_USER=harbora -e POSTGRES_PASSWORD=harbora \
  -e POSTGRES_DB=harbora -p 5432:5432 postgres:16-alpine

cd src/Harbora.Web && npm install && npm run build && cd ../..   # build the Vue islands + Tailwind
dotnet run --project src/Harbora.Web                             # auto-migrates + seeds → /setup
```

## 🔧 Troubleshooting

**Start with `harbora doctor`** — it checks everything in the table below and names the fix.

| Symptom | Cause | Fix |
|---|---|---|
| **Nothing comes up after an update** — panel container restarts in a loop | `deploy/.env` was created by an older installer and has no `HARBORA_MASTER_KEY`. The panel now *refuses to start* without one rather than leave every stored secret trivially decryptable | `harbora doctor` names it; `harbora fix-key` generates one and restarts. Installer `update` now backfills it automatically |
| **Can't sign in** / password forgotten / owner account lost | — | `harbora reset-password` (owner), or `harbora make-owner --email you@example.com`. Both work while the panel is down |
| **Wrong domain** after moving servers or changing DNS | `PANEL_DOMAIN`/`ROOT_DOMAIN` still point at the old name | `harbora set-domain panel.example.com apps.example.com` |
| Not sure what's configured | — | `harbora info` (as the app sees it) or `harbora env` (raw, secrets hidden) |
| Panel returns **404** (Traefik default page) | Traefik didn't read the panel container's labels — usually the Docker-API error below, or Traefik started before the panel | `docker compose logs traefik \| tail -50` → then `docker compose restart traefik` |
| Traefik log: **`client version 1.24 is too old. Minimum supported API version is 1.40`** | Old Traefik (≤ v3.2) with new Docker Engine (27+/29) | This repo pins **traefik:v3.6** (compatible). Update: `docker compose pull traefik && docker compose up -d traefik` |
| **No SSL certificate** / browser warning persists | DNS doesn't point at the server, or port **80** isn't reachable (HTTP-01 challenge needs it) | Check DNS: `getent hosts panel.your-domain` must return the server IP. Open port 80. ACME log: `docker logs harbora-traefik 2>&1 \| grep -i acme \| tail -20` |
| **DNS wrong** — installer warned during setup | A/wildcard records missing or pointing elsewhere | Add `A panel.example.com → server IP` and `A *.apps.example.com → server IP`, wait for propagation, re-run the installer (`update`) |
| **Ports 80/443 already in use** | Another web server (nginx/apache) on the host | `systemctl stop nginx && systemctl disable nginx` (or apache2), then `docker compose up -d` |
| **ARM64 server** | — | Fully supported: the installer detects `aarch64/arm64`; Traefik/Postgres/Redis/.NET images are all multi-arch. First build is just slower |
| Panel container **exits on boot** | DB not ready or bad `.env` | `docker compose logs panel` — it retries the DB; check `POSTGRES_*` values in `.env` |
| Want a clean re-install | — | `docker compose down -v` (**destroys data**), remove `deploy/.env`, re-run installer |

## ⚠️ Known limitations

- Multi-server routes cross-node via published host ports (no shared overlay), so an app and the managed
  services it attaches to should live on the same node.
- Git OAuth requires registering an OAuth app (client id/secret); token connection needs no setup.
- Usage metering records the billing *basis* (GB-hours / vCPU-hours from committed size); there's no
  invoicing/payment engine yet.
- Health checks HTTP-probe the app's health path (falling back to container-liveness when none is set).

## License

TBD.
