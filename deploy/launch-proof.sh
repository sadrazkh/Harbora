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
#   19  nothing in the launch path logged an error of its own
#
# Run it BY HAND on any throwaway Linux host (it installs Docker if it has to):
#
#   export LP_REF=my-branch          # a branch, as install.sh's REPO_BRANCH takes
#   sudo -E bash deploy/launch-proof.sh
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

API_TOKEN=""
ANNOTATIONS_USED=0
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
  body="$(printf '%s' "$body" | scrub)"
  body="${body:0:3800}"
  body="${body//'%'/%25}"; body="${body//$'\r'/%0D}"; body="${body//$'\n'/%0A}"
  title="${title//'%'/%25}"; title="${title//':'/%3A}"; title="${title//','/%2C}"
  printf '::%s title=%s::%s\n' "$level" "$title" "$body"
}

report_failure() {
  local title="$1" fact="$2" launch deployment view
  annotate "$title" "$fact"

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
  LAUNCH_EXIT=0
  ( cd "$dir" && timeout "$W_LAUNCH" "$HARBORA" launch --yes "$@" ) > "$WORK/out/$label.txt" 2>&1 || LAUNCH_EXIT=$?
  cat "$WORK/out/$label.txt"
  tr -s ' \r\n' ' ' < "$WORK/out/$label.txt" > "$OUT"
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

# Whether a value is anywhere in an image: every layer and config `docker save` gives, unpacked as far
# as gzip goes, and its history. Every caller of this for "it is nowhere" also asks it about a value
# that must be found, because a search that cannot find anything would pass that test for ever.
image_contains() {
  local image="$1" value="$2" tmp found=1 file
  tmp="$(mktemp -d "$WORK/image.XXXXXX")"
  docker save "$image" | tar -x -C "$tmp"
  while IFS= read -r file; do
    if zcat -f -- "$file" 2>/dev/null | grep -aqF -- "$value"; then found=0; break; fi
  done < <(find "$tmp" -type f)
  if docker history --no-trunc "$image" 2>/dev/null | grep -qF -- "$value"; then found=0; fi
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
  DEPLOY_EXIT=0
  ( cd "$dir" && timeout "$W_LAUNCH" "$HARBORA" deploy ) > "$WORK/out/$label.txt" 2>&1 || DEPLOY_EXIT=$?
  cat "$WORK/out/$label.txt"
  tr -s ' \r\n' ' ' < "$WORK/out/$label.txt" > "$WORK/out/$label.flat"
  DEPLOY_ID="$(grep -oE 'Queued deployment [0-9a-f-]{36}' "$WORK/out/$label.flat" | awk '{print $3}' | head -1 || true)"
}

# ---------------------------------------------------------------------------------------------
step "0 — preconditions: this host really is clean"
# ---------------------------------------------------------------------------------------------
[ "$(id -u)" -eq 0 ] || die "Run as root: this installs Docker and writes to $HARBORA_DIR."

[ ! -e "$HARBORA_DIR" ] || die "This host is not clean: $HARBORA_DIR already exists. Every claim here depends on installing from nothing, so re-image it."

log "Installing this script's own prerequisites…"
if   command -v apt-get >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq curl jq openssl ca-certificates iproute2 >/dev/null
elif command -v dnf >/dev/null 2>&1; then dnf install -y -q curl jq openssl ca-certificates iproute >/dev/null
elif command -v yum >/dev/null 2>&1; then yum install -y -q curl jq openssl ca-certificates iproute >/dev/null
else die "No supported package manager (apt/dnf/yum)."
fi
for tool in curl jq openssl ip zcat tar; do
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
if grep -q 'Installation finished WITH ERRORS' "$WORK/install.log"; then
  fail "install.sh reported its own verification failures: $(grep '✗' "$WORK/install.log" | tail -8)"
fi
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
  check_detail restarts | grep -qF "giving up on purpose" \
    || fail "The restarts check does not quote the app's last words: $(check_detail restarts)"
  say_must "It is running, but something is wrong"
  fact "crashes: a deployment that passed its probe, then exit 2 and SucceededWithProblems, and the restarts check says so and quotes the log."
}

