#!/usr/bin/env bash
#
# Harbora — easy installer. One command on a fresh Linux VPS:
#
#   curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install.sh | bash
#
# It installs every prerequisite itself (Docker, git, openssl), asks about your domain (or falls
# back to zero-DNS nip.io), tests DNS, builds the platform from source, starts it, and verifies
# the panel route + SSL — with clear bilingual (فارسی/English) messages.
#
#   ... | bash                      # install (default)
#   ... | bash -s -- update         # pull latest + rebuild (keeps your .env)
#   ... | bash -s -- uninstall      # stop & remove (prompts before deleting data)
#
# Non-interactive override:
#   PANEL_DOMAIN=panel.example.com ROOT_DOMAIN=apps.example.com ACME_EMAIL=you@example.com \
#     curl -fsSL .../install.sh | bash
#
# Idempotent: safe to re-run; an existing .env (your secrets) is never overwritten.
set -euo pipefail

HARBORA_DIR="${HARBORA_DIR:-/opt/harbora}"
REPO_URL="${REPO_URL:-https://github.com/sadrazkh/Harbora}"
REPO_BRANCH="${REPO_BRANCH:-master}"
APP_DIR="$HARBORA_DIR/app"
COMPOSE_DIR="$APP_DIR/deploy"

c_g='\033[0;32m'; c_b='\033[0;34m'; c_y='\033[1;33m'; c_r='\033[0;31m'; c_0='\033[0m'
log()  { echo -e "${c_b}➜${c_0} $*"; }
ok()   { echo -e "${c_g}✓${c_0} $*"; }
warn() { echo -e "${c_y}!${c_0} $*"; }
err()  { echo -e "${c_r}✗${c_0} $*" >&2; }
die()  { err "$*"; exit 1; }

require_root() { [ "$(id -u)" -eq 0 ] || die "Run as root (sudo bash). / با دسترسی root اجرا کنید."; }

detect_pkg() {
  if   command -v apt-get >/dev/null; then PKG=apt
  elif command -v dnf     >/dev/null; then PKG=dnf
  elif command -v yum     >/dev/null; then PKG=yum
  elif command -v apk     >/dev/null; then PKG=apk
  else die "No supported package manager (apt/dnf/yum/apk)."; fi
}

check_os() {
  [ "$(uname -s)" = "Linux" ] || die "Harbora installs on Linux only."
  case "$(uname -m)" in x86_64|aarch64|arm64) ;; *) die "Unsupported arch: $(uname -m)";; esac
  [ -f /etc/os-release ] && { . /etc/os-release; log "Detected ${PRETTY_NAME:-Linux} ($(uname -m))."; }
}

install_prereqs() {
  log "Installing prerequisites (curl, git, openssl)… / نصب پیش‌نیازها…"
  case "$PKG" in
    apt) export DEBIAN_FRONTEND=noninteractive; apt-get update -qq; apt-get install -y -qq curl git openssl ca-certificates >/dev/null;;
    dnf) dnf install -y -q curl git openssl ca-certificates >/dev/null;;
    yum) yum install -y -q curl git openssl ca-certificates >/dev/null;;
    apk) apk add --no-cache curl git openssl ca-certificates >/dev/null;;
  esac
  ok "Prerequisites ready."
}

install_docker() {
  if command -v docker >/dev/null 2>&1; then ok "Docker present ($(docker --version | awk '{print $3}' | tr -d ,)).";
  else
    log "Installing Docker… / نصب Docker…"
    curl -fsSL https://get.docker.com | sh >/dev/null
    ok "Docker installed."
  fi
  command -v systemctl >/dev/null && systemctl enable --now docker >/dev/null 2>&1 || true
  docker compose version >/dev/null 2>&1 || die "Docker Compose v2 is required (ships with modern Docker)."
}

fetch_source() {
  if [ -d "$APP_DIR/.git" ]; then
    log "Updating source… / به‌روزرسانی سورس…"
    git -C "$APP_DIR" fetch --depth 1 origin "$REPO_BRANCH" -q
    git -C "$APP_DIR" reset --hard "origin/$REPO_BRANCH" -q
  else
    log "Cloning $REPO_URL…"
    mkdir -p "$HARBORA_DIR"
    git clone --depth 1 -b "$REPO_BRANCH" "$REPO_URL" "$APP_DIR" -q
  fi
  ok "Source at $APP_DIR."
}

public_ip() {
  curl -fsS4 --max-time 5 https://api.ipify.org 2>/dev/null \
    || curl -fsS4 --max-time 5 https://ifconfig.me 2>/dev/null \
    || hostname -I 2>/dev/null | awk '{print $1}' \
    || echo "127.0.0.1"
}

