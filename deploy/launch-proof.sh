#!/usr/bin/env bash
#
# Harbora — launch proof.
#
# The first time `harbora launch` runs for real must not be on somebody's production server, in front
# of them. This walks it on a throwaway Linux host, against everything the tests fake: the real
# installer, the real image, the real migration on real Postgres, real Traefik, real databases, a real
# `docker build`, and at the end of each one the app itself answering over the proxy with what it was
# able to do with what it was given.
#
#    0  preconditions: this host really is clean
#    1  a local certificate authority (see below for why)
#    2  install from the repository's own installer, at the ref under test
#    3  make Traefik and the panel trust that authority
#    4  create the owner through the first-run wizard
#    5  publish the real CLI from the ref under test and sign in with it
#    6  the main case: a Node app and Postgres, nothing set up by hand
#    7  an app that crashes must not read as a success
#    8  an app that ignores its database must be caught by its log
#    9  a Python worker has no address and no HTTP probe, and reaches Redis
#   10  a repository with two apps asks, and then builds the one it was told
#   11  `harbora deploy` the next day: the change lands, the database is still there
#   12  the launch pages render over real data and carry no secret
#   13  a Vite site: built, served, routed, and its build variables go where they should
#   14  a second deploy after a build variable changes: the new value lands
#   15  a frontend whose build fails, and one that builds no site: the last line says so
#   16  a real Create React App project is a frontend, and its dev server really cannot serve
#   17  a Node build reads a public variable nobody ticked, and the image keeps none of it
#   18  a Python build sees a variable only once it is marked
#   19  a plain PHP app reaches MySQL through DB_HOST and its friends
#   20  a real Laravel project, generated now, migrates and serves
#   21  a .NET solution whose web project sits in a folder, and Postgres by connection string
#   22  a Go module, built on the Go its go.mod asks for, and NATS
#   23  a real Next.js app renders on the server, for each request
#   24  a pnpm project, and MongoDB
#   25  a yarn project, and MariaDB
#   26  a Python web app, and RabbitMQ
#   27  a Node app, and Meilisearch
#   28  a connection check that cannot fail proves nothing: a changed password and a stopped engine
#   29  a zip with everything in one wrapper folder, through the panel's own upload and confirm forms
#   30  harbora launch --git, from a repository this host serves
#   31  a private repository needs its token, and takes it
#   32  a launch that failed is tried again once its cause is put right
#   33  the panel restarts in the middle of a launch
#   34  nothing in the launch path logged an error of its own
#
# Run it BY HAND on any throwaway Linux host (it installs Docker if it has to):
#
#   export LP_REF=my-branch          # a branch, as install.sh's REPO_BRANCH takes
#   sudo -E bash deploy/launch-proof.sh
#
# The cases come in three sets, and LP_GROUPS says which to walk: `core` (5b to 20), `stacks` (21 to
# 28) and `flows` (29 to 33). Every set needs the install and nothing from another set, so the workflow
# gives each a host of its own and runs them side by side, which is what keeps a run near half an hour.
# By hand the default is all three, one after another, on one host.
#
# Or let .github/workflows/launch-proof.yml do it on a GitHub runner.
#
# ── Names and certificates, on a host nothing can reach ──────────────────────────────────────────
#
# A GitHub runner has no public address, so Let's Encrypt can never answer for it, and the installer's
# own fallback of nip.io on the public IP names an address nothing can reach. What happens then was
# read rather than assumed:
#
#   * nip.io on the runner's PRIVATE address resolves, on the host and from inside containers, and that
#     address is reachable from containers through Docker's hairpin. So every name below is real DNS.
#   * With no certificate, Traefik serves its self-signed default. AppVerifier's `url` check goes through
#     HttpUptimeProbe, whose certificate error is a Down with no status, and that stays Pending until
#     LaunchAdvance.ChecksPatience runs out (five minutes) and then fails. Each launch would cost five
#     minutes to end SucceededWithProblems, for a reason that has nothing to do with the app.
#   * And the CLI has no way to skip certificate checks, rightly, so it could not reach the panel at all.
#
# So this runs a real ACME server, Let's Encrypt's own Pebble, on the host. Installing it changes three
# things and no product code: ACME_CA_SERVER (the setting docker-compose.yml already has, the same one
# live-host-proof.sh uses for staging), one compose override, and the host's trust store. Everything
# else is the path a customer takes: Traefik asks over ACME on the first router, Pebble really connects
# to the name over port 80 for the HTTP-01 challenge, and the panel's probe and the CLI validate the
# chain like anything else. That gets a clean `Succeeded` honestly, and `curl -k` appears nowhere below.
# What it does not prove is that Let's Encrypt itself will issue: that takes a public address.
#
# Everything on this host is left as it is when the run ends. Failures stay readable: every failure is
# an annotation (`::error title=step N::`), which GitHub's API returns without a login, and the job's
# logs are not needed to read it.
set -Eeuo pipefail

# ---------------------------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------------------------
LP_REPO_URL="${LP_REPO_URL:-https://github.com/sadrazkh/Harbora}"
LP_REPO_RAW="${LP_REPO_RAW:-https://raw.githubusercontent.com/sadrazkh/Harbora}"
LP_REF="${LP_REF:-master}"
# When set, the commit the installer must have ended up on. A branch can move between the push that
# started this and the clone, and a proof of the wrong commit would say nothing about the right one.
LP_EXPECT_SHA="${LP_EXPECT_SHA:-}"
LP_PEBBLE_IMAGE="${LP_PEBBLE_IMAGE:-ghcr.io/letsencrypt/pebble:2.10.1}"
LP_GITEA_IMAGE="${LP_GITEA_IMAGE:-gitea/gitea:1.22}"
LP_GIT_PORT="${LP_GIT_PORT:-3080}"
LP_GROUPS="${LP_GROUPS:-core stacks flows}"

HARBORA_DIR="${HARBORA_DIR:-/opt/harbora}"
APP_DIR="$HARBORA_DIR/app"
COMPOSE_DIR="$APP_DIR/deploy"
PEBBLE_DIR="/var/lib/harbora-proof/pebble"

W_PANEL="${LP_WAIT_PANEL:-600}"
W_CERT="${LP_WAIT_CERT:-300}"
W_LAUNCH="${LP_WAIT_LAUNCH:-1200}"

RUN_ID="$(date -u +%Y%m%d%H%M%S)"
PROOF_STARTED="$(date +%s)"
WORK="$(mktemp -d /tmp/harbora-launch-proof.XXXXXX)"
mkdir -p "$WORK/out" "$WORK/apps" "$WORK/state"
COOKIES="$WORK/cookies.txt"
FAILED_STEPS="$WORK/failed-steps"
: > "$FAILED_STEPS"
: > "$WORK/facts"
: > "$WORK/known-secrets"
: > "$WORK/last-failure"
: > "$WORK/current-launch"
: > "$WORK/current-output"

API_TOKEN=""
ANNOTATIONS_USED=0

in_group() { case " $LP_GROUPS " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }
HOST_STATE_REPORTED=0

c_g='\033[0;32m'; c_b='\033[0;34m'; c_y='\033[1;33m'; c_r='\033[0;31m'; c_0='\033[0m'
log()  { echo -e "${c_b}➜${c_0} $*"; }
ok()   { echo -e "${c_g}✓${c_0} $*"; }
warn() { echo -e "${c_y}!${c_0} $*"; }
err()  { echo -e "${c_r}✗${c_0} $*" >&2; }

CURRENT_STEP="startup"
step() {
  CURRENT_STEP="$1"
  echo
  echo "──────────────────────────────────────────────────────────────────────────────"
  echo -e "${c_b}STEP ${1}${c_0}"
  echo "──────────────────────────────────────────────────────────────────────────────"
}

# ---------------------------------------------------------------------------------------------
# Failing, in a way that can be read without a login
# ---------------------------------------------------------------------------------------------

# What it looks like when something ran and said nothing useful: a value that must not reach an
# annotation, whichever command printed it.
remember_secret() { [ -n "${1:-}" ] && printf '%s\n' "$1" >> "$WORK/known-secrets"; return 0; }

scrub() {
  local text secret
  text="$(sed -E \
    -e 's#(://[^:/@ ]+:)[^@/ ]+@#\1***@#g' \
    -e 's/((password|passwd|pwd|secret|token|api[_-]?key)[A-Za-z_]*["'"'"']?[ ]*[=:][ ]*["'"'"']?)[^ "'"'"',;&]+/\1***/Ig')"
  while IFS= read -r secret; do
    [ -n "$secret" ] && text="${text//"$secret"/***}"
  done < "$WORK/known-secrets"
  printf '%s' "$text"
}

# GitHub keeps ten annotations of each level per step, so the eleventh error would vanish. Going on to
# warnings and then notices keeps a run with several failures readable to the end; the title says what
# each one is whatever its colour.
annotate() {
  local title="$1" body="$2" level
  ANNOTATIONS_USED=$((ANNOTATIONS_USED + 1))
  if   [ "$ANNOTATIONS_USED" -le 10 ]; then level=error
  elif [ "$ANNOTATIONS_USED" -le 20 ]; then level=warning
  else level=notice
  fi
  body="$(printf '%s' "$body" | sed -E 's/\x1b\[[0-9;]*[A-Za-z]//g' | scrub)"
  # An annotation with nothing in it is dropped, and silence would read as "nothing to report".
  [ -n "$body" ] || body="(nothing)"
  body="${body:0:3800}"
  body="${body//'%'/%25}"; body="${body//$'\r'/%0D}"; body="${body//$'\n'/%0A}"
  title="${title//'%'/%25}"; title="${title//':'/%3A}"; title="${title//','/%2C}"
  printf '::%s title=%s::%s\n' "$level" "$title" "$body"
}

report_failure() {
  local title="$1" fact="$2" launch deployment view
  annotate "$title" "$fact"

  # What the command said, since the job's log takes a login to read.
  if [ -s "$WORK/current-output" ] && [ -s "$(cat "$WORK/current-output")" ]; then
    annotate "$title - what the command said" "$(tail -c 3000 "$(cat "$WORK/current-output")")"
  fi

  launch="$(cat "$WORK/current-launch" 2>/dev/null || true)"
  [ -n "$launch" ] || return 0
  [ -n "$API_TOKEN" ] || return 0

  view="$(api GET "/api/v1/launch/$launch" 2>/dev/null || true)"
  [ -n "$view" ] || return 0
  annotate "$title - the launch" "$(printf '%s' "$view" \
    | jq -c '{id,status,sourceLabel,appId,appName,publicUrl,deploymentId,failureMessage,canRetry,
              steps:[.steps[]|{key,state,detail}],checks:[.checks[]|{key,status,title,detail}]}' 2>/dev/null \
    || printf '%s' "$view")"

  deployment="$(printf '%s' "$view" | jq -r '.deploymentId // empty' 2>/dev/null || true)"
  [ -n "$deployment" ] || return 0
  annotate "$title - its deployment log, the end of it" "$(api GET "/api/v1/deployments/$deployment/logs" 2>/dev/null \
    | jq -r '.[].message' 2>/dev/null | tail -n 40 | cut -c1-300 | tail -c 3700 || true)"
}

report_host_state() {
  [ "$HOST_STATE_REPORTED" -eq 0 ] || return 0
  HOST_STATE_REPORTED=1
  command -v docker >/dev/null 2>&1 || return 0
  annotate "the panel's log - errors first" "$( { docker logs harbora-panel 2>&1 | grep -E '^[0-9:]+ (fail|crit): ' | tail -c 2200; \
    echo '--- the last lines ---'; docker logs --tail 15 harbora-panel 2>&1 | cut -c1-200; } 2>/dev/null || true)"
  annotate "traefik's log - errors" "$(docker logs harbora-traefik 2>&1 | sed -E 's/\x1b\[[0-9;]*[A-Za-z]//g' | grep -E ' (ERR|WRN) ' | cut -c1-400 | tail -c 3700 || true)"
  annotate "docker ps -a" "$(docker ps -a --format '{{.Names}}  {{.Image}}  {{.Status}}' 2>/dev/null | cut -c1-160 | tail -c 3700 || true)"
}

# Every failure names its step and the fact that did not hold. `fail` says the fact; anything that
# stops the script without calling it is caught by the trap and named by its line.
fail() {
  printf '%s' "$*" > "$WORK/last-failure"
  err "$*"
  exit 1
}
die() { fail "$@"; }

on_error() {
  local code=$? line="${BASH_LINENO[0]:-?}" command="$BASH_COMMAND"
  [ -s "$WORK/last-failure" ] || printf 'exit code %s at line %s: %s' "$code" "$line" "$command" > "$WORK/last-failure"
}
trap on_error ERR

on_exit() {
  local code=$?
  trap - ERR EXIT
  [ "$code" -ne 0 ] || return 0
  # Only a foundation step reaches here: one that failed in step_case has already been reported.
  if [ -s "$WORK/last-failure" ]; then
    report_failure "step ${CURRENT_STEP%% *}" "${CURRENT_STEP}: $(cat "$WORK/last-failure")"
  else
    annotate "step ${CURRENT_STEP%% *}" "${CURRENT_STEP}: the script stopped with exit code $code."
  fi
  report_host_state
  err "FAILED in step: ${CURRENT_STEP}. Working files are at ${WORK}; this host is left as it is."
  exit "$code"
}
trap on_exit EXIT

# A fact established, for the summary a passing run ends on.
fact() { printf '%s\n' "$*" >> "$WORK/facts"; ok "$*"; }

# A step that may fail without stopping the steps after it, in its own subshell: a run takes half an
# hour, and finding out about one bug per run is how it takes a week. What a later step needs from an
# earlier one goes through $WORK/state.
step_case() {
  local title="$1" fn="$2" started rc
  step "$title"
  : > "$WORK/last-failure"
  : > "$WORK/current-launch"
  : > "$WORK/current-output"
  started="$(date +%s)"
  set +e
  ( set -e; "$fn" )
  rc=$?
  set -e
  if [ "$rc" -ne 0 ]; then
    printf '%s\n' "$title" >> "$FAILED_STEPS"
    report_failure "step ${title%% *}" "${title}: $(cat "$WORK/last-failure" 2>/dev/null || echo "exit code $rc")"
    err "Step failed, carrying on with the next: ${title}"
  else
    ok "Step passed in $(( $(date +%s) - started ))s: ${title}"
  fi
  : > "$WORK/last-failure"
}

must_eq() {
  [ "$2" = "$3" ] || fail "$1: expected '$3', got '$2'."
  ok "$1: $2"
}

# ---------------------------------------------------------------------------------------------
# Small helpers, the same ones live-host-proof.sh has
# ---------------------------------------------------------------------------------------------

wait_until() {
  local budget="$1"; shift
  local what="$1"; shift
  local started deadline now
  started="$(date +%s)"; deadline=$((started + budget))
  while :; do
    if "$@"; then
      now="$(date +%s)"
      ok "$what (after $((now - started))s)"
      return 0
    fi
    now="$(date +%s)"
    [ "$now" -lt "$deadline" ] || fail "Timed out after ${budget}s waiting for: $what"
    sleep 5
  done
}

web_get() { curl -fsS --max-time 60 -b "$COOKIES" -c "$COOKIES" "$PANEL_URL$1"; }

