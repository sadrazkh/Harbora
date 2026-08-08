#!/usr/bin/env bash
#
# Harbora — live-host proof (the "R0 proof").
#
# Walks the whole README claim on a real, disposable VPS and refuses to call any of it done unless
# it actually happened:
#
#   1  install from the repository's own installer, at the PREVIOUS release
#   2  trust the Let's Encrypt STAGING root, so certificates are real but cost no rate limit
#   3  create the owner through the first-run wizard
#   4  create an app from a prebuilt image and deploy it
#   5  attach a domain and take a certificate — asserted to have come from staging, by issuer
#   6  take a backup through the Backup module and restore it, byte for byte
#   7  mint an enrolment token, enrol a SECOND host as a node, and deploy onto that node
#   8  take a pre-upgrade database dump, upgrade to the ref under test, and confirm the panel returns
#
# Run it BY HAND on a throwaway VPS:
#
#   export E2E_PANEL_DOMAIN=panel.e2e.example.com
#   export E2E_ROOT_DOMAIN=apps.e2e.example.com
#   export E2E_ACME_EMAIL=you@example.com
#   export E2E_OWNER_EMAIL=owner@e2e.example.com
#   export E2E_OWNER_PASSWORD='a-long-throwaway-password'
#   export E2E_NODE_HOST=203.0.113.9            # a SECOND disposable host
#   export E2E_NODE_SSH_KEY=/root/.ssh/e2e_ed25519
#   export E2E_TO_REF=my-branch                 # the ref under test
#   sudo -E bash deploy/live-host-proof.sh
#
# DNS you must have in place before starting (all A records, all pointing at THIS host):
#
#   $E2E_PANEL_DOMAIN            the panel
#   nodes.$E2E_PANEL_DOMAIN      the node channel's own mTLS host
#   *.$E2E_ROOT_DOMAIN           the wildcard every deployed app is published under
#
# This host is left INSTALLED when the run finishes, so a failure can be inspected. That means the
# next run on the same host will refuse at the first precondition — which is the point. Either
# re-image between runs, or set E2E_TEARDOWN=1 to have a SUCCESSFUL run remove what it created.
# Nothing here ever deletes state it did not create itself.
#
# Every step is numbered and every failure names its step. There is no step that can pass by not
# running: each one asserts a fact about the system afterwards rather than trusting an exit code.
set -Eeuo pipefail

# ---------------------------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------------------------
E2E_PANEL_DOMAIN="${E2E_PANEL_DOMAIN:-}"
E2E_ROOT_DOMAIN="${E2E_ROOT_DOMAIN:-}"
E2E_ACME_EMAIL="${E2E_ACME_EMAIL:-}"
E2E_OWNER_EMAIL="${E2E_OWNER_EMAIL:-}"
E2E_OWNER_PASSWORD="${E2E_OWNER_PASSWORD:-}"

# The second host. Required unless the operator deliberately asks for less (see E2E_SKIP_NODE).
E2E_NODE_HOST="${E2E_NODE_HOST:-}"
E2E_NODE_SSH_USER="${E2E_NODE_SSH_USER:-root}"
E2E_NODE_SSH_KEY="${E2E_NODE_SSH_KEY:-}"
E2E_NODE_NAME="${E2E_NODE_NAME:-harbora-e2e-node}"

# "The previous release" is what an operator is running today. There are no release tags yet, so
# master is the honest answer; this is a variable so a tag can replace it without touching the file.
E2E_FROM_REF="${E2E_FROM_REF:-master}"
E2E_TO_REF="${E2E_TO_REF:-master}"

E2E_REPO_URL="${E2E_REPO_URL:-https://github.com/sadrazkh/Harbora}"
E2E_REPO_RAW="${E2E_REPO_RAW:-https://raw.githubusercontent.com/sadrazkh/Harbora}"

# Let's Encrypt STAGING. Production would exhaust the five-duplicate-certificates-per-week limit by
# Wednesday and then fail for reasons that have nothing to do with the product.
ACME_CA_SERVER="${ACME_CA_SERVER:-https://acme-staging-v02.api.letsencrypt.org/directory}"
E2E_STAGING_ROOTS="${E2E_STAGING_ROOTS:-https://letsencrypt.org/certs/staging/letsencrypt-stg-root-x1.pem https://letsencrypt.org/certs/staging/letsencrypt-stg-root-x2.pem}"

E2E_APP_IMAGE="${E2E_APP_IMAGE:-nginx:alpine}"
E2E_SKIP_NODE="${E2E_SKIP_NODE:-0}"
E2E_TEARDOWN="${E2E_TEARDOWN:-0}"

HARBORA_DIR="${HARBORA_DIR:-/opt/harbora}"
COMPOSE_DIR="$HARBORA_DIR/app/deploy"

# Waits. Generous, because a cold VPS building a .NET image is slow and a flaky timeout is a lane
# nobody trusts.
W_PANEL="${E2E_WAIT_PANEL:-900}"
W_DEPLOY="${E2E_WAIT_DEPLOY:-900}"
W_CERT="${E2E_WAIT_CERT:-300}"
W_BACKUP="${E2E_WAIT_BACKUP:-600}"
W_NODE="${E2E_WAIT_NODE:-600}"

PANEL_URL="https://${E2E_PANEL_DOMAIN}"
WORK="$(mktemp -d /tmp/harbora-e2e.XXXXXX)"
COOKIES="$WORK/cookies.txt"
API_TOKEN=""
RUN_ID="$(date -u +%Y%m%d-%H%M%S)"
MARKER="harbora-e2e-${RUN_ID}-$$"

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

