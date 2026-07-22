# 06 — User Flows

Each flow lists: **Goal · Preconditions · Steps (happy path) · System state/feedback · Failure &
recovery · Success criteria.** Flows target the "simple by default, powerful when needed" principle
and the staged **Progress · Success · Failure · Recovery** rule. Click counts are design targets.

Notation: → = user action; ⇒ = system response.

---

## 1. Install Harbora

**Goal:** a reachable HTTPS panel on a fresh VPS. **Pre:** root on a Linux VPS; ports 80/443 free.

1. → Run the one-line installer.
2. ⇒ OS/arch check; installs Docker/git/openssl if missing.
3. ⇒ Asks: real domain or `nip.io`? → user answers; if domain, derives `panel.` + `apps.` and
   pre-checks DNS (warns, lets continue).
4. ⇒ Asks Let's Encrypt email (sensible default).
5. ⇒ Generates secrets (incl. master key), writes `.env` (mode 600), builds, starts Traefik +
   Postgres + Redis + panel.
6. ⇒ **Verifies**: Traefik↔Docker API, panel route, SSL; prints the panel URL + `/setup` link.

**Failure & recovery:** DNS wrong ⇒ explicit bilingual warning + exact records to add + "SSL will
issue once DNS points here"; port in use ⇒ names the offending service and the stop command; panel
exits ⇒ prints the log command. Idempotent re-run reuses `.env`.
**Success:** browser reaches `https://<panel>/setup` with a valid (or `nip.io`) cert.

## 2. First-run setup (owner account)

**Goal:** create the owner + platform identity. **Pre:** install done; no users yet.

1. → Open `/setup`. 2. → Enter display name, email, password (≥8, confirmed), platform name, root
   domain, ACME email, language. 3. → Submit.
⇒ Creates owner + default workspace, seeds settings, signs in, **locks `/setup`**, lands on the
dashboard.
**Failure & recovery:** validation errors inline; race (a user already exists) ⇒ redirect to login.
**Success:** authenticated dashboard; `/setup` now redirects to `/`.

## 3. Add the first server (node)

**Goal:** have a placement target. **Pre:** owner signed in. (Local node is auto-seeded.)

*Local:* already present; the dashboard health strip shows Docker status.
*Remote:* 1. → Servers → "Add a server". 2. ⇒ Wizard shows the exact agent bootstrap
(compose + generated token) to run on the worker. 3. → Enter `http://<worker-ip>:9700` + token;
optional mTLS. 4. ⇒ Harbora health-checks the agent and marks it Online with live CPU/mem/disk.
**Failure & recovery:** unreachable/401 ⇒ node shown Offline with the failing check and a "Test"
action; never scheduled onto until healthy.
**Success:** node Online; new apps can target it.

## 4. Create the first project & application

**Goal:** an app defined in ≤ 4 fields. **Pre:** signed in; capacity available.

1. → Dashboard "Create App" (or Apps → New). 2. → Enter **Name**; pick **Source** from a card grid
(Git · Dockerfile · Compose · Image · Static · Template). 3. → Provide the source's one required
field (repo URL / image / pick template). 4. *(optional)* expand **Advanced** (slug, branch/tag,
token, port, instance size, domain, server). 5. → "Create & deploy".
⇒ Slug + `{slug}.{root}` domain auto-derived; quota + placement checked; app created; if "deploy
now", jumps straight to the live deploy view (flow 5).
**Failure & recovery:** missing required field ⇒ inline; over quota / no capacity ⇒ explicit reason
with a link to plan/servers. **Success:** app exists with a domain; deploy underway.

## 5. Deploy (and watch it)

**Goal:** running container reachable via its route, watched live. **Pre:** app exists.

1. → "Deploy" (or triggered by create/push/webhook/CLI). 2. ⇒ Deployment row created (`Queued`);
UI shows a **staged progress bar**: Queued → Building → Deploying → Health → Live, with the live
log stream beneath. 3. ⇒ Build (Dockerfile or buildpack) streams logs; image tagged
`…:build-{n}`. 4. ⇒ New container starts on the tenant network; **readiness** probed. 5. ⇒ On
healthy, route is switched to the new container (old one retired) and SSL ensured. 6. ⇒ Status
**Live**; app marked Running; success toast with the URL.
**Failure & recovery:** build/health failure ⇒ status **Failed** with the failing stage
highlighted, the error line surfaced above the log, the **previous version still serving**, and a
one-click **"Roll back / Retry"**. Process restart mid-deploy ⇒ reconciler resumes or fails it
cleanly (never stuck). **Success:** the domain serves the new version over HTTPS.

## 6. Recover a failed deploy

**Goal:** get back to healthy fast. **Pre:** a deploy failed.

