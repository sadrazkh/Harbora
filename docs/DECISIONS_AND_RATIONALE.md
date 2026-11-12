Last verified: 2026-11-08
Scope: decisions recorded in `docs/overhaul/09-architecture-decision-records.md`, `docs/superpowers/specs`, `docs/backup-sync/ARCHITECTURE.md`, code comments and round ledgers, each checked against the code at commit `b3fd974`. Updated 2026-11-12 for the round-nine work merged on `r9/int` (`7d7a0ae`), which is not on master or released; nothing else was re-verified.

# Decisions and rationale

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md). `Status` says whether the code still matches the decision. Where the reasoning was never written down, the entry says `Unknown / reconstructed from code` and asks in [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md).

## D-001: Modular monolith, not microservices  (2026-07-23)
- **Context:** One panel, a thin agent and a CLI, for operators running one to a few servers.
- **Decision:** Keep one process with Domain, Application ports, Infrastructure and Data, and Web layers.
- **Alternatives considered:** Splitting into deploy, proxy, backup and monitoring services (rejected: operational burden with no user value at this scale); event-driven microservices (same).
- **Consequences / limitations:** Boundaries hold by discipline only. They have eroded: Web uses concrete Infrastructure types (KI-050). One panel instance only.
- **Do not change without reconsidering:** Installation and upgrade are one compose stack because of this.
- **Status:** Verified in code (one Web host, `Harbora.slnx`), with the boundary caveat above.

## D-002: Docker is the only runtime  (2026-07-23)
- **Context:** The product promises simple self-hosting.
- **Decision:** Use the Docker Engine API behind `IDockerEngine` and `IServerEngineFactory`.
- **Alternatives considered:** Shelling out to the Docker CLI (rejected: injection risk); Kubernetes (rejected: multiplies complexity against the core value); a pluggable runtime (left open only if demand appears).
- **Consequences / limitations:** No overlay network; apps and their databases should share a node.
- **Do not change without reconsidering:** `ServerEngineFactory` must never silently use the local daemon for a remote server. See KI-032 for the one gap.
- **Status:** Verified in code.

## D-003: Traefik through a generated dynamic file  (2026-07-23)
- **Context:** Routes need hot reload, previews and validation.
- **Decision:** Render one YAML file; write through validation, temp file, `.bak` and atomic move.
- **Alternatives considered:** Traefik Docker labels; Nginx templating; Caddy (rejected: the file provider was already correct and gives full control).
- **Consequences / limitations:** Harbora owns rendering and validation.
- **Do not change without reconsidering:** The atomic swap and restore on failure (`TraefikProxyEngine.WriteAsync`).
- **Status:** Verified in code.

## D-004: An explicit deployment state machine  (2026-07-23)
- **Context:** Status used to be mutated inline, with no recovery after a crash.
- **Decision:** One hand-written state machine; illegal transitions throw.
- **Alternatives considered:** A workflow library (rejected as overkill for one nearly linear machine); keeping ad-hoc writes.
- **Consequences / limitations:** The original ADR's `Live` state became `Succeeded`, and `PendingApproval` was added (KI-051).
- **Do not change without reconsidering:** Enum values are persisted; never renumber them.
- **Status:** Verified in code (`DeploymentStateMachine.cs`).

## D-005: A durable job queue in PostgreSQL  (2026-07-23)
- **Context:** An in-memory queue stranded work on restart.
- **Decision:** Jobs are rows, transactional with domain writes, claimed by an in-process pool, with reconcilers at boot.
- **Alternatives considered:** Redis streams; Hangfire or Quartz (rejected: new surface and dependency).
- **Consequences / limitations:** Completed rows were never pruned until round nine (KI-040, fixed on `r9/int`). Redis, which the ADR kept for caching, is unused (KI-052). No tenant fairness.
- **Do not change without reconsidering:** The reconciler ordering behind `JobStartupGate`.
- **Status:** Verified in code.