# Every failure names the step it happened in. Without this a red run is a wall of curl output and
# somebody has to guess which of eight things broke.
on_error() {
  local code=$?
  echo
  err "FAILED in step: ${CURRENT_STEP}"
  err "  exit code ${code}, at ${BASH_SOURCE[1]:-?} line ${BASH_LINENO[0]:-?}"
  err "  working files kept at ${WORK} (they contain no secrets beyond what you supplied)"
  err "  the host is left as it is, deliberately, so it can be inspected:"
  err "    docker compose -f ${COMPOSE_DIR}/docker-compose.yml ps"
  err "    docker compose -f ${COMPOSE_DIR}/docker-compose.yml logs --tail 100 panel traefik"
  exit "$code"
}
trap on_error ERR

die() { err "$*"; exit 1; }

# ---------------------------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------------------------

# wait_until <seconds> <what> <command…> — polls until the command succeeds, then reports how long.
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
    if [ "$now" -ge "$deadline" ]; then
      die "Timed out after ${budget}s waiting for: $what"
    fi
    sleep 5
  done
}

# A cookie-session GET. No -k anywhere in this script: the staging root is in the trust store by
# step 2, so an untrusted certificate is a real failure rather than something to wave through.
web_get() { curl -fsS --max-time 60 -b "$COOKIES" -c "$COOKIES" "$PANEL_URL$1"; }

