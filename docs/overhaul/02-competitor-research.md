# 02 — Competitor Research: PaaS Market Landscape for Harbora

**Scope:** 25 products across five market segments.
**Method:** Desk research of official sites, documentation, GitHub repositories, changelogs, pricing pages, and community discussions (Hacker News, Reddit, Discord). Research date: July 2026.
**How to read this document:** Section 2 maps the market into segments and names what each segment means for Harbora. Section 3 gives a tight per-product capsule (4-8 bullets). Section 4 synthesises patterns across all 25 products into capability deep-dives with concrete Harbora recommendations. Section 5 ranks the top differentiator ideas. Section 6 lists anti-patterns to avoid.

---

## 1. Scope at a Glance

| Segment | Products |
|---|---|
| A — Self-hosted open-source PaaS | Coolify, CapRover, Dokploy, Easypanel, Dokku |
| B — Self-hosted adjacent (app-store / container management) | Cloudron, Kubero, Portainer, Rancher, OpenShift Dev |
| C — Managed cloud PaaS leaders | Railway, Render, Fly.io, Heroku, Vercel |
| D — Modern BYOC / multi-cloud PaaS | Netlify, Northflank, Porter, Qovery, Zeabur |
| E — Hyperscaler container platforms | DO App Platform, Firebase App Hosting, AWS App Runner, Cloud Run, Azure Container Apps |

Harbora competes most directly with **Segment A** (the install-on-your-VPS tools) and takes inspiration from Segments C and E for product polish.

---

## 2. Market Map and Segmentation

### Segment A — Self-Hosted Open-Source PaaS

**Who it is for:** Individual developers, agencies, and small businesses that want cloud-like convenience on their own servers without a monthly SaaS fee.

**The bar it sets:** One-command install, Git-push deploy, automatic TLS via Traefik or Nginx, one-click databases. Feature breadth is a primary competitive dimension — whoever has the longest list of one-click services wins mind-share in this segment.

**What Harbora should learn:** Preview deployments per PR (Coolify, Dokploy), Docker-Compose as a first-class deployment type (Dokploy), Dockerfile/Nixpacks/Buildpack parity, multi-channel notifications, in-browser terminals, and per-deployment log persistence. RBAC is consistently weak or absent; building it correctly from day one is a genuine differentiator.

### Segment B — Self-Hosted Adjacent (App-Store / Container Management)

**Who it is for:** Ops teams, prosumers, and platform engineers who manage containers or curated apps without deploying custom code (Cloudron, Portainer) or who run Kubernetes and want a PaaS layer on top (Kubero, Rancher, OpenShift).

**The bar it sets:** Cloudron sets the bar on backup UX and DNS automation. OpenShift's Topology view is the industry's best visual representation of deployed services. Portainer's multi-environment RBAC model is the clearest role-hierarchy in the group.

**What Harbora should learn:** Cloudron-style independent per-app backups with multiple storage targets and retention policies; OpenShift's Topology canvas view; Portainer's Operator role (view/restart/console but no create/delete). Kubernetes complexity itself is irrelevant to Harbora but the UX ideas are portable.

### Segment C — Managed Cloud PaaS Leaders

**Who it is for:** Teams who do not want to manage infrastructure at all and accept the cost premium for that convenience.

**The bar it sets:** Railway's variable reference syntax (`${{Postgres.DATABASE_URL}}`) and canvas UI are the industry benchmarks for multi-service developer experience. Vercel's preview deployments with an in-browser toolbar are the benchmark for PR-review UX. Fly.io's `fly.toml` and deployment strategy variety (rolling/canary/blue-green/immediate + machine checks) are the benchmark for deploy safety. Heroku's Release Phase command (pre-deploy one-off that aborts on failure) is the benchmark for safe migrations.

**What Harbora should learn:** Cross-service env var references, named deployment strategies, pre-deploy migration command as a first-class feature, instant rollback (routing change, not rebuild), and a GitOps config file (e.g., `harbora.yaml`).

### Segment D — Modern BYOC / Multi-Cloud PaaS

**Who it is for:** Seed-to-Series-B startups and platform teams who want managed PaaS economics while keeping workloads in their own cloud or on their own hardware.

**The bar it sets:** Qovery's full-stack ephemeral preview environments (entire environment — services + databases — cloned per PR, auto-destroyed on merge) are the gold standard. Northflank's release flows (DB backup → deploy → smoke test → promote) are the best-in-class deploy pipeline primitive. Render's environment groups (shared env vars attached to multiple services) are the cleanest multi-service env var model.

**What Harbora should learn:** Deployment ordering stages (run migrations before deploying the app), release flow pipelines, environment group / shared variable propagation. The Kubernetes underpinning is irrelevant; the UX patterns are directly portable to Docker.

### Segment E — Hyperscaler Container Platforms

**Who it is for:** Enterprise and cloud-native teams running on AWS, GCP, or Azure, needing serverless economics and deep ecosystem integration.

**The bar it sets:** Cloud Run and Azure Container Apps define the gold standard for traffic-splitting between revisions (canary/blue-green as a routing operation with no rebuild). DO App Platform's rollback diff (code changes + config changes side-by-side before confirming) is the best rollback UX in the survey. ACA's deployment labels (stable named routes: dev, staging, prod) on specific revisions is the cleanest named-slot model.

**What Harbora should learn:** Immutable revision model, traffic-weight-based rollback, pre-deploy health gating, all three probe types (startup, liveness, readiness), and named routing slots backed by Traefik weighted routing.

---

## 3. Per-Product Capsules

### Segment A — Self-Hosted Open-Source PaaS

#### Coolify
- **Positioning:** "Self-hosting with superpowers" — the broadest feature set in the self-hosted segment. Apache 2.0. 59k GitHub stars (v4.1.2, June 2026).
- **Pricing:** Free (OSS); Coolify Cloud from $5/mo for managed control plane.
- **Does best:** 280+ one-click service templates with magic env vars; PR preview deployments with auto-teardown; multi-channel notifications (Discord, Telegram, Slack, email, Pushover, webhook).
- **UX signature:** Wizard-guided server onboarding (SSH key + IP → auto-installs Docker); per-deployment streaming log pane; in-browser xterm.js terminal.
- **Weakness:** Instability on upgrades (797 open issues); Docker Swarm deprecated abruptly; RBAC had IDOR bugs; no dedicated CLI; no audit log.

