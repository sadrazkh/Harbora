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

That's it. The installer is **fully self-contained** — it:

1. checks OS/arch compatibility,
2. **installs every prerequisite itself** — `curl`, `git`, `openssl`, and **Docker** (with Compose) if missing,
3. fetches the source into `/opt/harbora/app`,
4. generates `/opt/harbora/app/deploy/.env` with **smart defaults** and freshly-random secrets,
5. **builds the platform from source** and starts it (Traefik + PostgreSQL + Redis + the panel),
6. prints your panel URL and next steps.

It is **safe to re-run** — an existing `.env` (your secrets) is never overwritten.

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

TLS certificates are obtained automatically from **Let's Encrypt** on first HTTPS hit.

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

## 🖥 CLI

```bash
dotnet publish src/Harbora.Cli -c Release      # or: dotnet run --project src/Harbora.Cli -- <args>

harbora login --server https://panel.example.com --token hbr_cli_xxx   # token from Settings → API Tokens
harbora apps
harbora deploy my-app --ref main               # deploys and follows live logs
harbora deploy my-app --tag v1.0.0
harbora logs <deploymentId>
harbora status
```

Drop `app: my-app` in a `harbora.yml` at your repo root so `harbora deploy` needs no arguments in CI.

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

- **Deploy** from Git repo, Dockerfile, docker-compose, prebuilt image, static site, or one-click templates.
- **Git integration**: connect GitHub/GitLab/Gitea by token **or OAuth**; deploy-on-push/tag via
  HMAC-verified webhooks; commit metadata, deploy history, rollback.
- **Visual routing designer**: drag-and-drop rules, host/path routing, SSL toggle, HTTP→HTTPS,
  WebSocket, basic-auth, custom headers, live Traefik-config preview, validate + apply with rollback.
- **Managed databases**: PostgreSQL, MySQL, MariaDB, Redis, MongoDB — provisioned with encrypted
  credentials, safe connection info, one-click attach to an app.
- **Backups**: app config, volume/database, full platform; local + S3-compatible; scheduled; retention;
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

## ⚠️ Known limitations

- Multi-server routes cross-node via published host ports (no shared overlay), so an app and the managed
  services it attaches to should live on the same node.
- Git OAuth requires registering an OAuth app (client id/secret); token connection needs no setup.
- Usage metering records the billing *basis* (GB-hours / vCPU-hours from committed size); there's no
  invoicing/payment engine yet.
- Health checks HTTP-probe the app's health path (falling back to container-liveness when none is set).

## License

TBD.
