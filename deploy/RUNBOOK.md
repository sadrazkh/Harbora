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
| `ACME_CERT_RESOLVER` | `letsencrypt` | Resolver used by panel, S3 and generated app routers. Cloudflare mode sets it to `cloudflare` |
| `TRUSTED_PROXY_NETWORKS` | Docker/private + Cloudflare networks | Comma-separated CIDRs whose forwarded headers the panel trusts. Processing stops at the first untrusted peer, so direct traffic cannot add a forged hop |
| `TRUSTED_PROXY_HOPS` | `2` | Maximum trusted hops for Cloudflare -> Traefik -> panel. Direct traffic still unwinds only Traefik because the next peer is not trusted |
| `FORWARDED_CLIENT_IP_DEPTH` | `0` | Forwarded address used by route IP allowlists. Cloudflare mode uses `1`; restrict direct origin access before relying on it |

> Changing `HARBORA_MASTER_KEY` later makes every stored secret permanently unreadable. `harbora
> fix-key` only replaces an existing key if you type `REPLACE` when it asks.

### Cloudflare Proxied mode

An Owner can now configure this from **Platform -> Cloudflare** in the panel: enter the zone and a
zone-scoped token, test it, then enable. The panel encrypts the stored token, sets Full (strict), can
turn the existing Harbora DNS records Proxied, and hot-switches panel/S3/app certificates to DNS-01.
The command below remains the recovery path when the panel itself cannot be reached.

Keep the panel, app wildcard and S3 DNS records **Proxied** (orange cloud). Create a Cloudflare API
token limited to the zones Harbora serves, with `Zone:Read`, `DNS:Edit`, and `Zone Settings:Edit`,
then activate the shipped overlay:

```bash
read -rsp 'Cloudflare API token: ' CF_TOKEN; echo
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install.sh | \
  sudo env CF_DNS_API_TOKEN="$CF_TOKEN" bash -s -- update
unset CF_TOKEN
```

The installer stores the token in the mode-600 `deploy/.env` and sets:

```dotenv
COMPOSE_FILE=docker-compose.yml:cloudflare.compose.yml
ACME_CERT_RESOLVER=cloudflare
TRUSTED_PROXY_HOPS=2
FORWARDED_CLIENT_IP_DEPTH=1
```

`cloudflare.compose.yml` configures Traefik DNS-01, so certificate issue and renewal do not depend
on exposing the origin for HTTP-01. Optional `CF_ZONE_API_TOKEN` supports a separately-scoped zone
lookup token. Override `CLOUDFLARE_TRUSTED_PROXY_NETWORKS` only when Cloudflare changes its published
address list before a Harbora update.

In Cloudflare set **SSL/TLS encryption mode to Full (strict)**. Flexible mode sends HTTP to an origin
that redirects to HTTPS and commonly creates redirect loops. Once working, restrict origin ports
80/443 to Cloudflare's published IP ranges (plus your explicit administration path). That restriction
also prevents a direct caller from spoofing forwarded visitor IPs used by rate limits and route IP
allowlists.

> **Node-channel exception:** if worker nodes are enabled, `NODE_DOMAIN` (normally
> `nodes.panel.example.com`) must remain **DNS-only / grey cloud**. The ordinary Cloudflare proxy
> terminates TLS and cannot pass the node's client certificate to Harbora's mTLS router. Panel, apps
> and S3 remain Proxied. If no worker nodes are used, do not publish this record.

Troubleshooting:

```bash
docker compose config
docker compose logs traefik | grep -iE 'acme|cloudflare|certificate'
harbora doctor
```

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
# On the panel host, from /opt/harbora/app/deploy. `install.sh` puts this script on the PATH as
# `harbora`; a hand-built install like this one runs it in place, which works because it finds
# everything relative to $HARBORA_DIR (default /opt/harbora).
./harbora node-ca \
  | sed -n '/-----BEGIN CERTIFICATE-----/,/-----END CERTIFICATE-----/p' \
  > traefik/dynamic/node-ca.pem          # the CA the router verifies node certificates against

