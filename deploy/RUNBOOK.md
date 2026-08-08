# Harbora — live end-to-end run on a real VPS

This walks a fresh Linux VPS from zero to a working, multi-tenant Harbora, building the panel
image from source (no image registry required for the first run).

> **The short way is `deploy/install.sh`.** It asks the same questions, generates the same secrets,
> and — importantly — writes *every* setting the stack has grown since, including ones added after
> your install. This runbook is the long way, for when you want to see each step. If you follow it
> by hand, §3 is the part that has to be complete, because nothing backfills a `.env` you wrote
> yourself.

## 0) Prerequisites

- A clean **Ubuntu 22.04/24.04** (or Debian) VPS with a public IP, **2 GB+ RAM**, ports **80 & 443** open.
- DNS records pointing at the VPS IP  ·  *(use your own domains)*:
  - `panel.example.com` → VPS IP (the dashboard)
  - `*.apps.example.com` → VPS IP (wildcard for deployed apps)
  - `s3.example.com` → VPS IP (object storage — the S3 API is published through Traefik)
  - `nodes.panel.example.com` → VPS IP — **only if you will add worker nodes** (§8). This is the
    mutual-TLS channel nodes dial. It is a separate host name on purpose: two routers on one name
    with different TLS options make Traefik fall back to options that ask for no client
    certificate, and the node then never comes online.
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

Every variable `docker-compose.yml` reads is here. Leave one out and Compose substitutes an empty
string, which is how a stack starts and then behaves strangely rather than failing.

```bash
cat > .env <<EOF
PANEL_DOMAIN=panel.example.com
ROOT_DOMAIN=apps.example.com
ACME_EMAIL=you@example.com
POSTGRES_USER=harbora
POSTGRES_DB=harbora
POSTGRES_PASSWORD=$(openssl rand -hex 24)
HARBORA_MASTER_KEY=$(openssl rand -base64 32)
S3_DOMAIN=s3.example.com
MINIO_ROOT_USER=harbora
MINIO_ROOT_PASSWORD=$(openssl rand -hex 24)
EOF
chmod 600 .env
```

| Variable | What it is |
|---|---|
| `PANEL_DOMAIN` | Where the dashboard answers. Traefik routes it and takes a certificate for it |
| `ROOT_DOMAIN` | The parent of every deployed app's default domain (`myapp.apps.example.com`) |
| `ACME_EMAIL` | The address Let's Encrypt sends expiry warnings to |
| `POSTGRES_USER` · `POSTGRES_DB` · `POSTGRES_PASSWORD` | The platform's own database. The panel's connection string is built from all three |
| `HARBORA_MASTER_KEY` | Encrypts every secret at rest — env vars, git tokens, node CA, agent tokens. **Keep a copy somewhere other than this server.** Lose it and the encrypted values are gone; the panel refuses to start without it rather than leaving them decryptable |
| `S3_DOMAIN` | The public host name of the object store. Pre-signed URLs are signed *for this name*, so a customer's client and the panel must agree on it |
| `MINIO_ROOT_USER` · `MINIO_ROOT_PASSWORD` | The object store's administrative credentials. The panel uses them to create buckets and per-bucket keys; nothing hands them to a customer |

Two more that Compose reads and `install.sh` fills in when you add nodes. Both have defaults, so a
`.env` without them starts — it just cannot enrol a node:

| Variable | Default | What it is |
|---|---|---|
| `NodeAgent__PublicUrl` | empty | `https://nodes.panel.example.com` — the address a node is handed at enrollment and keeps calling. Empty means an enrolled node stores an empty control-plane URL, reports success, and never opens a channel |
| `NodeAgent__TrustForwardedClientCertificate` | `false` | Lets the panel believe the client certificate Traefik forwards. Only safe once `traefik/dynamic/node-agent.yml` is on disk, because that router requires a client certificate and therefore always overwrites the header |

And one you should not normally touch:

| Variable | Default | What it is |
|---|---|---|
| `ACME_CA_SERVER` | `https://acme-v02.api.letsencrypt.org/directory` | Which ACME directory issues certificates. The default is Let's Encrypt production and is exactly what Traefik does when the setting is absent. It exists for the automated live-host proof, which would otherwise burn the duplicate-certificate rate limit every run. **A staging certificate is not trusted by browsers** |

> Changing `HARBORA_MASTER_KEY` later makes every stored secret permanently unreadable. `harbora
> fix-key` only replaces an existing key if you type `REPLACE` when it asks.

## 4) Build & start

```bash
docker compose up -d --build      # builds frontend + .NET panel image, starts the stack
docker compose ps                 # postgres healthy, traefik/panel/minio running
docker compose logs -f panel      # watch it migrate + seed, then "Application started"
```

> The stack also starts a `redis` container. Nothing in Harbora connects to it today — the queue
> lives in PostgreSQL. It is left in the file rather than removed in an upgrade that would have to
> reason about existing installs.

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

Apps live in an **environment**, which belongs to a **project** — that is the boundary of the private
network the app and its databases share. You do not have to create one first: leave the selector
alone and Harbora makes a default project and environment on the spot.

1. **Apps → New App** → source *Prebuilt image* → image `nginx:alpine`, port `80`,
   domain `test.apps.example.com`, size `nano`. Leave **Project environment** as it is → **Save**.
2. On the app page → **Deploy**. Watch live logs; it should pull, run, health-check, wire Traefik.
3. Open `https://test.apps.example.com` → the nginx welcome page (with a valid cert).
4. **Networks** shows the environment the app landed in and the internal address anything else in it
   can reach it at. Attach a managed database from **Databases → New** into that same environment
   and the connection is injected for you.

