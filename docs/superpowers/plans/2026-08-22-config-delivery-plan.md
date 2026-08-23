# Configuration delivery — connection strings and file overrides

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development.
> Three sub-projects, ordered. C1 and C2 are independent; C3 depends on C2.

**Goal:** Let a customer connect an app to a Harbora database without touching their code, and
without a secret ever reaching Git — by delivering configuration the way their framework already
reads it, not only as environment variables.

**The owner's problem, in their words:** a database must be reachable *by connection string*, not
only as environment variables. And separately: so that a password never lands in Git, the panel
should be able to **overwrite values inside a deployed app's own config file** (`appsettings.json`
or whatever the app reads) with values entered in the panel — read the file, show its keys, let
the operator say "always replace these parameters for this app", and apply it at deploy time.
That is both safer and closer to how most developers already work.

---

## Verified current state — checked 2026-08-22

| Fact | Evidence |
|---|---|
| **Attaching a database to an app injects nothing.** `DatabaseAccessGrant` is about a *person's* access to a database (temporary/permanent, Adminer, external), not an app's connection. Nothing in `DeploymentPipeline.BuildEnv` computes database credentials for an app. | `DatabaseAccessGrant.cs:43-55`; grepped `BuildEnv` |
| Two attach-and-inject paths **do** exist and are the shape to follow: buckets (`AppStorageBucket` → `S3_*`) and SMTP providers (`SMTP_*`), both merged through `ConfigGroupMerge`, both with provenance on the app's env page and a named-list delete refusal | `DeploymentPipeline.cs:76-82`, F5/F6 (2026-08-21) |
| `ConfigGroup`/`ConfigGroupEntry`/`AppConfigGroup` ship workspace-level shared variable groups with precedence (app's own value wins), secret masking via `ISecretProtector`, provenance, and the `HasUnpublishedChanges` "applies on next deploy" convention | 2026-08-20 sub-project 9 |
| **No file-overwrite capability exists for customer apps.** Every `appsettings`/`ConfigFile`/`PutArchive` hit in the codebase belongs to the panel itself or the Kopia backup module — verified individually, all four were false positives in a first sweep | grepped 2026-08-22 |
| Managed services and their generated credentials exist for PostgreSQL, MySQL, MariaDB, Redis, MongoDB, RabbitMQ, NATS, with rotation | `Enums.cs:140-157`, `ServiceCatalog.cs`, `CredentialRotationPlan.cs` |
| Secrets at rest use `ISecretProtector`; `EnvironmentVariable.IsSecret` carries ciphertext in `Value` | `SecurityAbstractions.cs`, `EnvironmentVariable.cs` |
| Credential rotation exists and a rotation's whole point is that the *wanted* value is new — so anything caching a credential must re-read it, not compare-and-skip | `CredentialRotationPlan.cs`; the 2026-08-17 rotation work |

---

## Global constraints — binding

Same as `docs/superpowers/plans/2026-08-21-functions-and-services-plan.md`'s **"Global constraints"**
section; read it in full. In summary: zero new build warnings (exactly 2 pre-existing NU1903 stay) ·
never assume a baseline, run `dotnet test Harbora.slnx` first and report both numbers · run both
`dotnet build` and `npm run build`, entry bundle ~126.36 kB gzip must not grow · test-first ·
the panel renders Persian by default so assert on `data-` attributes, never sentences · bilingual
`isFa`/`T["…"]` · semantic tokens only · technical tokens monospace `dir="ltr"` · three states per
table, never a fabricated value · migrations build-first-then-scaffold · one worktree per
sub-project, commit as you go, never `git add -A`, never stash/reset --hard/clean · no Docker or
live PostgreSQL here, so say plainly what was and was not proven.

**The law:** twenty times in this programme a capability assumed missing already existed — four of
them inside plans that warn against exactly this. **Search for what a thing does, not for what you
would have called it**, and report what you found.

**Standing owner instruction:** cybersecurity and vulnerability review are out of scope. Building
these features is in scope; adversarially auditing them is not.

---

## C1 — Attach a database to an app, and give it a real connection string

**The gap:** a customer creates a PostgreSQL service and an app, and there is no supported way to
connect them. They copy credentials by hand — which is exactly the manual wiring a PaaS exists to
remove, and exactly how a password ends up in Git.

**Build:** an `AppManagedService` attach, mirroring `AppStorageBucket` exactly — attach/detach on the
app and on the database, provenance on the app's env page, named-list refusal when deleting a
database that apps still use, `DeleteBehavior.Restrict` as the database-level backstop.

**What gets injected — this is the substance of the owner's request.** Not only discrete parts, but
a **ready-to-use connection string** in the dialect the engine actually needs:

- PostgreSQL: an ADO.NET/Npgsql string (`Host=…;Port=…;Database=…;Username=…;Password=…`) **and** a
  `postgres://` URI, because .NET and Node/Python ecosystems each expect a different one.
- MySQL/MariaDB, MongoDB, Redis: the same treatment, each in its own conventional form.
- The discrete parts too (`…_HOST`, `…_PORT`, `…_USER`, `…_PASSWORD`, `…_NAME`), because plenty of
  apps want those instead.

**Find whether a connection-string builder already exists in this codebase before writing one** —
Adminer, the backup engine's dump/restore, and the rotation path all reach these services and one of
them probably already composes a string. Reuse it; a second builder that formats differently is a
support burden and a bug waiting to happen.

**Naming:** an app attached to two databases must get unambiguous names. Decide the scheme (a
per-attachment alias the customer sets, defaulting to the service's slug) and make collisions
impossible rather than last-write-wins.

**Rotation must not leave a stale string behind.** When a database's credentials rotate, every app
attached to it has a connection string that no longer works. Follow what the existing rotation path
already does for apps — read `CredentialRotationPlan` and the 2026-08-17 rotation work — and make
the app's staleness visible with the `HasUnpublishedChanges` idiom rather than silently wrong.

**Test at the seam where env becomes container environment** — `RunRequests[...].Env` through the
fake engine, the way `ConfigGroupPipelineTests` and the bucket tests do. A test must prove a
connection string composed by the panel is byte-identical to what the app receives.

## C2 — File overrides: the panel replaces values inside the app's own config file

**This is the owner's main request and the reason C1 alone is not enough.** A .NET developer reads
`appsettings.json`. They do not want to rewrite their code to read environment variables, and they
must not commit a password. So: keep the file in Git with a placeholder, and have Harbora replace
the real values at deploy time.

**Shape:** per app, a list of override rules. Each names a **file path inside the container**
(`appsettings.json`, `appsettings.Production.json`, `config/database.yml`, …), a **key path within
that file** (`ConnectionStrings:Default`, `Redis:Host`), and a **value** — literal, secret
(encrypted at rest), or **a reference to an attached service's connection string from C1**. That
last one is what makes the two sub-projects worth building together: attach a database, point
`ConnectionStrings:Default` at it, and never see the password.

**Formats:** JSON first — it is what `appsettings.json` is and what the owner named. Decide whether
to add more (YAML, `.env`, `.ini`) now or later and say why. **Do not silently mangle a format you
do not fully parse**; refuse with a clear message instead.

**Applied at deploy time, inside the built image's container, before the app starts.** Work out
where in `DeploymentPipeline` that belongs, and prefer whatever mechanism the platform already has
for putting content into a container over inventing a new one. Two properties are not negotiable:

1. **If an override cannot be applied, the deployment must fail loudly and say which rule and why** —
   a missing file, an unparseable file, a key path that does not exist. An app that silently starts
   with its placeholder password, connects to nothing, and reports "deployed" is precisely the defect
   class this codebase has spent weeks removing.
2. **A secret value must never be written into the image, only into the running container**, and
   never echoed back to the panel in plaintext. Masked in the UI like every other secret.

**The read-and-show half the owner asked for:** the panel should be able to **read the deployed
app's config file and show its keys**, so an operator can pick which ones to override instead of
typing paths blindly. Check what already exists — `AppDataController` browses volumes at
`apps/{id}/data`, and that is the kind of capability that was assumed missing here before and
turned out to ship. If reading a file out of a running container is already possible, reuse it.

**Rollback and re-deploy must re-apply the overrides**, deriving them fresh from current panel state
— never baked into the image, never carried from a previous run. The config-groups work established
exactly this and has a test for it; follow both.

## C3 — Make the choice legible

*Depends on C2.*

Three ways to configure an app now exist — environment variables, shared config groups, and file
overrides — plus attached services feeding all three. **An operator must be able to see, in one
place, what this app will actually receive and where each value came from.** The env page already
shows provenance for groups, buckets and SMTP; extend that view to cover connection strings and file
overrides rather than adding a fourth separate list.

Include a short Learning Centre guide explaining when to reach for which — env vars for
twelve-factor apps, file overrides for frameworks that read files, groups for values shared across
apps. Follow the existing guide structure.

---

## Landing order

C1 and C2 are independent enough to run in parallel **if** C2's value-reference to a connection
string is stubbed behind an interface C1 fills in; otherwise run C1 first. C3 last. Deploy after
each, and verify each migration against the live server's PostgreSQL — the lane that exists and
went unused for a week.