antiforgery_token() {
  local html token
  html="$(web_get "$1")" || fail "GET $1 failed, so there is no form to read a token from."
  token="$(printf '%s' "$html" \
    | grep -o '<input[^>]*__RequestVerificationToken[^>]*>' \
    | head -1 | grep -o 'value="[^"]*"' | head -1 | sed 's/^value="//; s/"$//')" || true
  [ -n "$token" ] || fail "No antiforgery token on $1: the page did not render the form we expected."
  printf '%s' "$token"
}

web_post() {
  local path="$1" token="$2"; shift 2
  local args=() field
  for field in "$@"; do args+=(--data-urlencode "$field"); done
  curl -sS --max-time 120 -o "$WORK/last.body" -D "$WORK/last.headers" -w '%{http_code}' \
    -b "$COOKIES" -c "$COOKIES" -H "X-CSRF-TOKEN: $token" "${args[@]}" "$PANEL_URL$path"
}

api() {
  local method="$1" path="$2" body="${3:-}"
  if [ -n "$body" ]; then
    curl -fsS --max-time 120 -X "$method" -H "Authorization: Bearer $API_TOKEN" \
      -H 'Content-Type: application/json' -d "$body" "$PANEL_URL$path"
  else
    curl -fsS --max-time 120 -X "$method" -H "Authorization: Bearer $API_TOKEN" "$PANEL_URL$path"
  fi
}

psql_panel() {
  # shellcheck disable=SC1091
  ( . "$COMPOSE_DIR/.env"; docker exec harbora-postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Atc "$1" )
}

# http_get <url> <output file>: the status, with no -k and no --resolve, as anybody's browser asks.
http_get() { curl -sS --max-time 30 -o "$2" -w '%{http_code}' "$1" || echo 000; }

# The address answers, with this status. The check that said so can have been a moment ago.
answers() { [ "$(http_get "$1" "$2")" = "$3" ]; }

newest_launch() { psql_panel 'SELECT "Id" FROM "AppLaunches" ORDER BY "CreatedAt" DESC LIMIT 1'; }

# A command's output as one line of plain words. Spectre folds at 80 columns with no terminal behind
# it, and under GitHub Actions it colours redirected output too, so a word in bold is otherwise cut off
# from the one before it by escape codes.
flatten() { sed -E 's/\x1b\[[0-9;]*[A-Za-z]//g' | tr -s ' \r\n' ' '; }

# A runner's capacity is real, and this launches more apps than four cores allow at a quarter of a core
# each: by the last steps the panel rightly refuses. An app nothing further needs goes the way a person
# removes one, which is also what stops the crash loop from eating the host.
retire_app() {
  local app_id="$1" token status
  [ -n "$app_id" ] || fail "There is no app to retire."
  token="$(antiforgery_token "/apps/details/$app_id")"
  status="$(web_post "/Apps/Delete/$app_id" "$token" "__RequestVerificationToken=$token" "removeVolumes=false")"
  case "$status" in
    302|303) ok "Retired app $app_id." ;;
    *) fail "Deleting app $app_id answered HTTP $status, not a redirect." ;;
  esac
}

# A copy, because a launch writes harbora.yml into its folder and step 11 changes a file.
fixture() {
  local name="$1" from="$COMPOSE_DIR/proof-fixtures/$1" target="$WORK/apps/$1"
  [ -d "$from" ] || fail "The ref under test has no fixture called $name at $from."
  rm -rf "$target"
  cp -a "$from" "$target"
  printf '%s' "$target"
}

# run_launch <label> <folder> [flags...]: runs what a customer runs, and reads what it says happened.
# Leaves LAUNCH_EXIT, LAUNCH_ID, OUT (the output with its folded lines put back together, because
# Spectre wraps at 80 columns with no terminal behind it) and VIEW (GET /launch/{id}) behind it.
run_launch() {
  local label="$1" dir="$2" before
  shift 2
  before="$(newest_launch)"
  OUT="$WORK/out/$label.flat"
  printf '%s' "$WORK/out/$label.txt" > "$WORK/current-output"
  LAUNCH_EXIT=0
  ( cd "$dir" && timeout "$W_LAUNCH" "$HARBORA" launch --yes "$@" ) > "$WORK/out/$label.txt" 2>&1 || LAUNCH_EXIT=$?
  cat "$WORK/out/$label.txt"
  flatten < "$WORK/out/$label.txt" > "$OUT"
  [ "$LAUNCH_EXIT" -ne 124 ] || fail "harbora launch was still following after ${W_LAUNCH}s. It must end by itself."

  LAUNCH_ID="$(newest_launch)"
  [ -n "$LAUNCH_ID" ] && [ "$LAUNCH_ID" != "$before" ] || fail "harbora launch left no new launch behind it. Its output: $(tail -c 800 "$OUT")"
  printf '%s' "$LAUNCH_ID" > "$WORK/current-launch"

  VIEW="$WORK/out/$label.view.json"
  api GET "/api/v1/launch/$LAUNCH_ID" > "$VIEW" || fail "GET /api/v1/launch/$LAUNCH_ID failed."
  log "Launch $LAUNCH_ID: exit $LAUNCH_EXIT, $(jq -r .status "$VIEW"), checks: $(jq -r '[.checks[]|"\(.key)=\(.status)"]|join(" ")' "$VIEW")"
}

say_must()     { grep -qF -- "$1" "$OUT" || fail "harbora launch did not say \"$1\". It said: $(tail -c 900 "$OUT")"; }
view_field()   { jq -r ".$1 // empty" "$VIEW"; }
check_status() { jq -r --arg k "$1" 'first(.checks[]|select(.key==$k)|.status) // "absent"' "$VIEW"; }
check_detail() { jq -r --arg k "$1" 'first(.checks[]|select(.key==$k)|.detail) // ""' "$VIEW"; }
service_checks() { jq -r '[.checks[]|select(.key|startswith("service:"))|.status]|join(",")' "$VIEW"; }
slug_of_app()  { api GET /api/v1/apps | jq -r --arg id "$1" 'first(.[]|select(.id==$id)|.slug) // empty'; }

# Nothing reported green that did not pass: every check there was is Passed, and each one named is there.
all_checks_passed() {
  local other key
  other="$(jq -r '[.checks[]|select(.status!="Passed")|"\(.key)=\(.status): \(.title)"]|join("; ")' "$VIEW")"
  [ -z "$other" ] || fail "Not every check passed: $other"
  for key in "$@"; do
    must_eq "the $key check" "$(check_status "$key")" Passed
  done
}

all_steps_done() {
  local other
  other="$(jq -r '[.steps[]|select(.state!="Done" and .state!="Skipped")|"\(.key)=\(.state)"]|join(" ")' "$VIEW")"
  [ -z "$other" ] || fail "A launch that ended still has steps not done: $other"
}

deployment_log() { api GET "/api/v1/deployments/$1/logs" | jq -r '.[].message'; }
image_of_app()   { docker ps --filter "label=harbora.app=$1" --format '{{.Image}}' | head -1; }
container_of()   { docker ps --filter "label=harbora.app=$1" --format '{{.Names}}' | head -1; }

# Whether stdin holds this text, having read all of it. `grep -q` stops at the first match, whatever is
# feeding it then dies of SIGPIPE, and under pipefail that reads as "not found", which for a search
# proving a secret is nowhere is the one wrong answer that looks right.
holds() { grep -acF -- "$1" >/dev/null; }

# Whether a value is anywhere in an image: every layer and config `docker save` gives, unpacked as far
# as gzip goes, and its history. Every caller of this for "it is nowhere" also asks it about a value
# that must be found, because a search that cannot find anything would pass that test for ever.
image_contains() {
  local image="$1" value="$2" tmp found=1 file
  tmp="$(mktemp -d "$WORK/image.XXXXXX")"
  docker save "$image" | tar -x -C "$tmp"
  while IFS= read -r file; do
    if { zcat -f -- "$file" 2>/dev/null || true; } | holds "$value"; then found=0; break; fi
  done < <(find "$tmp" -type f)
  if { docker history --no-trunc "$image" 2>/dev/null || true; } | holds "$value"; then found=0; fi
  rm -rf "$tmp"
  return "$found"
}

# Sets a variable the way a person does, on the app's page. `harbora launch --set` cannot mark one.
set_variable_on_page() {
  local app_id="$1" key="$2" value="$3" marked="${4:-false}" token status
  token="$(antiforgery_token "/apps/details/$app_id")"
  status="$(web_post "/apps/$app_id/env" "$token" \
    "__RequestVerificationToken=$token" "key=$key" "value=$value" "availableAtBuild=$marked")"
  case "$status" in
    302|303) ;;
    *) fail "Saving $key on the app's page answered HTTP $status, not a redirect." ;;
  esac
}

# run_deploy <folder> <output label>: what a customer runs the next day. Leaves DEPLOY_EXIT and DEPLOY_ID.
run_deploy() {
  local dir="$1" label="$2"
  printf '%s' "$WORK/out/$label.txt" > "$WORK/current-output"
  DEPLOY_EXIT=0
  ( cd "$dir" && timeout "$W_LAUNCH" "$HARBORA" deploy ) > "$WORK/out/$label.txt" 2>&1 || DEPLOY_EXIT=$?
  cat "$WORK/out/$label.txt"
  flatten < "$WORK/out/$label.txt" > "$WORK/out/$label.flat"
  DEPLOY_ID="$(grep -oE 'Queued deployment [0-9a-f-]{36}' "$WORK/out/$label.flat" | awk '{print $3}' | head -1 || true)"
}

# ---------------------------------------------------------------------------------------------
step "0 — preconditions: this host really is clean"
# ---------------------------------------------------------------------------------------------
[ "$(id -u)" -eq 0 ] || die "Run as root: this installs Docker and writes to $HARBORA_DIR."

for group in $LP_GROUPS; do
  case "$group" in
    core|stacks|flows) ;;
    *) die "LP_GROUPS names '$group', and the sets of cases are core, stacks and flows." ;;
  esac
done
[ -n "${LP_GROUPS// /}" ] || die "LP_GROUPS is empty, so this would install, launch nothing and pass."
log "Walking: $LP_GROUPS"

[ ! -e "$HARBORA_DIR" ] || die "This host is not clean: $HARBORA_DIR already exists. Every claim here depends on installing from nothing, so re-image it."

log "Installing this script's own prerequisites…"
if   command -v apt-get >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq curl jq openssl ca-certificates iproute2 git zip python3 >/dev/null
elif command -v dnf >/dev/null 2>&1; then dnf install -y -q curl jq openssl ca-certificates iproute git zip python3 >/dev/null
elif command -v yum >/dev/null 2>&1; then yum install -y -q curl jq openssl ca-certificates iproute git zip python3 >/dev/null
else die "No supported package manager (apt/dnf/yum)."
fi
for tool in curl jq openssl ip zcat tar git zip python3; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is not on PATH."
done

if ! command -v docker >/dev/null 2>&1; then
  log "Installing Docker the way install.sh does, because the certificate authority needs it first…"
  curl -fsSL https://get.docker.com | sh >/dev/null
fi
docker info >/dev/null 2>&1 || die "Docker is installed but does not answer."

# A volume from an earlier install would be adopted by the installer, and a "fresh install" would be a
# lie from there on. Unanchored for the reason live-host-proof.sh spells out: most are deploy_harbora_*.
orphans="$(docker volume ls -q --filter name='harbora_' 2>/dev/null || true)"
[ -z "$orphans" ] || die "This host already has Harbora data volumes: $orphans. Re-image it."

SERVER_IP="${LP_SERVER_IP:-$(ip -4 route get 1.1.1.1 | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -1)}"
[ -n "$SERVER_IP" ] || die "Could not work out this host's own address. Set LP_SERVER_IP."
ok "This host's address: $SERVER_IP"

# nip.io first, because it is what the installer itself falls back to. Whether it answers from here is
# found out now rather than after a twenty-minute build.
DNS_SUFFIX=""
for candidate in nip.io sslip.io; do
  resolved="$(getent hosts "panel.${SERVER_IP}.${candidate}" 2>/dev/null | awk '{print $1}' | head -1 || true)"
  if [ "$resolved" = "$SERVER_IP" ]; then DNS_SUFFIX="$candidate"; break; fi
  warn "panel.${SERVER_IP}.${candidate} resolved to '${resolved}', not this host."
done
[ -n "$DNS_SUFFIX" ] || die "Neither nip.io nor sslip.io resolves to $SERVER_IP from here, so no name in this proof can work."

in_container="$(docker run --rm alpine:3.20 getent hosts "panel.${SERVER_IP}.${DNS_SUFFIX}" 2>/dev/null | awk '{print $1}' | head -1 || true)"
[ "$in_container" = "$SERVER_IP" ] || die "panel.${SERVER_IP}.${DNS_SUFFIX} resolves on the host, but inside a container it gave '${in_container}'."
fact "Names: $DNS_SUFFIX on this host's private address resolves on the host and inside containers."

export PANEL_DOMAIN="panel.${SERVER_IP}.${DNS_SUFFIX}"
export ROOT_DOMAIN="apps.${SERVER_IP}.${DNS_SUFFIX}"
export ACME_EMAIL="proof@harbora-proof.test"
export REPO_URL="$LP_REPO_URL"
export REPO_BRANCH="$LP_REF"
PANEL_URL="https://${PANEL_DOMAIN}"

OWNER_EMAIL="${LP_OWNER_EMAIL:-owner@harbora-proof.test}"
OWNER_PASSWORD="${LP_OWNER_PASSWORD:-$(openssl rand -hex 16)}"
remember_secret "$OWNER_PASSWORD"

# For the Git server the flows set starts on this host. By address rather than by name: it speaks plain
# HTTP on a port of its own, and nothing about it goes through Traefik.
GIT_URL="http://${SERVER_IP}:${LP_GIT_PORT}"
GIT_PASSWORD="$(openssl rand -hex 16)"
remember_secret "$GIT_PASSWORD"

