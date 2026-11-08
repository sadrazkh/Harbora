Last verified: 2026-11-08
Scope: repository at commit `b3fd974` on `master`, read statically (code, config, CI, existing docs, git history). Nothing was built or run for this baseline. Test figures come from the full run of 2026-11-05 on `72899f5`, whose code is identical to `b3fd974`.

# Harbora: master context

Start here. This file says what the project is, where it stands, and which document holds what.

Labels used in every baseline document: **[Verified]** was checked against the code and names the file or function. **[Assumption]** was not checked or cannot be checked from the repo. **[Recommendation]** is advice, not a fact. Where a whole table carries one label, the label sits above the table.

## What Harbora is

[Verified] A self-hosted platform-as-a-service. An operator installs one panel on a server (`deploy/install.sh`); people then deploy applications and managed databases through it, on that server or on enrolled nodes. It is multi-tenant: workspaces, plans, quotas, a prepaid wallet and hourly billing (`src/Harbora.Domain`, `src/Harbora.Infrastructure/Billing`).

[Verified] It is one ASP.NET Core process (`src/Harbora.Web/Program.cs`) using Razor views with Vue islands, PostgreSQL through EF Core, the Docker Engine API (`IDockerEngine`), and Traefik as the reverse proxy. Background work runs in-process from a durable queue stored in PostgreSQL (`src/Harbora.Infrastructure/Jobs`).

## Current state

**[Verified]** every row, from the source named.

| Fact | Value | Source |
|---|---|---|
| Product version | 0.12.0 | `Directory.Build.props` |
| Latest release tag | `v0.12.0`, 2026-11-05 | `git tag` |
| First commit, commit count | 2026-07-06, 1075 | `git log` |
| Runtime | .NET 10, Node 22 (front-end build only) | `Dockerfile` |
| Database | PostgreSQL 16, 128 `DbSet`s, 122 migrations | `deploy/docker-compose.yml`, `src/Harbora.Data` |
| Production C# (no migrations) | about 159,000 lines; Infrastructure is 80,930 of them | line count of `src/**/*.cs` |
| Razor views | 17,358 lines | `src/Harbora.Web/Views` |
| `Harbora.Tests` | 9,457 passed, 0 failed | run of 2026-11-05 |
| `Harbora.NodeAgent.Tests` | 500 passed, 17 skipped | run of 2026-11-05 |
| `Harbora.NodeIngress.Tests` | 15 passed | run of 2026-11-05 |
| `Harbora.Postgres.Tests` | 117; needs Docker, green in CI run 42031668424 | CI |
| Front-end tests (`node --test`) | 533 passed | run of 2026-11-05 |
| Backlog tracker | 66 items: 41 done, 16 partial, 9 open | `docs/product-audit/backlog.json` |
| License | none; the README says "TBD" and there is no LICENSE file | `README.md` |

[Verified] Production runs commit `7e90af0` (v0.12.0), deployed 2026-11-05 (`.superpowers/sdd/r8/progress.md`). The two later commits on `master` change only that ledger.

[Verified] The repository is public on GitHub. Production host addresses and access details are deliberately not written in these documents.

## Repository map

| Path | What it holds |
|---|---|
| `src/Harbora.Web` | The panel: controllers, Razor views, Vue islands (`Scripts/`), middleware, `Program.cs` |
| `src/Harbora.Infrastructure` | Almost all behaviour: deploy pipeline, Docker, Traefik, billing, backups, agent, launch |
| `src/Harbora.Application` | Ports (`Abstractions/`), interfaces only |
| `src/Harbora.Domain` | Entities and enums |
| `src/Harbora.Data` | `HarboraDbContext`, migrations |
| `src/Harbora.Shared` | Code the panel and the CLI both need, such as the source inspector |
| `src/Harbora.Cli` | The `harbora` command developers install |
| `src/Harbora.NodeAgent`, `src/Harbora.NodeAgent.Contracts`, `contracts/node-agent/v1` | Outbound node agent and its versioned protocol |
| `src/Harbora.Agent` | The older inbound HTTP agent, deprecated and frozen |
| `src/Modules/Backup`, `src/Modules/Sync` | Kopia/native backup module and Syncthing module, both off by default |
| `deploy/` | Installer, compose stack, recovery script `deploy/harbora`, runbook, proof scripts |
| `tests/` | Four .NET test projects |
| `scripts/` | Mutation scripts and tutorial tooling |
| `docs/tutorial` | The Persian tutorial, also served inside the panel by the Learning Centre |
| `.superpowers/sdd/r3` to `r8` | Per-round ledgers, briefs and reports |

