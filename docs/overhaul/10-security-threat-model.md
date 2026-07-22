# 10 — Security & Reliability Threat Model

Method: STRIDE-flavored, asset- and trust-boundary-driven, mapped to Harbora's actual components.
For each threat: **risk**, **current state** (as-built), and **mitigation** (target). No real
secrets/tokens appear anywhere in code, tests, logs, or commits — enforced by the redactor and by
review.

> Overall posture: Harbora already has strong primitives (AES-GCM secrets, PBKDF2, HMAC webhooks,
> hashed tokens, secret redaction, CSRF, per-tenant network isolation, optional agent mTLS). The
> gaps are (1) a fail-open master-key default, (2) build-input trust (repos/Dockerfiles/compose),
> (3) SSRF from user-supplied URLs, (4) per-action authorization, and (5) audit coverage.

## 1. Assets & trust boundaries

**Assets:** user credentials & sessions; API/CLI tokens; app secrets/env; Git credentials; TLS
private keys/certs; backup archives (may contain DB data); the master encryption key; the Docker
socket (≈ root on the host); tenant data & network isolation; the panel↔agent channel.

**Trust boundaries:**
1. Internet → Traefik → panel (public HTTP).
2. Panel → Docker socket / remote agent (privileged runtime control).
3. Panel → managed databases / user containers (tenant workloads).
4. Panel → external systems (Git providers, S3, ACME, notification webhooks).
5. Tenant ↔ tenant (multi-tenancy isolation).
6. Build system → arbitrary user-supplied code (repos, Dockerfiles, compose, images).

## 2. Threats & mitigations

### 2.1 Git credential storage
- **Risk:** stored provider tokens leak (DB dump, logs).
- **Current:** tokens encrypted with AES-GCM (`ISecretProtector`) before persistence; redactor
  strips them from logs.
- **Mitigation:** ratify; ensure tokens are never returned in API responses or rendered; scope
  tokens minimally; support rotation; add tests asserting non-exposure.

### 2.2 Secret encryption & the master key
- **Risk (critical):** `AesGcmSecretProtector` defaults to `dev-insecure-master-key-change-me` when
  `HARBORA_MASTER_KEY` is unset → all "encrypted" secrets trivially decryptable.
- **Current:** installer generates a strong key; but an app started without it silently uses the
  insecure default.
- **Mitigation:** **fail closed in Production** (ADR-009): refuse boot without an explicit key;
  versioned keys + rotation; document recovery. Dev keeps a loud-warning default only.

### 2.3 SSH / agent key & token management
- **Risk:** agent bearer token or mTLS material leaks → full remote Docker control.
- **Current:** bearer token required on every agent call except health; optional mTLS (custom CA);
  token not logged.
- **Mitigation:** prefer mTLS for multi-server; rotate tokens from the UI; store agent creds
  encrypted; never expose in API/UI; alert on repeated 401s (possible probing).

### 2.4 Docker socket access / privilege escalation / container escape
- **Risk (critical):** the panel/agent hold the Docker socket = effective root; a compromised panel
  or a malicious build can escalate to the host.
- **Current:** typed `Docker.DotNet` API (no shell strings); containers run with memory/CPU limits;
  restart policy set.
- **Mitigation:** run user workloads with least privilege (`no-new-privileges`, drop capabilities,
  non-root where possible, read-only rootfs where feasible, no host bind of the socket into tenant
  containers); never mount the Docker socket into user apps; consider rootless/`sysbox` runtime as
  an option; keep the agent minimal and network-restricted.

### 2.5 Command injection
- **Risk:** attacker-controlled names/refs/paths injected into shelled commands.
- **Current:** container ops use the typed Docker API; git via LibGit2 (no shell). One-off backup
  containers run fixed `sh -c "tar …"` with controlled paths.
- **Mitigation:** keep zero shell-string interpolation of user input; validate slugs/refs/paths
  against strict allowlists; treat any new shell usage as a review red flag; unit-test injection
  attempts.