# ---------------------------------------------------------------------------------------------
step "1 — a local certificate authority"
# ---------------------------------------------------------------------------------------------
# Pebble's own listener needs a certificate too, and Traefik has to trust that: it is what
# LEGO_CA_CERTIFICATES is for. The certificate it then issues to apps comes from a chain Pebble makes
# up when it starts, so that has to be read back from it. Host network, so that its HTTP-01 challenge
# goes to this host's port 80 by the same address a name resolves to, with no NAT of its own to go wrong.
mkdir -p "$PEBBLE_DIR"
chmod 755 /var/lib/harbora-proof "$PEBBLE_DIR"
(
  cd "$PEBBLE_DIR"
  openssl req -x509 -newkey rsa:2048 -nodes -days 3 -subj "/CN=harbora proof, the ACME listener's CA" \
    -keyout tls-ca.key -out tls-ca.pem >/dev/null 2>&1
  openssl req -newkey rsa:2048 -nodes -subj "/CN=$SERVER_IP" -keyout key.pem -out cert.csr >/dev/null 2>&1
  printf 'subjectAltName=IP:%s\n' "$SERVER_IP" > cert.ext
  openssl x509 -req -in cert.csr -CA tls-ca.pem -CAkey tls-ca.key -CAcreateserial -days 3 \
    -extfile cert.ext -out cert.pem >/dev/null 2>&1
  chmod 644 ./*
)
cat > "$PEBBLE_DIR/config.json" <<'PEBBLE'
{
  "pebble": {
    "listenAddress": "0.0.0.0:14000",
    "managementListenAddress": "0.0.0.0:15000",
    "certificate": "/pebble/cert.pem",
    "privateKey": "/pebble/key.pem",
    "httpPort": 80,
    "tlsPort": 443,
    "ocspResponderURL": "",
    "externalAccountBindingRequired": false,
    "retryAfter": { "authz": 3, "order": 5 },
    "keyAlgorithm": "ecdsa",
    "profiles": { "default": { "description": "The proof's", "validityPeriod": 7776000 } }
  }
}
PEBBLE

# PEBBLE_VA_NOSLEEP is the only leniency: its validator otherwise sleeps at random to imitate a slow
# one. PEBBLE_WFE_NONCEREJECT=0 stops it rejecting 5% of requests to test clients' retries, which is a
# test of lego, not of anything here. It still really validates each challenge over port 80.
docker run -d --name harbora-proof-pebble --network host \
  -e PEBBLE_VA_NOSLEEP=1 -e PEBBLE_WFE_NONCEREJECT=0 \
  -v "$PEBBLE_DIR:/pebble:ro" "$LP_PEBBLE_IMAGE" -config /pebble/config.json >/dev/null

pebble_root() { curl -fsS --max-time 5 --cacert "$PEBBLE_DIR/tls-ca.pem" "https://${SERVER_IP}:15000/roots/0" -o "$PEBBLE_DIR/root.pem"; }
wait_until 60 "Pebble answers and gives its root" pebble_root
grep -q 'BEGIN CERTIFICATE' "$PEBBLE_DIR/root.pem" || die "What Pebble gave as its root is not a certificate."

export ACME_CA_SERVER="https://${SERVER_IP}:14000/dir"

if   [ -d /usr/local/share/ca-certificates ]; then
  cp "$PEBBLE_DIR/root.pem" /usr/local/share/ca-certificates/harbora-proof-pebble.crt
  update-ca-certificates >/dev/null 2>&1 || die "Refreshing the system trust store failed."
  system_bundle=/etc/ssl/certs/ca-certificates.crt
elif [ -d /etc/pki/ca-trust/source/anchors ]; then
  cp "$PEBBLE_DIR/root.pem" /etc/pki/ca-trust/source/anchors/harbora-proof-pebble.crt
  update-ca-trust >/dev/null 2>&1 || die "Refreshing the system trust store failed."
  system_bundle=/etc/pki/tls/certs/ca-bundle.crt
else
  die "This host has no system trust store I know how to add to."
fi
cat "$system_bundle" > "$PEBBLE_DIR/bundle.pem"
grep -qF "$(sed -n 2p "$PEBBLE_DIR/root.pem")" "$PEBBLE_DIR/bundle.pem" || die "Pebble's root did not make it into the system's bundle."
chmod 644 "$PEBBLE_DIR"/*
fact "A real ACME server (Pebble) is running on this host, and this host trusts its root."

# ---------------------------------------------------------------------------------------------
step "2 — install from the repository's own installer, at $LP_REF"
# ---------------------------------------------------------------------------------------------
installer_ref="${LP_EXPECT_SHA:-$LP_REF}"
log "curl -fsSL ${LP_REPO_RAW}/${installer_ref}/deploy/install.sh | bash"
curl -fsSL "${LP_REPO_RAW}/${installer_ref}/deploy/install.sh" -o "$WORK/install.sh"
grep -q 'Harbora' "$WORK/install.sh" || die "What came from ${LP_REPO_RAW}/${installer_ref}/deploy/install.sh is not the installer."
install_exit=0
bash "$WORK/install.sh" install </dev/null 2>&1 | tee "$WORK/install.log" || install_exit=$?
[ "$install_exit" -eq 0 ] || fail "install.sh exited $install_exit. Its last lines: $(tail -n 25 "$WORK/install.log" | cut -c1-300)"

# Its exit code is not evidence: it calls `verify_install || true` and prints the banner either way.
[ -f "$COMPOSE_DIR/.env" ] || die "install.sh finished but there is no .env at $COMPOSE_DIR/.env."

installed_sha="$(git -C "$APP_DIR" rev-parse HEAD)"
if [ -n "$LP_EXPECT_SHA" ] && [ "$installed_sha" != "$LP_EXPECT_SHA" ]; then
  die "The installer cloned $installed_sha, and this run is for $LP_EXPECT_SHA: the branch moved, so this would prove the wrong commit."
fi
ok "The installed source is $installed_sha."

[ "$(docker inspect -f '{{.State.Status}}' harbora-panel 2>/dev/null)" = "running" ] || die "The panel container is not running after the install."

applied="$(psql_panel "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%AppLaunches'")"
[ "$applied" = "1" ] || die "The AppLaunches migration is not in the migration history (found $applied)."
psql_panel 'SELECT count(*) FROM "AppLaunches"' >/dev/null || die "The migration is recorded, but the AppLaunches table cannot be read."
fact "The real installer ran from nothing, at $installed_sha, and the launch migration applied on real Postgres."

# ---------------------------------------------------------------------------------------------
step "3 — make Traefik and the panel trust that authority"
# ---------------------------------------------------------------------------------------------
# With a compose override, as live-host-proof.sh does it: compose merges these maps and lists, so this
# adds keys without restating anything. ACME_CA_SERVER goes into .env so every later compose command
# keeps it. The panel reads SSL_CERT_FILE because .NET's chain builder honours it, and the probe that
# reads a customer's address is an ordinary HttpClient in that process, which is the whole point.
if ! grep -q '^ACME_CA_SERVER=' "$COMPOSE_DIR/.env"; then
  printf 'ACME_CA_SERVER=%s\n' "$ACME_CA_SERVER" >> "$COMPOSE_DIR/.env"
fi
cat > "$COMPOSE_DIR/docker-compose.override.yml" <<OVERRIDE
# Written by deploy/launch-proof.sh. NOT part of a normal install.
services:
  traefik:
    environment:
      LEGO_CA_CERTIFICATES: /pebble/tls-ca.pem
    volumes:
      - ${PEBBLE_DIR}:/pebble:ro
  panel:
    environment:
      SSL_CERT_FILE: /pebble/bundle.pem
    volumes:
      - ${PEBBLE_DIR}:/pebble:ro
OVERRIDE
( cd "$COMPOSE_DIR" && docker compose up -d traefik panel )

panel_is_trusted() { curl -fsS --max-time 15 -o /dev/null "$PANEL_URL/healthz"; }
wait_until "$W_PANEL" "the panel answers /healthz with a certificate this host trusts" panel_is_trusted

issuer="$(openssl s_client -servername "$PANEL_DOMAIN" -connect "${PANEL_DOMAIN}:443" </dev/null 2>/dev/null \
  | openssl x509 -noout -issuer 2>/dev/null || true)"
case "$issuer" in
  *Pebble*|*pebble*) ok "The panel's certificate came from Pebble over ACME: $issuer" ;;
  *) die "The panel's certificate was not issued by Pebble. Issuer: '$issuer'." ;;
esac
fact "Traefik took the panel's certificate over ACME HTTP-01, and nothing below uses curl -k."

# Asked after the certificate, so that it is the channel's answer rather than the missing certificate's.
# A failure here fails the run and stops nothing: no launch goes through this router, and a run takes
# half an hour, so what it can still say about the launch is worth having.
case_install_verified() {
  if grep -q 'Installation finished WITH ERRORS' "$WORK/install.log"; then
    log "What install.sh said was wrong:"
    grep '✗' "$WORK/install.log" | sed -E 's/\x1b\[[0-9;]*[A-Za-z]//g' | tail -n 8 || true
    log "/dynamic as Traefik sees it:"
    docker exec harbora-traefik ls -la /dynamic || true
    log "Traefik's errors:"
    docker logs harbora-traefik 2>&1 | sed -E 's/\x1b\[[0-9;]*[A-Za-z]//g' | grep -E ' (ERR|WRN) ' | tail -n 15 | cut -c1-300 || true
    fail "install.sh reported its own verification failures: $(grep '✗' "$WORK/install.log" | sed -E 's/\x1b\[[0-9;]*[A-Za-z]//g' | tail -n 8)"
  fi
  ok "install.sh's own verification passed."
}

# The node channel's host carries one router, and that router requires a client certificate: no request
# gets an HTTP answer, whatever install.sh could see before the certificate existed.
case_node_channel_refuses() {
  local node_domain code rc=0
  node_domain="$(sed -n 's/^NODE_DOMAIN=//p' "$COMPOSE_DIR/.env" | head -1)"
  [ -n "$node_domain" ] || fail "The install wrote no NODE_DOMAIN."
  [ -f "$COMPOSE_DIR/traefik/dynamic/node-agent.yml" ] || fail "The node channel's router was never rendered."
  code="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 15 "https://${node_domain}/api/node-agent/v1/channel" 2>/dev/null)" || rc=$?
  log "A request to the node channel with no client certificate: curl exit $rc, HTTP $code"
  case "$rc" in
    35|56) ok "It refuses a connection with no client certificate." ;;
    *) fail "The node channel at $node_domain answered a request with no client certificate: curl exit $rc, HTTP $code. Traefik: $(docker logs harbora-traefik 2>&1 | sed -E 's/\x1b\[[0-9;]*[A-Za-z]//g' | grep -E ' (ERR|WRN) ' | tail -n 6 | cut -c1-300)" ;;
  esac
}

step_case "3b — what install.sh verified about itself" case_install_verified
step_case "3c — the node channel asks for a client certificate" case_node_channel_refuses

# ---------------------------------------------------------------------------------------------
step "4 — create the owner through the first-run wizard"
# ---------------------------------------------------------------------------------------------
setup_token="$(antiforgery_token /setup)"
status="$(web_post /setup "$setup_token" \
  "__RequestVerificationToken=$setup_token" \
  "PlatformName=Harbora Launch Proof" \
  "DisplayName=Proof Owner" \
  "Email=$OWNER_EMAIL" \
  "Password=$OWNER_PASSWORD" \
  "ConfirmPassword=$OWNER_PASSWORD" \
  "RootDomain=$ROOT_DOMAIN" \
  "AcmeEmail=$ACME_EMAIL" \
  "Culture=en")"
case "$status" in
  302|303) ok "The first-run wizard accepted the owner (HTTP $status)." ;;
  *) die "POST /setup answered HTTP $status, not a redirect." ;;
esac

rm -f "$COOKIES"
login_token="$(antiforgery_token /account/login)"
status="$(web_post /account/login "$login_token" \
  "__RequestVerificationToken=$login_token" "Email=$OWNER_EMAIL" "Password=$OWNER_PASSWORD")"
case "$status" in
  302|303) ok "The owner can sign in through the real login form." ;;
  *) die "Signing in as the new owner answered HTTP $status, not a redirect." ;;
esac

API_TOKEN="$(curl -fsS --max-time 60 -X POST -H 'Content-Type: application/json' \
  -d "$(jq -nc --arg e "$OWNER_EMAIL" --arg p "$OWNER_PASSWORD" '{email:$e, password:$p, name:"launch-proof"}')" \
  "$PANEL_URL/api/v1/auth/token" | jq -er '.token')"
remember_secret "$API_TOKEN"
[ -n "$API_TOKEN" ] || die "POST /api/v1/auth/token returned no token."
must_eq "apps on a fresh install" "$(api GET /api/v1/apps | jq 'length')" 0

# ---------------------------------------------------------------------------------------------
step "5 — publish the real CLI from the ref under test and sign in with it"
# ---------------------------------------------------------------------------------------------
# In a container, so that nothing has to be installed on this host, from a copy of the installed
# source, so that this is the ref under test and leaves no obj/ in the installer's checkout. The same
# flags as release-cli.yml, so that this is the file a customer downloads and not a debug build.
mkdir -p "$WORK/cli-src" "$WORK/cli"
git -C "$APP_DIR" archive HEAD src/Harbora.Cli src/Harbora.Shared Directory.Build.props | tar -x -C "$WORK/cli-src"
docker run --rm -v "$WORK/cli-src:/src" -v "$WORK/cli:/out" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish src/Harbora.Cli/Harbora.Cli.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false \
  -o /out --nologo -v q > "$WORK/out/publish.txt" 2>&1 \
  || fail "dotnet publish of the CLI failed: $(tail -n 15 "$WORK/out/publish.txt" | cut -c1-300)"
HARBORA="$WORK/cli/harbora"
[ -x "$HARBORA" ] || die "dotnet publish finished, and there is no executable at $HARBORA."

"$HARBORA" login --server "$PANEL_URL" --email "$OWNER_EMAIL" --password "$OWNER_PASSWORD" > "$WORK/out/login.txt" 2>&1 \
  || die "harbora login failed: $(cat "$WORK/out/login.txt")"
"$HARBORA" whoami > "$WORK/out/whoami.txt" 2>&1 || die "harbora whoami failed: $(cat "$WORK/out/whoami.txt")"
grep -qF "$OWNER_EMAIL" "$WORK/out/whoami.txt" || die "harbora whoami does not name the owner: $(cat "$WORK/out/whoami.txt")"
fact "The CLI built from this commit signs in and talks to the panel over real TLS."

# ---------------------------------------------------------------------------------------------
# The cases. Each is its own subshell; see step_case.
# ---------------------------------------------------------------------------------------------

# Nothing in a launch needs a bucket, and the installer starts the store regardless. Here because its
# image once stopped every fresh install at the first pull, so the replacement has to be seen working:
# the healthcheck runs its mc, and a bucket is made by the panel's own helper from the same image.
case_object_store() {
  object_store_healthy() { [ "$(docker inspect -f '{{.State.Health.Status}}' harbora-minio 2>/dev/null)" = "healthy" ]; }
  wait_until 120 "the object store's healthcheck passes" object_store_healthy

  local token status bucket
  bucket="proof-bucket-$RUN_ID"
  token="$(antiforgery_token /storage)"
  status="$(web_post /storage/buckets "$token" "__RequestVerificationToken=$token" "name=$bucket")"
  case "$status" in
    302|303) ;;
    *) fail "POST /storage/buckets answered HTTP $status, not a redirect." ;;
  esac
  must_eq "buckets called $bucket" "$(psql_panel "SELECT count(*) FROM \"StorageBuckets\" WHERE \"Name\"='$bucket' AND \"Status\"=1")" 1
  fact "The object store from the install is healthy, and the panel's helper made a bucket on it with the same image's mc."
}

case_node_postgres() {
  local dir url body status secret
  dir="$(fixture node-postgres)"
  printf '%s' "$dir" > "$WORK/state/node-postgres.dir"

  # What it was told, and what it chose to say. Then what it stored. Then what the app says.
  run_launch node-postgres "$dir" --set API_KEY=proof-answer
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  say_must "Your app is running"

  must_eq "the launch's status" "$(view_field status)" Succeeded
  all_steps_done
  all_checks_passed running restarts url logs
  must_eq "one database check, passed" "$(service_checks)" Passed

  url="$(view_field publicUrl)"
  [ -n "$url" ] || fail "A web app's launch ended with no public address."
  case "$url" in
    https://*".${ROOT_DOMAIN}"*) ok "Its address: $url" ;;
    *) fail "Its address is not under the root domain: $url" ;;
  esac

  [ -f "$dir/harbora.yml" ] || fail "harbora launch wrote no harbora.yml into $dir, so the next day's deploy has nothing to go on."
  grep -q '^app: ' "$dir/harbora.yml" || fail "harbora.yml names no app: $(cat "$dir/harbora.yml")"
  grep -qF "server: $PANEL_URL" "$dir/harbora.yml" || fail "harbora.yml names no server: $(cat "$dir/harbora.yml")"

  # The whole point. Nothing but the running app itself can say this.
  body="$WORK/out/node-postgres.body.json"
  status="$(http_get "$url/" "$body")"
  must_eq "GET / through Traefik" "$status" 200
  cat "$body"; echo
  jq -e '.viaUrl.ok == true and .viaUrl.one == 1' "$body" >/dev/null \
    || fail "The app could not query its database through DATABASE_URL: $(jq -c .viaUrl "$body")"
  jq -e '.viaHost.ok == true and .viaHost.one == 1' "$body" >/dev/null \
    || fail "The app could not query its database through DB_HOST and PGUSER/PGPASSWORD/PGDATABASE: $(jq -c .viaHost "$body")"
  must_eq "the answer given with --set" "$(jq -r .apiKey "$body")" proof-answer
  must_eq "the example file's value" "$(jq -r .logLevel "$body")" info
  must_eq "the version it was built from" "$(jq -r .version "$body")" first

  case "$(jq -r .dbHost "$body")" in
    ""|null|localhost|127.0.0.1) fail "The alias DB_HOST reached the app as '$(jq -r .dbHost "$body")'." ;;
    *) ok "The alias DB_HOST reached the app as a real host." ;;
  esac

  local length
  length="$(jq -r .jwtSecretLength "$body")"
  [ "$length" -ge 32 ] || fail "JWT_SECRET reached the app at a length of $length: that is not a generated secret."

  local slug
  slug="$(slug_of_app "$(view_field appId)")"
  [ -n "$slug" ] || fail "GET /api/v1/apps has no app with the launch's id."
  api GET "/api/v1/apps/$slug/env" > "$WORK/out/node-postgres.env.json"
  secret="$(jq -r 'first(.[]|select(.key=="JWT_SECRET")|.value) // ""' "$WORK/out/node-postgres.env.json")"
  remember_secret "$secret"
  must_eq "JWT_SECRET's stored length against the app's" "${#secret}" "$length"
  must_eq "JWT_SECRET is kept as a secret" "$(jq -r 'first(.[]|select(.key=="JWT_SECRET")|.isSecret)' "$WORK/out/node-postgres.env.json")" true
  if grep -qF -- "$secret" "$WORK/out/node-postgres.txt"; then fail "harbora launch printed the generated secret."; fi
  if grep -qF -- "$secret" "$VIEW"; then fail "GET /api/v1/launch/{id} carries the generated secret."; fi

  printf '%s' "$LAUNCH_ID" > "$WORK/state/node-postgres.launch"
  printf '%s' "$url" > "$WORK/state/node-postgres.url"
  printf '%s' "$slug" > "$WORK/state/node-postgres.slug"
  fact "node-postgres: exit 0, Succeeded, every check passed; the running app itself ran SELECT 1 through DATABASE_URL and through the DB_HOST alias, read a generated JWT_SECRET of $length characters, API_KEY from --set and LOG_LEVEL from the example file."
}

case_crashes() {
  local dir
  dir="$(fixture crashes)"
  run_launch crashes "$dir"

  [ "$(view_field status)" != "Succeeded" ] || fail "An app that exits 8 seconds after every start was reported Succeeded. Checks: $(jq -c '[.checks[]|{key,status}]' "$VIEW")"
  [ "$LAUNCH_EXIT" -ne 0 ] || fail "An app that keeps crashing ended harbora launch with exit 0."

  # It answers its probe first, so the deployment passes: what catches it is the check, and that is the
  # one thing a green deployment could never say.
  must_eq "the launch's status" "$(view_field status)" SucceededWithProblems
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 2
  must_eq "the restarts check" "$(check_status restarts)" Failed
  check_detail restarts | holds "giving up on purpose" \
    || fail "The restarts check does not quote the app's last words: $(check_detail restarts)"
  say_must "It is running, but something is wrong"
  retire_app "$(view_field appId)"
  fact "crashes: a deployment that passed its probe, then exit 2 and SucceededWithProblems, and the restarts check says so and quotes the log."
}

case_wrong_database_host() {
  local dir url detail
  dir="$(fixture wrong-database-host)"
  run_launch wrong-database-host "$dir"

  # A page answering 200 while the log says it cannot reach its database is not a success: nothing
  # about the container is wrong, and the warning is the only thing that knows. So it must not end green.
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 2
  must_eq "the launch's status" "$(view_field status)" SucceededWithProblems
  must_eq "the logs check" "$(check_status logs)" Warning
  detail="$(check_detail logs)"
  case "$detail" in
    *localhost*|*127.0.0.1*|*::1*) ok "The logs check quotes the address: $detail" ;;
    *) fail "The logs check does not name what it connected to: $detail" ;;
  esac
  must_eq "the database it was given still answers" "$(service_checks)" Passed
  url="$(view_field publicUrl)"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$WORK/out/wrong-db.body.json")" 200
  must_eq "the app's own account of its database" "$(jq -r .database "$WORK/out/wrong-db.body.json")" false
  retire_app "$(view_field appId)"
  fact "wrong-database-host: exit 2 and SucceededWithProblems because of the logs check, which names the loopback address it could not reach, while the database it ignored answered."
}

case_python_worker() {
  local dir slug container
  dir="$(fixture python-worker-redis)"
  run_launch python-worker-redis "$dir"

  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  must_eq "the launch's status" "$(view_field status)" Succeeded
  must_eq "a worker's url check" "$(check_status url)" absent
  [ -z "$(view_field publicUrl)" ] || fail "A worker was given a public address: $(view_field publicUrl)"
  say_must "It has no address of its own"
  must_eq "the running check" "$(check_status running)" Passed
  must_eq "the restarts check" "$(check_status restarts)" Passed
  [ "$(jq '[.checks[]|select(.key|startswith("service:"))]|length' "$VIEW")" = "1" ] || fail "There should be one Redis check: $(jq -c '[.checks[]|{key,status}]' "$VIEW")"
  case "$(service_checks)" in
    Failed*) fail "The Redis it was given does not answer: $(jq -c '[.checks[]|select(.key|startswith("service:"))]' "$VIEW")" ;;
    *) ok "The Redis check: $(service_checks)" ;;
  esac

  slug="$(slug_of_app "$(view_field appId)")"
  container="$(container_of "$slug")"
  [ -n "$container" ] || fail "No running container carries the label harbora.app=$slug."
  docker logs "$container" 2>&1 | tail -n 5
  docker logs "$container" 2>&1 | holds 'redis answered: ping=True' \
    || fail "The worker's log never shows Redis answering: $(docker logs --tail 5 "$container" 2>&1 | cut -c1-300)"
  retire_app "$(view_field appId)"
  fact "python-worker-redis: a worker from a Procfile line, with no address and no url check, whose own log shows Redis answering through REDIS_URL."
}

case_two_apps() {
  local dir apps_before url
  dir="$(fixture two-apps)"
  apps_before="$(api GET /api/v1/apps | jq 'length')"

  # With nothing to say which, and nobody to ask.
  run_launch two-apps-unanswered "$dir"
  must_eq "harbora launch's exit code with no --folder" "$LAUNCH_EXIT" 1
  say_must "api, web"
  say_must "--folder"
  must_eq "the launch's status" "$(view_field status)" Abandoned
  must_eq "apps created by that" "$(api GET /api/v1/apps | jq 'length')" "$apps_before"
  [ ! -f "$dir/harbora.yml" ] || fail "A launch that created nothing wrote a harbora.yml."

  run_launch two-apps-api "$dir" --folder api
  must_eq "harbora launch's exit code with --folder api" "$LAUNCH_EXIT" 0
  must_eq "the launch's status" "$(view_field status)" Succeeded
  url="$(view_field publicUrl)"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$WORK/out/two-apps.body.json")" 200
  must_eq "the app that answers" "$(jq -r .app "$WORK/out/two-apps.body.json")" api
  retire_app "$(view_field appId)"
  fact "two-apps: with no --folder, exit 1, naming both, nothing created; with --folder api, it built and served that folder."
}

case_deploy_again() {
  local dir url body slug
  [ -s "$WORK/state/node-postgres.dir" ] && [ -s "$WORK/state/node-postgres.url" ] \
    || fail "Step 6 did not finish, so there is no launched app to deploy again."
  dir="$(cat "$WORK/state/node-postgres.dir")"
  url="$(cat "$WORK/state/node-postgres.url")"
  slug="$(cat "$WORK/state/node-postgres.slug")"

  printf 'second\n' > "$dir/version.txt"
  run_deploy "$dir" deploy-again
  must_eq "harbora deploy's exit code" "$DEPLOY_EXIT" 0
  [ -n "$DEPLOY_ID" ] || fail "harbora deploy named no deployment. It said: $(tail -c 800 "$WORK/out/deploy-again.flat")"

  body="$WORK/out/deploy-again.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  must_eq "the version it now serves" "$(jq -r .version "$body")" second
  jq -e '.viaUrl.ok == true and .viaHost.ok == true' "$body" >/dev/null \
    || fail "After a second deploy the app can no longer reach its database: $(jq -c '{viaUrl,viaHost}' "$body")"
  must_eq "the answer given with --set, still there" "$(jq -r .apiKey "$body")" proof-answer
  fact "harbora deploy, from the folder and the harbora.yml the launch wrote: the change is served, and the app still reaches its database both ways ($slug)."
}

case_pages() {
  local launch slug secret password
  [ -s "$WORK/state/node-postgres.launch" ] || fail "Step 6 did not finish, so there is no finished launch to look at."
  launch="$(cat "$WORK/state/node-postgres.launch")"
  slug="$(cat "$WORK/state/node-postgres.slug")"
  printf '%s' "$launch" > "$WORK/current-launch"

  secret="$(api GET "/api/v1/apps/$slug/env" | jq -r 'first(.[]|select(.key=="JWT_SECRET")|.value) // ""')"
  [ "${#secret}" -ge 32 ] || fail "Could not read the generated secret to look for it."
  password="$(api GET "/api/v1/apps/$slug/env" | jq -r 'first(.[]|select(.key=="PGPASSWORD")|.value) // ""')"
  [ -n "$password" ] || fail "Could not read the database's password to look for it."
  remember_secret "$password"

  page() {
    local path="$1" file="$WORK/out/page$2.html" code
    code="$(curl -sS --max-time 60 -o "$file" -w '%{http_code}' -b "$COOKIES" "$PANEL_URL$path")"
    must_eq "GET $path" "$code" 200
    shift 2
    local marker
    for marker in "$@"; do
      grep -qF -- "$marker" "$file" || fail "GET $path has no $marker in it. Its start: $(head -c 400 "$file" | tr '\n' ' ')"
    done
    if grep -qF -- "$secret" "$file"; then fail "GET $path carries the generated JWT_SECRET in its HTML."; fi
    if grep -qF -- "$password" "$file"; then fail "GET $path carries the database's password in its HTML."; fi
    ok "GET $path has what it should and neither secret."
  }

  page /launch 1 'data-launch-question' 'data-launch-git'
  page "/launch/$launch" 2 'data-launch-state' 'data-status="Succeeded"' 'data-launch-checks' 'data-check-status="Passed"' 'data-launch-address-link'
  page "/launch/$launch/progress" 3 'data-launch-state' 'data-terminal="true"' 'data-launch-checks'

  if docker logs harbora-panel 2>&1 | holds "$secret"; then fail "The panel's log holds the generated JWT_SECRET."; fi
  fact "The launch pages render over a real finished launch with the markers they should have, and neither the generated secret nor the database password is in their HTML or the panel's log."
}

case_vite() {
  local dir url greeting private root asset slug image app_id log_file
  dir="$(fixture vite-spa)"
  greeting="proof-greeting-$RUN_ID"
  private="proof-private-$RUN_ID"
  run_launch vite-spa "$dir" --set "VITE_GREETING=$greeting" --set "PROOF_PRIVATE_NOTE=$private"

  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  must_eq "the launch's status" "$(view_field status)" Succeeded
  all_checks_passed running restarts url logs

  log_file="$WORK/out/vite-spa.deploy.log"
  deployment_log "$(view_field deploymentId)" > "$log_file"
  grep -qF "Frontend (Nginx)" "$log_file" || fail "The deploy log never says this was built as a frontend."

  url="$(view_field publicUrl)"
  root="$WORK/out/vite-root.html"
  must_eq "GET /" "$(http_get "$url/" "$root")" 200
  grep -qF 'id="app"' "$root" || fail "GET / is not the built page: $(head -c 300 "$root")"
  grep -qF "<title>$greeting</title>" "$root" || fail "The built page does not hold VITE_GREETING's value: $(head -c 300 "$root")"

  must_eq "GET /a/deep/path" "$(http_get "$url/a/deep/path" "$WORK/out/vite-deep.html")" 200
  cmp -s "$root" "$WORK/out/vite-deep.html" || fail "A deep path does not fall back to the page."

  asset="$(grep -oE 'assets/index-[A-Za-z0-9_-]+\.js' "$root" | head -1 || true)"
  [ -n "$asset" ] || fail "The built page names no hashed asset: $(head -c 400 "$root")"
  must_eq "GET /$asset" "$(http_get "$url/$asset" "$WORK/out/vite-asset.js")" 200
  grep -qF "$greeting" "$WORK/out/vite-asset.js" || fail "The hashed bundle does not hold VITE_GREETING's value."

  must_eq "GET /assets/" "$(http_get "$url/assets/" "$WORK/out/vite-listing.html")" 403
  if grep -qF "${asset#assets/}" "$WORK/out/vite-listing.html"; then fail "/assets/ lists the folder's files."; fi

  slug="$(slug_of_app "$(view_field appId)")"
  image="$(image_of_app "$slug")"
  [ -n "$image" ] || fail "No running container carries the label harbora.app=$slug."
  image_contains "$image" "$greeting" || fail "The search of the image cannot find even the value that is certainly in it, so it proves nothing."
  if image_contains "$image" "$private"; then fail "A non-public variable is in the image or its history."; fi

  printf '%s' "$dir" > "$WORK/state/vite.dir"
  printf '%s' "$url" > "$WORK/state/vite.url"
  printf '%s' "$greeting" > "$WORK/state/vite.greeting"
  view_field appId > "$WORK/state/vite.app"
  fact "vite-spa: built as a frontend; / is the built page, a deep path falls back to it, a hashed asset is served, /assets/ is a 403 with no listing; VITE_GREETING is in the page and the bundle; a non-public variable is nowhere in the image or its history (and the same search does find the public one); nginx's start-up lines raised no logs warning."
}

case_vite_redeploy() {
  local dir url old new app_id root asset log_file
  [ -s "$WORK/state/vite.dir" ] || fail "Step 13 did not finish, so there is no Vite app to deploy again."
  dir="$(cat "$WORK/state/vite.dir")"
  url="$(cat "$WORK/state/vite.url")"
  old="$(cat "$WORK/state/vite.greeting")"
  app_id="$(cat "$WORK/state/vite.app")"
  new="proof-changed-$RUN_ID"

  set_variable_on_page "$app_id" VITE_GREETING "$new"
  run_deploy "$dir" vite-redeploy
  must_eq "harbora deploy's exit code" "$DEPLOY_EXIT" 0
  [ -n "$DEPLOY_ID" ] || fail "harbora deploy named no deployment."

  log_file="$WORK/out/vite-redeploy.deploy.log"
  deployment_log "$DEPLOY_ID" > "$log_file"
  grep -E '^Build cache:' "$log_file" || true
  grep -qF "Build cache: reusing layers" "$log_file" || fail "The second deploy was not built from the first one's cache, so this says nothing about --cache-from: $(grep -E '^Build cache:' "$log_file" | head -2)"

  root="$WORK/out/vite-redeploy.html"
  must_eq "GET /" "$(http_get "$url/" "$root")" 200
  grep -qF "<title>$new</title>" "$root" || fail "After changing VITE_GREETING and deploying, the page still says: $(grep -o '<title>[^<]*</title>' "$root")"
  asset="$(grep -oE 'assets/index-[A-Za-z0-9_-]+\.js' "$root" | head -1 || true)"
  must_eq "GET /$asset" "$(http_get "$url/$asset" "$WORK/out/vite-redeploy.js")" 200
  grep -qF "$new" "$WORK/out/vite-redeploy.js" || fail "The new bundle does not hold the new value."
  if grep -qF "$old" "$WORK/out/vite-redeploy.js"; then fail "The new bundle still holds the old value."; fi
  fact "A second deploy after VITE_GREETING changed, built with --cache-from on the multi-stage image: the new value is in the page and the bundle, and the old one is gone."
}

# The last line of a deployment's log is what the page and `harbora logs` leave a person looking at.
last_log_line() { api GET "/api/v1/deployments/$1/logs" | jq -r '.[-1].message // ""'; }

case_frontend_failures() {
  local dir last

  dir="$(fixture frontend-build-fails)"
  run_launch frontend-build-fails "$dir"
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 1
  must_eq "the launch's status" "$(view_field status)" Failed
  last="$(last_log_line "$(view_field deploymentId)")"
  log "The deploy log's last line: $last"
  case "$last" in
    *"npm run build"*"this build fails on purpose"*|*"this build fails on purpose"*"npm run build"*)
      ok "The last line names the command and what it printed." ;;
    *) fail "A failed build's last log line does not say why. It is: $last" ;;
  esac
  case "$(view_field failureMessage)" in
    *"this build fails on purpose"*) ok "The launch's own message says why too." ;;
    *) fail "The launch's message does not say why the build failed: $(view_field failureMessage)" ;;
  esac
  : > "$WORK/current-launch"

  dir="$(fixture frontend-no-index)"
  run_launch frontend-no-index "$dir"
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 1
  must_eq "the launch's status" "$(view_field status)" Failed
  last="$(last_log_line "$(view_field deploymentId)")"
  log "The deploy log's last line: $last"
  case "$last" in
    *"without producing an index.html"*) ok "The last line is the sentence." ;;
    *) fail "A build with no index.html does not say so on its last log line. It is: $last" ;;
  esac
  case "$(view_field failureMessage)" in
    *"without producing an index.html"*) ok "The launch's own message says why too." ;;
    *) fail "The launch's message does not say the build produced no site: $(view_field failureMessage)" ;;
  esac
  fact "A frontend whose build fails, and one that builds no site, each end Failed at exit 1 with the reason on the last line of the deploy log and in the launch's message."
}

case_cra() {
  local dir url root started elapsed result
  dir="$(fixture cra-shaped)"
  run_launch cra-shaped "$dir"
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  must_eq "the launch's status" "$(view_field status)" Succeeded
  deployment_log "$(view_field deploymentId)" | holds "Frontend (Nginx)" \
    || fail "A Create React App project was not built as a frontend."

  url="$(view_field publicUrl)"
  root="$WORK/out/cra-root.html"
  must_eq "GET /" "$(http_get "$url/" "$root")" 200
  grep -qF 'id="root"' "$root" || fail "GET / is not the page: $(head -c 300 "$root")"
  grep -qE 'static/js/main\.[a-z0-9]+\.js' "$root" || fail "GET / is the source's template rather than the built page: $(head -c 400 "$root")"
  if grep -qF '%PUBLIC_URL%' "$root"; then fail "GET / still has %PUBLIC_URL% in it: this is the unbuilt template."; fi

  # What the old path did. A record rather than an assertion: either way a site with a build is built and
  # served, and this is what that rule costs somebody whose dev server was running until now.
  started="$(date +%s)"
  result="$(docker run --rm -v "$dir:/app" -w /app node:20-alpine sh -c '
    npm install --no-audit --no-fund >/dev/null 2>&1
    npm start </dev/null >/tmp/start.log 2>&1 &
    pid=$!
    for attempt in $(seq 1 24); do
      sleep 5
      if ! kill -0 "$pid" 2>/dev/null; then echo "it stopped by itself"; exit 0; fi
      if wget -qO- http://127.0.0.1:3000/ 2>/dev/null | grep -q "id=\"root\""; then echo "it stayed up and served its page"; exit 0; fi
    done
    echo "it stayed up and served nothing in two minutes"
  ' | tail -n 1)"
  elapsed="$(( $(date +%s) - started ))"
  log "react-scripts start, run directly in a container with stdin closed, after ${elapsed}s: $result"
  retire_app "$(view_field appId)"
  fact "cra-shaped: a real react-scripts project with 'start: react-scripts start' built as a frontend and served its built page. Run directly in a container with stdin closed, that start command: $result."
}

case_node_build_var() {
  local dir url slug image value
  dir="$(fixture node-build-var)"
  value="https://api.proof-$RUN_ID.example"
  run_launch node-build-var "$dir" --set "NEXT_PUBLIC_API=$value"
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  must_eq "the launch's status" "$(view_field status)" Succeeded

  url="$(view_field publicUrl)"
  must_eq "GET /" "$(http_get "$url/" "$WORK/out/node-var.json")" 200
  must_eq "what the build wrote" "$(jq -r .builtWith "$WORK/out/node-var.json")" "$value"

  slug="$(slug_of_app "$(view_field appId)")"
  image="$(image_of_app "$slug")"
  [ -n "$image" ] || fail "No running container carries the label harbora.app=$slug."
  if docker inspect "$image" --format '{{json .Config.Env}}' | holds "NEXT_PUBLIC_API"; then
    fail "NEXT_PUBLIC_API is in the image's ENV: it should only ever be an ARG. $(docker inspect "$image" --format '{{json .Config.Env}}')"
  fi
  retire_app "$(view_field appId)"
  fact "node-build-var: a build on the Node image read a public variable set with --set and ticked by nobody; the image's ENV does not hold it."
}

case_python_marked() {
  local dir url app_id value slug image
  dir="$(fixture python-marked-var)"
  value="proof-mark-$RUN_ID"
  run_launch python-marked-var "$dir"
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  must_eq "the launch's status" "$(view_field status)" Succeeded

  url="$(view_field publicUrl)"
  must_eq "GET /" "$(http_get "$url/" "$WORK/out/py-mark-1.json")" 200
  must_eq "what the build saw before anything was marked" "$(jq -r .builtWith "$WORK/out/py-mark-1.json")" ""

  app_id="$(view_field appId)"
  set_variable_on_page "$app_id" BUILD_MARK "$value" true
  run_deploy "$dir" python-marked-redeploy
  must_eq "harbora deploy's exit code" "$DEPLOY_EXIT" 0
  must_eq "GET /" "$(http_get "$url/" "$WORK/out/py-mark-2.json")" 200
  must_eq "what the build saw once it was marked" "$(jq -r .builtWith "$WORK/out/py-mark-2.json")" "$value"

  slug="$(slug_of_app "$app_id")"
  image="$(image_of_app "$slug")"
  [ -n "$image" ] || fail "No running container carries the label harbora.app=$slug."
  if docker inspect "$image" --format '{{json .Config.Env}}' | holds "BUILD_MARK"; then
    fail "BUILD_MARK is in the image's ENV: it should only ever be an ARG."
  fi
  # A record rather than an assertion: Buildpacks.ArgLines says a single-stage image's history can show
  # a marked value, and what it really shows depends on the daemon.
  if { docker history --no-trunc "$image" 2>/dev/null || true; } | holds "$value"; then history_says="shows"; else history_says="does not show"; fi
  retire_app "$app_id"
  fact "python-marked-var: a pip install step saw nothing until the variable was marked AvailableAtBuild, then saw its value; the image's ENV does not hold it, and on Docker $(docker version --format '{{.Server.Version}}' 2>/dev/null) its docker history $history_says the value (a single-stage image, which Buildpacks.ArgLines warns about)."
}

case_php_pdo() {
  local dir url body
  dir="$(fixture php-pdo)"

  # What --yes can do: it takes the proposal as it stands, and there is no flag to tick an engine the
  # inspector did not find. So this project says which in the one place a PHP project can, its composer
  # requirements, and it needs no --set: the four names are the attachment's to fill.
  run_launch php-pdo "$dir"
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  must_eq "the launch's status" "$(view_field status)" Succeeded
  all_checks_passed running restarts url logs
  must_eq "one database check, passed" "$(service_checks)" Passed

  url="$(view_field publicUrl)"
  body="$WORK/out/php-pdo.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  cat "$body"; echo
  jq -e '.ok == true and .one == 1' "$body" >/dev/null \
    || fail "The PHP app could not query MySQL through DB_HOST, DB_USER, DB_PASSWORD and DB_NAME: $(jq -c . "$body" 2>/dev/null || head -c 500 "$body")"
  case "$(jq -r .dbHost "$body")" in
    ""|null|localhost|127.0.0.1) fail "DB_HOST reached the PHP app as '$(jq -r .dbHost "$body")'." ;;
    *) ok "DB_HOST reached the PHP app as a real host." ;;
  esac
  fact "php-pdo: a plain PHP app on the generated image ran SELECT 1 against the MySQL it was given, through DB_HOST, DB_USER, DB_PASSWORD and DB_NAME, with no --set."
}

case_laravel() {
  local dir url body log_file
  dir="$WORK/apps/laravel-real"

  # Generated now, because that is what a person has. Locked for the PHP the generated image runs: a
  # project locked on a newer PHP than the image is a different question, which this does not ask. The
  # one change is what somebody with MySQL makes: its example file says so, as Laravel's docs have it.
  docker run --rm -v "$WORK/apps:/apps" -w /apps composer:2 sh -c '
    composer create-project --no-interaction --no-progress --no-install --no-scripts "laravel/laravel:^12.0" laravel-real &&
    cd laravel-real &&
    composer config platform.php 8.3.30 &&
    composer install --no-interaction --no-progress --no-scripts
  ' > "$WORK/out/laravel-create.txt" 2>&1 \
    || fail "composer create-project failed: $(tail -n 12 "$WORK/out/laravel-create.txt" | cut -c1-300)"
  [ -f "$dir/artisan" ] || fail "composer create-project made no artisan."

  sed -i -e 's/^DB_CONNECTION=sqlite/DB_CONNECTION=mysql/' -e 's/^# DB_/DB_/' "$dir/.env.example"
  cat >> "$dir/routes/web.php" <<'ROUTE'

// Added by the proof. What the framework itself can say about the database it was given.
Route::get('/proof', fn () => [
    'driver' => Illuminate\Support\Facades\DB::connection()->getDriverName(),
    'one' => Illuminate\Support\Facades\DB::selectOne('select 1 as one')->one,
    'migrations' => Illuminate\Support\Facades\DB::table('migrations')->count(),
]);
ROUTE

  run_launch laravel-real "$dir"
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  must_eq "the launch's status" "$(view_field status)" Succeeded

  log_file="$WORK/out/laravel-real.deploy.log"
  deployment_log "$(view_field deploymentId)" > "$log_file"
  grep -qF "php artisan migrate --force" "$log_file" || fail "The deploy log never shows the release task running the migrations."

  url="$(view_field publicUrl)"
  must_eq "GET /" "$(http_get "$url/" "$WORK/out/laravel-root.html")" 200
  grep -qi 'laravel' "$WORK/out/laravel-root.html" || fail "GET / is not Laravel's page: $(head -c 300 "$WORK/out/laravel-root.html")"

  body="$WORK/out/laravel-proof.json"
  must_eq "GET /proof" "$(http_get "$url/proof" "$body")" 200
  must_eq "the framework's database driver" "$(jq -r .driver "$body")" mysql
  must_eq "SELECT 1 through the framework" "$(jq -r .one "$body")" 1
  [ "$(jq -r .migrations "$body")" -ge 1 ] || fail "The migrations table is empty: the release task did not reach this database. $(cat "$body")"
  fact "A real Laravel project from composer create-project: composer installed on the generated image, the release task migrated real MySQL, / is Laravel's page through public/, and the framework reads $(jq -r .migrations "$body") migrations back."
}

# ---------------------------------------------------------------------------------------------
# The stacks and engines people bring (21 to 28)
# ---------------------------------------------------------------------------------------------

# What a clean launch of a web app has in common, said once.
launched_clean() {
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  say_must "Your app is running"
  must_eq "the launch's status" "$(view_field status)" Succeeded
  all_steps_done
  all_checks_passed running restarts url logs
}

# What the pipeline says it built this as, in its own log.
built_as() {
  local log_file="$WORK/out/built-as.log"
  deployment_log "$(view_field deploymentId)" > "$log_file"
  grep -qF -- "auto-detected $1" "$log_file" \
    || fail "The deploy log does not say this was built as $1. It says: $(grep -F 'auto-detected' "$log_file" | head -n 1 | cut -c1-200 || true)"
  ok "The pipeline built it as $1."
}

service_id_of_launch() { jq -r 'first(.checks[]|select(.key|startswith("service:"))|.key) // ""' "$VIEW" | sed 's/^service://'; }

# The one service a launch created was really asked: its check passed, and says it logged in. Until
# round nine MongoDB, RabbitMQ and NATS could only say "not tested" here, which all_checks_passed
# would already have refused.
one_service_answered() {
  must_eq "one service check, passed" "$(service_checks)" Passed
  jq -r '[.checks[]|select(.key|startswith("service:"))|.detail]|join(" ")' "$VIEW" | holds "logged in with" \
    || fail "The service check passed without saying it logged in: $(jq -c '[.checks[]|select(.key|startswith("service:"))|{title,detail}]' "$VIEW")"
}

# remember_engine <name> <the variable holding its user> <and its password>: for step 28, which needs
# to get inside the engine after the app that used it has gone.
remember_engine() {
  local name="$1" user_key="$2" password_key="$3" slug id
  slug="$(slug_of_app "$(view_field appId)")"
  id="$(service_id_of_launch)"
  [ -n "$id" ] || fail "The launch's checks name no service."
  api GET "/api/v1/apps/$slug/env" > "$WORK/out/$name.env.json" || fail "Could not read the app's variables."
  jq -r --arg k "$user_key" 'first(.[]|select(.key==$k)|.value) // ""' "$WORK/out/$name.env.json" > "$WORK/state/$name.user"
  jq -r --arg k "$password_key" 'first(.[]|select(.key==$k)|.value) // ""' "$WORK/out/$name.env.json" > "$WORK/state/$name.password"
  remember_secret "$(cat "$WORK/state/$name.password")"
  psql_panel "SELECT \"ContainerName\" FROM \"ManagedServices\" WHERE \"Id\"='$id'" > "$WORK/state/$name.container"
  [ -s "$WORK/state/$name.password" ] && [ -s "$WORK/state/$name.container" ] \
    || fail "Could not read back the login or the container of the $name this launch created."
  printf '%s' "$id" > "$WORK/state/$name.service"
}

running_image() {
  local slug image
  slug="$(slug_of_app "$(view_field appId)")"
  image="$(image_of_app "$slug")"
  [ -n "$image" ] || fail "No running container carries the label harbora.app=$slug."
  printf '%s' "$image"
}

case_dotnet() {
  local dir url body host runtime
  dir="$(fixture dotnet-postgres)"
  run_launch dotnet-postgres "$dir"
  launched_clean
  built_as ".NET"
  one_service_answered

  url="$(view_field publicUrl)"
  body="$WORK/out/dotnet-postgres.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  cat "$body"; echo
  jq -e '.viaConnectionString.ok == true and .viaConnectionString.one == 1' "$body" >/dev/null \
    || fail "The .NET app could not query Postgres through ConnectionStrings:DefaultConnection: $(jq -c .viaConnectionString "$body" 2>/dev/null || head -c 400 "$body")"

  # Its appsettings.json says localhost, as every template's does. What the app read is what won.
  host="$(jq -r '.viaConnectionString.host // ""' "$body")"
  case "$host" in
    ""|localhost|127.0.0.1) fail "The connection string the app read points at '$host': its appsettings.json won over the attachment." ;;
    *) ok "The connection string the app read names a real host, not the localhost in its appsettings.json." ;;
  esac

  runtime="$(jq -r '.runtime // ""' "$body")"
  case "$runtime" in
    10.*) ok "It runs on .NET $runtime, which is what its project targets." ;;
    *) fail "The app runs on .NET '$runtime', and its project targets net10.0." ;;
  esac

  retire_app "$(view_field appId)"
  fact "dotnet-postgres: a solution whose web project sits in src/ and whose driver is in a project it references was built as .NET, runs on .NET $runtime as its project asks, and ran SELECT 1 through ConnectionStrings:DefaultConnection, which the attachment set over the localhost in its appsettings.json."
}

case_go() {
  local dir url body go_line built
  dir="$(fixture go-nats)"

  # go.sum is the one file of a Go project nobody can write by hand, and this was written on a machine
  # with no Go on it. So the fixture is finished here, with the command its owner would have run, the
  # way step 20 lets composer make its Laravel. Everything in it is pinned by go.mod, so what tidy
  # writes is the same every time.
  go_line="$(sed -n 's/^go \([0-9][0-9]*\.[0-9][0-9]*\).*/\1/p' "$dir/go.mod" | head -n 1)"
  [ -n "$go_line" ] || fail "The fixture's go.mod has no go line."
  docker run --rm -v "$dir:/src" -w /src "golang:${go_line}-alpine" go mod tidy > "$WORK/out/go-tidy.txt" 2>&1 \
    || fail "go mod tidy failed on the fixture: $(tail -n 8 "$WORK/out/go-tidy.txt" | cut -c1-300)"
  [ -s "$dir/go.sum" ] || fail "go mod tidy wrote no go.sum."

  run_launch go-nats "$dir"
  launched_clean
  built_as "Go"
  one_service_answered

  url="$(view_field publicUrl)"
  body="$WORK/out/go-nats.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  cat "$body"; echo
  jq -e '.ok == true and .echo == "hello"' "$body" >/dev/null \
    || fail "The Go app could not publish to NATS through NATS_URL and read the message back: $(jq -c . "$body" 2>/dev/null || head -c 400 "$body")"
  must_eq "whether the broker keeps messages (JetStream)" "$(jq -r .jetStream "$body")" true

  # The image used to be Go 1.22 whatever the module said, and a module asking for more did not build.
  built="$(jq -r '.go // ""' "$body")"
  case "$built" in
    "go${go_line}"|"go${go_line}."*) ok "It was built with $built, the Go its go.mod asks for." ;;
    *) fail "The app was built with '$built', and its go.mod asks for go $go_line." ;;
  esac

  remember_engine nats NATS_USER NATS_PASSWORD
  retire_app "$(view_field appId)"
  fact "go-nats: a Go module asking for go $go_line was built as Go with $built, and through NATS_URL it published a message and read it back from a NATS $(jq -r .server "$body") with JetStream on; the launch's check logged in to NATS over its own protocol."
}