# Worth doing once, so the recovery commands are there when you need them and not before:
sudo install -m 0755 ./harbora /usr/local/bin/harbora
```

You also need the mTLS router itself at `traefik/dynamic/node-agent.yml`, on the `NODE_DOMAIN` host
and requiring a client certificate against that CA. `install.sh update` renders it from the template
and then verifies the whole path; doing it by hand is where this runbook stops being the short way.

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

Or `curl -fsSL …/deploy/install.sh | sudo bash -s -- update`, which additionally backfills any `.env`
setting a newer version needs and your file predates.

Before applying any schema migration the panel dumps the database and **refuses to migrate if the
dump fails**. `harbora backups` lists the restore points; the automatic ones are named
`pre-upgrade-*`.

### What changes for you in this release

Four behaviour changes that need no action from you but will look like faults if nobody said so —
and one new feature that does nothing at all until you turn it on.

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
serial whatever this says, and a backup no longer waits behind somebody else's build.

If your panel is sized for one build at a time, put it back to one. **Setting it in `.env` does
nothing** — the compose file names the panel's environment variables explicitly and does not pass
this one through, so `.env` is only used for substitution. The supported way is a compose override
file, which Compose merges automatically and which `git pull` will never overwrite:

```bash
cat > /opt/harbora/app/deploy/docker-compose.override.yml <<'YAML'
services:
  panel:
    environment:
      Jobs__MaxConcurrency: "1"