resolve_ip() { # best-effort A-record lookup
  getent hosts "$1" 2>/dev/null | awk '{print $1}' | head -1
}

check_dns() { # check_dns <domain> <server-ip>
  local domain="$1" ip="$2" resolved
  resolved="$(resolve_ip "$domain")"
  if [ -z "$resolved" ]; then
    warn "DNS: '$domain' resolve نمی‌شود. / '$domain' does not resolve yet."
    warn "     یک رکورد A برای آن به $ip اضافه کنید. / Add an A record pointing to $ip."
    return 1
  elif [ "$resolved" != "$ip" ]; then
    warn "DNS: '$domain' به $resolved اشاره می‌کند، نه $ip. / points to $resolved, not this server ($ip)."
    return 1
  fi
  ok "DNS: $domain → $ip"
}

# ---------------------------------------------------------------------------
# Interactive domain / email configuration (Persian-first, env vars override)
# ---------------------------------------------------------------------------
configure_domains() {
  SERVER_IP="$(public_ip)"

  # Fully specified via env → non-interactive.
  if [ -n "${PANEL_DOMAIN:-}" ] && [ -n "${ROOT_DOMAIN:-}" ]; then
    log "Using domains from environment. / استفاده از دامنه‌های داده‌شده."
  elif [ -t 0 ]; then
    echo
    echo "── پیکربندی دامنه · Domain setup ─────────────────────────"
    echo "آیا دامنه‌ی واقعی دارید؟ (اگر نه، موقتاً از nip.io با IP سرور استفاده می‌شود)"
    read -rp "Do you have a real domain? [y/N] " has_domain
    if [[ "${has_domain:-N}" =~ ^[Yy] ]]; then
      local root=""
      while [ -z "$root" ]; do
        read -rp "دامنه‌ی اصلی (مثلاً example.com) / Root domain: " root
        root="${root,,}"; root="${root#http://}"; root="${root#https://}"; root="${root%%/*}"
      done
      read -rp "دامنه‌ی پنل [panel.${root}] / Panel domain: " _p
      PANEL_DOMAIN="${_p:-panel.${root}}"
      read -rp "دامنه‌ی اپ‌ها (زیر آن wildcard می‌خواهید) [apps.${root}] / Apps root domain: " _r
      ROOT_DOMAIN="${_r:-apps.${root}}"
    else
      PANEL_DOMAIN="panel.${SERVER_IP}.nip.io"
      ROOT_DOMAIN="apps.${SERVER_IP}.nip.io"
      ok "استفاده از nip.io (بدون نیاز به DNS): $PANEL_DOMAIN"
    fi
  else
    # Piped run with no env vars → zero-DNS defaults.
    PANEL_DOMAIN="${PANEL_DOMAIN:-panel.${SERVER_IP}.nip.io}"
    ROOT_DOMAIN="${ROOT_DOMAIN:-apps.${SERVER_IP}.nip.io}"
    log "No TTY — using zero-DNS defaults (nip.io). / بدون ترمینال تعاملی؛ پیش‌فرض nip.io."
  fi

  # ACME email (blank → admin@panel-domain).
  if [ -z "${ACME_EMAIL:-}" ]; then
    if [ -t 0 ]; then
      read -rp "ایمیل Let's Encrypt [admin@${PANEL_DOMAIN}] / ACME email: " _e
      ACME_EMAIL="${_e:-admin@${PANEL_DOMAIN}}"
    else
      ACME_EMAIL="admin@${PANEL_DOMAIN}"
    fi
  fi

  # DNS sanity check — warn loudly, but let the user continue (they may fix DNS later).
  echo
  log "بررسی DNS… / Checking DNS…"
  local dns_ok=1
  check_dns "$PANEL_DOMAIN" "$SERVER_IP" || dns_ok=0
  check_dns "test.$ROOT_DOMAIN" "$SERVER_IP" || dns_ok=0
  # The node channel's own host. It needs a record of its own on a real domain (nip.io answers for
  # it already), and without one no node can ever open its channel — so it is asked for here, with
  # the others, rather than discovered when the first node fails to connect.
  check_dns "nodes.$PANEL_DOMAIN" "$SERVER_IP" || {
    dns_ok=0
    warn "     nodes.$PANEL_DOMAIN فقط برای کانال نودهاست. / used only by the node channel (mTLS)."
  }
  if [ "$dns_ok" -eq 0 ]; then
    warn "DNS کامل نیست؛ نصب ادامه می‌یابد ولی SSL تا اصلاح DNS صادر نمی‌شود."
    warn "DNS is incomplete; install continues, but SSL won't issue until DNS points here."
    if [ -t 0 ]; then
      read -rp "ادامه می‌دهید؟ Continue? [Y/n] " go
      [[ "${go:-Y}" =~ ^[Nn] ]] && die "Cancelled. / لغو شد."
    fi
  fi
}