## D-006: Keep built images, and roll back without rebuilding  (2026-07-23)
- **Context:** Rebuilding on rollback is slow and can fail.
- **Decision:** Retain the last N images per app and re-release a stored tag.
- **Alternatives considered:** Rebuilding from an old ref; registry-backed artifacts.
- **Consequences / limitations:** Disk use. `Runtime:ImageRetentionCount` defaults to 5; 1 removes instant rollback, 0 disables pruning. Compose rollback does not work (KI-012).
- **Do not change without reconsidering:** The rollback pre-flight that checks the image still exists.
- **Status:** Verified in code for ordinary apps.

## D-007: Health decides the cutover  (2026-07-23)
- **Context:** Slow-booting apps were at risk under a single check.
- **Decision:** Start the new replicas, require them healthy, then switch the route and retire the old ones. The ADR's three separate probes were not found as such; the code has one gate.
- **Alternatives considered:** A single check after the route switch.
- **Consequences / limitations:** With no configured path, a 404 at `/` passes, on purpose: an API with no root route is healthy (`HealthProbeRule`).
- **Do not change without reconsidering:** The cutover order (`docs/product-audit/19-do-not-change-list.md` item 2).
- **Status:** Verified in code, with the single-gate difference.

## D-008: One unsettled deployment per app  (2026-07-23)
- **Context:** Racing deploys and duplicate webhooks.
- **Decision:** A second request coalesces onto the unsettled deployment; remote ports come from `HostPortAllocations`.
- **Alternatives considered:** Unknown / reconstructed from code.
- **Consequences / limitations:** A newer commit can be absorbed by an older build (KI-016). The archive route already refuses in this case.
- **Do not change without reconsidering:** The unique "in-flight" guarantee.
- **Status:** Verified in code.

## D-009: Fail closed without a master key  (2026-07-23)
- **Context:** A silent insecure default is unacceptable for stored secrets.
- **Decision:** Outside Development, refuse to start with a missing or known-placeholder `HARBORA_MASTER_KEY`.
- **Alternatives considered:** A loud warning only.
- **Consequences / limitations:** An old `.env` needs the backfill in `install.sh`. Changing the key makes every stored secret unreadable.
- **Do not change without reconsidering:** `MasterKeyResolver`.
- **Status:** Verified in code.

## D-010: Razor with Vue islands, no SPA  (2026-07-23)
- **Context:** A stated constraint: no separate SPA, simple build and deploy.
- **Decision:** Razor renders pages; Vue mounts only interactive pieces.
- **Alternatives considered:** A full Vue or Nuxt SPA; HTMX and Alpine.
- **Consequences / limitations:** No client-side router.
- **Do not change without reconsidering:** One deployable artifact.
- **Status:** Verified in code.

## D-011: Forward-only migrations with a restore point first  (2026-07-23)
- **Context:** A schema change is the one step of an update that cannot be undone.
- **Decision:** At boot: restore point, migrate, seed; exit non-zero on failure. The update is refused if the dump fails.
- **Alternatives considered:** Unknown / reconstructed from code.
- **Consequences / limitations:** `HARBORA_SKIP_UPGRADE_BACKUP=1` is the escape hatch.
- **Do not change without reconsidering:** Boot order (`docs/product-audit/19-do-not-change-list.md` item 13).
- **Status:** Verified in code (`Program.cs:192-228`).

## D-012: Node Agent v1 dials out, and takes only named verbs  (2026-08-04)
- **Context:** The older agent needs an inbound port and has no tests.
- **Decision:** An outbound WebSocket with mTLS against the panel's own CA, a closed verb list, and no shell verb. The old agent is frozen and deprecated, with no removal date.
- **Alternatives considered:** Extending the HTTP agent (rejected: inbound firewall rule, no NAT).
- **Consequences / limitations:** No build or one-off on a node; several features need the panel's own host.
- **Do not change without reconsidering:** Contract rules: schema, C# and conformance tests change together; additive only within v1.
- **Status:** Verified in code.

## D-013: Two backup systems, on purpose  (2026-08-04)
- **Context:** The Kopia brief assumed backup was new; a complete engine already existed.
- **Decision:** Leave the existing engine alone, add a new port and module with Kopia as a second implementation selectable per repository.
- **Alternatives considered:** Replacing the engine (rejected: it encodes hard-won cases such as restore rehearsal and pre-restore snapshots).
- **Consequences / limitations:** Two tables, schedulers and UIs, and two meanings of "verified" (KI-026). The module lacks the legacy host handling (KI-021).
- **Do not change without reconsidering:** The module stays off by default until parity (HARBORA-0046).
- **Status:** Verified in code.