YAML
cd /opt/harbora/app/deploy && docker compose up -d panel
```

That restores the previous serial worker exactly, and it needs no redeploy of anything. There is no
first-class setting for it yet (HARBORA-0065).

**4. A node needs a second DNS record.** `nodes.<panel domain>` — see §0. Only if you use nodes, and
only on a real domain; a `nip.io` install needs no new record. No new port is opened. `install.sh
update` writes the router and the settings; a hand-built install needs §8.

**5. Pay-as-you-go billing has arrived, switched off, and nothing is charging anybody.** New tables,
new screens and a new setting — and after you upgrade, no customer of yours is billed a single unit
and nothing of theirs stops. `Billing:Enabled` ships as `false`. Charging people is a commercial
decision and an install that upgraded into it unasked would start billing tenants who were never told
there was a price, so you have to say yes on purpose. The switch controls only automatic hourly
charging, overage and balance-based suspension: **Billing**, **Billing vouchers**, manual tenant
credit/adjustment, and **Billing runs** history remain visible and usable from the panel while it is
off.

Read the rest of this item before you say yes. Billing is connected to the durable job queue: one
`BillingRun` is persisted for every ended UTC hour, missing hours are discovered oldest-first after
a restart, and an incomplete hour is retried without duplicating ledger lines that already landed.
The first activation charges only the hour that has just ended — it never reaches backwards into the
period in which billing was deliberately disabled.

There is deliberately **no online payment gateway**. Money enters an account in either of two ways:
an administrator credits a tenant from **Tenants → tenant → Credit**, or creates a single-use code
under **Platform → Billing vouchers**. The plaintext voucher is shown once; only its SHA-256 hash is
stored. Any member of a workspace can redeem it on their own **Billing** page. Both routes append one
idempotent credit line, and a replay cannot move the balance twice. An administrator can also append
an Adjustment from the tenant page to reverse a mistaken credit; the original line is never edited
or deleted. Customers see those signed corrections in a separate **Balance adjustments** table on
their bill rather than mixed into resource costs. The voucher console can filter by text, state,
expiry window and redeeming workspace.

The provider can inspect the latest hourly accounting history under **Platform → Billing runs**.
Incomplete runs show their counters and failure summary and can be retried there while billing is
enabled. A retry uses the durable queue and its live-job uniqueness guard, so repeated or concurrent
clicks do not create two live executions. When `Billing:Enabled` is `false`, history remains visible
but the retry action is deliberately unavailable.

**"Sell past the caps" changes what a plan refuses, not just what it charges.** The Plans form gains a
tick box for it. On a plan with it set, that plan's limits on apps, databases, memory, CPU and disk
stop refusing — a customer can go past them and is charged for what they use at the ordinary rate.
Leave it off and the caps behave exactly as they always have. It does nothing at all while
`Billing:Enabled` is `false`, so it cannot hand out free capacity on an install that is not charging.

**Blank prices are not free prices.** Every rate arrives unset — on plans and on instance sizes — and
that is deliberately different from a rate of zero. Unset means nobody has decided; the hourly pass
writes no line for it at all and names it in that run's warnings instead. Zero means you decided it
is free. If you enable billing and set no prices, the hour is recorded as incomplete and retried;
it never invents a zero or calls the hour settled. Type a `0` where you mean free.

**Enable it through `.env`, deliberately.** The compose file maps the friendly keys below to the
panel settings, with safe defaults for an older `.env` that has none of them:

```bash
printf '\nBILLING_ENABLED=true\nBILLING_CURRENCY=IRR\nBILLING_MAX_BACKFILL_HOURS=72\n' >> .env
cd /opt/harbora/app/deploy && docker compose up -d panel
```

**What your customers will experience once it is switched on.** Every workspace is
charged once an hour for what it held during the hour that just ended — apps and databases at their
instance size's rate, running or stopped, plus disk, plus the plan's hourly minimum if the hour came
to less than that. They get one warning when the balance is worth fewer hours than they asked to be
warned at. **At zero, their apps and their managed databases are stopped, and starting anything is
refused until somebody credits the account.** Their data is untouched and a credit brings back
exactly what the suspension stopped — but their site goes down, and it goes down without a human
deciding it should. Tell them the terms before you enable this, not afterwards.

Two exceptions, both deliberate: **your own workspace is never stopped for money** (the panel lives
in it, so collecting that debt would take down the screen you would fix it from), and **a tenant an
operator has suspended by hand is left exactly as they are** — their balance goes on falling and
their workloads go on running, because lifting that suspension is not billing's to do. The pass names
that tenant in its warnings every run until somebody acts.

**A tenant with no alert channel gets no warning at all.** The low-balance warning goes to that
workspace's alert rules, and nothing creates one for them — somebody has to add a channel on that
tenant's **Alerts** page. A workspace with none is told nothing, and the first they know of it is
their site stopping. The pass names every workspace it could not warn in that run's warnings, so it
is at least visible — to you, not to them.

**One thing it will not charge for as things stand, so do not price as though it will.**

- *Traffic.* Nothing anywhere in Harbora measures bandwidth. A plan can be sold with a traffic
  allowance printed on it and the platform will neither count it nor enforce it, so plan copy that
  implies a metered allowance is a promise you cannot keep. Metering it is a project of its own, not
  a setting somebody forgot to switch on.

## Troubleshooting

- `docker compose logs panel` — startup and migration errors. Most often the database is not
  accepting connections yet at boot; the panel retries.
- No cert / 404 on the app domain — check DNS resolves to the VPS and ports 80/443 are open
  (`docker compose logs traefik`).
- App shows *Failed* health check — the app must listen on the **Container Port** you set and
  return `<400` on its health path.
- A node enrols but never comes online — the mTLS channel in §8. **`harbora doctor` will not find
  this**: it checks the master key, the domains, the database password, three container states and
  ports 80/443, and knows nothing about nodes, the CA or the channel router. It will say "No
  configuration problems found" while the node sits offline. The thing that actually checks the node
  channel is the installer: `bash install.sh update` renders the router, backfills
  `NodeAgent__PublicUrl`, and then verifies both. After that, `harbora logs panel` names what the
  panel saw — it logs the certificate it was offered and why it did not match.
- **Recovering from a lost panel, a lost app or a lost node:**
  [../docs/disaster-recovery.md](../docs/disaster-recovery.md).
- Reset everything (destroys data): `docker compose down -v`.