#### CapRover
- **Positioning:** "Heroku on steroids" — CLI-first, Swarm-based, targets cost-conscious solo developers. License listed as NOASSERTION. 15k stars.
- **Pricing:** Free, self-hosted only.
- **Does best:** Per-app customisable Nginx config template (power-user escape hatch); CLI-driven workflow (`caprover deploy`); third-party one-click app repo URLs.
- **UX signature:** Simple five-tab app page; deploy via tarball upload or CLI.
- **Weakness:** No RBAC (single admin only); no preview environments; no database backups; NetData analytics controversy; Swarm lock-in.

#### Dokploy
- **Positioning:** Docker Compose-native modern alternative; clean Traefik integration; tiered Cloud offering. 35k stars (v0.29.13, July 2026).
- **Pricing:** Free (OSS); Cloud from $4.50/mo/server; Enterprise custom.
- **Does best:** First-class Docker Compose resource type; in-browser Traefik file editor; comprehensive notifications (Gotify, Ntfy, Lark, Slack, Discord, Telegram, email, webhook).
- **UX signature:** Clean Next.js dashboard; monitoring tab per-resource with live charts; queue-managed concurrent builds.
- **Weakness:** Young project (2024); audit logs Enterprise-only; build concurrency capped at 2 in OSS; RBAC still maturing.

#### Easypanel
- **Positioning:** Fastest time-to-running-app in the segment; proprietary closed-source panel. Per-server subscription pricing.
- **Pricing:** Free (3 projects); Hobby $10.90/mo; Growth $16.90/mo; Business $29.90/mo — all per server.
- **Does best:** In-browser database client (run SQL/NoSQL queries without a local tool); fastest onboarding; well-wired templates (e.g., FusionAuth + OpenSearch pre-connected).
- **UX signature:** Project-centric view; zero-downtime deploy as default behaviour.
- **Weakness:** Closed source — cannot audit what runs on your server; backups paywalled behind Hobby; no CLI or public API; multi-server under development.

#### Dokku
- **Positioning:** The original self-hosted mini-Heroku. MIT. 32k stars, 13 years old. CLI-only by default; Dokku Pro adds a paid SPA GUI.
- **Pricing:** Free (OSS); Dokku Pro ~$199/year.
- **Does best:** `git push`-to-deploy with Heroku-compatible output; per-process-type scaling (`web=3 worker=2`); Vector log aggregation built-in; richest builder ecosystem (Herokuish, CNB, Nixpacks, Railpack).
- **UX signature:** The terminal IS the UI; `CHECKS` file for zero-downtime health gates; app.json manifest.
- **Weakness:** No GUI by default; no RBAC; no marketplace; no preview environments; env vars in plain text on disk; K3s path for multi-server adds significant complexity.

---

### Segment B — Self-Hosted Adjacent

#### Cloudron
- **Positioning:** App Store for servers — curated, pre-packaged apps; not a code-deploy PaaS. Closed-source, subscription. ~200 apps.
- **Pricing:** Free (2 apps); Pro €15/mo; Max €25/mo.
- **Does best:** Best-in-class backup system: per-app independent backups, multiple storage backends (S3, B2, CIFS, SSHFS, R2), AES-256 encryption, configurable retention, dry-run restore; automatic DNS management via provider API.
- **UX signature:** App Store browse + install; card-based dashboard with inline operation overlay.
- **Weakness:** No Git/Dockerfile deploy for custom code; no horizontal scaling; no webhook alerting; single-server only.

#### Kubero
- **Positioning:** Heroku DX on Kubernetes — GPL-3. 4.3k stars. Kubernetes required.
- **Pricing:** Free (OSS).
- **Does best:** Pipeline-centric view with named phases (review/test/stage/production); GitOps review apps (PR-open → deploy, PR-close → destroy); 160+ app templates; built-in vulnerability scanning.
- **UX signature:** Horizontal phase lanes per pipeline; templates browser at kubero.dev.
- **Weakness:** Kubernetes hard requirement; rollback UX is weak; no one-click rollback button; GPL-3 license may concern commercial redistribution.

#### Portainer
- **Positioning:** Container management UI — Docker, Swarm, K8s, Podman — not a developer PaaS. CE free; BE commercial.
- **Pricing:** CE free; BE: 5 nodes free, then per-seat/node commercial.
- **Does best:** Multi-environment control plane (one dashboard, N Docker/K8s hosts); granular RBAC (Operator role: restart/view/console, no create/delete); image freshness indicator per stack (green/orange/grey).
- **UX signature:** Table-list layout; stack ownership model; effective-access viewer for admins.
- **Weakness:** RBAC, git-based auto-deploy, and webhooks all gated to paid BE; no DNS/SSL management; no backup system.

#### Rancher
- **Positioning:** Enterprise Kubernetes multi-cluster management (SUSE). Apache 2.0 OSS; Prime commercially supported. Not a developer PaaS.
- **Pricing:** OSS free; Prime commercial.
- **Does best:** Fleet GitOps engine for deploying across hundreds of clusters simultaneously; Recent Operations log (live console output per Helm operation); Helm-based app catalog with multiple repository sources (official, partner, OCI, custom Git).
- **UX signature:** Cluster tile cards with CPU/memory/node summary; dual global/per-cluster perspective.
- **Weakness:** Kubernetes-only; steep learning curve; no Docker Standalone; monitoring requires a separate chart install.

#### OpenShift Developer Console / S2I
- **Positioning:** Enterprise Kubernetes developer experience (Red Hat). Expensive subscription.
- **Pricing:** OCP subscription (enterprise); Developer Sandbox free 30-day trial.
- **Does best:** Topology view — graph/canvas of all deployed services with build-status badges, pod count, and drag-to-connect service wiring; `+Add` card-tile deploy source picker; S2I auto-detection of build strategy from repo contents.
- **UX signature:** Animated build-progress badge on topology node; click-to-stream logs without leaving the canvas.
- **Weakness:** Enterprise pricing; SCC restrictions break many Docker Hub images; DeploymentConfig deprecated; S2I opaque to developers who want Dockerfile control.