## D-014: Sync is separate from backup  (2026-08-04)
- **Context:** Deletions propagate in sync.
- **Decision:** A separate module, never counted as a backup.
- **Alternatives considered:** Folding it into the backup module.
- **Consequences / limitations:** Needs a Syncthing overlay; off by default.
- **Do not change without reconsidering:** Sync must never appear as a backup.
- **Status:** Verified in code (`Program.cs:50-52`).

## D-015: Prepaid hourly billing with no payment gateway  (2026-08-09)
- **Context:** A provider needs to charge customers.
- **Decision:** The balance belongs to the workspace. Charging is on what is reserved, not measured. `charge(hour) = max(planBaseRate, sum of resource rates)`. Funding is operator credit or single-use vouchers.
- **Alternatives considered:** A user-level wallet (rejected: who pays when two people share a workspace?); measuring disk (rejected: unmeasured volumes would under-bill silently).
- **Consequences / limitations:** Disk billed on allocation. Bandwidth unmetered. Open defects KI-001 to KI-004.
- **Do not change without reconsidering:** The ledger is append-only and each resource-hour has a unique slot.
- **Status:** Verified in code.

## D-016: Locked features are shown, not hidden  (2026-08-16)
- **Context:** Capabilities hide what a role cannot reach, but a provider selling plans needs customers to see what they could buy.
- **Decision:** Entitlements are a third axis, with states Enabled, Locked and Hidden. Every lock is enforced server-side; the grey control is a courtesy.
- **Alternatives considered:** Reusing capabilities or config flags (rejected: neither can say "visible but not yours yet").
- **Consequences / limitations:** This deliberately differs from the capability rule. It is not a bug.
- **Do not change without reconsidering:** Server-side enforcement.
- **Status:** Verified in code.

## D-017: Functions are ordinary apps  (2026-08-18)
- **Context:** Customers want code with no repository.
- **Decision:** A function app is an `App`; publishing generates a build context for the normal pipeline.
- **Alternatives considered:** A per-request runtime with scale to zero (rejected: needs a router that starts containers, a different project).
- **Consequences / limitations:** No scale to zero, no per-request billing, no package installs.
- **Do not change without reconsidering:** Reusing the deployment pipeline.
- **Status:** Verified in code.

## D-018: Launch uses no AI  (2026-11-04)
- **Context:** The owner's biggest complaint was that learning the product is too hard: "a form, give it your app, it deploys". Then: AI is not needed if the job can be done without it.
- **Decision:** A deterministic inspector and one confirm screen. No model anywhere in this path.
- **Alternatives considered:** A model-assisted setup (dropped as phase 4 of the plan); regrouping the 19-field form (rejected: it already shows only what a chosen source needs).
- **Consequences / limitations:** Works with no provider configured. Only stacks the inspector knows are recognised.
- **Do not change without reconsidering:** Nothing is created before the confirm screen.
- **Status:** Verified in code.

## D-019: A launch earns its green  (2026-11-05)
- **Context:** Review found launches going green when nothing had been verified.
- **Decision:** `Succeeded` needs no Failed check, no Warning, and `running` Passed for long-running kinds. Otherwise `SucceededWithProblems`.
- **Alternatives considered:** Counting only Failed checks (the original behaviour).
- **Consequences / limitations:** A launch can honestly end "up, but I could not be sure it works".
- **Do not change without reconsidering:** `LaunchAdvance.VerdictOn`.
- **Status:** Verified in code.

## D-020: A replica does not hold a deployment back  (2026-11-05)
- **Context:** The new wait for databases counted replicas, which can seed for longer than the wait, and it hung a test forever.
- **Decision:** Leave replicas out of the wait; the app gets `REPLICA_URL` on the next deployment after the replica is ready.
- **Alternatives considered:** Waiting for replicas up to the bound (rejected: a read-only address the app does not boot on).
- **Consequences / limitations:** First deployment after a replica is created has no `REPLICA_URL`.
- **Do not change without reconsidering:** `WaitForServicesBesideItAsync`.
- **Status:** Verified in code.