case_nextjs() {
  local dir url note public root first second script found=0 image
  dir="$(fixture nextjs-ssr)"
  note="proof-note-$RUN_ID"
  public="https://api.proof-$RUN_ID.example"
  run_launch nextjs-ssr "$dir" --set "SERVER_NOTE=$note" --set "NEXT_PUBLIC_API=$public"
  launched_clean
  built_as "Node.js"

  # SERVER_NOTE does not exist while the image is built. A page that holds it was rendered by the
  # running server, and one whose clock moves between two requests was rendered for each of them.
  url="$(view_field publicUrl)"
  root="$WORK/out/nextjs-root.html"
  must_eq "GET /" "$(http_get "$url/" "$root")" 200
  grep -qF -- "<p id=\"server-note\">$note</p>" "$root" \
    || fail "The page does not hold SERVER_NOTE, which only the running server has. It has: $(grep -oE '<p id="server-note">[^<]*' "$root" | head -n 1 || true) / $(head -c 300 "$root" | tr '\n' ' ')"
  first="$(grep -oE 'id="rendered-at">[0-9]+' "$root" | grep -oE '[0-9]+$' | head -n 1 || true)"
  sleep 2
  must_eq "GET / again" "$(http_get "$url/" "$WORK/out/nextjs-root-2.html")" 200
  second="$(grep -oE 'id="rendered-at">[0-9]+' "$WORK/out/nextjs-root-2.html" | grep -oE '[0-9]+$' | head -n 1 || true)"
  [ -n "$first" ] && [ -n "$second" ] || fail "The page carries no render time: $(head -c 300 "$root" | tr '\n' ' ')"
  [ "$second" -gt "$first" ] \
    || fail "Two requests two seconds apart were rendered at the same moment ($first and $second): this page was built once, not rendered for each request."
  ok "Rendered on the server for each request: $first, then $second."

  # NEXT_PUBLIC_API belongs to the build: it is written into the JavaScript a browser downloads.
  grep -oE '(/_next/)?static/chunks/[^"\\ ]+\.js' "$root" | sed 's#^/_next/##' | sort -u > "$WORK/out/nextjs-scripts.txt" || true
  [ -s "$WORK/out/nextjs-scripts.txt" ] || fail "The page names no script of its own: $(head -c 400 "$root" | tr '\n' ' ')"
  while IFS= read -r script; do
    if [ "$(http_get "$url/_next/$script" "$WORK/out/nextjs-chunk.js")" = "200" ] && holds "$public" < "$WORK/out/nextjs-chunk.js"; then
      found=1
      break
    fi
  done < "$WORK/out/nextjs-scripts.txt"
  [ "$found" -eq 1 ] \
    || fail "None of the $(wc -l < "$WORK/out/nextjs-scripts.txt") scripts the page loads holds NEXT_PUBLIC_API's value, so the build never saw it."
  ok "The browser's bundle holds NEXT_PUBLIC_API's value: $script"

  must_eq "GET /api/health" "$(http_get "$url/api/health" "$WORK/out/nextjs-health.json")" 200
  must_eq "the route handler's answer" "$(jq -r .ok "$WORK/out/nextjs-health.json")" true

  image="$(running_image)"
  if docker inspect "$image" --format '{{json .Config.Env}}' | holds "NEXT_PUBLIC_API"; then
    fail "NEXT_PUBLIC_API is in the image's ENV: it should only ever be an ARG."
  fi
  image_contains "$image" "$public" || fail "The search of the image cannot find even the value the build wrote into it, so it proves nothing."
  if image_contains "$image" "$note"; then fail "SERVER_NOTE is in the image or its history: it is not a build variable, and nothing of it belongs there."; fi

  retire_app "$(view_field appId)"
  fact "nextjs-ssr: a real Next.js $(jq -r '.dependencies.next' "$dir/package.json") app was built as Node.js and runs next start on Node $(jq -r .node "$WORK/out/nextjs-health.json"). Its page holds a variable only the running server has and a clock that moved between two requests, so it is rendered on the server for each one; a route handler answers; NEXT_PUBLIC_API is in the bundle a browser downloads; and the server-only variable is nowhere in the image."
}