# Adds a key to .env only if it is absent. Upgrades depend on this: a .env written by an older
# installer has no HARBORA_MASTER_KEY, and since the panel now fails closed without one it would
# refuse to start after an update with no obvious reason. Backfilling is what makes `update` safe.
backfill_env() {
  local key="$1" value="$2"
  if grep -q "^${key}=" .env 2>/dev/null && [ -n "$(sed -n "s/^${key}=//p" .env | head -1)" ]; then
    return 1
  fi
  grep -v "^${key}=" .env > .env.tmp 2>/dev/null || true
  printf '%s=%s
' "$key" "$value" >> .env.tmp
  mv .env.tmp .env
  chmod 600 .env
  return 0
}

repair_env() {
  local repaired=0
  backfill_env HARBORA_MASTER_KEY "$(openssl rand -base64 32)" && {
    warn "HARBORA_MASTER_KEY was missing — generated one. / کلید اصلی نبود؛ ساخته شد."
    repaired=1; }
  backfill_env POSTGRES_USER "harbora" && repaired=1
  backfill_env POSTGRES_DB "harbora" && repaired=1

  # Object storage, added after the first installs existed. Derived from the panel's own hostname so
  # an upgrade needs no answers: "panel.example.com" becomes "s3.example.com", and the nip.io case
  # works the same way. Generated once and then left alone — regenerating the root password on every
  # run would orphan every bucket key already issued.
  local _panel
  _panel="$(sed -n 's/^PANEL_DOMAIN=//p' .env | head -1)"
  backfill_env S3_DOMAIN "s3.${_panel#panel.}" && repaired=1
  backfill_env MINIO_ROOT_USER "harbora" && repaired=1
  backfill_env MINIO_ROOT_PASSWORD "$(openssl rand -hex 24)" && {
    warn "Generated the object-storage root password. / رمز ریشهٔ فضای ذخیره‌سازی ساخته شد."
    repaired=1; }

  # The node channel gets a host name of its own, and this is not decoration. Traefik resolves TLS
  # options per SNI host name: two routers claiming one host with different options make it log a
  # conflict and fall back to the DEFAULT options, which ask for no client certificate. An mTLS
  # router sharing PANEL_DOMAIN with the panel's own catch-all therefore stops requiring the
  # credential it exists to check, and every node gets a 401 it cannot do anything about.
  #
  # Derived from the panel's domain, so nip.io needs no DNS at all (nodes.panel.<ip>.nip.io resolves
  # by construction) and a real domain needs exactly one more A record. Under ROOT_DOMAIN it would
  # have needed none — but apps are published as Host(`<name>.$ROOT_DOMAIN`) into the same file
  # provider, so one tenant naming an app "nodes" would re-create the conflict from the outside.
  backfill_env NODE_DOMAIN "nodes.${_panel}" && repaired=1

  local _node
  _node="$(sed -n 's/^NODE_DOMAIN=//p' .env | head -1)"

  # What an enrolled node keeps calling: the mTLS host, not the panel's. Enrollment itself happens on
  # the panel's host — a node has no certificate yet — and the enrollment response hands this back.
  backfill_env NodeAgent__PublicUrl "https://${_node}" && repaired=1

  # NodeAgent__TrustForwardedClientCertificate is deliberately NOT written here. It is the operator
  # asserting that Traefik requires and overwrites the certificate header, and repair_env runs before
  # `start` — so writing it here would boot the panel trusting an inbound header for the whole build
  # and wait window, with nothing overwriting it. enable_node_channel writes it once the router that
  # makes it true is on disk, and restarts the panel then.

  [ "$repaired" -eq 1 ] && ok "Repaired .env (existing values untouched)." || true
}

write_env() {
  mkdir -p "$COMPOSE_DIR/traefik/dynamic"
  cd "$COMPOSE_DIR"
  if [ -f .env ]; then
    ok "Existing .env kept (secrets preserved). / تنظیمات قبلی حفظ شد."
    repair_env
    return
  fi

  configure_domains

  log "Writing configuration (.env)… / نوشتن پیکربندی…"
  cat > .env <<EOF
PANEL_DOMAIN=${PANEL_DOMAIN}
ROOT_DOMAIN=${ROOT_DOMAIN}
ACME_EMAIL=${ACME_EMAIL}
POSTGRES_USER=harbora
POSTGRES_DB=harbora
POSTGRES_PASSWORD=$(openssl rand -hex 24)
HARBORA_MASTER_KEY=$(openssl rand -base64 32)
S3_DOMAIN=s3.${PANEL_DOMAIN#panel.}
MINIO_ROOT_USER=harbora
MINIO_ROOT_PASSWORD=$(openssl rand -hex 24)
EOF
  chmod 600 .env
  ok "Config written (secrets generated, mode 600)."

  # Settings that are derived rather than asked for live in repair_env, and a fresh .env goes through
  # it too. One path, so an install and an upgrade end up with the same keys — the alternative is a
  # setting that only exists on installs that happen to have been upgraded once.
  repair_env
}

preflight_ports() {
  # Ports 80/443 must be free — unless it's our own Traefik already running (re-run/update).
  if docker ps --format '{{.Names}}' 2>/dev/null | grep -q '^harbora-traefik$'; then return; fi
  for p in 80 443; do
    if ss -ltn 2>/dev/null | awk '{print $4}' | grep -qE "[:.]${p}\$"; then
      warn "پورت ${p} توسط برنامه‌ی دیگری اشغال است (مثلاً nginx/apache). / Port ${p} is in use by another program."
      warn "     آن را متوقف کنید: systemctl stop nginx  (یا apache2) — سپس نصب را دوباره اجرا کنید."
    fi
  done
}

start() {
  cd "$COMPOSE_DIR"
  log "Building and starting Harbora (first build takes a few minutes)… / ساخت و اجرای Harbora…"
  docker compose up -d --build
  ok "Containers started."
}

# Waits for the panel to be READY, not merely started. "running" happens in about a second, while
# migrations and seeding take the better part of a minute — checking in between made the installer
# announce a broken install right after a perfectly good update.
wait_panel() {
  log "Waiting for the panel to become ready… / در انتظار آماده شدن پنل…"
  local state health
  for _ in $(seq 1 60); do
    state="$(docker inspect -f '{{.State.Status}}' harbora-panel 2>/dev/null || echo '')"
    health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' harbora-panel 2>/dev/null || echo none)"

    if [ "$state" = "exited" ] || [ "$state" = "restarting" ]; then
      err "پنل هنگام بوت متوقف شد. / Panel failed to boot."
      err "  دلیل / Reason:"
      docker logs --tail 15 harbora-panel 2>&1 | sed 's/^/     /'
      err "  تشخیص / Diagnose:  harbora doctor"
      exit 1
    fi

    case "$health" in
      healthy) ok "Panel is ready."; return 0 ;;
      # No healthcheck in this image (older build): fall back to running + a grace period.
      none)    if [ "$state" = "running" ]; then sleep 20; ok "Panel container is running."; return 0; fi ;;
    esac
    sleep 3
  done
  warn "Panel not ready yet; check: harbora logs panel"
}