# The antiforgery request token. The header name is customised to X-CSRF-TOKEN (Program.cs), but the
# form field keeps the stock name, so this reads the field and sends it as the header — which works
# for every POST without having to model each form's own hidden inputs.
antiforgery_token() {
  local html token
  html="$(web_get "$1")"
  token="$(printf '%s' "$html" \
    | grep -o '<input[^>]*__RequestVerificationToken[^>]*>' \
    | head -1 | grep -o 'value="[^"]*"' | head -1 | sed 's/^value="//; s/"$//')" || true
  [ -n "$token" ] || die "No antiforgery token on $1 — the page did not render the form we expected."
  printf '%s' "$token"
}

# A cookie-session form POST. Writes headers to $WORK/last.headers and the body to $WORK/last.body,
# and prints the HTTP status, so callers can tell a 302 (accepted) from a 200 (form re-rendered with
# validation errors, which is a failure wearing a success's clothes).
web_post() {
  local path="$1" token="$2"; shift 2
  local args=()
  for field in "$@"; do args+=(--data-urlencode "$field"); done
  curl -sS --max-time 120 -o "$WORK/last.body" -D "$WORK/last.headers" -w '%{http_code}' \
    -b "$COOKIES" -c "$COOKIES" \
    -H "X-CSRF-TOKEN: $token" \
    "${args[@]}" \
    "$PANEL_URL$path"
}

last_location() {
  grep -i '^location:' "$WORK/last.headers" | tail -1 | tr -d '\r' | sed 's/^[Ll]ocation:[[:space:]]*//'
}

# Bearer API call. api <METHOD> <path> [json]
api() {
  local method="$1" path="$2" body="${3:-}"
  if [ -n "$body" ]; then
    curl -fsS --max-time 120 -X "$method" \
      -H "Authorization: Bearer $API_TOKEN" -H 'Content-Type: application/json' \
      -d "$body" "$PANEL_URL$path"
  else
    curl -fsS --max-time 120 -X "$method" \
      -H "Authorization: Bearer $API_TOKEN" "$PANEL_URL$path"
  fi
}

panel_exec() { docker exec harbora-panel sh -c "$1"; }

node_ssh() {
  ssh -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=20 \
    -i "$E2E_NODE_SSH_KEY" "${E2E_NODE_SSH_USER}@${E2E_NODE_HOST}" "$@"
}

# ---------------------------------------------------------------------------------------------
step "0 — preconditions (refuse early, refuse loudly)"
# ---------------------------------------------------------------------------------------------

[ "$(id -u)" -eq 0 ] || die "Run as root: this installs Docker and writes to $HARBORA_DIR."

missing=""
for var in E2E_PANEL_DOMAIN E2E_ROOT_DOMAIN E2E_ACME_EMAIL E2E_OWNER_EMAIL E2E_OWNER_PASSWORD; do
  [ -n "${!var}" ] || missing="$missing $var"
done
[ -z "$missing" ] || die "Missing required environment:${missing}. Nothing has been changed on this host."

[ "${#E2E_OWNER_PASSWORD}" -ge 8 ] || die "E2E_OWNER_PASSWORD must be at least 8 characters — the wizard enforces it and this is a nicer place to find out."

if [ "$E2E_SKIP_NODE" = "1" ]; then
  warn "═══════════════════════════════════════════════════════════════════════════"
  warn "  E2E_SKIP_NODE=1 — the node half of this proof will NOT run."
  warn "  NOT PROVEN by this run: token minting, node enrolment, the mTLS channel,"
  warn "  and deploying a workload onto a node. Those are the flagship multi-server"
  warn "  claims, and they are the ones a single host cannot honestly test."
  warn "  A green result from this run does NOT mean the node feature works."
  warn "═══════════════════════════════════════════════════════════════════════════"
else
  [ -n "$E2E_NODE_HOST" ] || die "E2E_NODE_HOST is required: enrolling a node needs a SECOND disposable host, because a node that never crosses a network has not been tested. Set E2E_SKIP_NODE=1 to deliberately run the rest without it."
  [ -n "$E2E_NODE_SSH_KEY" ] || die "E2E_NODE_SSH_KEY is required to reach $E2E_NODE_HOST."
  [ -f "$E2E_NODE_SSH_KEY" ] || die "No SSH key at $E2E_NODE_SSH_KEY."
fi

# The one refusal that protects every claim after it. An existing install means the previous run
# left state behind, so "fresh install" would be a lie from here on. Deliberately NOT cleaned up
# automatically: an unattended rm -rf on a host this script did not provision is worse than a red
# run.
if [ -e "$HARBORA_DIR" ]; then
  err "This host is not clean: $HARBORA_DIR already exists."
  err "Every claim this lane makes depends on installing from nothing, so it will not continue."
  err "Re-image the host, or if you are certain, remove the previous install BY HAND:"
  err "    bash $COMPOSE_DIR/install.sh uninstall     # prompts about data volumes"
  err "    rm -rf $HARBORA_DIR"
  exit 1
fi

if [ "$E2E_FROM_REF" = "$E2E_TO_REF" ]; then
  warn "E2E_FROM_REF and E2E_TO_REF are both '$E2E_FROM_REF'."
  warn "  Step 8 will therefore re-apply the same code. That still proves the update path runs and"
  warn "  the panel returns, but it does NOT prove a version transition. Set E2E_TO_REF to the ref"
  warn "  under test to get the stronger claim."
fi

log "Installing this script's own prerequisites…"
if   command -v apt-get >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq curl jq openssl ca-certificates openssh-client >/dev/null
elif command -v dnf >/dev/null 2>&1; then dnf install -y -q curl jq openssl ca-certificates openssh-clients >/dev/null
elif command -v yum >/dev/null 2>&1; then yum install -y -q curl jq openssl ca-certificates openssh-clients >/dev/null
elif command -v apk >/dev/null 2>&1; then apk add --no-cache curl jq openssl ca-certificates openssh-client >/dev/null
else die "No supported package manager (apt/dnf/yum/apk). This script cannot prepare the host."
fi
for tool in curl jq openssl; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is still not on PATH after installing prerequisites."
done
ok "curl, jq and openssl are present."

# DNS before anything expensive. A certificate that cannot be issued is a 20-minute failure if we
# find out at step 5 and a 5-second one if we find out here.
SERVER_IP="$(curl -fsS4 --max-time 10 https://api.ipify.org)"
ok "This host's public address: $SERVER_IP"

dns_bad=0
for name in "$E2E_PANEL_DOMAIN" "nodes.$E2E_PANEL_DOMAIN" "harbora-e2e-probe.$E2E_ROOT_DOMAIN"; do
  resolved="$(getent hosts "$name" 2>/dev/null | awk '{print $1}' | head -1 || true)"
  if [ -z "$resolved" ]; then
    err "DNS: $name does not resolve. Add:  $name  A  $SERVER_IP"
    dns_bad=1
  elif [ "$resolved" != "$SERVER_IP" ]; then
    err "DNS: $name resolves to $resolved, not this host ($SERVER_IP)."
    dns_bad=1
  else
    ok "DNS: $name → $SERVER_IP"
  fi
done
[ "$dns_bad" -eq 0 ] || die "DNS is not ready. Let's Encrypt would refuse every name here, so nothing has been installed."

# ---------------------------------------------------------------------------------------------
# The staging CA knob must exist in the compose file before anything is installed.
#
# Traefik's ACME resolver defaults to PRODUCTION Let's Encrypt. This lane needs staging: production
# has a five-duplicate-certificates-per-week limit that a nightly exhausts by Wednesday, after which
# it fails for reasons that have nothing to do with the product. Staging is selected by
# ACME_CA_SERVER, which compose can only act on if deploy/docker-compose.yml interpolates it.
#
# At the time this script was written it does NOT, and that is checked here rather than discovered
# at step 5 — because the failure mode of not checking is the one thing this lane must never do:
# quietly issue real certificates from production and burn the rate limit of a domain somebody
# cares about. Both refs are checked, because step 8 re-renders the stack from E2E_TO_REF.
#
# The one-line change this wants, in deploy/docker-compose.yml under the traefik service's command:
#
#   - --certificatesresolvers.letsencrypt.acme.caserver=${ACME_CA_SERVER:-https://acme-v02.api.letsencrypt.org/directory}
#
# The default is byte-identical to today's behaviour, so an operator who never sets the variable
# still gets production Let's Encrypt and nothing about a normal install changes.
log "Checking that the compose file can be pointed at a staging CA…"
compose_knob_missing=""
for ref in "$E2E_FROM_REF" "$E2E_TO_REF"; do
  curl -fsSL "${E2E_REPO_RAW}/${ref}/deploy/docker-compose.yml" -o "$WORK/compose-${ref//\//_}.yml" \
    || die "Could not download deploy/docker-compose.yml at ref '$ref' to check it."
  grep -q 'acme\.caserver' "$WORK/compose-${ref//\//_}.yml" || compose_knob_missing="$compose_knob_missing $ref"
done
if [ -n "$compose_knob_missing" ]; then
  err "deploy/docker-compose.yml at ref(s)${compose_knob_missing} has no ACME caserver setting."
  err "Traefik would therefore request certificates from PRODUCTION Let's Encrypt, and this lane"
  err "would burn a real rate limit every night. Refusing before anything is installed."
  err ""
  err "Add this line to the traefik service's command: list in deploy/docker-compose.yml:"
  err "  - --certificatesresolvers.letsencrypt.acme.caserver=\${ACME_CA_SERVER:-https://acme-v02.api.letsencrypt.org/directory}"
  err ""
  err "The default is exactly today's behaviour, so nothing changes for an ordinary install."
  exit 1
fi
ok "The compose file honours ACME_CA_SERVER at both refs."

ok "Preconditions met. Working directory: $WORK"

# ---------------------------------------------------------------------------------------------
step "1 — trust the Let's Encrypt staging root"
# ---------------------------------------------------------------------------------------------
# This is not cosmetic and it is not a way of being lenient. Two things need it:
#
#   * the node agent. Harbora.NodeAgent's ControlPlaneTls treats a certificate error as fatal and
#     never falls back, so a node whose host does not trust the staging root enrols and then fails
#     TLS on every connect afterwards — which is exactly the bug install.sh's own SSL check exists
#     to catch.
#   * install.sh's verification. Its panel and node-channel certificate probes deliberately run
#     WITHOUT -k. Against an untrusted staging certificate they degrade to warnings and stop being
#     able to tell "issued" from "never issued". Putting the root in the store keeps them meaningful.
#
# It also means this script never needs curl -k, so an untrusted certificate anywhere is a failure
# rather than something waved through.

install_staging_roots() {  # runs on whichever host it is called on
  local dest_dir refresh
  if   [ -d /usr/local/share/ca-certificates ]; then dest_dir=/usr/local/share/ca-certificates; refresh=update-ca-certificates
  elif [ -d /etc/pki/ca-trust/source/anchors ];  then dest_dir=/etc/pki/ca-trust/source/anchors;  refresh=update-ca-trust
  else return 9
  fi
  local n=0
  for url in $E2E_STAGING_ROOTS; do
    n=$((n + 1))
    curl -fsS --max-time 60 "$url" -o "${dest_dir}/harbora-le-staging-${n}.crt" || return 8
    grep -q 'BEGIN CERTIFICATE' "${dest_dir}/harbora-le-staging-${n}.crt" || return 7
  done
  "$refresh" >/dev/null 2>&1 || return 6
  return 0
}

if ! install_staging_roots; then
  case $? in
    9) die "This host has neither /usr/local/share/ca-certificates nor /etc/pki/ca-trust/source/anchors, so the staging root cannot be trusted. Without it the node agent will refuse every connection." ;;
    8) die "Could not download a Let's Encrypt staging root from: $E2E_STAGING_ROOTS. If Let's Encrypt has moved these files, set E2E_STAGING_ROOTS to the current URLs." ;;
    7) die "What was downloaded from $E2E_STAGING_ROOTS is not a PEM certificate." ;;
    6) die "Refreshing the system trust store failed." ;;
    *) die "Could not install the staging roots." ;;
  esac