## 7) Offer it to a customer (multi-tenant)

1. **Plans** → confirm/create a plan (e.g. Starter). **Tenants → New tenant** → assign the plan.
2. Open the tenant → **Add a user** (their email + temp password + role *Workspace admin*).
3. The customer logs in at `https://panel.example.com` and sees only their workspace; their apps
   are quota-limited, network-isolated, and metered (visible back on the tenant page).

## 8) Add a helper node (optional, multi-server)

Use **Node Agent v1**. It opens no inbound port — the node dials the panel — so it needs no firewall
rule, no public IP and no port-forward, and it works behind NAT.

The panel needs its mTLS channel configured before a node can enrol. If you installed with
`install.sh` it is already done; by hand it is:

```bash
# On the panel host, from /opt/harbora/app/deploy:
harbora node-ca \
  | sed -n '/-----BEGIN CERTIFICATE-----/,/-----END CERTIFICATE-----/p' \
  > traefik/dynamic/node-ca.pem          # the CA the router verifies node certificates against
```

then set `NodeAgent__PublicUrl=https://nodes.panel.example.com` and
`NodeAgent__TrustForwardedClientCertificate=true` in `.env` and `docker compose up -d panel`.
Re-running `install.sh update` does all of this for you and checks it afterwards.

Then, in the panel: **Nodes → Add a node** → copy the command. On the **worker VPS**, as root:

```bash
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/node-agent/install.sh \
  | bash -s -- --control-plane https://panel.example.com --token hbr_enroll_xxx --name web-01
```

The enrollment token is single-use and short-lived. The node registers, gets its own certificate,
and the ongoing link is mTLS against a CA the panel owns. It becomes a scheduling target
automatically. Full guide: **[../docs/node-agent/installation.md](../docs/node-agent/installation.md)**.

If the node is on a home connection or otherwise unreachable, open it in **Nodes** and press
**"Serve through the ingress tunnel"** — otherwise routes to it resolve and time out.

> **What a helper node cannot do yet.** A volume or managed database on any server other than the
> panel's own host cannot be backed up or restored. Harbora refuses before it starts. See
> [../docs/disaster-recovery.md](../docs/disaster-recovery.md).

> **The older HTTP agent (`Servers → Add a server`, port 9700) is deprecated** as of v0.2.0. It
> still works and stays supported for at least two more minor versions, but it is not how to add a
> node any more and this runbook no longer teaches it. Existing servers on it keep working; the
> install steps are in the README under *The older HTTP agent*.

## Updating

```bash
cd /opt/harbora/app && git pull && cd deploy && docker compose up -d --build
```

Or `curl -fsSL …/deploy/install.sh | bash -s -- update`, which additionally backfills any `.env`
setting a newer version needs and your file predates.

Before applying any schema migration the panel dumps the database and **refuses to migrate if the
dump fails**. `harbora backups` lists the restore points; the automatic ones are named
`pre-upgrade-*`.

### What changes for you in this release

Four behaviour changes that need no action from you but will look like faults if nobody said so.

**1. Backups of anything on a second server now fail, loudly.** If you run any server other than the
panel's own host — a v1 node or a legacy agent — its volumes and managed databases can no longer be
backed up. You will see the first failure on that schedule's next tick.

This is a fix. Those backups were *already* not working: the helper container wrote a correct
archive onto the other machine's disk, the panel then looked for it on its own disk and did not find
it, and each scheduled run left another orphan behind. What is new is that Harbora now refuses in
front of the work and says which server and why, instead of failing afterwards with a message about
a missing staging volume on a machine where everything looks fine. **Nothing is read, nothing is
written, nothing is left behind.**

There is no workaround inside Harbora. Until the artifact transport lands, back those volumes up on
the machine they live on, or keep the data on the panel host.

**2. Deployments will start failing that used to report success.** A deployment whose Traefik
configuration does not apply is now `Failed` and raises a `DeployFailed` alert; it used to log one
warning line and report `Succeeded` while traffic still pointed at the old container.

Expect a spike of failures on the first deploys after upgrading, and read them as pre-existing
routing breakage that has become visible — not as something the upgrade broke. The alert names the
app; fix its routes and redeploy.

**3. Builds now run several at a time.** `Jobs:MaxConcurrency` defaults to `min(4, ProcessorCount)`,
so a 4-core panel can run four builds at once on its own host. Deployments of the *same* app stay
serial, and a backup no longer waits behind somebody else's build. If your panel is sized for one
build at a time, set `Jobs__MaxConcurrency=1` in `.env` and restart — that restores the old serial
worker exactly, with no redeploy of anything.

**4. A node needs a second DNS record.** `nodes.<panel domain>` — see §0. Only if you use nodes, and
only on a real domain; a `nip.io` install needs no new record. No new port is opened. `install.sh
update` writes the router and the settings; a hand-built install needs §8.

## Troubleshooting

- `docker compose logs panel` — startup and migration errors. Most often the database is not
  accepting connections yet at boot; the panel retries.
- No cert / 404 on the app domain — check DNS resolves to the VPS and ports 80/443 are open
  (`docker compose logs traefik`).
- App shows *Failed* health check — the app must listen on the **Container Port** you set and
  return `<400` on its health path.
- A node enrols but never comes online — the mTLS channel in §8. `harbora doctor` names it.
- **Recovering from a lost panel, a lost app or a lost node:**
  [../docs/disaster-recovery.md](../docs/disaster-recovery.md).
- Reset everything (destroys data): `docker compose down -v`.