case_pnpm_mongo() {
  local dir url body image
  dir="$(fixture pnpm-mongo)"
  run_launch pnpm-mongo "$dir"
  launched_clean
  built_as "Node.js"
  one_service_answered

  # That pnpm installed it and starts it, from the image itself.
  image="$(running_image)"
  docker run --rm --entrypoint sh "$image" -c 'test -d /app/node_modules/.pnpm' \
    || fail "The image has no node_modules/.pnpm: whatever installed this project's dependencies, it was not pnpm."
  docker inspect "$image" --format '{{json .Config.Cmd}}' | holds "pnpm start" \
    || fail "The image does not start with pnpm: $(docker inspect "$image" --format '{{json .Config.Cmd}}')"

  url="$(view_field publicUrl)"
  body="$WORK/out/pnpm-mongo.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  cat "$body"; echo
  jq -e '.viaAttachName.ok == true and .viaAttachName.said == "hello"' "$body" >/dev/null \
    || fail "The app could not write to MongoDB and read it back through MONGODB_URI: $(jq -c .viaAttachName "$body" 2>/dev/null || head -c 400 "$body")"
  jq -e '.viaExampleName.ok == true and .viaExampleName.said == "hello"' "$body" >/dev/null \
    || fail "The app could not reach MongoDB through MONGO_URL, the name its own example file uses: $(jq -c .viaExampleName "$body")"
  [ -n "$(jq -r '.viaAttachName.database // ""' "$body")" ] || fail "The address names no database: $(jq -c .viaAttachName "$body")"

  remember_engine mongo MONGO_USER MONGO_PASSWORD
  retire_app "$(view_field appId)"
  fact "pnpm-mongo: a project with a pnpm-lock.yaml and no packageManager line was installed and is started by pnpm (Node $(jq -r .node "$body")); it wrote a document to MongoDB and read it back through MONGODB_URI and through MONGO_URL, the alias from its example file; the launch's check logged in to MongoDB with mongosh."
}