fi
ok "Let's Encrypt staging root(s) trusted on the panel host."

# ---------------------------------------------------------------------------------------------
step "2 — install from the repository's own installer, at $E2E_FROM_REF"
# ---------------------------------------------------------------------------------------------
# Curled from GitHub rather than run from a local checkout, because that is the command the README
# gives a stranger and it is the command under test. ACME_CA_SERVER is exported so the compose file
# picks staging up for this run; it is also written into .env afterwards so that every later
# `docker compose up` on this host — harbora restart, the update in step 8 — keeps using it.
export ACME_CA_SERVER
export PANEL_DOMAIN="$E2E_PANEL_DOMAIN"
export ROOT_DOMAIN="$E2E_ROOT_DOMAIN"
export ACME_EMAIL="$E2E_ACME_EMAIL"
export REPO_URL="$E2E_REPO_URL"
export REPO_BRANCH="$E2E_FROM_REF"

log "curl -fsSL ${E2E_REPO_RAW}/${E2E_FROM_REF}/deploy/install.sh | bash"
curl -fsSL "${E2E_REPO_RAW}/${E2E_FROM_REF}/deploy/install.sh" -o "$WORK/install-from.sh"
grep -q 'Harbora' "$WORK/install-from.sh" || die "What was downloaded from ${E2E_REPO_RAW}/${E2E_FROM_REF}/deploy/install.sh is not the Harbora installer."
bash "$WORK/install-from.sh" install 2>&1 | tee "$WORK/install.log"

# install.sh exits 0 even when its own verify_install failed — it calls `verify_install || true` and
# then prints the banner. So its exit code is not evidence, and everything below re-establishes the
# facts independently.
if grep -q 'Installation finished WITH ERRORS' "$WORK/install.log"; then
  err "install.sh reported its own verification failures (see $WORK/install.log)."
  grep -n '✗' "$WORK/install.log" | tail -20 || true
  die "Refusing to build a proof on top of an install that told us it was broken."
fi

[ -f "$COMPOSE_DIR/.env" ] || die "install.sh finished but there is no .env at $COMPOSE_DIR/.env."
if ! grep -q '^ACME_CA_SERVER=' "$COMPOSE_DIR/.env"; then
  printf 'ACME_CA_SERVER=%s\n' "$ACME_CA_SERVER" >> "$COMPOSE_DIR/.env"
  chmod 600 "$COMPOSE_DIR/.env"
fi
ok "Staging CA persisted into .env, so later compose invocations keep it."

panel_healthy() { curl -fsS --max-time 15 -o /dev/null "$PANEL_URL/healthz"; }
wait_until "$W_PANEL" "the panel answers /healthz over public DNS and a trusted certificate" panel_healthy

# ---------------------------------------------------------------------------------------------
step "3 — enable the Backup module, then create the owner"
# ---------------------------------------------------------------------------------------------
# The Backup module ships OFF (appsettings.json "Features": {"Backup": false}) and enabling it is
# described there as a deliberate act. This lane is that deliberate act, and it is worth being
# explicit that step 6 therefore tests a NON-DEFAULT configuration.
#
# Done with a compose override rather than by editing docker-compose.yml: compose MERGES mappings
# like `environment`, so an override adds one key without restating anything and cannot drift.
# (`command` is a list and is REPLACED, which is why the ACME setting had to be a variable in the
# real file instead.) git reset --hard in the updater does not remove untracked files, so this
# survives step 8.
cat > "$COMPOSE_DIR/docker-compose.override.yml" <<'OVERRIDE'
# Written by deploy/live-host-proof.sh. Not part of a normal install.
services:
  panel:
    environment:
      Features__Backup: "true"
OVERRIDE

(cd "$COMPOSE_DIR" && docker compose up -d panel)
wait_until "$W_PANEL" "the panel is healthy again with the Backup module enabled" panel_healthy

setup_token="$(antiforgery_token /setup)"
status="$(web_post /setup "$setup_token" \
  "__RequestVerificationToken=$setup_token" \
  "PlatformName=Harbora E2E" \
  "DisplayName=E2E Owner" \
  "Email=$E2E_OWNER_EMAIL" \
  "Password=$E2E_OWNER_PASSWORD" \
  "ConfirmPassword=$E2E_OWNER_PASSWORD" \
  "RootDomain=$E2E_ROOT_DOMAIN" \
  "AcmeEmail=$E2E_ACME_EMAIL" \
  "Culture=en")"