---

### Segment C — Managed Cloud PaaS Leaders

#### Railway
- **Positioning:** "Heroku successor" — usage-based, canvas-UI, 50M+ builds/month. Hobby $5/mo min, Pro $20/mo min.
- **Does best:** Infinite-canvas UI where services appear as connected cards; cross-service variable references (`${{Postgres.DATABASE_URL}}`); full ephemeral environments (databases included) per PR; template creator monetisation model.
- **UX signature:** `Cmd+K` command palette; service card with animated deploy-progress stages; `railway scale` TUI region picker.
- **Weakness:** Reliability incidents acknowledged in 2026; no native CDN; no log search in dashboard; SAML gated to Enterprise; audit log thin.

#### Render
- **Positioning:** "The cloud for builders" — fully managed, strong DX, usage-based + flat workspace fee. Hobby free; Pro $25/mo.
- **Does best:** Environment Groups (shared env var sets attached to multiple services with propagation on change); pre-deploy command (migrations between build and traffic cutover); `render.yaml` Blueprint as IaC; PgBouncer connection pooling included at no extra cost.
- **UX signature:** Events timeline combining deploys + config changes + scaling events into one feed.
- **Weakness:** Preview environments require Pro; no template marketplace; five fixed regions; SAML at $499/mo Scale plan; persistent disk disables zero-downtime deploys.

#### Fly.io
- **Positioning:** Multi-region infrastructure-closer-to-metal; 35+ regions; pure usage metering, no free tier.
- **Does best:** Richest deployment strategy menu (rolling/canary/blue-green/immediate); machine checks (test new image in isolated throwaway machine before routing); release command (pre-deploy one-off, abort on failure); WireGuard 6PN private mesh network; auto-provisioned Grafana+Prometheus per app.
- **UX signature:** `fly.toml` as per-app IaC; `fly scale eu-west=2 us-east=1` per-region replica control.
- **Weakness:** Surprise billing changes in 2026 (volume snapshot fees, inter-region network fees); no native Nixpacks; preview deploys need custom GitHub Actions; managed Postgres still early; no free tier.

#### Heroku
- **Positioning:** Category creator — 13M developer accounts; now Salesforce-owned. Per-dyno billing, no free tier.
- **Does best:** Pipeline UI (Review → Staging → Production) with artifact promotion (no rebuild between stages); Release Phase command (pre-deploy one-off, abort on failure = no deploy); versioned release history with `heroku rollback v<N>`; mature add-on ecosystem.
- **UX signature:** Activity tab as chronological release log with "what changed" diff; dyno horizontal-scale slider.
- **Weakness:** Expensive at scale; no free tier since Nov 2022; Fir generation missing many Cedar features (no monorepo, no autoscaling, no VPN yet); SAML gated to Enterprise.

#### Vercel
- **Positioning:** DX platform for frontend teams; Next.js creator. Hobby free; Pro $20/user/mo.
- **Does best:** Preview deployments with Vercel Toolbar (inline UI annotation, feature-flag overrides, accessibility audit, CMS editing — all inside the preview app); instant rollback (promote any previous artifact, no rebuild, seconds); team-level env vars with project-level overrides.
- **UX signature:** Permanent preview URLs per branch that update on each commit; "Promote to Production" button on any preview.
- **Weakness:** Frontend only — no arbitrary Docker, no persistent workers, no filesystem; SAML $300/mo add-on (widely criticised); audit logs Enterprise-only; vendor lock-in concern via Next.js features.

---

### Segment D — Modern BYOC / Multi-Cloud PaaS

#### Netlify
- **Positioning:** Frontend Jamstack hosting; credit-based pricing (since Sep 2025); no persistent server support.
- **Does best:** Deploy preview URL pattern (`deploy-preview-42--site.domain`) with auto PR comment; one-click rollback to any prior deploy (365-day retention on Enterprise); smart secret detection in build logs.
- **UX signature:** "Why did it fail?" AI-assisted build error explanation; drag-and-drop deploy area for static sites.
- **Weakness:** No Docker or persistent process support; past "$104K DDoS bill" incident; credit model unpredictability; downgrade friction.

#### Northflank
- **Positioning:** "Kubernetes-grade PaaS with Heroku DX"; BYOC into any cloud or bare metal; 70k+ developers.
- **Does best:** Full-stack per-PR preview environments via pipeline templates (services + databases cloned together, auto-torn down); release flows as named automation steps (backup DB → deploy → smoke test → promote); per-second GPU billing (H100 $2.74/hr).
- **UX signature:** Multi-step build timeline (Checkout → Build → Push → Deploy); JSON templates-as-code with visual drag-and-drop editor.
- **Weakness:** Steeper learning curve than Railway/Render; proprietary control plane; many primitive types (services vs combined services vs addons vs jobs).

#### Porter
- **Positioning:** Heroku ease in your own AWS/GCP/Azure Kubernetes cluster; raised $20M Series A.
- **Does best:** `porter.yaml` configuration-as-code alongside application code; prominent cost estimate before provisioning; no-lock-in messaging (K8s cluster remains yours if you cancel).
- **UX signature:** Auto-generated GitHub Actions workflow committed to repo on first deploy.
- **Weakness:** $200+/mo infrastructure floor (K8s cluster cost); GitHub-only CI; 30-45 minute cluster provisioning; no first-party managed databases.

#### Qovery
- **Positioning:** "Your cloud stays yours" — pure control plane on customer's cloud; French company, EU compliance focus. Team plan from $899/mo.
- **Does best:** Deployment stages with explicit ordering (define which services deploy in which order per environment); full-stack ephemeral environment cloning per PR; Terraform exporter (export current environment as Terraform HCL).
- **UX signature:** Deployment stages drag-and-drop ordering UI; ephemeral environment panel showing all active PR environments with URLs.
- **Weakness:** $899/mo price floor — inaccessible to small teams; pricing restructured multiple times (trust damage); on-premise control plane only on Enterprise.

