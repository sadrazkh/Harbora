#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# restore-drill.sh — proves the latest database backup actually restores.
#
# Runs on the panel host, because it needs Docker: it restores the newest *.sql.gz in the backup
# volume into a throwaway Postgres container, never the running stack's own one, then runs a few
# sanity queries against it and prints a dated PASS or FAIL verdict. See "Rehearse it" in
# docs/disaster-recovery.md for how this fits into the rest of disaster recovery, and what it does
# and does not prove — it is the automated form of the manual quarterly rehearsal that document
# already describes, not a second, competing procedure.
#
# A developer machine with no Docker daemon cannot exercise this script end to end. Its
# failure-detection logic — the part that matters — is covered instead by
# tests/Harbora.Tests/RestoreDrillScriptTests.cs, which runs this exact script against fixture
# backup files with `docker` shadowed by a fake on PATH, so every branch below is exercised without
# ever needing a real container. Only a run on the real server proves the real restore; that lane
# is deliberately not duplicated here.
#
# The reason this script exists at all, restated because it is the point of the whole sub-project:
# a drill that reports "restored OK" for a run that never had a backup to restore is the exact
# defect class this codebase has spent weeks removing — a surface reporting success for work it
# never did. Every exit path below either PASSes with what it actually checked, or FAILs with
# specifically which thing it could not do. There is no third path, and nothing here is optimistic.
# ---------------------------------------------------------------------------
set -uo pipefail
# Deliberately not -e: every risky command below is checked by hand so this script can print a
# specific FAIL reason and still record it, rather than dying on an unrelated exit code first.

HARBORA_DIR="${HARBORA_DIR:-/opt/harbora}"
COMPOSE_DIR="${COMPOSE_DIR:-$HARBORA_DIR/app/deploy}"

# Where the backup volume lives in production — see the "harbora_backups" row in
# docs/disaster-recovery.md's own "where the data actually is" table. Overridable so this script
# can be pointed at a fixture directory in tests, without a real Harbora install anywhere nearby.
BACKUP_DIR="${HARBORA_BACKUP_DIR:-/var/lib/docker/volumes/harbora_backups/_data}"

# The scratch container this drill restores into. Named and imaged separately from the real
# "harbora-postgres" container on purpose — a typo that pointed this at the live container would
# turn a drill into an incident, which is the one failure mode worse than every one this script
# checks for.
SCRATCH_NAME="${HARBORA_DRILL_CONTAINER:-harbora-restore-drill}"
SCRATCH_IMAGE="${HARBORA_DRILL_IMAGE:-postgres:16-alpine}"
SCRATCH_DB="drill"
SCRATCH_USER="drill"
SCRATCH_PASSWORD="drill"

c_red=$'\033[31m'; c_green=$'\033[32m'; c_yellow=$'\033[33m'; c_off=$'\033[0m'

VERDICT=""
REASON=""
STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

fail() {
  VERDICT="fail"
  REASON="$1"
  echo "${c_red}✗ FAIL${c_off} [$STARTED_AT] $REASON"
}

pass() {
  VERDICT="pass"
  REASON="$1"
  echo "${c_green}✓ PASS${c_off} [$STARTED_AT] $REASON"
}

# Records the verdict on the admin settings page through `harbora record-drill-result`. Runs from
# the exit trap below so it fires exactly once on every path this script can leave by — including
# one nobody wrote an explicit fail() for. A drill that crashed before recording anything is the
# silent-repeat-of-the-last-good-run failure this whole feature exists to prevent.
record_result() {
  if [ -z "$VERDICT" ]; then
    VERDICT="fail"
    REASON="drill exited unexpectedly before reaching a pass or fail check"
    echo "${c_red}✗ FAIL${c_off} [$STARTED_AT] $REASON" >&2
  fi

  local bin=""
  if command -v harbora >/dev/null 2>&1; then
    bin="harbora"
  elif [ -x "$COMPOSE_DIR/harbora" ]; then
    bin="$COMPOSE_DIR/harbora"
  fi

  if [ -z "$bin" ]; then
    echo "${c_yellow}!${c_off} harbora CLI not found (checked PATH and $COMPOSE_DIR/harbora) — the verdict above was not recorded on the admin page." >&2
    return
  fi

  if "$bin" record-drill-result --verdict "$VERDICT" --detail "$REASON"; then
    echo "  Recorded on the admin settings page ($VERDICT)."
  else
    echo "${c_yellow}!${c_off} '$bin record-drill-result' failed — the admin page will keep showing the previous drill." >&2
  fi
}

on_exit() {
  docker rm -f "$SCRATCH_NAME" >/dev/null 2>&1 || true
  record_result
}
trap on_exit EXIT

# ---- 1. find the latest backup -------------------------------------------------------------

if [ ! -d "$BACKUP_DIR" ]; then
  fail "no backup found: $BACKUP_DIR does not exist"
  exit 1
fi

BACKUP_FILE="$(ls -1t "$BACKUP_DIR" 2>/dev/null | grep -E '\.sql\.gz$' | head -1 || true)"
if [ -z "$BACKUP_FILE" ]; then
  fail "no backup found: no *.sql.gz file in $BACKUP_DIR"
  exit 1