# ---------------------------------------------------------------------------
# The node channel: the mTLS router Traefik needs before any node can enroll.
#
# Two artifacts, in this order and never the other way round:
#
#   1. traefik/dynamic/node-ca.pem  — the CA the panel signs node certificates with.
#   2. traefik/dynamic/node-agent.yml — rendered from traefik/node-agent.yml.template, which names
#      NODE_DOMAIN (the channel's own host) and points clientAuth at (1).
#
# The order matters. Traefik falls back to its DEFAULT TLS options when a named one cannot be built,
# and the default asks for no client certificate — so a router placed before its CA file exists
# publishes the channel unauthenticated instead of refusing it. That is also why a CA that goes
# missing under an already-rendered router is not merely refused but moved aside: leaving the router
# behind is the same unauthenticated state, arrived at from the other direction.
#
# Neither generated file is tracked in git, so `git reset --hard` in the update path leaves both
# alone.
# ---------------------------------------------------------------------------
node_template()  { echo "$COMPOSE_DIR/traefik/node-agent.yml.template"; }
node_rendered()  { echo "$COMPOSE_DIR/traefik/dynamic/node-agent.yml"; }
node_disabled()  { echo "$COMPOSE_DIR/traefik/node-agent.yml.disabled"; }
node_ca_file()   { echo "$COMPOSE_DIR/traefik/dynamic/node-ca.pem"; }

