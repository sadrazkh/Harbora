# Round eight ledger — Launch (owner request 2026-11-04)

Owner: «بزرگترین مشکل اینه که سازگاری و یادگرفتن کار کردن با این پروژه برای مشتری سخته … شبیه یه فرم پر کنه اپشو بده … طرف اپ رو نوشته فقط بزنه دیپلوی بشه دیتابیس ساخته بشه کانکت بشه تست بشه».
Then: «نیازی به ai لزوما نیست اگر میشه بدون ai هرجایی رو برد جلو بهترم هست برنامه ریزی کن و ایجنتهاشو اماده کن و بفرست».
Then: «برو جلو انجام بده چیزی که لازمه اضافه کن چک کن مرج کن و دیپلوی کن اگر برنامه ای برای ویندوز نوشتی برای دیپلویش حتما با تگ بیلدش رو گیت هم بیاد».
Chosen: phases 2 (inspector), 1 (one screen), 3 (checks), 5 (friction). Phase 4 (AI) dropped.

| Task | What | Status | Report |
|---|---|---|---|
| D | Inspector: what the code is, what it needs | merged | d-report.md |
| S | Launch server: the state machine and its ticker | merged | s-report.md |
| V | Checks, the route check, the wait for services | merged (810a3ba; no final report text beyond v-report.md) | v-report.md |
| U | Pages: `/launch` and its one screen | merged | u-report.md |
| C | API and `harbora launch` | merged | c-report.md |
| H | Docs, README, tutorial chapter | merged | h-report.md |
| P | Real run on CI: `deploy/launch-proof.sh`, 12 fixtures, 8 real bugs fixed | merged | p-report.md |
| F1 | Fix pass, server side: 31 review findings shared with F2 | merged | f1-report.md |
| F2 | Fix pass, pages and terminal | merged | f2-report.md |

## Found at the very end

The final full run on r8/int hung for good, twice, with no CPU and no summary line. `--blame-hang --blame-hang-timeout 4m --blame-hang-dump-type none` named `ReplicaPipelineTests.A_replica_that_is_still_provisioning_never_reaches_the_container`. V's wait for databases counted a replica, and the harness's `FixedClock` never reaches the wait's bound, so it polled for ever. No branch had seen it: F1 and F2 ran their full suites before V landed.

It was also a real behaviour question, not only a test one. Seeding a large replica can outlast the whole ten minutes, and a replica is a second read-only address the app does not boot on. So replicas are left out of the wait (72899f5), pinned by `A_replica_still_seeding_holds_nothing_back_because_the_app_boots_on_its_primary`.

## Verification on the final code

| Check | Result |
|---|---|
| `Harbora.Tests.dll` | 9457 passed, 0 failed |
| `Harbora.NodeAgent.Tests.dll` | 500 passed, 17 skipped |
| `Harbora.NodeIngress.Tests.dll` | 15 passed |
| `Harbora.Postgres.Tests.dll` | skipped here (no Docker); ran in CI's Postgres lane |
| `node --test` | 533 passed |
| `npm run build` | built |
| CI 42031668424 on 7e90af0 | green, every job |
| Launch proof 42031668273 on 7e90af0 | success, about 19 minutes |
| Tag a release 42031668308, Release CLI 42031690463 | success: tag v0.12.0, `harbora-win-x64.exe`, `harbora-win-arm64.exe`, linux, osx, sha256 files, nupkg |

## Production deploy — 2026-11-05

- Pre-flight: server on 1e90475, disk 47%, panel healthy with 0 restarts, no pending or running job, no in-flight deployment.
- Rollback image `harbora/panel:before-7e90af0` (= a0d9758b14bf, the 1e90475 build).
- MinIO kept as it is, on purpose. `deploy/docker-compose.override.yml`, untracked on the host, pins the server and `Storage__S3__ClientImage` to `quay.io/minio/minio@sha256:9535594a…`, which is local and can no longer be pulled. `docker compose config --hash` matched the running containers for minio, redis, postgres and traefik before the update, and all four stayed up.
- `install.sh update` detached → HEAD 7e90af0, UPDATE_EXIT=0, "Harbora updated", every verify check passed including both certificates.
- Image built 2026-11-05T13:22:10Z against a commit at 12:58:00Z. Panel healthy, restarts 0, no error lines. Migration 20261104094952_AppLaunches applied. Anonymous `/launch` answers 302 to the login page.
- Not done: a launch through the production panel, which needs the owner's login.

## Open for the owner

- Upgrade MinIO on 57 (2024-10 → 2025-10 over existing data is untested), or stay.
- `ghcr.io/coollabsio/minio` as the default image for new installs is a choice to confirm.
- `Jobs` is 942 MB of a 1085 MB database: 4.2 million rows, the oldest from 2026-08-09. Not touched, and the cause was not looked into.
- 90eb216 and 6316507 went out with the global email rather than the owner's. Fixing it needs a force-push, so it was left.
