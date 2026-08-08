# Disaster recovery

Three things can be lost: **the panel host**, **one application**, or **a worker node**. This
document is what to do in each case, and — the part that matters more — **what cannot be recovered
today**, so you find that out now rather than during the outage.

Everything here is a command that exists. Nothing in this document is a plan.

---

## Read this before you need it

### The three things to keep off this server

| | Why |
|---|---|
| **`HARBORA_MASTER_KEY`** from `deploy/.env` | Every secret Harbora stores is encrypted with it: environment variables, git tokens, agent tokens, the node CA, database passwords. A database dump without this key restores rows whose secret columns are unreadable for ever. The panel refuses to start without a key rather than leave them decryptable, so you will notice — but by then it is too late to invent one |
| **A recent database dump** (`harbora backups`) | It is the entire platform: apps, routes, domains, users, plans, tenants, quotas, deployment history |
| **A copy of your object storage** if customers keep data in it | The panel's own dump does not contain bucket contents |

`harbora env` prints the whole `.env` with secrets masked, which is safe to paste into an issue.
It is *not* a backup — you need the real values.

### What the panel's own backups do and do not cover

| Backup type in the UI | What the artifact is | Restores |
|---|---|---|
| **App config** | The app's settings and environment variables, as JSON | Yes, into an existing app |
| **Volume** / **Database** | The volume's contents, or a logical dump (`pg_dump`, `mysqldump`, `mongodump`; Redis keeps its own snapshot file, so its volume is copied instead) | Yes, with a typed confirmation |
| **Full platform** | A JSON *description* — settings, and a list of app and route names | **Only the settings.** It is an inventory, not an image. It will not rebuild your apps, users or routes |

**The restore point for the platform itself is the database dump**, not the "full platform" backup:

```bash
harbora backups          # list them; pre-upgrade-* are taken automatically before every migration
harbora backup-db        # take one now
```

### The gap: a node's volumes cannot be backed up at all

**A volume or managed database that lives on any server other than the panel's own host cannot be
backed up or restored.** Not a v1 node, not a legacy HTTP agent. Harbora refuses the job before it
starts and names the server.

That refusal is the current, correct behaviour and it is newer than the feature. It used to *look*
like it worked: the helper container ran on the other machine, wrote a correct archive into that
machine's staging volume, and the panel then looked for the archive in its own staging volume and
failed with a message about a missing volume — sending an operator to `docker volume ls` on a host
where everything was exactly as it should be, while every scheduled run left another uncollected
archive behind. Now nothing is read, nothing is written, nothing is left behind, and the failure
says which server and why.

Closing it needs a way to carry an archive between two machines, which Harbora does not have
(**HARBORA-0034**; the node's own `SnapshotVolume` and `RestoreVolume` verbs exist and are tested on
the agent side, but the panel has nowhere to put what they produce).

**Until then, for stateful workloads: keep them on the panel host, or back their volumes up on the
machine they live on with something outside Harbora.** A node is safe for stateless applications;
`docker volume` data on one is not protected by anything Harbora does.

### Rehearse it

A restore you have never performed is a plan, not a recovery. Once per quarter, on a scratch VPS:

1. `harbora backup-db` on the live panel, copy the file and `deploy/.env` off the server.
2. Follow **Scenario A** below on the scratch host.
3. Sign in, open one app, and confirm its environment variables read back as real values rather
   than as decryption errors — that is the check that proves you kept the right master key.
4. Throw the scratch host away.

Step 3 is the whole point. Everything else usually works.

---

## Scenario A — the panel host is gone

The VPS is destroyed, unreachable, or being replaced. Apps on *worker nodes* keep serving while you
do this; apps on the panel host are down until it is back.

**You need:** `HARBORA_MASTER_KEY`, a database dump, and your DNS.

### 1. Build a new host

Install Harbora on the replacement exactly as a fresh install
(`deploy/install.sh`, or [../deploy/RUNBOOK.md](../deploy/RUNBOOK.md)). Let it complete and reach
`/setup` — do **not** create an owner account. The restore replaces the database it would go in.