#### Zeabur
- **Positioning:** AI-native zero-config deployment; hundreds of community templates; Asian market focus with global reach. Free to $79/mo.
- **Does best:** Template marketplace with hundreds of community one-click stacks; cross-service variable references (`${MYSQL_HOST}` auto-populated); `zbplan` AI build planner reads source code and generates optimal build strategy.
- **UX signature:** "Deploy from Template" as the primary empty-state CTA; AI Agent button for natural-language infra commands; Claude Code plugin.
- **Weakness:** No Docker Compose support (must convert to Zeabur YAML); RBAC only on $79/mo Team plan; database backups gated at Pro; Wonder Mesh BYOC is opaque.

---

### Segment E — Hyperscaler Container Platforms

#### DigitalOcean App Platform
- **Positioning:** Heroku simplicity at DigitalOcean prices; flat per-container monthly billing; no free tier for dynamic apps.
- **Does best:** Rollback diff UX — side-by-side comparison of code changes AND config changes before confirming rollback; request-based autoscaling GA (RPS + P95 latency triggers, May 2026); log forwarding to external sinks configurable per component.
- **UX signature:** Activity timeline with "Live app" badge on current production deployment; app spec YAML exposed and editable in Settings tab.
- **Weakness:** No preview/branch deployments; no traffic splitting; no per-app RBAC; dev database PostgreSQL-only with no backups; autoscaling previously required expensive dedicated-CPU plans.

#### Firebase App Hosting
- **Positioning:** Opinionated Next.js and Angular hosting on top of Cloud Run; GitHub-only; Firebase ecosystem.
- **Does best:** Route-based monitoring (per-URL error rate, latency, cache hit rate with rollout markers on charts); Automatic Base Image Updates (ABIU) — auto-patch base OS without a full redeploy; Cloud Secret Manager integration as first-class env var source.
- **UX signature:** Rollouts tab tied to git commits; "View Cloud Run revision" escape hatch from three-dot menu.
- **Weakness:** GitHub only; Next.js and Angular only; no preview URLs per PR; fragmented logs (build in Firebase console, runtime in GCP console); region locked at creation.

#### AWS App Runner (maintenance mode)
- **Positioning:** AWS's Heroku competitor — now closed to new customers as of mid-2026. Historical reference only.
- **Does best:** AutoScalingConfiguration as a reusable named resource shared across services; concurrency as the primary scaling trigger.
- **Notable lesson:** A managed PaaS positioned between Lambda and ECS was not differentiated enough — without preview environments, traffic splitting, or rollback, it could not compete. Relevant cautionary tale for Harbora's feature priorities.

#### Google Cloud Run
- **Positioning:** "Deploy any container, pay per request" — serverless containers, 35+ regions, scale-to-zero, fastest-growing in the survey.
- **Does best:** Richest revision and traffic-splitting model in the survey — immutable revisions, arbitrary percentage splits, tagged revision URLs for testing at 0% traffic, near-instant traffic routing independent of deploy; all three probe types (startup + liveness + readiness).
- **UX signature:** "Serve this revision immediately" checkbox unchecked = entry point to canary with zero friction; traffic percentage bars on Revisions tab.
- **Weakness:** Cold starts at scale-to-zero; cost has multiple billable dimensions; no built-in CI pipeline; GCP ecosystem lock-in.

#### Azure Container Apps
- **Positioning:** Serverless containers on KEDA + Knative; deep .NET/Dapr/Enterprise integration; Consumption and Dedicated plans.
- **Does best:** Deployment labels (stable named routes — dev/staging/prod — assigned to specific revisions); KEDA-based event-driven autoscaling (scale on queue depth, Service Bus, Kafka) surfaced as a type-picker UI; Service Connector wizard for common integrations (auto-injects credentials + validates connectivity).
- **UX signature:** Revision management panel with Single vs Multiple mode toggle; lifecycle states (Provisioning → Running → Scale to 0 → Degraded) exposed as named diagnostic labels.
- **Weakness:** Environment is a shared network/logging boundary — reorganising is painful; $0.10/hr dedicated-plan management fee; Kusto query language needed for non-trivial log analysis; complexity higher than Cloud Run for simple apps.

---

## 4. Capability Deep-Dives

### (a) Onboarding and First Deploy

**What the best players do:** Coolify and Dokploy provide a single `curl | bash` install that auto-provisions Docker, then walk through server-add → project → resource via a wizard. DigitalOcean and Railway reach first deploy in under 5 minutes from account creation. Firebase and CapRover require DNS pre-configuration before install, which causes confusion.

**Harbora recommendation:** Ship a single `curl | bash` install script. After install, launch an interactive wizard (TUI or web) that: (1) verifies the server meets requirements, (2) auto-installs Docker if missing, (3) creates the admin account, (4) optionally adds a wildcard DNS record with guidance, (5) deploys a hello-world app with a working HTTPS URL. Target: under 10 minutes from bare VPS to live app. Never require DNS to be pre-configured before the install step completes.

### (b) Deploy Sources and Build Systems

**What the best players do:** Railway and Coolify use Nixpacks for zero-Dockerfile deploys. Dokploy and Dokku support Nixpacks, Heroku Buildpacks, Dockerfile, and Docker Compose. Render supports build filters for monorepos. Fly.io requires a Dockerfile but provides `fly launch` auto-generation. OpenShift auto-detects strategy from repo contents (Dockerfile present → use it; otherwise S2I).

**Harbora recommendation:** Support in priority order: (1) Dockerfile auto-detected, (2) `docker-compose.yml` as a first-class resource type (not an afterthought), (3) Nixpacks for zero-config builds, (4) Heroku Buildpacks as a compatibility option. Auto-detect strategy from repo on connect. Display the detected strategy before the first build and allow override. Support monorepo root-directory override per service.

### (c) Preview / PR Environments

