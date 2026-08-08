# 17 — Next Roadmap

> ### ⛔ Superseded for planning
>
> **`docs/product-audit/17-implementation-roadmap.md` is the plan. This document is history.**
>
> Four documents in this directory number their phases in four incompatible ways, and each was
> written believing it replaced the previous one:
>
> | Document | Scheme |
> |---|---|
> | `12-implementation-roadmap.md` | **P0 – P15+** |
> | `15-phase-plan.md` | **Phase A, B, C, …**, re-sequencing doc 12 after PR #1 merged |
> | `16-paas-strategy.md` §7 | **Phase 0 – 9**, which is the scheme `progress.md` writes in |
> | `17-next-roadmap.md` | **R0 – R5** |
>
> So "Phase 2" means at least three different bodies of work depending on which one you have open,
> and "done" in one of them does not mean done in another. Nothing here is deleted — the design
> reasoning, the requirement traceability and the record of what was actually verified on a live
> host are all worth keeping, and several of these documents are the only place a decision was
> written down. But **do not schedule work from this directory.** Take the phase numbers in
> `docs/product-audit/17-implementation-roadmap.md`, which are the ones the current work uses.
>
> The delivery rules in `17-next-roadmap.md` are the exception: they are policy, not a plan, and
> `docs/product-audit/19-do-not-change-list.md` adopts them as an exit gate.


Updated 2026-08-03 after the panel and template-marketplace implementation. This document is the
short execution plan for work that remains; the earlier roadmaps retain the design history and
requirement traceability.

## Current baseline

Already delivered and therefore **not** future scope:

- project and environment model, project-scoped private networking and managed services;
- durable deployment jobs, safe cutover/rollback foundations and encrypted secrets;
- bilingual LTR/RTL application shell, responsive navigation, theme controls and command palette;
- a searchable template marketplace with detail and pre-deploy review screens;
- multi-service templates with dependency-first provisioning, reference variables, volumes,
  generated credentials, health paths, quota/authorization checks and an audit event;
- curated app starters and managed PostgreSQL, MariaDB, MySQL, Redis and MongoDB templates.

The baseline gate is a clean backend build, clean frontend production build, zero npm audit findings,
the complete automated test suite, and desktop/mobile browser smoke tests in both LTR and RTL.

## Order of execution

### R0 — release proof on a real Docker host (next)

**Outcome:** prove that the new marketplace creates resources that do not merely look correct in the
database but become healthy containers on a representative production host.

- Run WordPress + MariaDB and Redis Commander + Redis end to end on an isolated Docker server.
- Exercise one Git/buildpack starter, one image template and every managed database engine.
- Verify dependency ordering, internal DNS, secret injection, volume persistence, health checks,
  public route/SSL, cancellation, retry and rollback.
- Add the reproducible smoke script to CI's Docker-capable lane and retain sanitized diagnostics.

**Exit gate:** all scenarios reach Healthy; a deliberately broken health check never receives
traffic; restart preserves data; rollback restores the previous artifact.

### R1 — template platform v2

**Outcome:** turn the built-in catalog into an ecosystem that teams can safely extend and maintain.

- Version the manifest schema and add migrations plus forward-compatibility validation.
- Add editable CPU/RAM/replica/volume plans to the review screen without hiding defaults.
- Add a resource diff before creation, template version/update tracking and release notes.
- Add private organization catalogs, import/export, ownership, review workflow and optional signing.
- Expand the shipped Docker Compose allowlist and add a pre-create diff that shows accepted and
  unsupported fields before any resource is created.

**Exit gate:** an old manifest still deploys; every mutation is authorized and audited; the preview
exactly matches the created resource graph; unsupported Compose input is rejected atomically.

### R2 — real global search and operator UX

**Outcome:** make the command palette search resources and execute safe actions, not only navigate.

- Permission-filtered search across projects, apps, deployments, domains, databases and logs.
- Recent/favourite resources, keyboard-first actions, empty/loading/error states and fuzzy matching.
- Finish the remaining page-by-page migration to the shared design primitives.
- Add automated accessibility and visual-regression coverage for 390/768/1280/1536 px, light/dark,
  English/Persian and reduced motion.

**Exit gate:** no inaccessible resource appears in results; common destinations are reachable in at
most three keystrokes; WCAG 2.2 AA checks pass on critical flows; no horizontal overflow.

### R3 — safer delivery workflows

**Outcome:** provide the deployment controls expected from a mature PaaS.

- Separate startup, readiness and liveness probes.
- Pre-deploy/release commands for migrations, aborting cutover on failure.
- Queue/cancel-in-progress policy, deployment concurrency limits and idempotency reporting.
- Preview environments and artifact promotion from preview to staging to production without rebuild.
- Rolling, canary and blue/green strategies where capacity permits.

**Exit gate:** failed migrations and failed readiness never affect live traffic; promotion preserves
the artifact digest; concurrent webhook bursts produce one explainable release.

### R4 — observability and incident operations

**Outcome:** make failures diagnosable from Harbora without SSH as the default workflow.

- Durable indexed logs with search, time range, context, retention and download.
- Per-release CPU, memory, disk, network and latency/error-rate views.
- Alert rules, notification routing, acknowledgement, silencing and an incident timeline.
- Platform SLOs for deploy success, queue latency, certificate renewal and backup freshness.

**Exit gate:** an operator can move from an alert to the failing release and relevant log context in
one flow; retention/quota behavior is predictable and tested.

### R5 — security, collaboration and commercial readiness

**Outcome:** make the control plane suitable for larger teams and an internet-facing release.

- TOTP/WebAuthn, recovery codes, session/device management and optional OIDC/SAML SSO.
- Project/environment roles, temporary elevation, service accounts and scoped token rotation.
- Audit export, tamper-evidence, secret rotation and dependency/image vulnerability policy.
- Usage ledger reconciliation, invoices, plan transitions and quota grace behavior.
- Upgrade/rollback runbook, compatibility matrix, telemetry opt-in and operator documentation.

**Exit gate:** threat-model checks and restore drills pass; privilege boundaries have integration
tests; billing totals reconcile from immutable usage events; upgrades are rehearsed from the previous
supported release.

## Delivery rules

1. R0 is the only blocker for calling the current marketplace production-proven.
2. Work inside each milestone can run in parallel, but milestone exit gates are sequential.
3. Every change requires backend build/tests, frontend build/audit when applicable, authorization and
   tenant-isolation review, migration/rollback notes, and a browser pass for changed flows.
4. Documentation must describe shipped behavior only; future behavior stays in this roadmap.
5. No phase is marked done because its UI exists—the resource lifecycle and failure path must also be
   exercised.

## Product references

The template and delivery direction follows the review-before-deploy and infrastructure-as-code
patterns documented by Railway, Render and DigitalOcean:

- <https://docs.railway.com/templates/deploy>
- <https://docs.railway.com/templates/create>
- <https://docs.render.com/deploy-to-render>
- <https://docs.render.com/blueprint-spec>
- <https://docs.digitalocean.com/products/app-platform/reference/app-spec/>