case_yarn_mariadb() {
  local dir url body image version scheme as_given
  dir="$(fixture yarn-mariadb)"
  run_launch yarn-mariadb "$dir"
  launched_clean
  built_as "Node.js"
  one_service_answered

  image="$(running_image)"
  docker run --rm --entrypoint sh "$image" -c 'test -f /app/node_modules/.yarn-integrity' \
    || fail "The image has no node_modules/.yarn-integrity: whatever installed this project's dependencies, it was not yarn."
  docker inspect "$image" --format '{{json .Config.Cmd}}' | holds "yarn start" \
    || fail "The image does not start with yarn: $(docker inspect "$image" --format '{{json .Config.Cmd}}')"

  url="$(view_field publicUrl)"
  body="$WORK/out/yarn-mariadb.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  cat "$body"; echo
  jq -e '.viaParts.ok == true and .viaParts.one == 1' "$body" >/dev/null \
    || fail "The app could not query MariaDB through DB_HOST, DB_PORT, DB_USER, DB_PASSWORD and DB_NAME: $(jq -c .viaParts "$body" 2>/dev/null || head -c 400 "$body")"
  version="$(jq -r '.viaParts.version // ""' "$body")"
  case "$version" in
    *MariaDB*) ok "The database that answered is MariaDB: $version" ;;
    *) fail "The database that answered calls itself '$version', which is not MariaDB." ;;
  esac
  [ -n "$(jq -r '.viaParts.database // ""' "$body")" ] \
    || fail "DB_NAME, the name this project's example file uses, reached the app empty: $(jq -c .viaParts "$body")"
  case "$(jq -r '.dbHost // ""' "$body")" in
    ""|localhost|127.0.0.1) fail "DB_HOST reached the app as '$(jq -r .dbHost "$body")'." ;;
    *) ok "DB_HOST reached the app as a real host." ;;
  esac

  # A record rather than an assertion. MariaDB's own connector for Node only takes an address that
  # starts mariadb://, and the DATABASE_URL an attachment sets says mysql://, which is what Prisma and
  # mysql2 want. Which of them to serve is the owner's to decide; this says what happens today.
  scheme="$(jq -r '.urlScheme // "no"' "$body")"
  as_given="$(jq -r 'if .viaUrlAsGiven.ok then "connected" else "refused it (" + ((.viaUrlAsGiven.error // "") | .[0:70]) + ")" end' "$body")"

  retire_app "$(view_field appId)"
  fact "yarn-mariadb: a project with a yarn.lock was installed and is started by yarn; it queried $version through DB_HOST, DB_PORT, DB_USER and DB_PASSWORD and through DB_NAME, the alias from its example file. Given DATABASE_URL as it stands, a $scheme:// address, MariaDB's own connector $as_given."
}

case_python_rabbitmq() {
  local dir url body
  dir="$(fixture python-rabbitmq)"
  run_launch python-rabbitmq "$dir"
  launched_clean
  built_as "Python"
  one_service_answered

  url="$(view_field publicUrl)"
  body="$WORK/out/python-rabbitmq.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  cat "$body"; echo
  jq -e '.viaAttachName.ok == true and .viaAttachName.echo == "hello"' "$body" >/dev/null \
    || fail "The app could not publish to RabbitMQ through RABBITMQ_URL and read the message back: $(jq -c .viaAttachName "$body" 2>/dev/null || head -c 400 "$body")"
  jq -e '.viaExampleName.ok == true and .viaExampleName.echo == "hello"' "$body" >/dev/null \
    || fail "The app could not reach RabbitMQ through CLOUDAMQP_URL, the name its own example file uses: $(jq -c .viaExampleName "$body")"

  remember_engine rabbitmq RABBITMQ_USER RABBITMQ_PASSWORD
  retire_app "$(view_field appId)"
  fact "python-rabbitmq: a Python web app published a confirmed message to RabbitMQ and read it back, through RABBITMQ_URL and through CLOUDAMQP_URL, the alias from its example file; the launch's check logged in to RabbitMQ over AMQP."
}

case_node_meilisearch() {
  local dir url body length
  dir="$(fixture node-meilisearch)"
  run_launch node-meilisearch "$dir"
  launched_clean
  built_as "Node.js"
  one_service_answered

  url="$(view_field publicUrl)"
  body="$WORK/out/node-meilisearch.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  cat "$body"; echo
  jq -e '.search.ok == true and .search.title == "hello harbora"' "$body" >/dev/null \
    || fail "The app could not index a document in Meilisearch and find it again through MEILISEARCH_HOST and MEILISEARCH_API_KEY: $(jq -c .search "$body" 2>/dev/null || head -c 400 "$body")"
  case "$(jq -r '.host // ""' "$body")" in
    http://*) ok "MEILISEARCH_HOST reached the app as a whole address." ;;
    *) fail "MEILISEARCH_HOST reached the app as '$(jq -r .host "$body")', and the engine's client wants a whole address." ;;
  esac
  length="$(jq -r '.keyLength // 0' "$body")"
  [ "$length" -ge 16 ] || fail "MEILISEARCH_API_KEY reached the app at a length of $length."

  retire_app "$(view_field appId)"
  fact "node-meilisearch: an ES-module Node app indexed a document in Meilisearch with the engine's own client and found it again, through MEILISEARCH_HOST and MEILISEARCH_API_KEY, the aliases from its example file; the launch's check logged in with the master key."
}

# Presses Test connection on a database's page, the way a person does, and keeps the page that follows.
test_connection() {
  local id="$1" page="$2" token status
  token="$(antiforgery_token "/databases/$id")"
  status="$(web_post "/databases/$id/test" "$token" "__RequestVerificationToken=$token")"
  case "$status" in
    302|303) ;;
    *) fail "Pressing Test connection on $id answered HTTP $status, not a redirect." ;;
  esac
  web_get "/databases/$id" > "$page" || fail "The database's page did not come back after the test."
}
says_connected() { holds "Credentials accepted and the database answered" < "$1"; }
flash_of() {
  tr '\n' ' ' < "$1" \
    | grep -oE '(Connected\. Credentials|The database refused|Nothing is listening|The database.{1,8}s name does not resolve|The connection failed|Harbora has no way)[^<]{0,200}' \
    | head -n 1 || true
}

# A check that can only pass is worth less than none, and three of these are new. Each engine is first
# asked as it stands, then made wrong from the inside while Harbora's own record stays as it was, and
# the answer has to change to the right sentence and then change back.
case_checks_can_fail() {
  local page="$WORK/out/test-connection.html" wrong="wrong-$RUN_ID" id container user password
  local image stopped_says extra
  local -a networks=()

  for engine in mongo rabbitmq nats; do
    [ -s "$WORK/state/$engine.service" ] || fail "The step that launched on $engine did not finish, so there is nothing of it to ask."
  done

  # MongoDB: its root password changed behind Harbora's back.
  id="$(cat "$WORK/state/mongo.service")"; container="$(cat "$WORK/state/mongo.container")"
  user="$(cat "$WORK/state/mongo.user")"; password="$(cat "$WORK/state/mongo.password")"
  test_connection "$id" "$page"
  says_connected "$page" || fail "MongoDB, untouched, does not test as connected: $(flash_of "$page")"
  docker exec "$container" mongosh --quiet --norc -u "$user" -p "$password" --authenticationDatabase admin \
    --eval "db.getSiblingDB('admin').changeUserPassword('$user', '$wrong')" > "$WORK/out/mongo-password.txt" 2>&1 \
    || fail "Could not change MongoDB's password from inside it: $(tail -n 3 "$WORK/out/mongo-password.txt" | cut -c1-300)"
  test_connection "$id" "$page"
  if says_connected "$page"; then fail "MongoDB's password was changed behind Harbora's back, and its connection test still passes."; fi
  holds "refused the password" < "$page" \
    || fail "With its password changed, MongoDB's test fails, but not with the sentence about the password: $(flash_of "$page")"
  docker exec "$container" mongosh --quiet --norc -u "$user" -p "$wrong" --authenticationDatabase admin \
    --eval "db.getSiblingDB('admin').changeUserPassword('$user', '$password')" > "$WORK/out/mongo-password.txt" 2>&1 \
    || fail "Could not put MongoDB's password back: $(tail -n 3 "$WORK/out/mongo-password.txt" | cut -c1-300)"
  test_connection "$id" "$page"
  says_connected "$page" || fail "With its password put back, MongoDB does not test as connected: $(flash_of "$page")"
  ok "MongoDB: connected, refused with a changed password, connected again."

  # RabbitMQ: the same, with its own tool.
  id="$(cat "$WORK/state/rabbitmq.service")"; container="$(cat "$WORK/state/rabbitmq.container")"
  user="$(cat "$WORK/state/rabbitmq.user")"; password="$(cat "$WORK/state/rabbitmq.password")"
  test_connection "$id" "$page"
  says_connected "$page" || fail "RabbitMQ, untouched, does not test as connected: $(flash_of "$page")"
  docker exec "$container" rabbitmqctl change_password "$user" "$wrong" > "$WORK/out/rabbitmq-password.txt" 2>&1 \
    || fail "Could not change RabbitMQ's password from inside it: $(tail -n 3 "$WORK/out/rabbitmq-password.txt" | cut -c1-300)"
  test_connection "$id" "$page"
  if says_connected "$page"; then fail "RabbitMQ's password was changed behind Harbora's back, and its connection test still passes."; fi
  holds "refused the password" < "$page" \
    || fail "With its password changed, RabbitMQ's test fails, but not with the sentence about the password: $(flash_of "$page")"
  docker exec "$container" rabbitmqctl change_password "$user" "$password" > "$WORK/out/rabbitmq-password.txt" 2>&1 \
    || fail "Could not put RabbitMQ's password back: $(tail -n 3 "$WORK/out/rabbitmq-password.txt" | cut -c1-300)"
  test_connection "$id" "$page"
  says_connected "$page" || fail "With its password put back, RabbitMQ does not test as connected: $(flash_of "$page")"
  ok "RabbitMQ: connected, refused with a changed password, connected again."

  # NATS takes its password on its command line, so there is no changing it from inside. It is stopped,
  # which must not pass, and then a server that wants a different password stands where it was, under
  # its name and on its networks, which must not pass either.
  id="$(cat "$WORK/state/nats.service")"; container="$(cat "$WORK/state/nats.container")"
  user="$(cat "$WORK/state/nats.user")"
  test_connection "$id" "$page"
  says_connected "$page" || fail "NATS, untouched, does not test as connected: $(flash_of "$page")"

  image="$(docker inspect -f '{{.Config.Image}}' "$container")"
  mapfile -t networks < <(docker inspect "$container" | jq -r '.[0].NetworkSettings.Networks | keys[]')
  [ "${#networks[@]}" -ge 1 ] || fail "The NATS container is on no network."

  docker stop "$container" >/dev/null
  test_connection "$id" "$page"
  if says_connected "$page"; then fail "NATS is stopped, and its connection test still passes."; fi
  stopped_says="$(flash_of "$page")"

  docker rename "$container" "$container-real"
  docker run -d --name "$container" --network "${networks[0]}" "$image" --user "$user" --pass "$wrong" >/dev/null \
    || fail "Could not start a stand-in NATS with another password."
  for extra in "${networks[@]:1}"; do docker network connect "$extra" "$container"; done
  sleep 3
  test_connection "$id" "$page"
  if says_connected "$page"; then
    fail "A NATS that wants a different password stands where the real one was, and the connection test still passes: it does not log in."
  fi
  holds "refused the password" < "$page" \
    || fail "Against a NATS with another password the test fails, but not with the sentence about the password: $(flash_of "$page")"

  docker rm -f "$container" >/dev/null
  docker rename "$container-real" "$container"
  docker start "$container" >/dev/null
  nats_is_back() { test_connection "$id" "$page" && says_connected "$page"; }
  wait_until 90 "NATS, put back, tests as connected again" nats_is_back
  ok "NATS: connected, not connected while stopped, refused by a server with another password, connected again."

  fact "The three new connection checks can fail. Test connection on each database's page said connected; then MongoDB and RabbitMQ, their passwords changed from inside, and a NATS with another password standing in for the real one, each got the sentence about a refused password; a stopped NATS got '$(printf '%s' "$stopped_says" | cut -c1-60)'; and each said connected again once put back."
}