case "$status" in
  302|303) ok "The first-run wizard accepted the owner (HTTP $status)." ;;
  200) err "The wizard re-rendered its form instead of redirecting, which means it rejected the input:"
       grep -o 'validation-summary[^<]*<[^>]*>[^<]*' "$WORK/last.body" | head -5 || true
       die "Owner was not created." ;;
  *) die "POST /setup answered HTTP $status." ;;
esac

# The wizard signs the owner in, but prove the account exists on its own terms rather than trusting
# the redirect: a fresh login, and a bearer token issued from those same credentials.
rm -f "$COOKIES"
login_token="$(antiforgery_token /account/login)"
status="$(web_post /account/login "$login_token" \
  "__RequestVerificationToken=$login_token" \
  "Email=$E2E_OWNER_EMAIL" "Password=$E2E_OWNER_PASSWORD")"
case "$status" in
  302|303) ok "The owner can sign in through the real login form." ;;
  *) die "Signing in as the newly created owner answered HTTP $status, not a redirect." ;;
esac

API_TOKEN="$(curl -fsS --max-time 60 -X POST -H 'Content-Type: application/json' \
  -d "$(jq -nc --arg e "$E2E_OWNER_EMAIL" --arg p "$E2E_OWNER_PASSWORD" \
        '{email:$e, password:$p, name:"live-host-proof"}')" \
  "$PANEL_URL/api/v1/auth/token" | jq -er '.token')"
[ -n "$API_TOKEN" ] || die "POST /api/v1/auth/token returned no token."
api GET /api/v1/whoami > "$WORK/whoami.json"
ok "API token issued and accepted: $(jq -r '.email // "?"' "$WORK/whoami.json")"

# ---------------------------------------------------------------------------------------------
step "4 — create an app from a prebuilt image and deploy it"
# ---------------------------------------------------------------------------------------------
APP_SLUG="e2e-web-${RUN_ID}"

create_token="$(antiforgery_token /apps/create)"
status="$(web_post /Apps/Create "$create_token" \
  "__RequestVerificationToken=$create_token" \
  "Name=E2E Web ${RUN_ID}" \
  "Slug=${APP_SLUG}" \
  "SourceType=PrebuiltImage" \
  "PrebuiltImage=${E2E_APP_IMAGE}" \
  "ContainerPort=80" \
  "Kind=Web" \
  "DeployNow=false")"

case "$status" in
  302|303) : ;;
  200) err "Apps/Create re-rendered the form, so it refused the input:"
       grep -o 'field-validation-error[^<]*<[^>]*>[^<]*' "$WORK/last.body" | head -5 || true
       die "The app was not created." ;;
  *) die "POST /Apps/Create answered HTTP $status." ;;
esac

APP_ID="$(last_location | grep -o '[0-9a-fA-F-]\{36\}' | head -1)"
[ -n "$APP_ID" ] || die "Apps/Create redirected to '$(last_location)', which carries no app id."
ok "App created: $APP_SLUG ($APP_ID)"

deploy_app() {  # deploy_app <slug> <label>; echoes nothing, sets DEPLOYMENT_ID
  DEPLOYMENT_ID="$(api POST "/api/v1/apps/$1/deploy" '{}' | jq -er '.deploymentId')"
  [ -n "$DEPLOYMENT_ID" ] || die "Deploying $1 returned no deploymentId."
  log "$2: deployment $DEPLOYMENT_ID queued."
}

deployment_settled() {
  api GET "/api/v1/deployments/$DEPLOYMENT_ID" > "$WORK/deployment.json" 2>/dev/null || return 1
  case "$(jq -r '.status' "$WORK/deployment.json")" in
    Succeeded) return 0 ;;
    Failed|Cancelled|RolledBack)
      err "Deployment $DEPLOYMENT_ID ended $(jq -r '.status' "$WORK/deployment.json"): $(jq -r '.errorMessage // "no message"' "$WORK/deployment.json")"
      api GET "/api/v1/deployments/$DEPLOYMENT_ID/logs" | jq -r '.[] | .message' | tail -40 || true
      exit 1 ;;
    *) return 1 ;;
  esac
}

deploy_app "$APP_SLUG" "Local deploy"
wait_until "$W_DEPLOY" "deployment $DEPLOYMENT_ID reached Succeeded" deployment_settled

# ---------------------------------------------------------------------------------------------
step "5 — attach a domain and take a certificate from Let's Encrypt STAGING"
# ---------------------------------------------------------------------------------------------
APP_DOMAIN="${APP_SLUG}.${E2E_ROOT_DOMAIN}"

domain_token="$(antiforgery_token "/apps/$APP_ID")"
status="$(web_post "/apps/$APP_ID/domains" "$domain_token" \
  "__RequestVerificationToken=$domain_token" \
  "host=${APP_DOMAIN}" "ssl=true")"
case "$status" in
  302|303|200) ok "Domain $APP_DOMAIN attached (HTTP $status)." ;;
  *) die "Attaching $APP_DOMAIN answered HTTP $status." ;;
esac

app_serves_over_tls() { curl -fsS --max-time 20 -o /dev/null "https://${APP_DOMAIN}/"; }
wait_until "$W_CERT" "https://${APP_DOMAIN}/ answers with a certificate this host trusts" app_serves_over_tls

# The claim is not "TLS worked" — curl would have said that against a production certificate too,
# and a nightly quietly burning production rate limit is exactly the failure this is meant to avoid.
# The claim is "this certificate came from the staging CA", so the issuer is read and asserted.
openssl s_client -servername "$APP_DOMAIN" -connect "${APP_DOMAIN}:443" </dev/null 2>/dev/null \
  > "$WORK/appcert.txt" || die "Could not complete a TLS handshake with ${APP_DOMAIN}:443."

