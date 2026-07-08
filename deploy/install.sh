#!/usr/bin/env bash
#
# Harbora — easy installer. One command on a fresh Linux VPS:
#
#   curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install.sh | bash
#
# It installs every prerequisite itself (Docker, git, openssl), fetches the source, generates a
# config with sensible defaults, builds the platform from source, and starts it.
#
#   ... | bash                      # install (default)
#   ... | bash -s -- update         # pull latest + rebuild
#   ... | bash -s -- uninstall      # stop & remove (prompts before deleting data)
#
# Override defaults with env vars, e.g.:
#   PANEL_DOMAIN=panel.example.com ROOT_DOMAIN=apps.example.com ACME_EMAIL=you@example.com \
#     curl -fsSL .../install.sh | bash
#
# Safe to re-run: an existing .env (with your secrets) is never overwritten.
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
die()  { echo -e "${c_r}✗ $*${c_0}" >&2; exit 1; }

require_root() { [ "$(id -u)" -eq 0 ] || die "Run as root (or: sudo bash)."; }

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
  log "Installing prerequisites (curl, git, openssl)…"
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
    log "Installing Docker…"
    curl -fsSL https://get.docker.com | sh >/dev/null
    ok "Docker installed."
  fi
  command -v systemctl >/dev/null && systemctl enable --now docker >/dev/null 2>&1 || true
  docker compose version >/dev/null 2>&1 || die "Docker Compose v2 is required (ships with modern Docker)."
}

fetch_source() {
  if [ -d "$APP_DIR/.git" ]; then
    log "Updating source…"; git -C "$APP_DIR" fetch --depth 1 origin "$REPO_BRANCH" -q; git -C "$APP_DIR" reset --hard "origin/$REPO_BRANCH" -q
  else
    log "Cloning $REPO_URL…"; mkdir -p "$HARBORA_DIR"; git clone --depth 1 -b "$REPO_BRANCH" "$REPO_URL" "$APP_DIR" -q
  fi
  ok "Source at $APP_DIR."
}

public_ip() {
  curl -fsS4 --max-time 5 https://api.ipify.org 2>/dev/null \
    || curl -fsS4 --max-time 5 https://ifconfig.me 2>/dev/null \
    || hostname -I 2>/dev/null | awk '{print $1}' \
    || echo "127.0.0.1"
}

rand() { openssl rand "$1" 2>/dev/null | head -c 200; }

write_env() {
  mkdir -p "$COMPOSE_DIR/traefik/dynamic"
  cd "$COMPOSE_DIR"
  if [ -f .env ]; then ok "Existing .env kept (secrets preserved)."; return; fi

  local ip; ip="$(public_ip)"
  # Smart defaults: nip.io resolves *.<ip>.nip.io to the server IP, so it works with no DNS setup.
  PANEL_DOMAIN="${PANEL_DOMAIN:-panel.${ip}.nip.io}"
  ROOT_DOMAIN="${ROOT_DOMAIN:-apps.${ip}.nip.io}"
  ACME_EMAIL="${ACME_EMAIL:-admin@${PANEL_DOMAIN}}"

  # Prompt only when attached to a terminal and the value wasn't provided via env.
  if [ -t 0 ]; then
    read -rp "Panel domain [$PANEL_DOMAIN]: " _p; PANEL_DOMAIN="${_p:-$PANEL_DOMAIN}"
    read -rp "Apps root domain [$ROOT_DOMAIN]: " _r; ROOT_DOMAIN="${_r:-$ROOT_DOMAIN}"
    read -rp "Let's Encrypt email [$ACME_EMAIL]: " _e; ACME_EMAIL="${_e:-$ACME_EMAIL}"
  fi

  log "Writing configuration (.env)…"
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

start() {
  cd "$COMPOSE_DIR"
  log "Building and starting Harbora (first build can take a few minutes)…"
  docker compose up -d --build
  ok "Harbora is starting."
}

wait_healthy() {
  log "Waiting for the panel container to come up…"
  for _ in $(seq 1 40); do
    local state; state="$(docker inspect -f '{{.State.Status}}' harbora-panel 2>/dev/null || echo '')"
    if [ "$state" = "running" ]; then ok "Panel container is running."; sleep 3; return; fi
    if [ "$state" = "exited" ]; then die "Panel exited on boot. Inspect: cd $COMPOSE_DIR && docker compose logs panel"; fi
    sleep 3
  done
  warn "Panel not running yet; check: cd $COMPOSE_DIR && docker compose logs -f panel"
}

next_steps() {
  cd "$COMPOSE_DIR"; . ./.env
  echo
  ok "Installation complete."
  echo -e "  ${c_g}Panel:${c_0}        https://${PANEL_DOMAIN}"
  echo -e "  ${c_g}First setup:${c_0}  https://${PANEL_DOMAIN}/setup"
  echo
  echo "Next:"
  echo "  1) Point DNS for ${PANEL_DOMAIN} and *.${ROOT_DOMAIN} at this server (skip if using the nip.io default)."
  echo "  2) Open the setup URL and create your owner account."
  echo "  3) Create an app and deploy — or a tenant workspace to resell to a customer."
  echo
  echo "Manage:  cd ${COMPOSE_DIR} && docker compose [ps | logs -f panel | restart]"
}

cmd_install() { require_root; check_os; detect_pkg; install_prereqs; install_docker; fetch_source; write_env; start; wait_healthy; next_steps; }

cmd_update() {
  require_root; detect_pkg
  [ -d "$APP_DIR/.git" ] || die "Harbora is not installed at $APP_DIR."
  fetch_source; start; wait_healthy
  ok "Harbora updated."
}

cmd_uninstall() {
  require_root
  [ -d "$COMPOSE_DIR" ] || die "Nothing to uninstall at $COMPOSE_DIR."
  cd "$COMPOSE_DIR"
  warn "This stops Harbora and removes its containers."
  local del="N"; [ -t 0 ] && read -rp "Also delete volumes (databases, backups, apps' data)? [y/N] " del
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