### 2.6 Malicious repository / Dockerfile / unsafe compose
- **Risk:** hostile build content escapes the builder, exfiltrates secrets, or abuses build args.
- **Current:** builds run in Docker build; build-time env is limited to vars flagged
  `AvailableAtBuild`.
- **Mitigation:** build with least privilege and no host mounts; do not expose the master key or
  unrelated secrets to builds; time/size/resource limits on builds; for compose (ADR-014) enforce a
  **supported-directive allowlist** — reject host bind mounts, `privileged`, `pid/host`,
  socket mounts, arbitrary `cap_add`; scan images/base pins where feasible; document the sandbox.

### 2.7 Supply-chain attack
- **Risk:** compromised base images or build tools.
- **Current:** buildpacks pull public base images (unpinned); Traefik/Postgres/Redis pinned in
  compose.
- **Mitigation:** pin base images by **digest**; pin the .NET pack to current LTS; verify Traefik
  Docker-API compat (installer already does); optional image signature/scan step; lockfile-aware
  builds.

### 2.8 SSRF (server-side request forgery)
- **Risk:** user-supplied URLs (git clone, image registry, webhook/notification endpoints, S3
  endpoint, ACME) make the panel fetch internal/metadata endpoints (`169.254.169.254`, RFC1918).
- **Current:** the panel makes outbound calls to user-configured Git APIs, notification webhooks,
  S3 — with no egress allowlist.
- **Mitigation:** validate/normalize URLs; block link-local/loopback/RFC1918 targets for
  user-provided webhook/notification/registry URLs unless explicitly allowed; DNS-rebinding-safe
  resolution; timeouts + size caps; egress policy documented; separate "internal" vs "user-supplied"
  HTTP clients.

### 2.9 Path traversal
- **Risk:** `../` in build context path, volume mount path, backup key, or log/download routes
  escapes intended directories.
- **Current:** build context derived from `BuildContextPath` (trimmed) under a workdir; backup keys
  server-generated.
- **Mitigation:** canonicalize + assert containment under the app/workspace root for every
  filesystem path; reject traversal; never use user input directly as a download filename/path;
  tests for `../`, absolute paths, symlinks.

### 2.10 Webhook forgery
- **Risk:** forged Git webhooks trigger unauthorized deploys.
- **Current:** `GitWebhookProcessor` verifies HMAC signatures; per-repo secret.
- **Mitigation:** ratify; constant-time compare; reject on missing/invalid signature; **de-duplicate
  by delivery id** (also fixes idempotency H3); rate-limit; log verification failures to audit.

### 2.11 Cross-tenant data leakage / authorization bypass
- **Risk:** a tenant reads/acts on another tenant's apps, services, logs, backups, or reaches their
  containers.
- **Current:** queries scope by `WorkspaceId`; each tenant gets an isolated Docker network;
  controllers check `WorkspaceId` on most reads.
- **Mitigation:** centralize workspace scoping (a query filter / guard) so no endpoint can forget
  it; **enforce authorization per action** (ADR/RBAC), not just per nav; verify network isolation
  (no cross-tenant reachability) with a test; object-reference checks on every id (prevent IDOR).

### 2.12 Authorization (RBAC) enforcement
- **Risk (high):** roles exist but actions aren't consistently gated → a Viewer/Developer performs
  privileged operations via direct POST/API.
- **Current:** `[Authorize]` + coarse role checks in nav; not uniformly enforced per action.
- **Mitigation:** ASP.NET **authorization policies** per capability (deploy, delete, restore,
  manage-members, manage-servers); apply to controllers **and** the API; deny-by-default; test the
  matrix (doc 13); hide + enforce.

### 2.13 Auditability
- **Risk:** no reliable record of who did what (incident response, insider risk, compliance).
- **Current:** `AuditLog` entity exists; coverage sparse; no UI/export.
- **Mitigation:** write an audit entry for every privileged/destructive action (deploy, rollback,
  env/secret change, member/role change, server add, backup/restore, token issue) with actor,
  workspace, target, ip, timestamp; tamper-evident (append-only) storage; UI + CSV/webhook export.