## D-021: Move new installs to another MinIO image, and keep production where it is  (2026-11-05)
- **Context:** `quay.io/minio/minio` began answering 401, so every fresh install failed. MinIO stopped publishing images.
- **Decision:** Pin `ghcr.io/coollabsio/minio` by digest for new installs. Production stays on its existing local image through an untracked `docker-compose.override.yml`, which also sets `Storage__S3__ClientImage`.
- **Alternatives considered:** Upgrading production at the same time (rejected: the 2024-10 to 2025-10 jump over existing data is untested).
- **Consequences / limitations:** A third party builds the default image. Production's image cannot be pulled again, so it must not be pruned.
- **Do not change without reconsidering:** Removing the override without testing the upgrade on a copy.
- **Status:** Verified in code for the repository (`deploy/docker-compose.yml`, `ObjectStorageImagePinTests`). The production override is recorded in `.superpowers/sdd/r8/progress.md`.

## D-022: No Content-Security-Policy  (recorded in code, date unknown)
- **Context:** Vue islands and inline scripts would need a nonce pipeline.
- **Decision:** Send `nosniff`, `X-Frame-Options: DENY` and a referrer policy, without CSP.
- **Alternatives considered:** A CSP without nonces (rejected in the comment: a broken CSP "fails open" because people turn it off).
- **Consequences / limitations:** No CSP protection.
- **Do not change without reconsidering:** Adding CSP needs the nonce work first.
- **Status:** Verified in code (`Program.cs:241-255`).

## D-023: One version line drives every release  (2026-08-24)
- **Context:** Tags and the version file diverged, so every build reported 0.2.0 for a long stretch.
- **Decision:** `<Version>` in `Directory.Build.props` is the single source. Changing it on `master` tags and publishes.
- **Alternatives considered:** Tagging by hand (the cause of the problem). Tags pushed with `GITHUB_TOKEN` cannot trigger workflows, so the release is dispatched explicitly.
- **Consequences / limitations:** Merging a version change publishes. There is no staging step.
- **Do not change without reconsidering:** `tag-release.yml` and `CliReleaseWiringTests`.
- **Status:** Verified in code.

## D-024: One CI check to require  (2026-08)
- **Context:** A PR could go green with half the repo untested. Later, one bad expression silently disabled CI for seven weeks.
- **Decision:** One workflow whose `complete` job fails if any job failed, was skipped or was cancelled.
- **Alternatives considered:** Separate workflows per suite (the earlier state).
- **Consequences / limitations:** The launch proof stays outside the gate because it takes much longer.
- **Do not change without reconsidering:** Keeping "not run" equal to "failed".
- **Status:** Verified in code (`.github/workflows/ci.yml`).

## D-025: Persian is the default language  (date unknown)
- **Context:** Unknown / reconstructed from code. The default has been `fa` since the first commit.
- **Decision:** Default culture `fa`, with `en` as the alternative; the cookie decides first.
- **Alternatives considered:** Unknown / reconstructed from code.
- **Consequences / limitations:** Timestamps in filenames must be invariant-culture, because Persian culture produced Jalali years in artifact names (`docs/backup-sync/ARCHITECTURE.md`).
- **Do not change without reconsidering:** Unknown. See OQ-07.
- **Status:** Verified in code; the rationale is not recorded.

## D-026: The default billing currency is IRR  (2026-08-10)
- **Context:** Unknown / reconstructed from code. Currency is fixed per install.
- **Decision:** `Billing:Currency` defaults to `IRR`.
- **Alternatives considered:** Unknown / reconstructed from code.
- **Consequences / limitations:** An install elsewhere must change it before billing starts.
- **Do not change without reconsidering:** Changing currency after ledger lines exist.
- **Status:** Verified in code; the rationale is not recorded.

## D-027: Redis stays in the compose stack  (date unknown)
- **Context:** Unknown / reconstructed from code. ADR-005 expected a use for it; none exists.
- **Decision:** The container and the package remain.
- **Alternatives considered:** Unknown / reconstructed from code.
- **Consequences / limitations:** An idle container on every install.
- **Do not change without reconsidering:** See OQ-08.
- **Status:** Verified in code; the rationale is not recorded.