fi
BACKUP_PATH="$BACKUP_DIR/$BACKUP_FILE"
echo "Latest backup: $BACKUP_FILE ($(du -h "$BACKUP_PATH" 2>/dev/null | cut -f1))"

# ---- 2. verify the archive is not truncated or corrupt --------------------------------------

if [ ! -s "$BACKUP_PATH" ]; then
  fail "dump truncated: $BACKUP_FILE is zero bytes"
  exit 1
fi

if ! gzip -t "$BACKUP_PATH" 2>/dev/null; then
  fail "dump truncated: $BACKUP_FILE failed gzip integrity check (gzip -t)"
  exit 1
fi

# ---- 3. restore into a scratch Postgres container — never the real one ----------------------

docker rm -f "$SCRATCH_NAME" >/dev/null 2>&1 || true

if ! docker run -d --name "$SCRATCH_NAME" \
      -e POSTGRES_DB="$SCRATCH_DB" -e POSTGRES_USER="$SCRATCH_USER" -e POSTGRES_PASSWORD="$SCRATCH_PASSWORD" \
      "$SCRATCH_IMAGE" >/dev/null 2>/tmp/harbora-drill-run.log; then
  fail "restore errored: could not start the scratch Postgres container ($SCRATCH_IMAGE) — see /tmp/harbora-drill-run.log"
  exit 1
fi

ready=0
# Overridable so a fixture test asserting the "never becomes ready" path does not have to spend the
# full production wait to prove it — real drills keep the 30-second default.
READY_RETRIES="${HARBORA_DRILL_READY_RETRIES:-30}"
for _ in $(seq 1 "$READY_RETRIES"); do
  if docker exec "$SCRATCH_NAME" pg_isready -U "$SCRATCH_USER" -d "$SCRATCH_DB" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
if [ "$ready" -ne 1 ]; then
  fail "restore errored: the scratch Postgres container never became ready"
  exit 1
fi

if ! (set -o pipefail
      gunzip -c "$BACKUP_PATH" | docker exec -i "$SCRATCH_NAME" psql -q -U "$SCRATCH_USER" -d "$SCRATCH_DB" \
        >/tmp/harbora-drill-restore.log 2>&1); then
  fail "restore errored: psql reported an error applying $BACKUP_FILE — see /tmp/harbora-drill-restore.log"
  exit 1
fi

# ---- 4. sanity queries: the restore has to look like a real Harbora database ----------------
#
# "Returned nothing" below means the query itself could not be answered — psql or docker exec
# failed — not that the honest answer happened to be zero. A production Harbora install always has
# migrations and workspaces; it is not assumed to always have ledger activity (billing can be off),
# so an empty ledger is reported, not treated as a failure. See docs/disaster-recovery.md.
#
# run_query prints the trimmed result on stdout and returns docker exec's own exit status — read
# with `$?` immediately after the assignment below, NEVER through a variable this function sets
# itself. `VAR="$(run_query …)"` runs the function in a subshell (that is what `$(...)` does), so
# an assignment made inside it — this repository's own first draft used a global `LAST_QUERY_OK`
# flag — never escapes back to the caller; the check that read it was reading a value that could
# never change, and every query looked identical to a failed one. `$?` is the one piece of state a
# command substitution still hands back correctly.
run_query() {
  docker exec "$SCRATCH_NAME" psql -tA -U "$SCRATCH_USER" -d "$SCRATCH_DB" -c "$1" \
    2>/tmp/harbora-drill-query.log | tr -d '[:space:]'
}

MIGRATIONS_COUNT="$(run_query 'SELECT COUNT(*) FROM "__EFMigrationsHistory";')"
if [ $? -ne 0 ] || [ -z "$MIGRATIONS_COUNT" ]; then
  fail "sanity query returned nothing: migrations history table (__EFMigrationsHistory) could not be read — see /tmp/harbora-drill-query.log"
  exit 1
fi
if [ "$MIGRATIONS_COUNT" = "0" ]; then
  fail "restored database has an empty migrations history — the schema did not come back"
  exit 1
fi

WORKSPACES_COUNT="$(run_query 'SELECT COUNT(*) FROM "Workspaces";')"
if [ $? -ne 0 ] || [ -z "$WORKSPACES_COUNT" ]; then
  fail "sanity query returned nothing: Workspaces table could not be read — see /tmp/harbora-drill-query.log"
  exit 1
fi

LEDGER_AGE_SECONDS="$(run_query 'SELECT EXTRACT(EPOCH FROM (now() - MAX("CreatedAt")))::bigint FROM "BillingLedger";')"
if [ $? -ne 0 ]; then
  fail "sanity query returned nothing: BillingLedger table could not be read — see /tmp/harbora-drill-query.log"
  exit 1
fi
LEDGER_SUMMARY="${LEDGER_AGE_SECONDS:+${LEDGER_AGE_SECONDS}s old}"
LEDGER_SUMMARY="${LEDGER_SUMMARY:-no ledger rows}"

echo "  migrations applied : $MIGRATIONS_COUNT"
echo "  workspaces          : $WORKSPACES_COUNT"
echo "  newest ledger row   : $LEDGER_SUMMARY"

pass "restored $BACKUP_FILE — $MIGRATIONS_COUNT migrations, $WORKSPACES_COUNT workspaces, newest ledger row $LEDGER_SUMMARY"
exit 0