### 2.14 Backup encryption & storage
- **Risk:** backup archives (DB data) leak at rest or in transit to S3.
- **Current:** local + S3 backups; retention; not encrypted at rest by default.
- **Mitigation:** encrypt backup archives (envelope encryption with the master key or a per-backup
  key); TLS to S3; restrict local backup dir perms; document restore-key handling; **dry-run
  restore** to avoid destructive surprises.

### 2.15 Certificate storage
- **Risk:** TLS private keys (ACME) readable by other containers/users.
- **Current:** Traefik manages ACME storage (`acme.json`) in its volume.
- **Mitigation:** ensure `acme.json` is `600`, on a restricted volume, not exposed to tenant
  containers; back it up encrypted; document rotation.

### 2.16 Server↔agent authentication & update integrity
- **Risk:** MITM or rogue agent; tampered updates.
- **Current:** token (+optional mTLS); installer verifies Traefik/Docker compat.
- **Mitigation:** prefer mTLS in production multi-server; pin agent image by digest; sign releases
  (checksums at minimum) so `install.sh update` verifies integrity; document the trust chain.

### 2.17 Session & transport
- **Current:** cookie auth (HttpOnly, SameSite=Lax, Secure when HTTPS), 7-day sliding; CSRF header;
  HSTS in production.
- **Mitigation:** `Secure` cookies always in production (HTTPS enforced by Traefik); consider
  shorter session + refresh; add optional 2FA (TOTP) for owner/admin; brute-force throttling on
  login; rotate session on privilege change.

### 2.18 Rate limiting & abuse
- **Risk:** brute force on login/API; deploy/backup floods.
- **Current:** none explicit.
- **Mitigation:** rate-limit auth + token endpoints + webhooks; per-workspace concurrency caps on
  deploys/backups (also reliability); alert on anomalies.

## 3. Reliability & recovery (safety as security)

- **Crash recovery (C2):** boot reconciler resolves in-flight deploys/backups; nothing left
  "Building" forever (ADR-005). *Test:* kill mid-deploy → clean state.
- **Idempotency (H3):** deploys, webhooks, backups keyed and de-duplicated (ADR-008).
- **Atomic config:** Traefik apply is write-tmp→backup→swap→rollback (as-built). *Test:* golden
  files + forced-failure rollback.
- **Zero-downtime cutover (C4):** old container serves until new is ready (ADR-007).
- **Backups before risk:** auto pre-upgrade/pre-restore backups (R-BAK-3).
- **Data integrity:** forward-only migrations, tested on a copy, preceded by a backup (doc 11/14).

## 4. Secure defaults checklist (must hold out-of-the-box)

- [ ] No boot in Production without an explicit master key (fail closed).
- [ ] HTTPS enforced; Secure/HttpOnly cookies; HSTS on.
- [ ] Secrets encrypted at rest; never returned or logged (redactor on).
- [ ] Webhooks HMAC-verified + de-duplicated; auth + webhook endpoints rate-limited.
- [ ] Authorization deny-by-default, enforced per action, on UI **and** API.
- [ ] Tenant network isolation verified; workspace scoping centralized (no IDOR).
- [ ] User workloads run least-privilege; Docker socket never exposed to tenant containers.
- [ ] User-supplied URLs SSRF-guarded; filesystem paths containment-checked.
- [ ] Backups + `acme.json` encrypted/permission-restricted; releases checksum/signature-verified.
- [ ] Every privileged action audited; audit exportable.

## 5. Priority (for the roadmap)

1. Fail-closed master key (2.2) · 2. Per-action authorization + workspace scoping (2.11/2.12) ·
3. Build-input least-privilege + compose allowlist (2.4/2.6) · 4. SSRF + path-traversal guards
(2.8/2.9) · 5. Webhook de-dup + rate limiting (2.10/2.18) · 6. Audit coverage + export (2.13) ·
7. Backup/cert encryption (2.14/2.15) · 8. 2FA + session hardening (2.17). These map to roadmap doc
12 (interleaved with the feature phases they protect).
