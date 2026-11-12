Last verified: 2026-11-08
Scope: `README.md`, `deploy/RUNBOOK.md`, `deploy/install.sh`, `deploy/docker-compose.yml`, `Dockerfile`, `.github/workflows/ci.yml`, `src/Harbora.Web/appsettings.json` at commit `b3fd974`. The commands were read, not run, for this baseline.

# Environment and setup

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md). Unless marked otherwise, this document is **[Verified]** against the files above. `deploy/RUNBOOK.md` is the fuller operator guide.

## Local development

Prerequisites: .NET 10 SDK, Node 22, PostgreSQL 16. Docker is needed only to deploy from a local panel and to run the container-backed tests.

```bash
docker run -d --name harbora-pg -e POSTGRES_USER=harbora -e POSTGRES_PASSWORD=harbora \
  -e POSTGRES_DB=harbora -p 5432:5432 postgres:16-alpine

cd src/Harbora.Web && npm ci && npm run build && cd ../..
dotnet run --project src/Harbora.Web
```

The panel migrates and seeds on start, then sends you to `/setup`. The default connection string is `Host=localhost;Port=5432;Database=harbora;Username=harbora;Password=harbora` (`Program.cs:39-40`).

In Development a missing master key falls back to a known insecure value, with a warning. If `ASPNETCORE_ENVIRONMENT` is unset, the code treats the process as Production and refuses to start without a real key (`DependencyInjection.cs:90-93`, `MasterKeyResolver`).

## Build and test

CI is the reference (`.github/workflows/ci.yml`).

| Step | Command |
|---|---|
| Restore | `dotnet restore Harbora.slnx` |
| Build | `dotnet build Harbora.slnx -c Release --no-restore` |
| Main suite | `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj -c Release --no-build` |
| Node agent | `dotnet test tests/Harbora.NodeAgent.Tests/Harbora.NodeAgent.Tests.csproj -c Release --no-build` |
| Ingress | `dotnet test tests/Harbora.NodeIngress.Tests/Harbora.NodeIngress.Tests.csproj -c Release --no-build` |
| Postgres lane (needs Docker) | `dotnet test tests/Harbora.Postgres.Tests/Harbora.Postgres.Tests.csproj -c Release` |
| Front-end tests | `npm ci` in `src/Harbora.Web`, then, from the repo root, `node --test src/Harbora.Web/Scripts/tests/*.test.mjs` |
| Front-end build | `npm run build` in `src/Harbora.Web` |
| Real launch proof | `deploy/launch-proof.sh` on a throwaway Linux host, or the `Launch proof` workflow |

There is no `npm test` script. Do not run a front-end build at the same time as a .NET build or test; see [AI_WORKING_GUIDELINES.md](AI_WORKING_GUIDELINES.md).

[Assumption] Some machines have a private NuGet feed in their user-level configuration. The repo has no `nuget.config`. If restore fails with NU1301, `dotnet restore Harbora.slnx --ignore-failed-sources` got past it on the development machine.

## Production install

On a Linux server as root (Ubuntu, Debian, Fedora or Alpine; 2 GB or more of RAM; ports 80 and 443 open):

```bash
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install.sh | sudo bash
```

The installer is idempotent, never overwrites an existing `deploy/.env`, and builds the panel from source into `/opt/harbora/app`. With no domain it falls back to `nip.io`.

| DNS record | For |
|---|---|
| `panel.example.com` | The panel |
| `*.apps.example.com` | Every app's default address |
| `s3.example.com` | Object storage |
| `nodes.panel.example.com` | Node channel, only if you add nodes |

## Updating

```bash
sudo bash /opt/harbora/app/deploy/install.sh update
```

- It must run as root. On a host where the login user is not in the `docker` group, every command needs `sudo`.
- It updates with `git reset --hard`, so untracked files survive.
- After an update, compare the panel image's build time with the commit time; an image older than the commit means the build did not happen:

```bash
docker image inspect "$(docker inspect harbora-panel --format '{{.Image}}')" --format '{{.Created}}'
```

- Rolling back the code: tag the current image before updating (`harbora/panel:before-<sha>`), then point `harbora/panel:latest` back at it. Migrations are forward-only, so going back across a migration also needs the restore point.

## Configuration

Set in `deploy/.env` and passed by `deploy/docker-compose.yml`. Lose none of these.

| Variable | Default | Meaning |
|---|---|---|
| `PANEL_DOMAIN` | none | The panel's host name |
| `ROOT_DOMAIN` | none | Parent of every app's default domain |
| `ACME_EMAIL` | none | Let's Encrypt contact |
| `POSTGRES_USER`, `POSTGRES_DB`, `POSTGRES_PASSWORD` | none | The platform's own database |
| `HARBORA_MASTER_KEY` | none | Encrypts every stored secret. Keep a copy off the server; changing it makes them unreadable |
| `S3_DOMAIN` | none | Public object-storage host, which presigned URLs are signed for |
| `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD` | none | Object-store administration |
| `BILLING_ENABLED` | `true` | Hourly billing |
| `BILLING_CURRENCY` | `IRR` | Fixed per install |
| `BILLING_MAX_BACKFILL_HOURS` | `72` | See KI-002 |
| `Jobs__MaxConcurrency` | `min(4, cores)` | Parallel background jobs |
| `Runtime__ImageRetentionCount` | `5` | Rollback targets kept per app; 1 removes instant rollback, 0 disables pruning |
| `Runtime__DanglingImageSweepHours` | `24` | How often untagged images are pruned; 0 is off |
| `NodeAgent__PublicUrl` | empty | Address handed to nodes |
| `NodeAgent__TrustForwardedClientCertificate` | `false` | Only once the node router is on disk |
| `ACME_CA_SERVER`, `ACME_CERT_RESOLVER` | Let's Encrypt production, `letsencrypt` | Certificate issuer |
| `TRUSTED_PROXY_NETWORKS`, `TRUSTED_PROXY_HOPS`, `FORWARDED_CLIENT_IP_DEPTH` | private and Cloudflare ranges, `2`, `0` | Real client IP handling |

Feature switches in `Features:` default to false: `Terminal`, `Backup`, `Sync`. Enabling `Backup` or `Sync` needs `deploy/backup-sync.compose.yml`. Assistant and Functions are entitlements under **Platform → Features**, not config.

## Recovering a server

`harbora` on the server (`deploy/harbora`) works while the panel is down.

| Command | Purpose |
|---|---|
| `harbora doctor` | Start here: config, containers, ports, recent errors |
| `harbora reset-password`, `make-owner`, `unlock` | Get back in |
| `harbora backups`, `backup-db`, `restore-db <file>` | Database restore points |
| `harbora fix-key` | Only when no usable master key exists |
| `harbora set-domain <panel> <apps>` | Change domains |

Do not run the developer CLI installer on a server without reading the detection note in `README.md`: the same name `harbora` is used by both tools.

## Developer CLI

```bash
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.sh | bash
```

Windows uses `deploy/install-cli.ps1`. Building from source does not change the installed binary, so a fix in `src/Harbora.Cli` is invisible until the binary is republished or replaced.

## CI secrets

[Verified] `nightly-live-host.yml` needs `E2E_SSH_HOST`, `E2E_SSH_USER`, `E2E_SSH_KEY`, `E2E_NODE_HOST`, `E2E_NODE_SSH_KEY`, `E2E_PANEL_DOMAIN`, `E2E_ROOT_DOMAIN`, `E2E_ACME_EMAIL`, `E2E_OWNER_EMAIL` and `E2E_OWNER_PASSWORD`, and two disposable hosts.
