Last verified: 2026-11-08
Scope: `docs/product-audit/17-implementation-roadmap.md`, `backlog.json`, the Launch round's closing list, and [KNOWN_ISSUES_AND_TECH_DEBT.md](KNOWN_ISSUES_AND_TECH_DEBT.md). Priorities below are advice until the owner decides. Updated 2026-11-12 for the round-nine work merged on `r9/int` (`7d7a0ae`), which is not on master or released; nothing else was re-verified.

# Roadmap

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md).

[Verified] The last agreed plan is `docs/product-audit/17-implementation-roadmap.md` (2026-08), with phases 1 to 16. Phases 1, 2, 3 (except previews), 6 and 9 were delivered per `docs/superpowers/STATUS.md`. Later work ran as rounds driven by owner requests (`.superpowers/sdd/r3` to `r8`), so the phase plan is no longer the running order.

[Assumption] No roadmap newer than that document exists. The order below is proposed by this baseline, not decided by the owner. See OQ-02.

## Proposed next, in order

**[Recommendation]** every row. Sizes are rough judgements. Rows 1 and 9 propose reversing items that were accepted as limits for the Launch round (KI-081, KI-089).

| # | Work | Why now | Issues | Size |
|---|---|---|---|---|
| 1 | Stop uploading `.env.production` and `.env.staging` from the CLI | Secrets can leave a developer's machine | KI-081 | Small |
| 2 | Fix billing: price an hour by what existed then, bound backfill properly, cap incomplete retries, bill spend-limited hours correctly | Real money, and customers can be overcharged | KI-001 to KI-004 | Medium |
| 3 | Make approvals and cancels truthful: build the pinned SHA, refuse an unknown ref, keep Cancelled | A wrong build can be approved; a cancel raises false alarms | KI-010, KI-011 | Small to medium |
| 4 | Prune `Jobs`, build directories and the other unbounded tables; stop the prune-job flood | Largest table in production and a disk risk | KI-025, KI-040 to KI-042 | Small to medium |
| 5 | Give cron runs the real effective environment | Cron jobs with a database do not get its connection | KI-014 | Small |
| 6 | Rename or finish "Full platform" and "app config" restore | An operator relies on it in the worst moment | KI-020 | Medium |
| 7 | Decide on the SSL-off controls: honour them or remove them | The page promises what the proxy cannot do | KI-015 | Small |
| 8 | Settle databases left at Provisioning after a hard restart | One stuck row slows every deploy in that environment | KI-019 | Small |
| 9 | Extend the launch proof to the unproven stacks and engines, and add readiness waits | The headline feature has gaps on record | KI-089, status tables | Medium |
| 10 | Browser-driven launch test; retry and restart in the middle of a launch | Only fakes cover these | Status tables | Medium |
| 11 | Make the AI gateway sellable, or hide it | Cannot be enabled today | KI-060, KI-061 | Medium |
| 12 | Backup module parity before turning it on | Silent empty backups on remote workloads | KI-021, KI-022, KI-026, HARBORA-0046 | Large |
| 13 | Compose parity or an honest narrower promise | Many silent gaps | KI-012, KI-013 | Large |
| 14 | Test the MinIO upgrade on a copy | Production cannot stay on an image that cannot be pulled again | D-021, OQ-04 | Medium |
| 15 | Set the nightly proof's secrets and hosts | It is the only lane that proves two real hosts | HARBORA-0022 | Small, needs the owner |

## Round nine, 2026-11-12

[Verified] The owner chose twelve items for easier, safer deploys. Merged on `r9/int` and tested there, not yet on master: rows 1, 5 and 8 of the table above; of row 4, the `Jobs` retention and the build directories (the prune-job flood and the other unbounded tables are still open); and, beyond the table, a daily backup for a new database, the repository picker, the webhook through the provider, a retry that keeps answers, a CI example and the import size.

[Verified] Held by the owner with their work committed: the check after every deployment with the offer to go back, the extended real proof (row 9), and workspace selection for the CLI and API (KI-030).

## Open backlog themes

[Verified] From `docs/product-audit/backlog.json`:

| Theme | Items |
|---|---|
| Node fleet | HARBORA-0011, 0034, 0063 |
| Preview environments | HARBORA-0040 |
| Email | HARBORA-0038, 0039 |
| Marketplace and IaC | HARBORA-0041, 0043, 0044 |
| API and CLI | HARBORA-0042, plus KI-030 |
| Operations and onboarding | HARBORA-0048, 0052, 0053 |
| Metering | HARBORA-0049 |
| Certificates | HARBORA-0050 |
| Localization and modes | HARBORA-0045 |

## v1.0 criteria on record

[Verified] From `docs/product-audit/20-final-recommendation.md`:

- The live-host lane green nightly for 30 days.
- No open P0 or P1 from the reliability audit.
- README and RUNBOOK hold no claim the code cannot demonstrate.
- A newcomer reaches a deployed app with a domain and a backup in under 15 minutes on a fresh server.
- Every alert-worthy event reaches the in-app centre with delivery status visible.

[Assumption] These are still the criteria. See OQ-02.

## Deliberately not planned

See "Non-goals" in [PRODUCT_SPEC.md](PRODUCT_SPEC.md).