# Asks the panel image for the CA, creating it if this install has never had one. `compose run`
# starts a one-off container, so this works whether or not the running panel is healthy — the same
# reason every other database-side admin command is invoked that way.
export_node_ca() {
  local target tmp noise
  target="$(node_ca_file)"; tmp="$(mktemp)"; noise="$(mktemp)"

  # Only the certificate is kept: `compose run` writes its own progress lines, and everything but
  # the PEM would be a Traefik parse error rather than a trust anchor.
  if (cd "$COMPOSE_DIR" && docker compose run --rm -T panel admin node-ca 2>"$noise") \
       | sed -n '/-----BEGIN CERTIFICATE-----/,/-----END CERTIFICATE-----/p' > "$tmp" \
     && grep -q -- "-----END CERTIFICATE-----" "$tmp"; then
    # Only when it changed: rewriting the file makes Traefik reload, and a reload on every install
    # run of an unchanged fleet is churn for nothing.
    if cmp -s "$tmp" "$target" 2>/dev/null; then rm -f "$tmp"; else
      mv "$tmp" "$target"; chmod 644 "$target"      # a CA certificate is public; Traefik must read it
    fi
    rm -f "$noise"
    return 0
  fi

  # Say why. "Could not export the CA" with no reason sends the operator to the wrong place.
  tail -5 "$noise" 2>/dev/null | sed 's/^/     /' >&2 || true
  rm -f "$tmp" "$noise"
  return 1
}

# Substitution in bash rather than sed or envsubst: a domain needs no escaping this way, and envsubst
# is not installed on a minimal server. The pattern is quoted so it is matched literally.
NODE_DOMAIN_PLACEHOLDER='{{NODE_DOMAIN}}'

render_node_router() {
  local domain="$1" template rendered tmp line
  template="$(node_template)"; rendered="$(node_rendered)"; tmp="$(mktemp)"

  while IFS= read -r line || [ -n "$line" ]; do
    printf '%s\n' "${line//"$NODE_DOMAIN_PLACEHOLDER"/$domain}"
  done < "$template" > "$tmp"

  if cmp -s "$tmp" "$rendered" 2>/dev/null; then rm -f "$tmp"; return 1; fi
  mv "$tmp" "$rendered"; chmod 644 "$rendered"
  return 0
}

enable_node_channel() {
  cd "$COMPOSE_DIR"; . ./.env
  [ -f "$(node_template)" ] || { warn "No node channel template in this source tree; skipping."; return 0; }

  log "پیکربندی کانال نودها… / Configuring the node channel…"

  # The rule is "no router without a CA file", not "no router without a fresh export": a transient
  # Docker failure on an update must not take a working fleet's routing away, and the CA on disk is
  # the same CA it was five minutes ago.
  if export_node_ca; then :
  elif [ -s "$(node_ca_file)" ]; then
    warn "CA نودها تازه‌سازی نشد؛ فایل موجود حفظ شد. / Could not refresh the node CA; keeping $(node_ca_file)."
  else
    # Refusing to render is only half of it. On a host where an earlier run DID render the router and
    # the CA file has since been lost, walking away leaves a live router whose named TLS option
    # Traefik cannot build — and an option it cannot build is the default one, which asks for no
    # client certificate. That is the same unauthenticated channel the ordering above exists to
    # prevent, so the orphan is moved out of the watched directory. Traefik reloads on the removal.
    if [ -f "$(node_rendered)" ]; then
      mv -f "$(node_rendered)" "$(node_disabled)"
      err "روتر نودها بدون CA رها شده بود؛ غیرفعال شد. / An orphaned node router was moved aside:"
      err "    $(node_disabled)"
    fi
    err "گواهی CA نودها استخراج نشد؛ مسیر mTLS نصب نشد."
    err "Could not export the node CA — the node mTLS router was NOT installed."
    err "  یعنی هیچ نودی ثبت نمی‌شود. / No node can enroll until this is fixed."
    err "  بعداً / Later:  harbora node-ca | sed -n '/-----BEGIN CERTIFICATE-----/,/-----END CERTIFICATE-----/p' > $(node_ca_file)"
    err "                 cd $COMPOSE_DIR && bash install.sh update"
    return 1
  fi

  local node_domain="${NODE_DOMAIN:-nodes.$PANEL_DOMAIN}"
  if render_node_router "$node_domain"; then
    ok "کانال نودها آماده است ($node_domain). / Node channel configured for $node_domain."
  else
    ok "کانال نودها از قبل به‌روز بود. / Node channel already up to date."
  fi

  # Only now. The flag tells the panel to believe X-Forwarded-Tls-Client-Cert, and what makes that
  # safe is the router above: it requires a client certificate, so passTLSClientCert always has a
  # peer certificate to overwrite the header with. (It does not strip an inbound header when there
  # is none — which is why the flag must never be true while the router is absent.) Written through
  # backfill_env, so an operator who set it to false keeps their answer.
  if backfill_env NodeAgent__TrustForwardedClientCertificate "true"; then
    log "اعمال تنظیم گواهی نودها روی پنل… / Applying the node certificate setting to the panel…"
    # `up -d`, not `restart`: the value is an environment variable baked in at container creation,
    # and `restart` re-runs the same container with the same environment it already had.
    docker compose up -d panel >/dev/null
    wait_panel
  fi
}