issuer="$(grep -m1 '^issuer=' "$WORK/appcert.txt" || true)"
[ -n "$issuer" ] || die "No issuer line in the certificate served by $APP_DOMAIN."
echo "  $issuer"
case "$issuer" in
  *STAGING*|*staging*|*Pretend*|*Doctored*)
    ok "The certificate was issued by the Let's Encrypt STAGING CA." ;;
  *)
    err "The certificate for $APP_DOMAIN was NOT issued by Let's Encrypt staging."
    err "  $issuer"
    err "  Either ACME_CA_SERVER did not reach Traefik — check that deploy/docker-compose.yml"
    err "  carries --certificatesresolvers.letsencrypt.acme.caserver — or this is Traefik's own"
    err "  self-signed default, which means ACME never issued anything at all."
    exit 1 ;;
esac

# Belt and braces: a self-signed Traefik default would also fail this, and it proves the name on the
# certificate is the name we asked for rather than some other host's.
if ! grep -qi "CN *= *${APP_DOMAIN}" "$WORK/appcert.txt" \
   && ! openssl s_client -servername "$APP_DOMAIN" -connect "${APP_DOMAIN}:443" </dev/null 2>/dev/null \
      | openssl x509 -noout -text 2>/dev/null | grep -qi "DNS:${APP_DOMAIN}"; then
  die "The certificate served by $APP_DOMAIN does not name $APP_DOMAIN in its subject or SANs."
fi
ok "The certificate names $APP_DOMAIN."

# ---------------------------------------------------------------------------------------------
step "6 — take a backup through the Backup module and restore it"
# ---------------------------------------------------------------------------------------------
# A marker file goes in, a snapshot is taken, the snapshot is restored somewhere else, and the
# marker is looked for in what came back. "The job said Completed" is not the claim; "the bytes
# returned" is.
SRC_DIR="/var/lib/harbora/builds/e2e-${RUN_ID}"
DST_DIR="/var/lib/harbora/builds/e2e-${RUN_ID}-restored"

panel_exec "mkdir -p '$SRC_DIR' && printf '%s\n' '$MARKER' > '$SRC_DIR/marker.txt'"
panel_exec "grep -q '$MARKER' '$SRC_DIR/marker.txt'" || die "Could not seed the marker file inside the panel container."
ok "Seeded $SRC_DIR/marker.txt with $MARKER"

# Enums are serialised by NUMBER: nothing registers a JsonStringEnumConverter, so "Local" would be a
# 400. Local = 0, Native = 0, Directory = 2, Folder = 1, Overwrite = 1.
REPO_ID="$(api POST /api/v1/backup/repositories "$(jq -nc \
  --arg name "e2e-${RUN_ID}" --arg pw "$MARKER" --arg path "/var/lib/harbora/backups/e2e-${RUN_ID}" \
  '{name:$name, type:0, engine:0, password:$pw, localPath:$path}')" | jq -er '.id')"
[ -n "$REPO_ID" ] || die "Creating a backup repository returned no id. If this was a 404, the Backup module did not come up enabled."
ok "Backup repository $REPO_ID"

SNAPSHOT_ID="$(api POST /api/v1/backup/snapshots "$(jq -nc \
  --arg repo "$REPO_ID" --arg ref "$SRC_DIR" \
  '{repositoryId:$repo, targetType:2, targetRef:$ref}')" | jq -er '.snapshotId')"
[ -n "$SNAPSHOT_ID" ] || die "Queueing a snapshot returned no snapshotId."

snapshot_settled() {
  api GET "/api/v1/backup/snapshots/$SNAPSHOT_ID" > "$WORK/snapshot.json" 2>/dev/null || return 1
  case "$(jq -r '.status' "$WORK/snapshot.json")" in
    Completed|CompletedWithWarnings) return 0 ;;
    Failed|Cancelled)
      err "Snapshot $SNAPSHOT_ID ended $(jq -r '.status' "$WORK/snapshot.json"): $(jq -r '.errorMessage // .failureReason // "no reason recorded"' "$WORK/snapshot.json")"
      exit 1 ;;
    *) return 1 ;;
  esac
}
wait_until "$W_BACKUP" "snapshot $SNAPSHOT_ID completed" snapshot_settled

RESTORE_ID="$(api POST /api/v1/backup/restore-jobs "$(jq -nc \
  --arg snap "$SNAPSHOT_ID" --arg dest "$DST_DIR" \
  '{snapshotId:$snap, destination:$dest, restoreType:1, conflictStrategy:1}')" | jq -er '.restoreJobId')"
[ -n "$RESTORE_ID" ] || die "Queueing a restore returned no restoreJobId."

restore_settled() {
  api GET "/api/v1/backup/restore-jobs/$RESTORE_ID" > "$WORK/restore.json" 2>/dev/null || return 1
  case "$(jq -r '.status' "$WORK/restore.json")" in
    Completed) return 0 ;;
    Failed|Cancelled)
      err "Restore $RESTORE_ID ended $(jq -r '.status' "$WORK/restore.json"): $(jq -r '.errorMessage // "no reason recorded"' "$WORK/restore.json")"
      exit 1 ;;
    *) return 1 ;;
  esac
}
wait_until "$W_BACKUP" "restore $RESTORE_ID completed" restore_settled

# The only assertion that matters. Searched recursively because the restore's internal layout is not
# part of the claim — that the bytes survived the round trip is.
panel_exec "grep -rq '$MARKER' '$DST_DIR'" \
  || die "The restore reported Completed, but $MARKER is nowhere under $DST_DIR. The job succeeded and the data did not come back."
ok "The marker came back out of the restore: the round trip moved real bytes."

# ---------------------------------------------------------------------------------------------
step "7 — enrol a node with a minted token and deploy onto it"
# ---------------------------------------------------------------------------------------------
if [ "$E2E_SKIP_NODE" = "1" ]; then
  warn "Skipped by E2E_SKIP_NODE=1. Node enrolment and node deployment are NOT proven by this run."