**What the best players do:** Vercel is the benchmark — every PR gets a permanent, updating preview URL; the Vercel Toolbar injects review tools into the live preview. Railway and Qovery provision the full stack (databases included) per PR. Render uses `render.yaml` blueprints to define what gets cloned. Kubero and Coolify support PR-linked preview environments with auto-teardown.

**Harbora recommendation:** Implement preview environments as Traefik weighted-routing slots. When a PR is opened against a configured branch: (1) build the new image, (2) create a Traefik route at `pr-{N}.{app-domain}` pointing to a new container, (3) post the URL as a GitHub/GitLab commit status or PR comment. On PR close/merge: remove the container and the route. For databases, clone a lightweight snapshot or use a separate dev-mode database instance. Keep the implementation Docker-native — no Swarm, no K8s.

### (d) Deployment Revisions, Rollback, and Traffic Splitting

**What the best players do:** Cloud Run and ACA define the gold standard — every deploy creates an immutable revision; traffic splitting is a pure routing operation (no rebuild); rollback = change route weights; tagged revision URLs allow testing at 0% traffic. Heroku's `heroku rollback v<N>` is the simplest named-version rollback. DO App Platform shows the best rollback UX — a diff of code changes plus config changes before confirming.

**Harbora recommendation:** Model every deploy as a numbered, immutable revision linked to an image digest. Store the last 10 revisions per app. The rollback UI must show: old vs new image tag, env var diff, and resource change diff before confirming. Rollback should re-point Traefik to the old container (kept running for a configurable overlap window) before destroying the new one — zero-downtime by default. For advanced users, expose Traefik weighted routing (current_rev=80, new_rev=20) as a "canary" toggle. Traffic weight change must be a separate operation from a new deploy.

### (e) Logs — Build and Runtime

**What the best players do:** All modern platforms stream build logs in real-time over WebSocket. Fly.io uses NATS for log streaming. Cloud Run logs go to Cloud Logging with structured JSON support. Dokku ships Vector for log aggregation and routing. Render's pre-deploy command logs are distinct from deploy logs. Firebase's fragmented log experience (build in Firebase console, runtime in GCP console) is the anti-pattern.

**Harbora recommendation:** Build and runtime logs must be on separate tabs within the same page — never split across different dashboards. Stream build logs live via WebSocket during active builds. Runtime logs: tail from Docker container stdout/stderr; persist the last 10,000 lines per container per day in the Harbora PostgreSQL database. Provide a log-level filter (info/warn/error parsed from common formats). Add structured log forwarding (Datadog, Loki, Syslog) as an optional per-app setting. Do not fragment build and runtime logs across different UI sections.

### (f) Environment Variables, Secrets, and Cross-Service References

**What the best players do:** Railway's `${{Postgres.DATABASE_URL}}` cross-service reference is the most developer-friendly pattern in the survey — it eliminates copy-paste on database provisioning. Render's Environment Groups propagate shared vars to multiple services on change. Vercel scopes vars by environment (production/preview/development) and by feature (build-time vs runtime). Fly.io secrets are write-only (names visible, values not).

**Harbora recommendation:** Implement three tiers: (1) instance-level shared variables (available to all apps in the Harbora installation — e.g., `SMTP_HOST`), (2) project-level shared variables, (3) per-app variables. Support cross-service references: when app B references `${{database-name.CONNECTION_URL}}`, Harbora resolves it at deploy time from the provisioned database's connection string. Mark variables as build-time, runtime, or both. Secrets stored AES-256 encrypted in PostgreSQL; never logged or shown in build output after initial set. Allow raw bulk-edit mode (paste KEY=VALUE block) for migration.

### (g) Domains, DNS, and SSL

**What the best players do:** Coolify and Dokploy use Traefik with Let's Encrypt auto-provisioned on custom domain add. Cloudron integrates with DNS provider APIs to create records automatically. Railway shows in-dashboard CNAME/A record values to copy. CapRover requires a wildcard DNS record before install, which is a friction point.

**Harbora recommendation:** Never require DNS to be pre-configured at install time. After install, show the server IP prominently and provide copy-paste DNS instructions per domain. When a user adds a custom domain, provision a Let's Encrypt certificate automatically via Traefik's ACME integration. Support wildcard certificates via DNS-01 challenge with API credentials for major DNS providers (Cloudflare, Hetzner, DigitalOcean, Route53) as an optional upgrade. Multiple domains per app. HTTP→HTTPS force-redirect toggle per app. Display certificate expiry and renewal status clearly.

### (h) Health Checks and Zero-Downtime Deploy Strategies

**What the best players do:** Fly.io's machine checks (boot a throwaway machine with the new image, run integration tests, destroy before routing any traffic) are the safest deploy gate in the survey. Cloud Run and ACA separate startup, liveness, and readiness probes. Heroku's Release Phase (pre-deploy one-off command) is the most used for migrations. Render's deploy policy (queue vs cancel-in-progress) gives teams control over concurrent deploy behaviour.

**Harbora recommendation:** Every deploy should follow: (1) build new image, (2) run optional pre-deploy command (migration container — abort deploy if exit code ≠ 0), (3) start new container, (4) wait for health check to pass (configurable HTTP path + timeout), (5) route Traefik to new container, (6) stop old container after a configurable drain period (default 15s). Expose all three probe types (startup, liveness, readiness) per app. Provide a deploy policy toggle: "Queue" (finish current, run latest) vs "Cancel in progress." Default is zero-downtime; make explicit downtime deploys opt-in only.

### (i) Scaling

**What the best players do:** Horizontal scaling is standard. Fly.io exposes per-region replica counts. ACA and Cloud Run use concurrency as the primary scale trigger (not just CPU). Render supports CPU + memory threshold autoscaling on Pro. Heroku scales per-process-type independently (`web=2:Standard-1X worker=1:Standard-2X`). No self-hosted tool in Segment A has autoscaling.

**Harbora recommendation:** For Harbora's Docker-only model, implement: (1) manual horizontal replica count per app (across one or multiple servers in the pool), (2) per-process-type replica counts (web, worker, cron exposed as separate scalable dimensions). Autoscaling based on CPU threshold is achievable via Docker container stats + a background reconciler loop — implement this as a beta feature gated behind an explicit opt-in toggle. Document clearly that scaling across multiple servers requires the multi-server feature to be configured.

