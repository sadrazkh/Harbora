# Harbora — live end-to-end run on a real VPS

This walks a fresh Linux VPS from zero to a working, multi-tenant Harbora, building the panel
image from source (no image registry required for the first run).

## 0) Prerequisites

- A clean **Ubuntu 22.04/24.04** (or Debian) VPS with a public IP, **2 GB+ RAM**, ports **80 & 443** open.
- Two DNS records pointing at the VPS IP:
  - `panel.example.com` → VPS IP (the dashboard)
  - `*.apps.example.com` → VPS IP (wildcard for deployed apps)  ·  *(use your own domains)*
- Root/sudo shell.

## 1) Install Docker

```bash
curl -fsSL https://get.docker.com | sh
docker --version && docker compose version
```

## 2) Get the source

```bash
sudo mkdir -p /opt/harbora && sudo chown "$USER" /opt/harbora
git clone https://github.com/sadrazkh/Harbora /opt/harbora/app
cd /opt/harbora/app/deploy
```

## 3) Configure (.env)

```bash
cat > .env <<EOF
PANEL_DOMAIN=panel.example.com
ROOT_DOMAIN=apps.example.com
ACME_EMAIL=you@example.com
POSTGRES_USER=harbora
POSTGRES_DB=harbora
POSTGRES_PASSWORD=$(openssl rand -hex 24)
HARBORA_MASTER_KEY=$(openssl rand -base64 32)
EOF
chmod 600 .env
```

> `HARBORA_MASTER_KEY` encrypts all secrets at rest — keep it safe; changing it makes stored
> secrets unreadable.

## 4) Build & start

```bash
docker compose up -d --build      # builds frontend + .NET panel image, starts the stack
docker compose ps                 # postgres healthy, traefik/panel/redis running
docker compose logs -f panel      # watch it migrate + seed, then "Application started"
```

Verify the panel is alive (from the VPS):

```bash
curl -s http://localhost:8080/healthz            # {"status":"ok"} — via the panel container
# or through Traefik once DNS/TLS is ready:
curl -sk https://panel.example.com/healthz
```

## 5) First-run setup

Open **https://panel.example.com/setup** → create the owner (this is *you*, the provider).
Traefik requests a Let's Encrypt cert automatically on first HTTPS hit (allow ~30s).

## 6) Smoke-test a deploy

1. **Apps → New App** → source *Prebuilt image* → image `nginx:alpine`, port `80`,
   domain `test.apps.example.com`, size `nano` → **Save**.
2. On the app page → **Deploy**. Watch live logs; it should pull, run, health-check, wire Traefik.
3. Open `https://test.apps.example.com` → the nginx welcome page (with a valid cert).

## 7) Offer it to a customer (multi-tenant)

1. **Plans** → confirm/create a plan (e.g. Starter). **Tenants → New tenant** → assign the plan.
2. Open the tenant → **Add a user** (their email + temp password + role *Workspace admin*).
3. The customer logs in at `https://panel.example.com` and sees only their workspace; their apps
   are quota-limited, network-isolated, and metered (visible back on the tenant page).

## 8) Add a helper node (optional, multi-server)

On the **worker VPS** (Docker installed, reachable from the panel):

```bash
git clone https://github.com/sadrazkh/Harbora /opt/harbora/app
cd /opt/harbora/app/deploy
# build the agent image locally:
docker build -f Dockerfile.agent -t harbora/agent:latest ..
export HARBORA_AGENT_TOKEN=$(openssl rand -hex 24); echo "TOKEN=$HARBORA_AGENT_TOKEN"
docker compose -f agent.compose.yml up -d
```

Then in the panel: **Servers → Add a server** → `http://<worker-ip>:9700` + that token → **Add & test**
(should report *online*). New apps are auto-scheduled onto whichever node has capacity.

## Updating

```bash
cd /opt/harbora/app && git pull && cd deploy && docker compose up -d --build
```

## Troubleshooting

- `docker compose logs panel` — startup/migration errors (most often DB connat boot; it retries).
- No cert / 404 on the app domain — check DNS resolves to the VPS and ports 80/443 are open
  (`docker compose logs traefik`).
- App shows *Failed* health check — the app must listen on the **Container Port** you set and
  return `<400` on its health path.
- Reset everything (destroys data): `docker compose down -v`.