else
  node_ssh 'echo ok' >/dev/null || die "Cannot reach ${E2E_NODE_SSH_USER}@${E2E_NODE_HOST} over SSH with $E2E_NODE_SSH_KEY."
  node_ssh "test ! -e /etc/harbora-node" \
    || die "$E2E_NODE_HOST already has /etc/harbora-node — it is not a clean host. Re-image it, or remove the previous agent by hand with deploy/node-agent/uninstall.sh."
  ok "The node host is reachable and clean."

  # The node must trust the staging root too, for the reason in step 1: ControlPlaneTls treats a
  # certificate error as fatal, so without this the node enrols, reports success, and never connects.
  node_ssh "E2E_STAGING_ROOTS='$E2E_STAGING_ROOTS' bash -s" <<EOF || die "Could not install the staging roots on $E2E_NODE_HOST."
set -euo pipefail
command -v curl >/dev/null 2>&1 || { (command -v apt-get >/dev/null && DEBIAN_FRONTEND=noninteractive apt-get update -qq && apt-get install -y -qq curl ca-certificates) || (command -v dnf >/dev/null && dnf install -y -q curl ca-certificates) || (command -v yum >/dev/null && yum install -y -q curl ca-certificates) || (command -v apk >/dev/null && apk add --no-cache curl ca-certificates); }
$(declare -f install_staging_roots)
install_staging_roots
EOF
  ok "Staging root trusted on $E2E_NODE_HOST."

  ENROLL_TOKEN="$(api POST /api/v1/nodes/tokens "$(jq -nc --arg n "$E2E_NODE_NAME" \
    '{nodeName:$n, lifetimeMinutes:60}')" | jq -er '.token')"
  [ -n "$ENROLL_TOKEN" ] || die "Minting an enrolment token returned nothing."
  ok "Enrolment token minted (prefix $(printf '%s' "$ENROLL_TOKEN" | cut -c1-20)…)."

  # Enrolment is addressed to the PANEL's host: a node has no certificate yet, so it cannot be asked
  # for one. The panel hands back the mTLS host (NODE_DOMAIN) that the node uses from then on.
  node_ssh "curl -fsSL '${E2E_REPO_RAW}/${E2E_TO_REF}/deploy/node-agent/install.sh' | bash -s -- \
      --control-plane '${PANEL_URL}' --token '${ENROLL_TOKEN}' --name '${E2E_NODE_NAME}'" \
    2>&1 | tee "$WORK/node-install.log" \
    || die "The node agent installer failed on $E2E_NODE_HOST — see $WORK/node-install.log."

  # Enrolment alone leaves the node Pending. Online means it opened the mTLS channel, which is the
  # part that actually needs DNS, the certificate and the router to all be right.
  node_online() {
    api GET /api/v1/nodes > "$WORK/nodes.json" 2>/dev/null || return 1
    [ "$(jq -r --arg n "$E2E_NODE_NAME" '[.[] | select(.name == $n) | .status] | first // "absent"' "$WORK/nodes.json")" = "Online" ]
  }
  wait_until "$W_NODE" "node $E2E_NODE_NAME reached Online (mTLS channel open)" node_online

  # The Server row is a projection the channel session creates, so it exists only once the node is
  # Online. There is no JSON endpoint that returns a Server id; the app-create form is the one place
  # the Guid is rendered, and the select only appears when there is more than one server — which is
  # true now, and is itself a check that the node became a placement target.
  NODE_SERVER_ID="$(web_get /apps/create \
    | grep -o '<option value="[0-9a-fA-F-]\{36\}"[^>]*>[^<]*</option>' \
    | grep -F "$E2E_NODE_NAME" \
    | grep -o 'value="[0-9a-fA-F-]\{36\}"' | head -1 | sed 's/^value="//; s/"$//')" || true
  [ -n "$NODE_SERVER_ID" ] \
    || die "The node is Online but no server option named $E2E_NODE_NAME appears on /apps/create, so it never became a deployment target."
  ok "The node is a placement target: server $NODE_SERVER_ID"

  NODE_APP_SLUG="e2e-node-${RUN_ID}"
  node_create_token="$(antiforgery_token /apps/create)"
  status="$(web_post /Apps/Create "$node_create_token" \
    "__RequestVerificationToken=$node_create_token" \
    "Name=E2E Node App ${RUN_ID}" \
    "Slug=${NODE_APP_SLUG}" \
    "SourceType=PrebuiltImage" \
    "PrebuiltImage=${E2E_APP_IMAGE}" \
    "ContainerPort=80" \
    "Kind=Web" \
    "ServerId=${NODE_SERVER_ID}" \
    "DeployNow=false")"
  case "$status" in
    302|303) ok "App $NODE_APP_SLUG created against the node." ;;
    200) err "Apps/Create refused the node-targeted app — most likely the scheduler declined the placement:"
         grep -o 'field-validation-error[^<]*<[^>]*>[^<]*' "$WORK/last.body" | head -5 || true
         die "The node-targeted app was not created." ;;
    *) die "POST /Apps/Create for the node app answered HTTP $status." ;;
  esac

  deploy_app "$NODE_APP_SLUG" "Node deploy"
  wait_until "$W_DEPLOY" "deployment $DEPLOYMENT_ID onto the node reached Succeeded" deployment_settled

  # A deployment can report Succeeded without the workload being on the host we meant. Ask the node.
  node_ssh "docker ps --format '{{.Image}} {{.Names}}'" > "$WORK/node-containers.txt" \
    || die "Could not list containers on $E2E_NODE_HOST."
  grep -q "$E2E_APP_IMAGE" "$WORK/node-containers.txt" \
    || { cat "$WORK/node-containers.txt"; die "The deployment says Succeeded, but no $E2E_APP_IMAGE container is running on $E2E_NODE_HOST. It was not deployed where the panel claims."; }
  ok "The workload is running on the node host itself."