### (j) Databases, Backups, and Restore

**What the best players do:** Cloudron has the best backup architecture in the entire survey: per-app independent backups, multiple concurrent storage targets (S3, B2, CIFS, SSHFS, local disk), AES-256 encryption, configurable retention policies (7 daily, 4 weekly), dry-run restore, migration workflow. Easypanel has the best in-browser database client. Coolify supports S3-backed scheduled backups with restore from the UI. Northflank auto-wires database credentials into dependent services at deploy time.

**Harbora recommendation:** Database provisioning (PostgreSQL, MySQL, Redis, MongoDB) as one-click containers with auto-generated credentials and automatic injection into the referencing app's env vars (cross-service reference pattern). Built-in scheduled backups: S3-compatible target, configurable retention, AES-256 encryption, per-database or per-volume. Restore from backup in the UI with a confirmation diff. An in-browser database client (SQL query tab per database — no external tool needed) is the single highest-impact UX differentiator in Segment A and should be prioritised. Backups must be available on the free/base tier; never paywall them.

### (k) Monitoring and Alerting

**What the best players do:** Fly.io auto-provisions Grafana + Prometheus per app — no setup required. Coolify ships a Sentinel container for server-level metrics. Dokploy shows per-resource CPU/RAM charts in the monitoring tab. Cloud Run exposes p50/p95/p99 request latency without additional tooling. Rancher and Porter bundle Prometheus as an opt-in chart install — the opt-in model means users often skip it.

**Harbora recommendation:** Ship lightweight built-in metrics from day one: per-server CPU/RAM/disk (Sentinel-style sidecar) and per-container CPU/RAM visible in the app dashboard. Do not require a separate Prometheus/Grafana install for basic visibility. For advanced monitoring, provide an easy one-click deploy of a pre-configured Prometheus + Grafana stack as a template. Alerting channels: at minimum Discord, Telegram, Slack, email, and generic webhook — mirroring Dokploy's comprehensive list. Alert events: deploy success/failure, health check failure, server disk threshold, backup failure, certificate expiry.

### (l) Multi-Server and Cluster

**What the best players do:** Coolify and Dokploy add remote servers via SSH key + IP with no agent to install — clean UX. Portainer uses a lightweight Agent container. Rancher's Fleet can target groups of servers simultaneously with per-group config overrides. Kubero inherits Kubernetes multi-node. CapRover's Swarm cluster attach is clean but tied to Docker Swarm.

