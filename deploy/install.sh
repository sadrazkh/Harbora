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
  if [ "$dns_ok" -eq 0 ]; then
    warn "DNS کامل نیست؛ نصب ادامه می‌یابد ولی SSL تا اصلاح DNS صادر نمی‌شود."
    warn "DNS is incomplete; install continues, but SSL won't issue until DNS points here."
    if [ -t 0 ]; then
      read -rp "ادامه می‌دهید؟ Continue? [Y/n] " go
      [[ "${go:-Y}" =~ ^[Nn] ]] && die "Cancelled. / لغو شد."
    fi
  fi
}

write_env() {
  mkdir -p "$COMPOSE_DIR/traefik/dynamic"
  cd "$COMPOSE_DIR"
  if [ -f .env ]; then ok "Existing .env kept (secrets preserved). / تنظیمات قبلی حفظ شد."; return; fi

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
EOF
  chmod 600 .env
  ok "Config written (secrets generated, mode 600)."
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

wait_panel() {
  log "Waiting for the panel container… / در انتظار بالا آمدن پنل…"
  for _ in $(seq 1 40); do
    local state; state="$(docker inspect -f '{{.State.Status}}' harbora-panel 2>/dev/null || echo '')"
    if [ "$state" = "running" ]; then sleep 5; ok "Panel container is running."; return 0; fi
    if [ "$state" = "exited" ]; then
      err "پنل هنگام بوت متوقف شد. / Panel exited on boot."
      err "  بررسی: cd $COMPOSE_DIR && docker compose logs panel"
      exit 1
    fi
    sleep 3
  done
  warn "Panel not running yet; check: cd $COMPOSE_DIR && docker compose logs -f panel"
}

# ---------------------------------------------------------------------------
# Post-install verification: Docker-API compat, panel route via Traefik, SSL.
# ---------------------------------------------------------------------------
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
    return 1
  fi
  ok "Traefik ↔ Docker API سازگار است."

  # 2) Panel route through Traefik (resolves the domain to localhost so DNS isn't required).
  local code
  code=$(curl -sk -o /dev/null -w '%{http_code}' --max-time 15 \
         --resolve "${PANEL_DOMAIN}:443:127.0.0.1" "https://${PANEL_DOMAIN}/healthz" 2>/dev/null || echo 000)
  case "$code" in
    200) ok "مسیر پنل از طریق Traefik سالم است. / Panel route via Traefik: OK." ;;
    404)
      err "Traefik برای پنل 404 می‌دهد — یعنی labels کانتینر پنل را نخوانده است."
      err "Traefik returns 404 for the panel — it didn't pick up the panel's labels."
      err "  بررسی / Inspect:  docker compose logs traefik | tail -50"
      err "  سپس / Then:      docker compose restart traefik"
      return 1 ;;
    000)
      warn "پاسخی از Traefik دریافت نشد (شاید هنوز در حال راه‌اندازی است). / No response from Traefik yet."
      warn "  بعداً تست کنید: curl -kI https://${PANEL_DOMAIN}/setup" ;;
    *)   warn "Panel route returned HTTP $code (expected 200). Check: docker compose logs traefik panel" ;;
  esac

  # 3) SSL certificate (needs public DNS → this server; nip.io passes automatically).
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
  ok "Installation complete. / نصب کامل شد."
  echo -e "  ${c_g}Panel:${c_0}        https://${PANEL_DOMAIN}"
  echo -e "  ${c_g}First setup:${c_0}  https://${PANEL_DOMAIN}/setup"
  echo
  echo "بعدی / Next:"
  echo "  1) اگر دامنه‌ی واقعی دارید، DNS را برای ${PANEL_DOMAIN} و *.${ROOT_DOMAIN} به این سرور بدهید."
  echo "  2) آدرس setup را باز کنید و حساب مدیر بسازید. / Open the setup URL, create the owner account."
  echo "  3) اولین اپ را بسازید و دیپلوی کنید. / Create and deploy your first app."
  echo
  echo "Manage:  cd ${COMPOSE_DIR} && docker compose [ps | logs -f panel | logs -f traefik | restart]"
}

cmd_install() {
  require_root; check_os; detect_pkg; install_prereqs; install_docker
  fetch_source; write_env; preflight_ports; start; wait_panel
  verify_install || true
  next_steps
}

cmd_update() {
  require_root; detect_pkg
  [ -d "$APP_DIR/.git" ] || die "Harbora is not installed at $APP_DIR."
  install_docker; fetch_source; start; wait_panel
  verify_install || true
  ok "Harbora updated. / به‌روزرسانی انجام شد."
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