# ---------------------------------------------------------------------------------------------
# The other ways in, and what happens when something goes wrong half-way (29 to 33)
# ---------------------------------------------------------------------------------------------

failed_step() { jq -r 'first(.steps[]|select(.state=="Failed")|.key) // ""' "$VIEW"; }

# Waits while a launch reads its source. Leaves VIEW at whatever came next.
wait_while_reading() {
  local id="$1" deadline
  deadline=$(( $(date +%s) + 300 ))
  while :; do
    api GET "/api/v1/launch/$id" > "$VIEW" || fail "GET /api/v1/launch/$id failed."
    [ "$(jq -r .status "$VIEW")" = "Inspecting" ] || return 0
    [ "$(date +%s)" -lt "$deadline" ] || fail "The launch was still reading its source after five minutes."
    sleep 2
  done
}

# Waits for a launch to end by itself, with nobody following it. Leaves VIEW at how it ended. A panel
# that does not answer for a moment is not an ending, which is the whole point of step 33.
wait_for_launch_end() {
  local id="$1" budget="${2:-$W_LAUNCH}" deadline
  deadline=$(( $(date +%s) + budget ))
  while :; do
    if api GET "/api/v1/launch/$id" > "$VIEW.next" 2>/dev/null; then
      mv "$VIEW.next" "$VIEW"
      [ "$(jq -r .isTerminal "$VIEW")" != "true" ] || return 0
    fi
    [ "$(date +%s)" -lt "$deadline" ] \
      || fail "Launch $id had not ended after ${budget}s. It is $(jq -r .status "$VIEW" 2>/dev/null || echo unknown), with steps $(jq -r '[.steps[]|"\(.key)=\(.state)"]|join(" ")' "$VIEW" 2>/dev/null || true)."
    sleep 5
  done
}

# What a page's red box says, for a failure nobody can otherwise read.
page_alert() {
  tr '\n' ' ' < "$1" | grep -oE '(alert-danger|data-launch-refusals|role="alert")[^>]*>.{0,500}' | head -n 1 \
    | sed -e 's/<[^>]*>/ /g' | tr -s ' ' | cut -c1-400 || true
}

# The fields a form would post from a browser with no JavaScript: every named input that is not a
# button, each ticked box, each chosen option and each textarea inside the form whose action ends with
# $2, one name=value per line and both of them encoded. Anything after that replaces a field's value,
# as typing into it would.
form_fields() {
  python3 - "$@" <<'PY'
import html.parser
import sys
import urllib.parse

page, action = sys.argv[1], sys.argv[2]
typed = dict(argument.split('=', 1) for argument in sys.argv[3:])


class Form(html.parser.HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.inside = False
        self.fields = []
        self.select = None
        self.textarea = None

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == 'form':
            self.inside = (a.get('action') or '').endswith(action)
            return
        if not self.inside or 'disabled' in a:
            return
        if tag == 'input':
            name, kind = a.get('name'), (a.get('type') or 'text').lower()
            if not name or kind in ('submit', 'button', 'file', 'image', 'reset'):
                return
            if kind in ('checkbox', 'radio'):
                if 'checked' in a:
                    self.fields.append((name, 'on' if a.get('value') is None else a['value']))
            else:
                self.fields.append((name, a.get('value') or ''))
        elif tag == 'select':
            self.select = {'name': a.get('name'), 'first': None, 'chosen': None}
        elif tag == 'option' and self.select is not None:
            value = a.get('value') or ''
            if self.select['first'] is None:
                self.select['first'] = value
            if 'selected' in a:
                self.select['chosen'] = value
        elif tag == 'textarea':
            self.textarea = [a.get('name'), '']

    def handle_endtag(self, tag):
        if tag == 'form':
            self.inside = False
        elif tag == 'select' and self.select is not None:
            if self.select['name']:
                chosen = self.select['first'] if self.select['chosen'] is None else self.select['chosen']
                self.fields.append((self.select['name'], chosen or ''))
            self.select = None
        elif tag == 'textarea' and self.textarea is not None:
            if self.textarea[0]:
                self.fields.append((self.textarea[0], self.textarea[1]))
            self.textarea = None

    def handle_data(self, data):
        if self.textarea is not None:
            self.textarea[1] += data


form = Form()
with open(page, encoding='utf-8') as handle:
    form.feed(handle.read())

if not form.fields:
    sys.exit('no form whose action ends with ' + action)
present = {name for name, _ in form.fields}
fields = [(name, typed.get(name, value)) for name, value in form.fields]
fields += [(name, value) for name, value in typed.items() if name not in present]
for name, value in fields:
    print(urllib.parse.quote(name, safe='') + '=' + urllib.parse.quote(value, safe=''))
PY
}

case_zip_wrapper() {
  local zip_dir zip token code location launch page form url body length
  VIEW="$WORK/out/zip-wrapper.view.json"

  # What GitHub's "Download ZIP" gives, and what compressing a folder gives: one folder with everything
  # inside it. The upload form is the only way in that takes a zip, so this goes through the panel's
  # own pages from end to end, with the session a person has and no JavaScript.
  zip_dir="$WORK/zip"
  rm -rf "$zip_dir"
  mkdir -p "$zip_dir"
  cp -a "$COMPOSE_DIR/proof-fixtures/node-postgres" "$zip_dir/my-shop-main"
  ( cd "$zip_dir" && zip -qr my-shop-main.zip my-shop-main ) || fail "Could not make the zip."
  zip="$zip_dir/my-shop-main.zip"

  token="$(antiforgery_token /launch)"
  code="$(curl -sS --max-time 180 -o "$WORK/last.body" -D "$WORK/last.headers" -w '%{http_code}' -b "$COOKIES" -c "$COOKIES" \
    -F "__RequestVerificationToken=$token" -F "File=@$zip;type=application/zip" "$PANEL_URL/launch")"
  case "$code" in
    302|303) ;;
    *) fail "Uploading the zip on /launch answered HTTP $code, not a redirect. The page says: $(page_alert "$WORK/last.body")" ;;
  esac
  location="$(grep -i '^location:' "$WORK/last.headers" | tail -n 1 | tr -d '\r' | awk '{print $2}')"
  launch="$(printf '%s' "$location" | grep -oE '[0-9a-f]{8}-[0-9a-f-]{27}' | head -n 1 || true)"
  [ -n "$launch" ] || fail "The upload redirected to '$location', which names no launch."
  printf '%s' "$launch" > "$WORK/current-launch"

  wait_while_reading "$launch"
  must_eq "the launch once its source has been read" "$(view_field status)" AwaitingConfirmation
  must_eq "the launch's source" "$(view_field source)" Upload

  # The wrapper is a fact about this one file. Kept as the app's root it would break the next deploy.
  api GET "/api/v1/launch/$launch/proposal" > "$WORK/out/zip-wrapper.proposal.json" || fail "GET /api/v1/launch/$launch/proposal failed."
  must_eq "what was found inside the wrapper" "$(jq -r .inspection.stack "$WORK/out/zip-wrapper.proposal.json")" Node
  must_eq "the folder the app would be built from" "$(jq -r '.defaults.rootDirectory // ""' "$WORK/out/zip-wrapper.proposal.json")" ""
  must_eq "folders offered to choose between" "$(jq -r '.inspection.rootCandidates | length' "$WORK/out/zip-wrapper.proposal.json")" 0

  page="$WORK/out/zip-wrapper.confirm.html"
  code="$(curl -sS --max-time 60 -o "$page" -w '%{http_code}' -b "$COOKIES" -c "$COOKIES" "$PANEL_URL/launch/$launch")"
  must_eq "GET the confirm screen" "$code" 200
  grep -qF 'data-launch-confirm' "$page" || fail "GET /launch/$launch is not the confirm screen: $(head -c 300 "$page" | tr '\n' ' ')"

  form="$WORK/out/zip-wrapper.form"
  form_fields "$page" "/launch/$launch/confirm" "Values[API_KEY]=zip-answer" > "$form" 2> "$WORK/out/zip-wrapper.form.err" \
    || fail "Could not read the confirm form's fields: $(cat "$WORK/out/zip-wrapper.form.err")"
  log "The form posts: $(cut -d= -f1 "$form" | sort -u | tr '\n' ' ')"
  grep -q '^__RequestVerificationToken=.' "$form" || fail "The confirm form carries no antiforgery token."
  grep -q '^Services=PostgreSql$' "$form" || fail "The confirm screen does not come with PostgreSQL ticked. It posts: $(cut -d= -f1 "$form" | sort -u | tr '\n' ' ')"
  grep -q '^InstanceSizeKey=.' "$form" || fail "The confirm screen comes with no size chosen. It posts: $(cut -d= -f1 "$form" | sort -u | tr '\n' ' ')"

  code="$(curl -sS --max-time 180 -o "$WORK/last.body" -D "$WORK/last.headers" -w '%{http_code}' -b "$COOKIES" -c "$COOKIES" \
    -H 'Content-Type: application/x-www-form-urlencoded' --data-binary "$(paste -sd'&' "$form")" "$PANEL_URL/launch/$launch/confirm")"
  case "$code" in
    302|303) ;;
    *) fail "Posting the confirm form answered HTTP $code, not a redirect. The form says: $(page_alert "$WORK/last.body")" ;;
  esac

  wait_for_launch_end "$launch"
  log "Launch $launch: $(view_field status), checks: $(jq -r '[.checks[]|"\(.key)=\(.status)"]|join(" ")' "$VIEW")"
  must_eq "the launch's status" "$(view_field status)" Succeeded
  all_steps_done
  all_checks_passed running restarts url logs
  must_eq "one database check, passed" "$(service_checks)" Passed
  view_field appName | holds "my-shop" || fail "The app is not named after the file it came in: $(view_field appName)"

  url="$(view_field publicUrl)"
  body="$WORK/out/zip-wrapper.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  cat "$body"; echo
  jq -e '.viaUrl.ok == true and .viaHost.ok == true' "$body" >/dev/null \
    || fail "The app from the zip cannot reach its database: $(jq -c '{viaUrl,viaHost}' "$body" 2>/dev/null || head -c 400 "$body")"
  must_eq "the answer typed into the form" "$(jq -r .apiKey "$body")" zip-answer
  must_eq "the example file's value" "$(jq -r .logLevel "$body")" info
  length="$(jq -r '.jwtSecretLength // 0' "$body")"
  [ "$length" -ge 32 ] || fail "JWT_SECRET reached the app at a length of $length: that is not a generated secret."

  code="$(curl -sS --max-time 60 -o "$WORK/out/zip-wrapper.done.html" -w '%{http_code}' -b "$COOKIES" "$PANEL_URL/launch/$launch")"
  must_eq "GET the launch's page once it has ended" "$code" 200
  grep -qF 'data-status="Succeeded"' "$WORK/out/zip-wrapper.done.html" || fail "The launch's page does not show it as succeeded."

  # Kept running: step 33 asks it whether it noticed the panel going away.
  printf '%s' "$url" > "$WORK/state/zip.url"
  fact "A zip with everything inside one wrapper folder, uploaded on /launch and confirmed on the panel's own form with no JavaScript: the wrapper was taken off (no root folder, nothing to choose between), PostgreSQL came ticked, and the app answers with both database queries working, the answer typed into the form, the example file's value and a generated secret of $length characters."
}

git_server_up() { curl -fsS --max-time 5 -o /dev/null "$GIT_URL/api/healthz"; }
git_api() { curl -fsS --max-time 60 -u "proof:$GIT_PASSWORD" -H 'Content-Type: application/json' "$@"; }

# A Git server of this host's own, because a launch reads a repository over http or https and nothing
# else, and a runner has no business with anybody's real one. Gitea, which is what people who host
# their own Git mostly run.
start_git_server() {
  [ ! -s "$WORK/state/git.ready" ] || return 0
  docker run -d --name harbora-proof-git -p "${LP_GIT_PORT}:3000" \
    -e GITEA__security__INSTALL_LOCK=true \
    -e GITEA__database__DB_TYPE=sqlite3 \
    -e GITEA__service__DISABLE_REGISTRATION=true \
    -e "GITEA__server__ROOT_URL=$GIT_URL/" \
    "$LP_GITEA_IMAGE" >/dev/null || fail "The Git server's container did not start."
  wait_until 180 "the Git server answers" git_server_up
  docker exec -u git harbora-proof-git gitea admin user create --username proof --password "$GIT_PASSWORD" \
    --email proof@harbora-proof.test --admin --must-change-password=false > "$WORK/out/git-user.txt" 2>&1 \
    || fail "Could not create a user on the Git server: $(tail -n 3 "$WORK/out/git-user.txt" | cut -c1-300)"
  printf 'ready' > "$WORK/state/git.ready"
}

# publish_repository <name> <fixture> <true if private>: makes it on the server and a working tree of
# it under $WORK/git, with nothing committed yet.
publish_repository() {
  local name="$1" from="$COMPOSE_DIR/proof-fixtures/$2" private="$3" tree="$WORK/git/$1"
  [ -d "$from" ] || fail "The ref under test has no fixture called $2."
  git_api -X POST -d "$(jq -nc --arg n "$name" --argjson p "$private" '{name:$n, private:$p, default_branch:"main"}')" \
    "$GIT_URL/api/v1/user/repos" > "$WORK/out/git-create-$name.json" \
    || fail "The Git server would not create the repository $name."
  rm -rf "$tree"
  mkdir -p "$WORK/git"
  cp -a "$from" "$tree"
  git -C "$tree" init -q -b main || fail "git init failed."
}

git_commit() {
  git -C "$1" add -A \
    && git -C "$1" -c user.name='Proof' -c user.email='proof@harbora-proof.test' commit -q -m "$2" \
    || fail "git commit failed in $1."
}

# git_push <repository> <branch…>
git_push() {
  local name="$1"
  shift
  git -C "$WORK/git/$name" push -q "http://proof:${GIT_PASSWORD}@${SERVER_IP}:${LP_GIT_PORT}/proof/${name}.git" "$@" > "$WORK/out/git-push.txt" 2>&1 \
    || fail "Pushing $name to the Git server failed: $(tail -n 4 "$WORK/out/git-push.txt" | cut -c1-300)"
}

# A launch from a repository is run from a folder with nothing in it: there is no folder to upload,
# and nothing must be written into the one it happens to be run from.
nowhere() {
  mkdir -p "$WORK/nowhere"
  printf '%s' "$WORK/nowhere"
}

case_git_public() {
  local tree url body
  start_git_server
  publish_repository shop git-node false
  tree="$WORK/git/shop"

  # Two branches that say which they are, so that the answer names the one that was cloned.
  printf 'main-%s\n' "$RUN_ID" > "$tree/marker.txt"
  git_commit "$tree" "The shop"
  git -C "$tree" checkout -q -b release
  printf 'release-%s\n' "$RUN_ID" > "$tree/marker.txt"
  git_commit "$tree" "What is released"
  git_push shop main release

  run_launch git-public "$(nowhere)" --git "$GIT_URL/proof/shop.git" --ref release
  launched_clean
  built_as "Node.js"
  must_eq "the launch's source" "$(view_field source)" Git
  view_field sourceLabel | holds "proof/shop.git" || fail "The launch does not name its repository: $(view_field sourceLabel)"
  view_field appName | holds "shop" || fail "The app is not named after its repository: $(view_field appName)"

  url="$(view_field publicUrl)"
  body="$WORK/out/git-public.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  must_eq "the branch that was built" "$(jq -r .marker "$body")" "release-$RUN_ID"
  [ ! -e "$(nowhere)/harbora.yml" ] || fail "A launch from a repository wrote a harbora.yml into the folder it was run from."

  retire_app "$(view_field appId)"
  fact "harbora launch --git --ref release, from a repository on a Git server this host runs (Gitea, over plain http): cloned, built as Node.js and served, and the app says it was built from the release branch and not the default one."
}