# ---------------------------------------------------------------------------
# Post-install verification: Docker-API compat, panel route via Traefik, the node channel, SSL.
#
# Set by verify_install when something it checked is genuinely broken, and read by the closing
# message. Without it the installer printed "Installation complete" over the top of its own errors.
# ---------------------------------------------------------------------------
VERIFY_FAILED=0

verify_install() {
  cd "$COMPOSE_DIR"; . ./.env
  echo
  log "تأیید نصب… / Verifying the installation…"

  # 1) Traefik ↔ Docker API compatibility (the classic Docker 27+/29 failure).
  if docker logs harbora-traefik 2>&1 | grep -qi "is too old"; then
    err "Traefik با نسخه‌ی Docker این سرور سازگار نیست (خطای Docker API version)."
    err "Traefik can't talk to this Docker version (API version error)."
    err "  رفع / Fix:  cd $COMPOSE_DIR && docker compose pull traefik && docker compose up -d traefik"
    err "  (این نسخه‌ی Harbora از traefik:v3.6 استفاده می‌کند که سازگار است.)"
    VERIFY_FAILED=1
    return 1
  fi
  ok "Traefik ↔ Docker API سازگار است."

  # 2) Panel route through Traefik (resolves the domain to localhost so DNS isn't required).
  # Retry: right after `compose up` Traefik may not have re-read the recreated panel's labels
  # yet, and a single early probe reports a scary false failure on a healthy install.
  local code=000 attempt
  for attempt in $(seq 1 12); do
  code=$(curl -sk -o /dev/null -w '%{http_code}' --max-time 15 \
         --resolve "${PANEL_DOMAIN}:443:127.0.0.1" "https://${PANEL_DOMAIN}/healthz" 2>/dev/null || echo 000)
    [ "$code" = "200" ] && break
    sleep 5
  done
  case "$code" in
    200) ok "مسیر پنل از طریق Traefik سالم است. / Panel route via Traefik: OK." ;;
    404)
      err "Traefik برای پنل 404 می‌دهد — یعنی labels کانتینر پنل را نخوانده است."
      err "Traefik returns 404 for the panel — it didn't pick up the panel's labels."
      err "  بررسی / Inspect:  docker compose logs traefik | tail -50"
      err "  سپس / Then:      docker compose restart traefik"
      VERIFY_FAILED=1
      return 1 ;;
    000)
      warn "پاسخی از Traefik دریافت نشد (شاید هنوز در حال راه‌اندازی است). / No response from Traefik yet."
      warn "  بعداً تست کنید: curl -kI https://${PANEL_DOMAIN}/setup" ;;
    *)   warn "Panel route returned HTTP $code (expected 200). Check: docker compose logs traefik panel" ;;
  esac

  # 3) The node enrollment endpoint, on the PANEL's host — which is where it belongs: a node has no
  # certificate when it enrolls, so it cannot be asked for one, and the panel's own router serves it.
  # A correct install refuses an empty request with a JSON error, because the route is anonymous by
  # design and "no enrollment token was supplied" means it is being served. A 404 means it is not,
  # and every `harbora node install` an operator is about to run would fail with the same 404 half an
  # hour from now instead of here.
  local enroll=000
  enroll=$(curl -sk -o /dev/null -w '%{http_code}' --max-time 15 -X POST \
           -H 'Content-Type: application/json' -d '{}' \
           --resolve "${PANEL_DOMAIN}:443:127.0.0.1" \
           "https://${PANEL_DOMAIN}/api/node-agent/v1/enroll" 2>/dev/null || echo 000)
  case "$enroll" in
    401|400|422) ok "مسیر ثبت نود پاسخ می‌دهد. / Node enrollment endpoint answers (HTTP $enroll)." ;;
    404)
      err "مسیر ثبت نود 404 می‌دهد — یعنی روتر پنل این مسیر را سرو نمی‌کند."
      err "The node enrollment route returns 404 — the panel is not serving it."
      err "  بررسی / Inspect:  docker compose logs traefik | grep -i node"
      err "  و / and:          docker compose logs panel | tail -50"
      err "  بازسازی / Rebuild: bash $COMPOSE_DIR/install.sh update"
      VERIFY_FAILED=1 ;;
    000)
      warn "مسیر ثبت نود پاسخی نداد. / No response from the node enrollment route yet." ;;
    *)   warn "Node enrollment endpoint returned HTTP $enroll (expected a JSON refusal). Check: docker compose logs traefik panel" ;;
  esac

  # 4) The node channel's own host — the check that can actually fail, and the one the enrollment
  # probe above cannot make: the panel's catch-all answers /enroll whether or not the mTLS router
  # exists, so a 401 there proves nothing about the channel.
  #
  # NODE_DOMAIN carries exactly one router and it requires a client certificate. So a curl WITHOUT
  # one must be refused during the TLS handshake — no HTTP status at all. Any HTTP status means one
  # of the two failures this router has:
  #   404 → the router did not load (missing file, bad YAML, Traefik never reloaded);
  #   anything else → it loaded but is not enforcing its TLS options, which is what Traefik does when
  #   two routers claim one host name with different options.
  # Both leave the channel open to a caller with no credential, which is the whole point of it.
  local node_domain="${NODE_DOMAIN:-nodes.$PANEL_DOMAIN}"

  if [ ! -f "$(node_rendered)" ]; then
    err "روتر mTLS نودها اصلاً ساخته نشد. / The node mTLS router was never rendered."
    err "  فایل موجود نیست / Missing: $(node_rendered)"
    err "  یعنی هیچ نودی وصل نمی‌شود. / No node can open its channel; enrollment alone is not enough."
    err "  دلیل معمول / Usual cause: the node CA could not be exported (see the errors above)."
    err "  بازسازی / Rebuild: bash $COMPOSE_DIR/install.sh update"
    VERIFY_FAILED=1
  else
    if ! grep -q "Host(\`${node_domain}\`)" "$(node_rendered)"; then
      err "روتر نودها برای میزبان دیگری تنظیم شده است. / The node router names a different host."
      err "  انتظار / Expected: $node_domain   —   $(node_rendered)"
      err "  رفع / Fix:  bash $COMPOSE_DIR/install.sh update"
      VERIFY_FAILED=1
    fi

    # What a node is handed at enrollment and calls forever after. If it names anything but the
    # host the router is on, every node enrolls successfully and then never connects.
    if [ "${NodeAgent__PublicUrl:-}" != "https://${node_domain}" ]; then
      err "NodeAgent__PublicUrl با میزبان روتر نودها یکی نیست. / does not match the node router's host."
      err "  در .env / In .env: ${NodeAgent__PublicUrl:-<unset>}"
      err "  انتظار / Expected: https://${node_domain}"
      err "  نودها ثبت می‌شوند ولی هرگز وصل نمی‌شوند. / Nodes would enrol and then never connect."
      VERIFY_FAILED=1
    fi

    local chan=000 chan_rc=0
    chan=$(curl -sk -o /dev/null -w '%{http_code}' --max-time 15 \
           --resolve "${node_domain}:443:127.0.0.1" \
           "https://${node_domain}/api/node-agent/v1/channel" 2>/dev/null) || chan_rc=$?
    if [ "$chan" = "000" ] && [ "$chan_rc" -ne 0 ]; then
      ok "کانال نودها بدون گواهی کلاینت رد می‌شود (mTLS فعال است). / Node channel refuses a connection with no client certificate — mTLS is enforced."
    elif [ "$chan" = "404" ]; then
      err "کانال نودها 404 می‌دهد — روتر mTLS بارگذاری نشده است."
      err "The node channel returns 404 — the mTLS router did not load."
      err "  بررسی / Inspect:  docker compose logs traefik | grep -i node"
      err "  فایل‌ها / Files:   $(node_ca_file)"
      err "                    $(node_rendered)"
      err "  سپس / Then:      docker compose restart traefik"
      VERIFY_FAILED=1
    else
      err "کانال نودها بدون گواهی کلاینت پاسخ داد (HTTP $chan) — mTLS اعمال نمی‌شود."
      err "The node channel answered (HTTP $chan) WITHOUT a client certificate — mTLS is not being enforced."
      err "  معمولاً یعنی دو روتر یک نام میزبان را ادعا کرده‌اند و Traefik به گزینه‌های پیش‌فرض برگشته است."
      err "  Usually two routers claim one host name, so Traefik fell back to the default TLS options."
      err "  بررسی / Inspect:  docker compose logs traefik | grep -i 'tls options'"
      err "  هیچ نودی احراز هویت نمی‌شود تا این درست شود. / Until this is fixed no node is authenticated."
      VERIFY_FAILED=1
    fi
  fi

  # 5) SSL certificate (needs public DNS → this server; nip.io passes automatically).
  if curl -s -o /dev/null --max-time 20 "https://${PANEL_DOMAIN}/healthz" 2>/dev/null; then
    ok "گواهی SSL معتبر صادر شده است. / Valid SSL certificate issued."
  else
    warn "گواهی SSL هنوز صادر نشده. / SSL certificate not issued yet."
    warn "  دلایل رایج / Common causes:"
    warn "   - DNS هنوز به IP این سرور اشاره نمی‌کند / DNS not pointing at this server"
    warn "   - پورت 80 از اینترنت باز نیست (برای HTTP challenge لازم است) / Port 80 not reachable"
    warn "  لاگ ACME:  docker logs harbora-traefik 2>&1 | grep -i acme | tail -20"
    docker logs harbora-traefik 2>&1 | grep -i "acme\|certificate" | tail -5 || true
  fi
}