### 2. Put your own secrets back

Edit `/opt/harbora/app/deploy/.env` and replace the generated values with the ones you saved:

```
HARBORA_MASTER_KEY=<the original>
MINIO_ROOT_USER=<the original>
MINIO_ROOT_PASSWORD=<the original>
```

`POSTGRES_PASSWORD` may stay whatever the new install generated — it is the platform's own database
login, not something encrypted in the dump.

```bash
harbora restart
harbora info          # confirm: master key present, domains, database reachable
```

> If `harbora info` reports the master key is missing or is the development default, **stop**.
> Restoring on top of a wrong key produces a panel that starts and then cannot read a single stored
> secret. `harbora fix-key` generates a key when none exists; it replaces an existing one only if
> you type `REPLACE`, and doing that here destroys exactly what you are trying to recover.

### 3. Put the database back

Copy your dump into the backup volume the CLI reads
(`/var/lib/docker/volumes/harbora_backups/_data`), then:

```bash
harbora backups                       # your file should be listed
harbora restore-db <your-file>.sql.gz # asks you to type the database name
```

`restore-db` saves the current database as `pre-restore-*` first, stops the panel so nothing writes
during the restore, drops and recreates the database rather than restoring over the top, and starts
the panel again. If the restore itself fails it tells you the `pre-restore-*` file to put back.

### 4. Point DNS at the new IP and let certificates re-issue

`panel.`, `*.apps.`, `s3.`, and `nodes.` if you use nodes. Certificates are not in the dump; Traefik
takes new ones from Let's Encrypt on the first HTTPS hit to each name once DNS has moved. Watch it:

```bash
harbora doctor
harbora logs traefik
```

### 5. Get back in, and redeploy

```bash
harbora users                                  # the accounts are back
harbora reset-password --email you@example.com # if you need to
```

Applications are rows, not running containers: the new host has none. Deploy each app from the panel
— a Git or upload source rebuilds, a prebuilt image re-pulls. **Their volumes are not in the
database dump.** Restore each one from its own Volume or Database backup (Backups → the target →
Restore, typed confirmation) before you send traffic at it.

### 6. Re-enrol the nodes

Every node's certificate was signed by a CA that lived in the old database. If you restored that
database, the CA came back with it and your nodes reconnect on their own once
`nodes.<panel domain>` resolves to the new host — nothing to do. Check **Nodes** for Online.

If you did **not** restore the database — you are rebuilding from nothing — there is a new CA, and
every node has to be re-enrolled from **Nodes → Add a node**. The same is true if the master key
does not match the CA in the database: the panel says `The node CA is present but could not be
decrypted` (`harbora logs panel` will show it), and the only remedy is a new CA and a re-enrollment
of every node.

---

## Scenario B — one application is lost

Its data is corrupted, someone deleted it, or a deployment destroyed something it should not have.
The platform is healthy.

### The app is still there and only its data is wrong

**Backups → find the app's volume or database → Restore.** It asks you to type the target's name.
The restore verifies the artifact's checksum first and refuses if it does not match, extracts to a
scratch location and swaps rather than untarring over live data, and stops the container before it
wipes anything.

If the app runs on a node or a legacy agent, this refuses — see *the gap*, above.

### The app was deleted

The app's rows are gone. Recreate it (**Apps → New App**) with the same slug and domain, restore its
configuration from an **App config** backup if you have one, deploy, then restore its volume.

If several apps went with it and you would rather rewind the whole panel, `harbora restore-db` is
Scenario A step 3 — but read the warning it prints: it takes the platform back to the moment of the
dump, and *everything* created since is gone, not just the thing you are trying to undo.

### A deployment broke it and the data is fine

Do not reach for a backup. **Apps → the app → Rollback** puts the previous release back; the image
still exists because retention keeps the running one. A rollback that cannot find its image says so
up front instead of failing part-way through.