fi

# ---------------------------------------------------------------------------------------------
step "8 — take a pre-upgrade dump, upgrade to $E2E_TO_REF, and confirm the panel comes back"
# ---------------------------------------------------------------------------------------------
harbora backup-db 2>&1 | tee "$WORK/backup-db.log"
harbora backups | tee "$WORK/backups.log"
grep -qE 'manual-[0-9]{8}-[0-9]{6}\.sql\.gz' "$WORK/backups.log" \
  || die "harbora backup-db reported success but no dump is listed by harbora backups. The documented way back does not exist."
ok "Pre-upgrade restore point written and listed."

DEPLOYMENTS_BEFORE="$(api GET /api/v1/apps | jq -r 'length')"
log "Apps before the upgrade: $DEPLOYMENTS_BEFORE"

export REPO_BRANCH="$E2E_TO_REF"
curl -fsSL "${E2E_REPO_RAW}/${E2E_TO_REF}/deploy/install.sh" -o "$WORK/install-to.sh"
grep -q 'Harbora' "$WORK/install-to.sh" || die "What was downloaded for $E2E_TO_REF is not the Harbora installer."
bash "$WORK/install-to.sh" update 2>&1 | tee "$WORK/update.log"

if grep -q 'updated, but with ERRORS' "$WORK/update.log"; then
  err "install.sh update reported its own verification failures:"
  grep -n '✗' "$WORK/update.log" | tail -20 || true
  die "The upgrade completed with errors."
fi

wait_until "$W_PANEL" "the panel answers /healthz again after the upgrade" panel_healthy

# "The panel comes back" has to mean more than a health endpoint: the session, the data and the API
# all have to have survived the migration.
rm -f "$COOKIES"
relogin_token="$(antiforgery_token /account/login)"
status="$(web_post /account/login "$relogin_token" \
  "__RequestVerificationToken=$relogin_token" \
  "Email=$E2E_OWNER_EMAIL" "Password=$E2E_OWNER_PASSWORD")"
case "$status" in
  302|303) ok "The owner can still sign in after the upgrade." ;;
  *) die "After the upgrade, signing in answered HTTP $status. The panel is up but its accounts are not." ;;
esac

API_TOKEN="$(curl -fsS --max-time 60 -X POST -H 'Content-Type: application/json' \
  -d "$(jq -nc --arg e "$E2E_OWNER_EMAIL" --arg p "$E2E_OWNER_PASSWORD" \
        '{email:$e, password:$p, name:"live-host-proof-post-upgrade"}')" \
  "$PANEL_URL/api/v1/auth/token" | jq -er '.token')"

DEPLOYMENTS_AFTER="$(api GET /api/v1/apps | jq -r 'length')"
[ "$DEPLOYMENTS_AFTER" = "$DEPLOYMENTS_BEFORE" ] \
  || die "There were $DEPLOYMENTS_BEFORE apps before the upgrade and $DEPLOYMENTS_AFTER after it. The migration lost or invented rows."
api GET /api/v1/apps | jq -er --arg s "$APP_SLUG" '[.[] | select(.slug == $s)] | length == 1' >/dev/null \
  || die "The app created in step 4 is not there after the upgrade."
ok "$DEPLOYMENTS_AFTER app(s) survived the upgrade, including $APP_SLUG."

app_serves_over_tls || die "The deployed app stopped serving after the upgrade."
ok "https://${APP_DOMAIN}/ still answers after the upgrade."

if [ "$E2E_SKIP_NODE" != "1" ]; then
  wait_until "$W_NODE" "the node reconnected after the panel restarted" node_online
fi

# ---------------------------------------------------------------------------------------------
step "9 — teardown"
# ---------------------------------------------------------------------------------------------
if [ "$E2E_TEARDOWN" = "1" ]; then
  log "E2E_TEARDOWN=1 — removing what this run created."
  if [ "$E2E_SKIP_NODE" != "1" ]; then
    node_ssh "curl -fsSL '${E2E_REPO_RAW}/${E2E_TO_REF}/deploy/node-agent/uninstall.sh' | bash -s -- --yes" \
      || warn "Node teardown did not complete cleanly; $E2E_NODE_HOST needs re-imaging before the next run. The next run will refuse on it rather than proceed."
  fi

  # Order matters, and so does refusing to finish the job halfway. If the containers do not go, the
  # directory must STAY: step 0's precondition is the existence of $HARBORA_DIR, so removing it
  # while Traefik still holds 80/443 would let the next run start and then fail somewhere less
  # obvious. Leaving it is what makes the next run refuse honestly.
  if (cd "$COMPOSE_DIR" && docker compose down -v); then
    rm -rf "$HARBORA_DIR"
    ok "Teardown complete: this host is ready for another run."
  else
    err "docker compose down failed, so $HARBORA_DIR has deliberately been left in place."
    err "  The next run will refuse at step 0, which is correct — this host needs re-imaging."
    exit 1
  fi
else
  warn "The install is left in place (E2E_TEARDOWN is not 1)."
  warn "  The NEXT run on this host will refuse at step 0 because $HARBORA_DIR exists."
  warn "  Re-image the host, or re-run with E2E_TEARDOWN=1, or remove it by hand."
fi

rm -rf "$WORK"

echo
ok "══════════════════════════════════════════════════════════════════════════"
ok "  The live-host proof passed."
ok "    installed from   $E2E_FROM_REF, upgraded to $E2E_TO_REF"
ok "    panel            $PANEL_URL"
ok "    app + staging TLS https://${APP_DOMAIN}/"
if [ "$E2E_SKIP_NODE" = "1" ]; then
ok "    node             NOT TESTED (E2E_SKIP_NODE=1)"
else
ok "    node             $E2E_NODE_NAME on $E2E_NODE_HOST, workload confirmed on the host"
fi
ok "══════════════════════════════════════════════════════════════════════════"
