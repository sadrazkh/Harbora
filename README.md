# Harbora

A self-hosted deployment platform — deploy and manage all your apps from a single, bilingual (فارسی/English, RTL/LTR) web UI. Think CapRover/Coolify in spirit, but with its own identity, a clean modular architecture, and a strong focus on UX and simplicity.

> **Status:** Phase 1 (foundation) + Phase 2 (visual route designer) + Phase 3 (managed services). Done: solution architecture, data model, single-server deploy engine (Git / Dockerfile / prebuilt image), Traefik routing + Let's Encrypt, live logs, first-run setup, auth, CLI, PWA, the **drag-and-drop routing designer** (visual route map, live Traefik-config preview, validate, save-and-apply with rollback), and **managed services** (Postgres / MySQL / MariaDB / Redis / MongoDB — provision, generated encrypted credentials, safe connection info, attach-to-app). Backups and monitoring dashboards have their **data model and engine seams in place** and land next.

---

## Why these choices

| Decision | Choice | Reason |
|---|---|---|
| Reverse proxy | **Traefik** | Routes change constantly on a deploy platform. Traefik hot-reloads a dynamic-config file with **no restart**, ships **built-in ACME/Let's Encrypt**, and discovers containers by label. Nginx would need full config regeneration + reload + a separate certbot. The visual designer emits `Route`s → Harbora renders/validates/applies Traefik config; you never hand-edit anything. |
| CSS | **Tailwind** | You wanted a premium, original UI that doesn't look like a stock admin template. Tailwind + a thin Vue layer gives full design control, first-class dark mode, and clean RTL/LTR via logical utilities. |
| Frontend | **Vue islands via Vite** | No separate SPA server. Vite compiles Vue components into `wwwroot/build`; Razor references the hashed assets through a manifest and hydrates only interactive nodes — like versioned jQuery plugins, but reactive. |
| Container access | **Docker.DotNet** | One `IDockerEngine` seam; no shell-string command building anywhere (removes a class of injection risks). |
| Live logs | **SignalR** | Build/deploy/container logs stream to the UI and CLI from one pipeline. |
| Secrets at rest | **AES-256-GCM** | Master key from `HARBORA_MASTER_KEY` (installer-generated), kept out of the DB. |

## Architecture (Clean, modular)

```
Harbora.Domain          entities, enums, domain rules (no dependencies)
Harbora.Application     use-cases + port interfaces (IDockerEngine, IProxyEngine, IGitService, IDeploymentEngine…)
Harbora.Infrastructure  adapters: Docker.DotNet, LibGit2Sharp, Traefik engine, AES-GCM, PBKDF2, job queue, deploy pipeline
Harbora.Data            EF Core + PostgreSQL, migrations
Harbora.Web             ASP.NET Core MVC/Razor + embedded Vue islands + SignalR + JSON API
Harbora.Agent           server-ops seam (in-process for the single-server MVP; ready for a remote agent)
Harbora.Cli             `harbora` CLI (System-friendly, Spectre.Console)
Harbora.Shared          cross-cutting contracts
```

Deployment logic lives in the **Application/Infrastructure deploy engine** and runs on a background worker — never in controllers.

## One-command install (Linux VPS)

```bash
curl -fsSL https://get.harbora.dev/install.sh | bash
```

The installer checks the OS, installs Docker if missing, generates secrets into `/opt/harbora/.env` (mode 600), writes the Traefik + Postgres + Redis + panel stack, and starts it. It's safe to re-run.

```bash
# update to the latest images
curl -fsSL https://get.harbora.dev/install.sh | bash -s -- update
# stop & remove (prompts before deleting data volumes)
curl -fsSL https://get.harbora.dev/install.sh | bash -s -- uninstall
```

After install, open **`https://<panel-domain>/setup`** to create your owner account.

## Local development

Prerequisites: **.NET 10 SDK**, **Node 22**, and a **PostgreSQL** (Docker is easiest).

```bash
# 1) Postgres
docker run -d --name harbora-pg -e POSTGRES_USER=harbora -e POSTGRES_PASSWORD=harbora \
  -e POSTGRES_DB=harbora -p 5432:5432 postgres:16-alpine

# 2) Frontend (Vue islands + Tailwind → wwwroot/build)
cd src/Harbora.Web
npm install
npm run build          # or: npm run dev  (then set Vite:UseDevServer=true in appsettings)

# 3) Run the panel (auto-migrates + seeds templates on boot)
cd ../..
dotnet run --project src/Harbora.Web
```

Open `http://localhost:5000` (or the shown port) → you'll be redirected to `/setup`.

## CLI

```bash
# build a single-file binary (or `dotnet run --project src/Harbora.Cli -- <args>`)
dotnet publish src/Harbora.Cli -c Release

harbora login --server https://panel.example.com --token hbr_cli_xxx   # token from Settings → API Tokens
harbora whoami
harbora apps
harbora deploy my-app --ref main       # deploys and follows the live logs
harbora deploy my-app --tag v1.0.0
harbora logs <deploymentId>
harbora status
```

Drop an `examples/harbora.yml` (`app: my-app`) in your repo so `harbora deploy` needs no arguments in CI.

## Security

First-run setup, PBKDF2 password hashing (210k iterations), RBAC roles, API/CLI tokens (only SHA-256 hashes stored), AES-GCM secret encryption at rest, CSRF on all MVC forms, secure cookies, webhook HMAC secrets per repo, secret redaction in logs, and an audit-log table. Docker access is fully typed — no shell command strings.

## Known limitations (Phase 1–2)

- **Single server** only (the remote-agent transport is designed but not wired).
- **Backups and monitoring charts** render as placeholders backed by a ready data model/engine — full UIs come next.
- Managed-service data volumes are preserved on remove (volume deletion is wired with the backup engine in Phase 4 to avoid accidental data loss); service backup/restore also lands in Phase 4.
- Route basic-auth: the toggle persists, but htpasswd credential injection at apply-time is the next refinement.
- Health checks are container-liveness based (HTTP health probing is the next refinement).
- The frontend must be built (`npm run build`) before publishing; the Docker image does this automatically.

## License

TBD.