next_steps() {
  cd "$COMPOSE_DIR"; . ./.env
  echo
  # "Installation complete" printed over the top of verify_install's own errors was how an install
  # with no node router at all still ended in a green tick.
  if [ "${VERIFY_FAILED:-0}" -eq 1 ]; then
    err "نصب با خطا تمام شد — موارد قرمز بالا هنوز کار نمی‌کنند."
    err "Installation finished WITH ERRORS — the red items above are not working yet."
  else
    ok "Installation complete. / نصب کامل شد."
  fi
  echo -e "  ${c_g}Panel:${c_0}        https://${PANEL_DOMAIN}"
  echo -e "  ${c_g}First setup:${c_0}  https://${PANEL_DOMAIN}/setup"
  echo
  echo "بعدی / Next:"
  echo "  1) اگر دامنه‌ی واقعی دارید، DNS را برای ${PANEL_DOMAIN}، ${NODE_DOMAIN:-nodes.${PANEL_DOMAIN}} و *.${ROOT_DOMAIN} به این سرور بدهید."
  echo "     (${NODE_DOMAIN:-nodes.${PANEL_DOMAIN}} is the node channel's own host — nodes cannot connect without it.)"
  echo "  2) آدرس setup را باز کنید و حساب مدیر بسازید. / Open the setup URL, create the owner account."
  echo "  3) اولین اپ را بسازید و دیپلوی کنید. / Create and deploy your first app."
  echo
  echo "Manage:  harbora status | harbora logs | harbora restart"
  echo "Trouble: harbora doctor          (checks config, containers, ports)"
  echo "Locked out? harbora reset-password"
}