## D-028: Real env files stay on the developer's machine by default  (2026-11-12)
- **Context:** The CLI excluded only `.env` and `.env.local`, so a folder with no ignore file uploaded `.env.production` with its secrets.
- **Decision:** `.env` and every `.env.<name>` that is not an example stay home, on the CLI and for a browser zip, through one rule in `Harbora.Shared/EnvFiles.cs`. A file left out is named in the output. A `!` line in `.dockerignore`, `.gitignore` or `harbora.yml` opts one in; `.env` and `.env.local` can never be opted in.
- **Alternatives considered:** Excluding silently (rejected: Vite, Next.js and Create React App read `.env.production` at build time, so a build would change with no explanation); a second opt-in mechanism (rejected in favour of the ignore files already read).
- **Consequences / limitations:** A Dockerfile that copies a kept env file now fails the preflight with what to do instead. `<name>.env` and `.envrc` are not covered. A Git deploy still carries a committed `.env.production`.
- **Do not change without reconsidering:** Example names must keep going, because Launch reads variable names from them.
- **Status:** Verified in code on `r9/int`.

## D-029: A new database gets a daily backup keeping seven  (2026-11-12)
- **Context:** A database made by Launch had no backup until its owner set one up.
- **Decision:** `DatabaseProvisioningService.CreateAsync` makes a daily schedule keeping seven after the creation has committed. The first run is 24 hours later. A refusal is a sentence, and the database is created regardless.
- **Alternatives considered:** The same transaction as the database (rejected: a schedule must never fail or slow a creation); a first run as soon as the database is running (rejected: a schedule firing during setup fails and alerts).
- **Consequences / limitations:** Only PostgreSQL, MySQL, MariaDB and MongoDB; not templates; none on a plan that keeps fewer than seven (KI-097). If the panel dies between the two steps the database has no schedule and nobody is told.
- **Do not change without reconsidering:** The plan's schedule and retention limits are checked the same way the manual path checks them.
- **Status:** Verified in code on `r9/int`.

## D-030: Settled jobs are kept 30 days  (2026-11-12)
- **Context:** `Jobs` held 4.2 million rows in production and was in no retention rule.
- **Decision:** Succeeded, Failed and Cancelled jobs older than `Retention:JobDays` (30) are deleted in batches of 10,000, oldest first; a job counts as old only when both queued and finished before the cutoff.
- **Alternatives considered:** One statement (rejected for a table that size); a new index (not needed: `(Status, CreatedAt)` exists, and the round added no migration).
- **Consequences / limitations:** Hourly-billing jobs are included, though a comment calls them an audit trail; nothing reads them. Deleting rows does not return disk space without `VACUUM (FULL)`. Not measured on a table of production size.
- **Do not change without reconsidering:** A Pending or Running job is never deleted, whatever its age.
- **Status:** Verified in code on `r9/int`.

## D-031: Whether a webhook is in place is asked of the provider, not stored  (2026-11-12)
- **Context:** The app page must say honestly whether pushes deploy on their own.
- **Decision:** The page asks the provider and holds the answer for five minutes (one if not confirmed). It says pushes deploy only when the provider has just listed Harbora's hook as active.
- **Alternatives considered:** A new column (rejected: no migration this round); an audit row (rejected: the retention sweep deletes it).
- **Consequences / limitations:** A miss can cost up to five seconds on the app page. No call has met a real GitHub, GitLab or Gitea.
- **Do not change without reconsidering:** The page never claims a hook it did not confirm.
- **Status:** Verified in code on `r9/int`.

## D-032: A build's directory does not outlive its deployment  (2026-11-12)
- **Context:** Build directories accumulated for ever on a persistent volume.
- **Decision:** The pipeline removes its directory when the deployment ends, after success or failure, and a daily sweep removes directories no unsettled deployment owns.
- **Alternatives considered:** Keeping a failed build's directory to look at (rejected: nothing reads it, and it is what fills the disk).
- **Consequences / limitations:** A failed checkout cannot be inspected afterwards. `_uploads` and Launch's own directories are not swept.
- **Do not change without reconsidering:** The sweep never follows a link and never leaves the work directory.
- **Status:** Verified in code on `r9/int`.