case_wrong_database_host() {
  local dir url detail
  dir="$(fixture wrong-database-host)"
  run_launch wrong-database-host "$dir"

  # The design: its log is a warning rather than a failure, because a line in a log can be about
  # anything. So it is Succeeded, and the warning has to be there and has to be the right one.
  must_eq "harbora launch's exit code" "$LAUNCH_EXIT" 0
  must_eq "the launch's status" "$(view_field status)" Succeeded
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
  fact "wrong-database-host: Succeeded with the logs check at Warning, naming the loopback address it could not reach, while the database it ignored answered."
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
  docker logs "$container" 2>&1 | grep -q 'redis answered: ping=True' \
    || fail "The worker's log never shows Redis answering: $(docker logs --tail 5 "$container" 2>&1 | cut -c1-300)"
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

  if docker logs harbora-panel 2>&1 | grep -qF -- "$secret"; then fail "The panel's log holds the generated JWT_SECRET."; fi
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
  deployment_log "$(view_field deploymentId)" | grep -qF "Frontend (Nginx)" \
    || fail "A Create React App project was not built as a frontend."

  url="$(view_field publicUrl)"
  root="$WORK/out/cra-root.html"
  must_eq "GET /" "$(http_get "$url/" "$root")" 200
  grep -qF 'id="root"' "$root" || fail "GET / is not the page: $(head -c 300 "$root")"
  grep -qE 'static/js/main\.[a-z0-9]+\.js' "$root" || fail "GET / is the source's template rather than the built page: $(head -c 400 "$root")"
  if grep -qF '%PUBLIC_URL%' "$root"; then fail "GET / still has %PUBLIC_URL% in it: this is the unbuilt template."; fi

  # Why the rule exists at all. `react-scripts start` is not a way to serve from a container, because it
  # exits when its stdin closes, and a container has none. That was reasoned from memory, so it is run.
  started="$(date +%s)"
  result="$(docker run --rm -v "$dir:/app" -w /app node:20-alpine sh -c \
    'npm install --no-audit --no-fund >/dev/null 2>&1; timeout 120 npm start </dev/null >/tmp/start.log 2>&1; code=$?; tail -n 2 /tmp/start.log | cut -c1-200; echo "exit=$code"')"
  elapsed="$(( $(date +%s) - started ))"
  log "react-scripts start, with stdin closed, after ${elapsed}s: $result"
  case "$result" in
    *"exit=124"*|*"exit=143"*) fail "react-scripts start kept running in a container for two minutes, so 'it cannot serve' is false: $result" ;;
    *"exit="*) ok "react-scripts start stopped by itself in a container." ;;
    *) fail "Could not tell what react-scripts start did: $result" ;;
  esac
  fact "cra-shaped: a real react-scripts project with 'start: react-scripts start' built as a frontend and served its built page; run directly with stdin closed, that start command stops by itself ($(printf '%s' "$result" | tail -n 1))."
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
  if docker inspect "$image" --format '{{json .Config.Env}}' | grep -qF "NEXT_PUBLIC_API"; then
    fail "NEXT_PUBLIC_API is in the image's ENV: it should only ever be an ARG. $(docker inspect "$image" --format '{{json .Config.Env}}')"
  fi
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
  if docker inspect "$image" --format '{{json .Config.Env}}' | grep -qF "BUILD_MARK"; then
    fail "BUILD_MARK is in the image's ENV: it should only ever be an ARG."
  fi
  # The accepted price, on a single-stage image, written down in Buildpacks.ArgLines. Not a failure.
  if docker history --no-trunc "$image" 2>/dev/null | grep -qF "$value"; then history_says="is"; else history_says="is not"; fi
  fact "python-marked-var: a pip install step saw nothing until the variable was marked AvailableAtBuild, then saw its value; the image's ENV does not hold it, and its docker history $history_says showing it (single-stage, as Buildpacks.ArgLines says)."
}

case_panel_log() {
  local noise
  # Each of these is a launch that moved on past something a person would want to know, or a query the
  # real database refused that the in-memory one in the tests would have run. A failing app's own
  # deployment logs at fail: as well, which is why this is not every fail: line.
  noise="$(docker logs harbora-panel 2>&1 | grep -E '^[0-9:]+ fail: (Harbora\.Infrastructure\.(Launch|Verification)|Microsoft\.AspNetCore\.Diagnostics|Microsoft\.EntityFrameworkCore)' | cut -c1-220 | head -n 60 || true)"
  [ -z "$noise" ] || fail "The panel logged errors from the launch path: $noise"
  fact "The panel logged no error from the launch tick, the checks, an unhandled request or a failed query across all of the above."
}

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
step_case "19 — nothing in the launch path logged an error of its own" case_panel_log

# ---------------------------------------------------------------------------------------------
# The verdict
# ---------------------------------------------------------------------------------------------
echo
elapsed_minutes="$(( ($(date +%s) - PROOF_STARTED) / 60 ))"
if [ -s "$FAILED_STEPS" ]; then
  report_host_state
  annotate "the verdict" "FAILED after ${elapsed_minutes} minutes, at $(git -C "$APP_DIR" rev-parse --short HEAD). Steps that failed:
$(cat "$FAILED_STEPS")

Established all the same:
$(cat "$WORK/facts")"
  err "FAILED steps:"
  sed 's/^/  /' "$FAILED_STEPS" >&2
  trap - ERR EXIT
  exit 1
fi

summary="Launch proof passed at $(git -C "$APP_DIR" rev-parse HEAD), in ${elapsed_minutes} minutes.
$(cat "$WORK/facts")"
summary="$(printf '%s' "$summary" | scrub)"
summary="${summary:0:3900}"
summary="${summary//'%'/%25}"; summary="${summary//$'\r'/%0D}"; summary="${summary//$'\n'/%0A}"
printf '::notice title=launch proof passed::%s\n' "$summary"
ok "Launch proof passed in ${elapsed_minutes} minutes."