case_git_private() {
  local tree token url body apps_before code
  [ -s "$WORK/state/git.ready" ] || fail "Step 30 did not start the Git server, so there is no repository to read."
  publish_repository vault git-node true
  tree="$WORK/git/vault"
  printf 'private-%s\n' "$RUN_ID" > "$tree/marker.txt"
  git_commit "$tree" "Not for everybody"
  git_push vault main

  token="$(git_api -X POST -d '{"name":"harbora-launch","scopes":["read:repository"]}' "$GIT_URL/api/v1/users/proof/tokens" | jq -er .sha1)" \
    || fail "The Git server gave no access token."
  remember_secret "$token"

  # That it really is private, or the rest of this says nothing.
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$GIT_URL/proof/vault.git/info/refs?service=git-upload-pack" || echo 000)"
  case "$code" in
    401|403|404) ok "Nobody can read the repository without signing in (HTTP $code)." ;;
    *) fail "The repository that should be private answered an anonymous read with HTTP $code." ;;
  esac

  # With no token: refused in words, and nothing made.
  apps_before="$(api GET /api/v1/apps | jq 'length')"
  run_launch git-private-no-token "$(nowhere)" --git "$GIT_URL/proof/vault.git"
  must_eq "harbora launch's exit code with no token" "$LAUNCH_EXIT" 1
  must_eq "the launch's status" "$(view_field status)" InspectFailed
  say_must "may be private"
  say_must "Nothing was created"
  must_eq "apps created by that" "$(api GET /api/v1/apps | jq 'length')" "$apps_before"
  : > "$WORK/current-launch"

  run_launch git-private "$(nowhere)" --git "$GIT_URL/proof/vault.git" --git-token "$token"
  launched_clean
  url="$(view_field publicUrl)"
  body="$WORK/out/git-private.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  must_eq "what was built" "$(jq -r .marker "$body")" "private-$RUN_ID"

  # The token is a password. It is in the one place it has to be and nowhere a person or a log can read it.
  if holds "$token" < "$WORK/out/git-private.txt"; then fail "harbora launch printed the Git token."; fi
  if holds "$token" < "$VIEW"; then fail "GET /api/v1/launch/{id} carries the Git token."; fi
  code="$(curl -sS --max-time 60 -o "$WORK/out/git-private.page.html" -w '%{http_code}' -b "$COOKIES" "$PANEL_URL/launch/$LAUNCH_ID")"
  must_eq "GET the launch's page" "$code" 200
  if holds "$token" < "$WORK/out/git-private.page.html"; then fail "The launch's page carries the Git token."; fi
  if docker logs harbora-panel 2>&1 | holds "$token"; then fail "The panel's log holds the Git token."; fi
  if psql_panel "SELECT row_to_json(l)::text FROM \"AppLaunches\" l WHERE l.\"Id\"='$LAUNCH_ID'" | holds "$token"; then
    fail "The launch's row holds the Git token as it was typed."
  fi

  retire_app "$(view_field appId)"
  fact "A private repository: with no token harbora launch ends at exit 1 saying it may be private and that nothing was created, and nothing was; with --git-token it is cloned, built and served. The token is not in the command's output, the API's answer, the launch's page, its row or the panel's log."
}

case_retry() {
  local tree launch app_id value url body token status
  [ -s "$WORK/state/git.ready" ] || fail "Step 30 did not start the Git server, so there is no repository to launch from."
  publish_repository builder git-build false
  tree="$WORK/git/builder"
  git_commit "$tree" "A build that needs a variable"
  git_push builder main
  value="https://api.proof-$RUN_ID.example"

  # The commonest first failure there is: the build wants a variable nobody has set yet.
  run_launch retry "$(nowhere)" --git "$GIT_URL/proof/builder.git"
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 1
  must_eq "the launch's status" "$(view_field status)" Failed
  must_eq "the step it stopped at" "$(failed_step)" deploy
  case "$(view_field failureMessage)" in
    *"NEXT_PUBLIC_API is not set"*) ok "The launch says what the build said." ;;
    *) fail "The launch's message does not say why the build failed: $(view_field failureMessage)" ;;
  esac
  must_eq "whether trying again could help" "$(jq -r .canRetry "$VIEW")" true
  say_must "Trying again could help"
  launch="$LAUNCH_ID"
  app_id="$(view_field appId)"
  [ -n "$app_id" ] || fail "A launch that failed at its deployment names no app to put right."

  # What a person then does: sets the variable on the app's page, and presses Try again on the launch's.
  set_variable_on_page "$app_id" NEXT_PUBLIC_API "$value"
  web_get "/launch/$launch" > "$WORK/out/retry.page.html" || fail "GET /launch/$launch failed."
  grep -qF 'data-launch-retry' "$WORK/out/retry.page.html" || fail "The failed launch's page offers no Try again."
  token="$(antiforgery_token "/launch/$launch")"
  status="$(web_post "/launch/$launch/retry" "$token" "__RequestVerificationToken=$token")"
  case "$status" in
    302|303) ;;
    *) fail "Pressing Try again answered HTTP $status, not a redirect." ;;
  esac

  wait_for_launch_end "$launch"
  log "Launch $launch after trying again: $(view_field status), checks: $(jq -r '[.checks[]|"\(.key)=\(.status)"]|join(" ")' "$VIEW")"
  must_eq "the launch's status after trying again" "$(view_field status)" Succeeded
  all_steps_done
  all_checks_passed running restarts url logs
  must_eq "the app it ended on" "$(view_field appId)" "$app_id"

  url="$(view_field publicUrl)"
  body="$WORK/out/retry.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  must_eq "what the second build read" "$(jq -r .builtWith "$body")" "$value"

  retire_app "$app_id"
  fact "A launch whose build failed for want of a variable ended Failed at exit 1, at the deploy step, quoting the build and saying trying again could help. With the variable set on the app's page, the Try again button on the launch's page cloned and built again, and the same launch ended Succeeded on the same app, which serves what the second build read."
}

case_restart() {
  local launch value url body deadline before after message code during pattern zip_url
  [ -s "$WORK/state/git.ready" ] || fail "Step 30 did not start the Git server, so there is no repository to launch from."
  [ -d "$WORK/git/builder" ] || fail "Step 32 did not publish the repository this launches."
  value="https://restart.proof-$RUN_ID.example"
  VIEW="$WORK/out/restart.view.json"

  # Nobody is following: what matters is what the launch does by itself when the panel goes away, which
  # is what every update does to whatever is running at the time.
  run_launch restart "$(nowhere)" --git "$GIT_URL/proof/builder.git" --no-follow \
    --set "NEXT_PUBLIC_API=$value" --set "NEXT_PUBLIC_BUILD_PAUSE_SECONDS=60"
  must_eq "harbora launch --no-follow's exit code" "$LAUNCH_EXIT" 0
  say_must "It carries on without you"
  launch="$LAUNCH_ID"

  # In the middle means in the middle: the image is being built, in the container the daemon runs that
  # step in, which this build keeps open for a minute.
  deadline=$(( $(date +%s) + 300 ))
  until docker ps --no-trunc --format '{{.Command}}' | holds "npm run build"; do
    [ "$(date +%s)" -lt "$deadline" ] \
      || fail "The build was never seen running, so a restart now would not be in the middle of anything. The launch is $(api GET "/api/v1/launch/$launch" | jq -r .status)."
    sleep 1
  done
  ok "The build is running."

  before="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  docker restart harbora-panel >/dev/null || fail "docker restart harbora-panel failed."
  wait_until "$W_PANEL" "the panel answers again after its restart" panel_is_trusted
  after="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '%s' "$before" > "$WORK/state/restart.before"
  printf '%s' "$after" > "$WORK/state/restart.after"

  # A build cannot be picked up where it stopped, so the deployment is written off and the launch has
  # to say so and offer to try again, rather than wait for ever on a build nobody is running.
  wait_for_launch_end "$launch" 600
  message="$(view_field failureMessage)"
  log "After the restart the launch ended $(view_field status): $message"
  must_eq "the launch's status after the restart" "$(view_field status)" Failed
  must_eq "the step it stopped at" "$(failed_step)" deploy
  [ -n "$message" ] || fail "A launch the restart interrupted ended Failed with nothing to say why."
  must_eq "whether trying again could help" "$(jq -r .canRetry "$VIEW")" true

  code="$(curl -sS --max-time 120 -o "$WORK/out/restart.retry.json" -w '%{http_code}' -X POST \
    -H "Authorization: Bearer $API_TOKEN" "$PANEL_URL/api/v1/launch/$launch/retry")"
  [ "$code" = "200" ] || fail "POST /api/v1/launch/$launch/retry answered HTTP $code: $(head -c 400 "$WORK/out/restart.retry.json")"

  wait_for_launch_end "$launch"
  log "Launch $launch after trying again: $(view_field status), checks: $(jq -r '[.checks[]|"\(.key)=\(.status)"]|join(" ")' "$VIEW")"
  must_eq "the launch's status after trying again" "$(view_field status)" Succeeded
  all_steps_done
  all_checks_passed running restarts url logs

  url="$(view_field publicUrl)"
  body="$WORK/out/restart.body.json"
  must_eq "GET / through Traefik" "$(http_get "$url/" "$body")" 200
  must_eq "what the build read" "$(jq -r .builtWith "$body")" "$value"

  # Whoever was signed in before is still signed in, and what was running before is still running.
  code="$(curl -sS --max-time 60 -o "$WORK/out/restart.page.html" -w '%{http_code}' -b "$COOKIES" "$PANEL_URL/launch/$launch")"
  must_eq "GET the launch's page with the session from before the restart" "$code" 200
  grep -qF 'data-status="Succeeded"' "$WORK/out/restart.page.html" || fail "The launch's page does not show it as succeeded."
  if [ -s "$WORK/state/zip.url" ]; then
    zip_url="$(cat "$WORK/state/zip.url")"
    must_eq "the app launched before the restart" "$(http_get "$zip_url/" "$WORK/out/restart.zip.json")" 200
    jq -e '.viaUrl.ok == true' "$WORK/out/restart.zip.json" >/dev/null \
      || fail "After the panel's restart an app launched before it can no longer reach its database: $(jq -c .viaUrl "$WORK/out/restart.zip.json")"
  fi

  # A record: what the panel logged while it was going down and coming back, which step 34 leaves out.
  pattern='^[0-9:]+ fail: (Harbora\.Infrastructure\.(Launch|Verification)|Microsoft\.AspNetCore\.Diagnostics|Microsoft\.EntityFrameworkCore)'
  during="$(docker logs --since "$before" --until "$after" harbora-panel 2>&1 | grep -cE "$pattern" || true)"

  retire_app "$(view_field appId)"
  fact "The panel was restarted while a launch's image was being built. The launch ended by itself, Failed at the deploy step, saying '$(printf '%s' "$message" | cut -c1-110)' and that trying again could help; tried again through the API it built from the start and ended Succeeded with every check passed. The session from before the restart still opens its page, an app launched before the restart still reaches its database, and the panel logged $during launch-path errors while going down and coming back."
}

case_panel_log() {
  local noise pattern
  # Each of these is a launch that moved on past something a person would want to know, or a query the
  # real database refused that the in-memory one in the tests would have run. A failing app's own
  # deployment logs at fail: as well, which is why this is not every fail: line.
  pattern='^[0-9:]+ fail: (Harbora\.Infrastructure\.(Launch|Verification)|Microsoft\.AspNetCore\.Diagnostics|Microsoft\.EntityFrameworkCore)'
  if [ -s "$WORK/state/restart.before" ] && [ -s "$WORK/state/restart.after" ]; then
    # Step 33 stopped the panel on purpose in the middle of its work. What it logged between being
    # told to stop and answering again is that step's subject, and is counted there.
    noise="$( { docker logs --until "$(cat "$WORK/state/restart.before")" harbora-panel 2>&1; \
                docker logs --since "$(cat "$WORK/state/restart.after")" harbora-panel 2>&1; } \
      | grep -E "$pattern" | cut -c1-220 | head -n 60 || true)"
  else
    noise="$(docker logs harbora-panel 2>&1 | grep -E "$pattern" | cut -c1-220 | head -n 60 || true)"
  fi
  [ -z "$noise" ] || fail "The panel logged errors from the launch path: $noise"
  fact "The panel logged no error from the launch tick, the checks, an unhandled request or a failed query across all of the above."
}

if in_group core; then
  step_case "5b — the object store the install started takes a bucket" case_object_store
  step_case "6 — the main case: a Node app and Postgres, nothing set up by hand" case_node_postgres
  step_case "7 — an app that crashes must not read as a success" case_crashes
  step_case "8 — an app that ignores its database must be caught by its log" case_wrong_database_host
  step_case "9 — a Python worker has no address and no HTTP probe, and reaches Redis" case_python_worker
  step_case "10 — a repository with two apps asks, then builds the one it was told" case_two_apps
  step_case "11 — harbora deploy the next day" case_deploy_again
  step_case "12 — the launch pages render over real data and carry no secret" case_pages
  step_case "13 — a Vite site is built, served and routed" case_vite
  step_case "14 — a second deploy after a build variable changes" case_vite_redeploy
  step_case "15 — a failed build says why on its last line" case_frontend_failures
  step_case "16 — a real Create React App project" case_cra
  step_case "17 — a Node build reads a public variable nobody ticked" case_node_build_var
  step_case "18 — a Python build sees a variable only once it is marked" case_python_marked
  step_case "19 — a plain PHP app reaches MySQL through DB_HOST and its friends" case_php_pdo
  step_case "20 — a real Laravel project, generated now, migrates and serves" case_laravel
fi

if in_group stacks; then
  step_case "21 — a .NET solution with its web project in a folder, and Postgres by connection string" case_dotnet
  step_case "22 — a Go module on the Go its go.mod asks for, and NATS" case_go
  step_case "23 — a real Next.js app renders on the server, for each request" case_nextjs
  step_case "24 — a pnpm project, and MongoDB" case_pnpm_mongo
  step_case "25 — a yarn project, and MariaDB" case_yarn_mariadb
  step_case "26 — a Python web app, and RabbitMQ" case_python_rabbitmq
  step_case "27 — a Node app, and Meilisearch" case_node_meilisearch
  step_case "28 — a connection check that cannot fail proves nothing" case_checks_can_fail
fi

if in_group flows; then
  step_case "29 — a zip with one wrapper folder, through the panel's own forms" case_zip_wrapper
  step_case "30 — harbora launch --git, from a repository this host serves" case_git_public
  step_case "31 — a private repository needs its token, and takes it" case_git_private
  step_case "32 — a failed launch is tried again once its cause is put right" case_retry
  step_case "33 — the panel restarts in the middle of a launch" case_restart
fi

step_case "34 — nothing in the launch path logged an error of its own" case_panel_log

# ---------------------------------------------------------------------------------------------
# The verdict
# ---------------------------------------------------------------------------------------------
echo
elapsed_minutes="$(( ($(date +%s) - PROOF_STARTED) / 60 ))"
if [ -s "$FAILED_STEPS" ]; then
  report_host_state
  annotate "the verdict" "FAILED ($LP_GROUPS) after ${elapsed_minutes} minutes, at $(git -C "$APP_DIR" rev-parse --short HEAD). Steps that failed:
$(cat "$FAILED_STEPS")

Established all the same:
$(cat "$WORK/facts")"
  err "FAILED steps:"
  sed 's/^/  /' "$FAILED_STEPS" >&2
  trap - ERR EXIT
  exit 1
fi

summary="Launch proof ($LP_GROUPS) passed at $(git -C "$APP_DIR" rev-parse HEAD), in ${elapsed_minutes} minutes.
$(cat "$WORK/facts")"
summary="$(printf '%s' "$summary" | scrub)"

# One notice holds about four thousand characters, and the facts of a whole set can be more than that.
# Cut at a line, never inside one, and say which part this is: a fact that fell off the end of the only
# notice would read as a fact that was never established.
chunks=()
chunk=""
while IFS= read -r line; do
  if [ -n "$chunk" ] && [ $(( ${#chunk} + ${#line} + 1 )) -gt 3600 ]; then
    chunks+=("$chunk")
    chunk="$line"
  else
    chunk="${chunk:+$chunk$'\n'}$line"
  fi
done <<< "$summary"
[ -z "$chunk" ] || chunks+=("$chunk")

part=0
for chunk in "${chunks[@]}"; do
  part=$((part + 1))
  title="launch proof passed"
  [ "${#chunks[@]}" -eq 1 ] || title="launch proof passed (part $part of ${#chunks[@]})"
  chunk="${chunk//'%'/%25}"; chunk="${chunk//$'\r'/%0D}"; chunk="${chunk//$'\n'/%0A}"
  printf '::notice title=%s::%s\n' "$title" "$chunk"
done
ok "Launch proof ($LP_GROUPS) passed in ${elapsed_minutes} minutes."