**Harbora recommendation:** Add servers via SSH (provide IP + private key; Harbora validates connectivity and auto-installs Docker if missing — Coolify's proven model). No proprietary agent to install. Servers are grouped into environments (e.g., prod-eu, prod-us, staging). Apps can be pinned to specific servers or to an environment (Harbora places them on the least-loaded server in the environment). Build isolation: support a separate "build server" designation to offload image compilation from production servers. No Swarm dependency — use a simple Docker-over-SSH orchestration model.

### (m) CLI, API, Webhooks, and GitOps Config Files

**What the best players do:** Dokku's CLI is the most comprehensive in the self-hosted segment (80+ subcommands). `fly.toml`, `render.yaml`, `porter.yaml`, and `apphosting.yaml` all demonstrate that a per-repo declarative config file is the expected IaC primitive in modern PaaS. Railway's TypeScript SDK (`railway.ts`) for project config is forward-looking. Heroku's `app.json` for one-click deploy buttons drives open-source adoption.

**Harbora recommendation:** Provide a `harbora.yaml` file in the repo root as the canonical source of truth for an app's configuration (build type, resources, health check path, env vars, pre-deploy command, replica count, domain). `harbora.yaml` is optional but takes precedence over dashboard settings when present. The Harbora CLI (`harbora`) should mirror all dashboard actions: `harbora deploy`, `harbora logs`, `harbora rollback`, `harbora env:set`, `harbora db:shell`. Provide a full REST API (OpenAPI documented) and per-app webhook URLs for CI/CD integration. A "Deploy to Harbora" one-click button (embeddable in README) backed by an `harbora.yaml` in the repo drives adoption — analogous to Heroku's "Deploy to Heroku" button.

### (n) Teams, RBAC, and Audit

**What the best players do:** Portainer's role model is the clearest: Owner, Environment Admin, Operator (restart/console but no create/delete), Helpdesk (read-only), Standard User (own resources). Dokploy's RBAC tiers are cleanly separated by plan. Qovery includes 7-day audit logs at team tier. Render offers workspace audit logs on Pro. Coolify's RBAC has had IDOR bugs and is still maturing.

**Harbora recommendation:** Implement RBAC from day one with at minimum four roles: Owner (everything), Admin (all resources, no billing/server-delete), Developer (deploy + logs + env vars within assigned projects), Viewer (read-only). The Operator pattern (restart/view/console but no create/delete) is worth adding as a fifth role for agency clients. Audit log: every deploy, config change, rollback, user add/remove, and secret set must be recorded with actor + timestamp. Expose basic audit log at team tier; never lock it behind an Enterprise paywall for a self-hosted product.

### (o) Templates and Marketplace

**What the best players do:** Coolify has 280+ service templates using "magic env vars" (auto-generated random passwords). Zeabur has hundreds of community one-click stacks with cross-service wiring. CapRover allows adding third-party template repository URLs. Easypanel's template quality (pre-wired dependencies, e.g., FusionAuth + OpenSearch) is praised in user reviews. Railway monetises template creators via kickback fees — unique in the survey.

**Harbora recommendation:** Ship a curated template library of 50–100 templates at launch covering the most common self-hosted stacks (n8n, Ghost, Nextcloud, Plausible, Gitea, Vaultwarden, Supabase, WordPress + MySQL, etc.). Templates should be Docker-Compose-based with magic env var generation (random passwords, secrets). Support adding custom template repository URLs (CapRover's model) so the community can extend. Template format should be open and documented so Harbora can import/convert existing Coolify or Dokploy templates. Each template should pre-wire cross-service references so databases connect to apps automatically on first deploy.

### (p) UI/UX Signatures

**Patterns worth adopting:**

- **Railway canvas:** Two-dimensional infinite canvas showing services as connected cards is the most intuitive representation for multi-service projects. Harbora should implement a canvas view as the project overview, togglable against a list view.
- **OpenShift Topology:** Graph nodes with per-corner status badges (build running, pod count, URL link) give at-a-glance operational insight without clicking into detail. Harbora should annotate app cards with: last deploy status, container health, and active domain link.
- **Vercel instant rollback:** Rollback as a "promote this artifact" action (not a redeploy) taking seconds is the gold standard. Harbora's revision model must support this.
- **DO App Platform rollback diff:** Showing code changes + config changes side-by-side before confirming a rollback is the most responsible UX for an action with production impact.
- **Heroku pipeline view:** Stage-by-stage promotion UI (Review → Staging → Production) with "out of sync" indicators is the best way to visualise multi-environment release flows.
- **Portainer image freshness indicator:** Green/orange/grey per-stack indicator for whether a newer image exists at the registry is a simple but high-value signal.
- **ACA deployment labels:** Assigning stable named labels (dev, staging, prod) to specific revisions creates named routing slots without maintaining separate apps — implement via Traefik subdomains.

---

## 5. Differentiator Shortlist for Harbora

Ranked by value-to-complexity ratio for a Docker-only, self-hosted product targeting individual developers, agencies, and small businesses.

### 1. True one-command install + interactive server wizard
**Problem:** CapRover requires DNS pre-setup; Cloudron doesn't support ARM or LXC; Dokku's install needs multiple manual steps after the script.
**Evidence:** Coolify's `curl | bash` + wizard is the most praised onboarding in HN discussions about self-hosted PaaS. Every managed platform (Railway, Render) uses under-5-minute first-deploy as a benchmark.
**Value/Cost/Risk:** Very high value (first impression determines retention); low cost (build once, maintain rarely); low risk (well-understood install pattern). Ship on day one.

### 2. Reliable rollback with a pre-confirm diff
**Problem:** Coolify's rollback had a regression bug (wrong commit SHA used). DO App Platform's diff UX is the only one in the survey that shows what will actually change before confirming.
**Evidence:** Rollback is the most-requested feature in Segment A GitHub issue trackers. Cloud Run and ACA demonstrate that rollback as a routing operation (not a rebuild) is technically achievable.
**Value/Cost/Risk:** High value (prevents production incidents); medium cost (requires immutable revision storage); low risk (Docker image layers are immutable by nature). Ship in v1.

### 3. Backups and restore from day one — never paywalled
**Problem:** Easypanel paywalls database backups behind Hobby ($10.90/mo); CapRover has no built-in backup; Cloudron's backup system (best in class) requires a subscription.
**Evidence:** Easypanel's paywall is the most common community criticism of the product. Cloudron's backup architecture (multiple targets, encryption, retention policies, dry-run restore) is the most praised feature in its segment.
**Value/Cost/Risk:** High value (data loss prevention is non-negotiable for any production workload); medium cost (implement S3-compatible backup worker + restore API); zero risk — this is table stakes, not a differentiator, if not present.

### 4. In-browser database client
**Problem:** Users must install local clients (psql, TablePlus, Adminer) to inspect and query databases — interrupts workflow and adds onboarding friction.
**Evidence:** Easypanel's in-browser SQL client is cited as its single strongest UX differentiator in user reviews. Coolify also provides this. Cloudron's web terminal is frequently praised.
**Value/Cost/Risk:** High value (agency clients in particular need this for client demos and debugging); medium cost (embed a lightweight SQL client component — pgAdmin lite or similar); low risk. Differentiator vs CapRover and Dokku.

### 5. Per-app preview environments via Traefik weighted routing
**Problem:** CapRover, Dokku, Easypanel, and Portainer have no preview environments. Coolify supports them but implementation had bugs. Segment B/C/D tools charge a premium tier for this feature (Render requires Pro).
**Evidence:** PR preview environments are the single most-requested feature in self-hosted PaaS GitHub issues. Kubero's review apps and Qovery's full-stack ephemeral clones demonstrate demand at both the simple and complex ends.
**Value/Cost/Risk:** Very high value (agencies use preview environments for client review; developers use them for QA); medium-high cost (requires per-PR container lifecycle management + Traefik route provisioning + GitHub/GitLab webhook handling); medium risk (stateless apps are straightforward; database cloning for previews adds complexity — ship stateless-only preview first). Harbora's Traefik-native architecture is an advantage here.

### 6. `harbora.yaml` GitOps config file
**Problem:** None of the Segment A competitors have a per-repo declarative config file. Users are locked into the dashboard; CI/CD integration relies on webhooks only.
**Evidence:** `fly.toml`, `render.yaml`, `porter.yaml`, and `apphosting.yaml` are all standard patterns in managed PaaS. Railway's TypeScript SDK is the most ambitious version. Render's Blueprint enables preview environments only when a `render.yaml` exists — showing the config file as an unlock for advanced features.
**Value/Cost/Risk:** High value (enables GitOps workflows, reproducible deploys, team review of infra changes via PR); low-medium cost (parse YAML at deploy time, merge with dashboard settings, dashboard settings as override layer); low risk (file is optional; dashboard fallback always works). Strong agency differentiator.

### 7. Cross-service environment variable references
**Problem:** When a user provisions a PostgreSQL database in Harbora and wants to connect it to their app, they must copy-paste the connection string manually — error-prone and breaks if the database is reprovisioned.
**Evidence:** Railway's `${{Postgres.DATABASE_URL}}` pattern is the most praised quality-of-life feature in Railway community discussions. Zeabur's `${MYSQL_HOST}` and Northflank's auto-wiring solve the same problem.
**Value/Cost/Risk:** High value (dramatically reduces setup friction for the most common use case — app + database); low cost (reference resolver at deploy time, string interpolation in env var values); zero risk. Should ship in v1 alongside database provisioning.

### 8. Monitoring and alerting from day one (no opt-in required)
**Problem:** Rancher requires a separate Helm chart install for Prometheus. CapRover's NetData is optional and controversial. Easypanel's monitoring is paywalled. Dokku has no built-in monitoring at all.
**Evidence:** Fly.io's auto-provisioned Grafana + Prometheus (zero setup) is praised as a differentiator even though Fly.io costs more than alternatives. Coolify's Sentinel sidecar is widely praised despite being proprietary.
**Value/Cost/Risk:** High value (production workloads without monitoring are blind); medium cost (ship a lightweight built-in metrics sidecar plus Discord/Telegram/Slack alerting); low risk. At minimum: CPU, RAM, disk per server, container health per app, alerting channels, cert expiry warnings.

### 9. Built-in multi-channel notifications
**Problem:** CapRover has no built-in notification channels. Easypanel's channels are undocumented (closed source). Dokku has none.
**Evidence:** Coolify supports 7 channels; Dokploy supports 9 (including Ntfy and Gotify — relevant for privacy-conscious self-hosters). Every managed PaaS surveyed includes Slack at minimum.
**Value/Cost/Risk:** High value (deploy failures at 3am need to reach developers wherever they are); low cost (ship Discord, Telegram, Slack, email, generic webhook at launch; add Gotify/Ntfy as they are the channels self-hosters run); low risk. Should be in v1.

### 10. Optional AI Gateway (manage providers, models, tokens, usage limits)
**Problem:** Developers building AI features manage API keys for multiple providers (OpenAI, Anthropic, Gemini, Mistral) scattered across services, with no unified usage visibility, token budgets, or rate limiting.
**Evidence:** Vercel is shipping `vercel ai-gateway` as a first-class CLI command. Qovery is shipping MCP server integration. Zeabur ships a Claude Code plugin. Railway has MCP server support. The trend toward AI-native infra tooling is clear in 2026.
**Assessment:** This is **worth building but not in v1**. The value is real — a self-hosted AI Gateway that lets platform owners manage provider API keys centrally, set per-workspace token quotas, log model usage, and enforce rate limits would be unique in the self-hosted segment. However, cost is medium-high (requires proxy layer, usage accounting, admin UI), and risk is medium (AI provider APIs change frequently; routing logic is non-trivial). Recommended approach: ship as an opt-in module in v2, positioned as "the self-hosted alternative to OpenAI proxy services." It differentiates Harbora from all Segment A competitors and appeals to agencies that resell AI-powered products to clients.

### 11. Deploy stages with pre-deploy migration command
**Problem:** Deploying a new app version that requires database schema changes requires careful orchestration — if the app starts before migrations run, it can crash. No Segment A tool handles this natively.
**Evidence:** Heroku's Release Phase (pre-deploy one-off dyno), Fly.io's release_command, Render's pre-deploy command, and Qovery's deployment stages all solve this pattern.
**Value/Cost/Risk:** High value (production safety for any app with database schema evolution); medium cost (run a one-off container before routing traffic; halt deploy if it exits non-zero); low risk. Ship in v1.

### 12. "Deploy to Harbora" embeddable button
**Problem:** Open-source projects use "Deploy to Heroku" and "Deploy to Render" buttons to drive adoption. No self-hosted PaaS has an equivalent.
**Evidence:** Heroku's app.json + Deploy button drove enormous community adoption of open-source projects. Railway's template marketplace (with creator monetisation) drives 12,000 new signups/day. Vercel's template gallery is a major acquisition channel.
**Value/Cost/Risk:** Medium value (adoption driver for open-source ecosystem, not a core product feature); low cost (implement a redirect URL that accepts a GitHub repo + `harbora.yaml` and provisions the stack); low risk. Ship once `harbora.yaml` format is stable.

---

## 6. Anti-Patterns to Avoid

**Paywalling backups.** Easypanel charges $10.90/mo (Hobby plan) before database backup automation is available. This is the most common community criticism of the product and signals that the platform does not treat user data safety as a baseline obligation. Harbora must include scheduled backup automation at all tiers.

**Closed source on an infrastructure product.** Easypanel's proprietary codebase means users cannot audit what runs on their VPS — a significant trust issue for security-conscious operators. Harbora's open-core or fully open-source model is a competitive advantage, not a cost centre.

**Telemetry without explicit consent.** CapRover's bundled NetData historically sent analytics to external servers without clear disclosure, causing lasting community trust damage. Any telemetry Harbora collects (error reports, usage statistics) must be opt-in, disclosed clearly at install time, and zero-PII by default.

**Docker Swarm lock-in.** Coolify deprecated Swarm in v4.1.2 without a ready replacement, breaking users who relied on it for horizontal scaling. CapRover's entire multi-server model is Swarm-based. Harbora's Docker-only architecture should never depend on Swarm — use Docker-over-SSH for multi-server orchestration to stay independent of the Swarm deprecation cycle.

**Fragmented log experience.** Firebase App Hosting splits build logs (Firebase console) from runtime logs (Google Cloud console). DO App Platform keeps both in one Activity timeline — the right model. Harbora must never split build and runtime logs across different pages or tools.

**Kubernetes complexity for its own sake.** Kubero, Porter, and Qovery all require Kubernetes, adding significant operational complexity for their target users. The consistent theme in self-hosted community discussions is that Docker-native tools win on simplicity for the individual developer and small business segment. Harbora's Docker-only constraint is a feature, not a limitation — communicate it as such.

**Audit logs behind an Enterprise paywall on a self-hosted product.** Dokploy locks audit logs to Enterprise. Render locks workspace audit logs to Pro ($25/mo) and organization audit logs to Scale ($499/mo). For a self-hosted platform where the operator is their own enterprise, basic audit logging of who deployed what and when must be included at team tier at the latest. Security-conscious agencies will not adopt a platform that cannot prove what happened and when.

---

*Document version 1.0 — July 2026. Research basis: official documentation, GitHub, pricing pages, and community discussion as of 2026-07-22. All prices in USD unless noted.*