## Where to read next

| Question | Document |
|---|---|
| How is it built? | [ARCHITECTURE.md](ARCHITECTURE.md) |
| What are the exact APIs, tables, states and defaults? | [TECHNICAL_SPEC.md](TECHNICAL_SPEC.md) |
| What works, and what only looks like it works? | [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) |
| What is wrong? | [KNOWN_ISSUES_AND_TECH_DEBT.md](KNOWN_ISSUES_AND_TECH_DEBT.md) |
| Why is it this way? | [DECISIONS_AND_RATIONALE.md](DECISIONS_AND_RATIONALE.md) |
| How do I run it? | [ENVIRONMENT_AND_SETUP.md](ENVIRONMENT_AND_SETUP.md) |
| What is it meant to do? | [PRODUCT_SPEC.md](PRODUCT_SPEC.md) |
| What comes next? | [ROADMAP.md](ROADMAP.md) |
| What does this word mean here? | [GLOSSARY.md](GLOSSARY.md) |
| What is undecided? | [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md) |
| How should an AI assistant or a new developer work here? | [AI_WORKING_GUIDELINES.md](AI_WORKING_GUIDELINES.md) |
| What changed, by release? | [CHANGELOG.md](CHANGELOG.md) |

## Older documents and how far to trust them

These predate the baseline and were left unchanged. Where they disagree with the code, the code wins; known disagreements are logged in [KNOWN_ISSUES_AND_TECH_DEBT.md](KNOWN_ISSUES_AND_TECH_DEBT.md).

| Set | Standing |
|---|---|
| `README.md`, `README.fa.md` | Current. Its "Known limitations" section is the honest public summary. A test holds parts of it to the code (`tests/Harbora.Tests/DocumentationDriftTests.cs`). |
| `deploy/RUNBOOK.md` | Current for operators; also held to the compose file by `DocumentationDriftTests`. |
| `docs/tutorial/` | Current for users, shipped in the image. Some screenshots are stale. |
| `docs/cli-deploy.md`, `docs/functions.md`, `docs/disaster-recovery.md`, `docs/node-agent/`, `docs/backup-sync/` | Feature guides and designs. Useful, but not re-verified line by line here. |
| `docs/product-audit/` | The audit of 2026-08. `backlog.json` stays live and a test enforces evidence on it (`BacklogStatusTests`). `19-do-not-change-list.md` is still policy. The rest is a snapshot. |
| `docs/superpowers/specs`, `plans` | Design records. They hold the reasoning, not the current state. |
| `docs/superpowers/STATUS.md` | Snapshot from 2026-08; stale (it cites 4,933 tests). |
| `docs/overhaul/` | History, marked superseded for planning in its own `17-next-roadmap.md`. Its ADRs are carried into [DECISIONS_AND_RATIONALE.md](DECISIONS_AND_RATIONALE.md). |

## Before you change anything

- [Verified] `docs/product-audit/19-do-not-change-list.md` lists 30 behaviours that carry lessons from real incidents, each with the tests guarding it. Read it before touching the pipeline, the node contract, tenancy or the installer.
- [Verified] Tenancy depends on EF global query filters that see an empty workspace outside a request or a trusted scope. See "Tenancy" in [ARCHITECTURE.md](ARCHITECTURE.md).
- [Verified] Merging to `master` with a changed `<Version>` line automatically tags and publishes a release (`.github/workflows/tag-release.yml`).
- [Verified] Billing, deploy cancellation and pinned approvals have open defects that affect real outcomes. They head [KNOWN_ISSUES_AND_TECH_DEBT.md](KNOWN_ISSUES_AND_TECH_DEBT.md).