---

## Scenario C — a node is lost

A worker VPS is destroyed or permanently unreachable. The panel and every app on other servers are
unaffected.

**What survives:** everything the panel knows — the apps, their configuration, their domains, their
routes, their environment variables. All of it is in the panel's database.

**What does not:** every named volume on that node. Databases, uploads, anything an application
wrote to disk. There is no copy, because Harbora could not take one (*the gap*, above). Say this out
loud before you promise a customer a node.

### 1. Take the node out of scheduling

**Nodes → the node → Revoke.** Its certificate stops being accepted immediately and the panel stops
trying to command it. If the machine is merely unreachable rather than gone, prefer **Drain**: it
refuses new work while leaving what is running alone.

### 2. Enrol a replacement

**Nodes → Add a node** on the new machine, with the same `--region` / `--environment` / `--labels`
placement hints, so the scheduler treats it as the same kind of capacity:

```bash
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/node-agent/install.sh \
  | bash -s -- --control-plane https://panel.example.com --token hbr_enroll_xxx --name web-01
```

If the replacement is behind NAT, turn on **Serve through the ingress tunnel** on its page before
you deploy anything to it. Otherwise routes to it will resolve and time out — the deploy will
succeed and the site will be dead.

### 3. Redeploy the applications

Each app that lived on the lost node needs a deployment; the scheduler places it on a node with
room. A v1 node runs prebuilt images, so what it pulls is a digest the panel already holds — the
same bytes that were running.

### 4. Restore what data you can

From wherever you kept it. Not from Harbora, for anything that was on that node.

---

## Commands used here

All of these are the server-side `harbora` command installed by `deploy/install.sh` at
`/usr/local/bin/harbora`. They deliberately do **not** need a healthy panel — each runs the app as a
one-off container that never starts the web server — so they still work while the panel is
crash-looping, which is when you need them.

| | |
|---|---|
| `harbora doctor` | Start here. Checks the master key, domains, DB password, container states, ports 80/443, and prints the panel's last errors |
| `harbora info` | Configuration as the app sees it: master key state, domains, database reachability, pending migrations, account counts |
| `harbora env` | Everything in `deploy/.env`, secrets masked |
| `harbora status` · `harbora logs [panel\|traefik\|postgres]` | Container states; follow logs |
| `harbora backups` | List restore points |
| `harbora backup-db` | Take one now |
| `harbora restore-db <file>` | Replace the database with a restore point |
| `harbora users` · `harbora reset-password` · `harbora make-owner` · `harbora unlock` | Get back in |
| `harbora fix-key` | Generate a master key when none exists. Read the warning above first |
| `harbora node-ca` | Print the node CA, for the Traefik mTLS router |
| `harbora restart` · `harbora stop` | Lifecycle |

Installed somewhere other than `/opt/harbora`? `HARBORA_DIR=/your/path harbora doctor`.

## Where the data actually is

For the times you are recovering a host by copying volumes rather than by restoring a dump. Compose
prefixes volume names with the directory it runs in (`deploy`), so `docker volume ls` shows
`deploy_harbora_pgdata` and so on — except `harbora_backups`, which is pinned to an unprefixed name
because the panel and its one-off helper containers both have to mount it *by that name*.

| Volume | Holds | If you lose it |
|---|---|---|
| `harbora_pgdata` | The platform database | Everything. Restore from a dump |
| `harbora_backups` | The dumps and staged artifacts | Your restore points, if they are only here |
| `harbora_objects` | MinIO buckets — customer data | Customer data. Nothing else has a copy |
| `harbora_keys` | ASP.NET Data Protection keyring | Everyone is signed out. Nothing worse |
| `harbora_acme` | Let's Encrypt certificates | Nothing. They are re-issued on the next HTTPS hit |
| `harbora_builds` | Build output | Nothing. The next deploy rebuilds |
| `harbora_redis` | Nothing Harbora uses | Nothing. The queue is in PostgreSQL |