1. ⇒ Failed view shows: which stage failed, the key error, suggested causes (e.g., "no Dockerfile &
stack not detected — add a Dockerfile or pick a template"), and the full logs. 2. → Choose
**Retry** (same ref) or **Roll back** (to last Live artifact) or **Edit & redeploy** (fix env/port).
⇒ Rollback re-points traffic to the prior image in seconds (no rebuild) after a pre-confirm diff.
**Success:** app Live again; the incident is in history + audit.

## 7. Connect a domain

**Goal:** a custom domain with SSL. **Pre:** app exists; user controls the domain's DNS.

1. → App → Domains → "Add domain" → enter host. 2. ⇒ UI shows the **exact DNS record** to create
(`A <host> → <server IP>`) and a live **"resolves correctly?"** indicator. 3. → After it resolves,
→ "Apply". 4. ⇒ Route created (SSL on, HTTPS-redirect on); cert issued on first HTTPS hit.
**Failure & recovery:** not resolving ⇒ stays pending with guidance; ACME failure ⇒ shows the
reason (port 80 reachability / DNS) and a retry. **Success:** `https://host` serves the app with a
valid cert.

## 8. Create a database & attach it to an app

**Goal:** a managed DB wired into an app with no copy-paste. **Pre:** signed in; capacity.

1. → Databases → New → pick engine (Postgres/MySQL/MariaDB/Redis/Mongo) + size. 2. ⇒ Provisions the
container with encrypted credentials; shows safe connection info. 3. → App → "Attach database" →
pick the service. 4. ⇒ On next deploy, the app receives the connection env automatically
(`${{db.CONNECTION_URL}}`). **Failure & recovery:** provision failure ⇒ service marked Failed with
logs + retry. **Success:** app can reach its DB using injected env; nothing pasted by hand.

## 9. Back up & 10. Restore

**Back up — Goal:** a restorable, encrypted snapshot. 1. → Backups → "Run backup" (or a schedule)
→ pick scope (app config / volume-db / full platform) + destination (Local / S3). 2. ⇒ Runs as a
background job with progress; artifact encrypted; retention applied; downloadable.
**Restore — Goal:** return a target to a snapshot. 1. → Backups → pick a backup → "Restore" →
**typed confirmation**. 2. ⇒ Optional **dry-run** reports what would change. 3. ⇒ Stops target,
restores data, restarts; progress shown throughout.
**Failure & recovery:** backup failure ⇒ alert + retained logs; restore failure ⇒ target left in a
defined state with guidance; pre-restore auto-backup enables undo.
**Success:** backup listed & downloadable; restore returns the exact backed-up state.

## 11. Roll back a deployment

**Goal:** instant return to a known-good version. 1. → App → Deployments → pick a prior **Live**
deployment → "Roll back". 2. ⇒ Pre-confirm **diff** (image + env deltas). 3. → Confirm. ⇒ Traffic
re-pointed to that artifact in seconds; a new `RolledBack`-trigger deployment records it.
**Success:** app serves the chosen prior version without a rebuild.

## 12. Add a team member

**Goal:** grant scoped access. **Pre:** Admin/Owner. 1. → Settings → Team (or Tenants → workspace)
→ "Invite" → email + role (Admin/Operator/Developer/Viewer). 2. ⇒ Invite created (temp password or
link). 3. → Member signs in; sees only permitted workspace(s)/actions.
**Failure & recovery:** duplicate email ⇒ inline error. **Success:** member can do exactly their
role's actions; privileged actions are blocked and audited.

## 13. Install a one-click app (template)

**Goal:** a working stack in ≤ 3 clicks. 1. → Templates → pick (e.g., WordPress) → "Deploy" →
confirm name/size/domain. 2. ⇒ Provisions all required services (e.g., WordPress + MariaDB), wires
env, assigns a domain, deploys, streams progress. **Failure & recovery:** any sub-service failure ⇒
whole template marked Failed with per-service logs + retry. **Success:** the template's URL serves a
working app with its dependencies running.

## 14. Deploy from CI (GitOps)

**Goal:** repeatable deploys from a pipeline. 1. → Add `harbora.yaml` to the repo. 2. → In CI:
`harbora deploy` (token from Settings → API Tokens) **or** a per-app deploy webhook. 3. ⇒ Build +
deploy run; CI streams/polls status and fails the job on a failed deploy.
**Success:** merges to the target branch deploy automatically and reproducibly.

---

### Flow-level UX rules (apply to all)
- Show **Queued/Loading**, **Success**, **Failure**, and a **Recovery** action for every long or
  destructive operation.
- Prefer **progressive disclosure**: the common path fits on one screen; advanced options are one
  click away.
- After a restart, any in-flight operation is **reconciled**, never left ambiguous.
- Destructive actions require **typed confirmation** and are **audited**.