# Puts the `harbora` admin command on PATH. It is the documented way back in when the panel is
# unreachable, so it is installed (and refreshed on update) rather than left in the repo.
install_command() {
  install -m 0755 "$COMPOSE_DIR/harbora" /usr/local/bin/harbora
  ok "Installed the 'harbora' command. Try: harbora doctor"
}

cmd_install() {
  require_root; check_os; detect_pkg; install_prereqs; install_docker
  fetch_source; write_env; install_command; preflight_ports; start; wait_panel
  enable_node_channel || true
  verify_install || true
  next_steps
}

cmd_update() {
  require_root; detect_pkg
  [ -d "$APP_DIR/.git" ] || die "Harbora is not installed at $APP_DIR."
  install_docker; fetch_source
  # An older .env may predate settings the new code requires — repair before starting, or the
  # update looks like it "broke everything".
  (cd "$COMPOSE_DIR" && repair_env)
  install_command; start; wait_panel
  # Same treatment as a fresh install: the template moved out of the watched directory, so an
  # install that predates it has no rendered router at all, and one that has it may be on a domain
  # that changed since.
  enable_node_channel || true
  verify_install || true
  if [ "${VERIFY_FAILED:-0}" -eq 1 ]; then
    err "به‌روزرسانی با خطا تمام شد — موارد قرمز بالا را ببینید."
    err "Harbora updated, but with ERRORS — see the red items above."
  else
    ok "Harbora updated. / به‌روزرسانی انجام شد."
  fi
  echo "  If anything looks wrong:  harbora doctor"
}

cmd_uninstall() {
  require_root
  [ -d "$COMPOSE_DIR" ] || die "Nothing to uninstall at $COMPOSE_DIR."
  cd "$COMPOSE_DIR"
  warn "This stops Harbora and removes its containers. / Harbora متوقف و کانتینرها حذف می‌شوند."
  local del="N"; [ -t 0 ] && read -rp "دیتاها هم حذف شوند؟ Also delete volumes (databases, apps' data)? [y/N] " del
  if [ "${del:-N}" = "y" ] || [ "${del:-N}" = "Y" ]; then docker compose down -v; warn "Volumes deleted.";
  else docker compose down; ok "Containers removed; data volumes kept."; fi
  echo "Source + config remain in ${HARBORA_DIR}. Remove manually if you're done."
}

case "${1:-install}" in
  install)   cmd_install ;;
  update)    cmd_update ;;
  uninstall) cmd_uninstall ;;
  *) die "Unknown command '$1'. Use: install | update | uninstall" ;;
esac
