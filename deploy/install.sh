#!/usr/bin/env bash
#
# Harbora installer — one command to stand up the platform on a fresh Linux VPS.
#
#   curl -fsSL https://get.harbora.dev/install.sh | bash
#   curl -fsSL https://get.harbora.dev/install.sh | bash -s -- update
#   curl -fsSL https://get.harbora.dev/install.sh | bash -s -- uninstall
#
# Safe to re-run: it never overwrites an existing .env (so secrets are stable) and only
# creates what is missing.
set -euo pipefail

HARBORA_DIR="${HARBORA_DIR:-/opt/harbora}"
REPO_RAW="${REPO_RAW:-https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy}"
COMPOSE="docker compose"

c_green='\033[0;32m'; c_blue='\033[0;34m'; c_yellow='\033[1;33m'; c_red='\033[0;31m'; c_reset='\033[0m'
log()  { echo -e "${c_blue}➜${c_reset} $*"; }
ok()   { echo -e "${c_green}✓${c_reset} $*"; }
warn() { echo -e "${c_yellow}!${c_reset} $*"; }
die()  { echo -e "${c_red}✗ $*${c_reset}" >&2; exit 1; }

require_root() {
  [ "$(id -u)" -eq 0 ] || die "Please run as root (or via sudo)."
}

check_os() {
  [ "$(uname -s)" = "Linux" ] || die "Harbora installs on Linux only."
  if [ -f /etc/os-release ]; then . /etc/os-release; log "Detected ${PRETTY_NAME:-Linux}."; fi
  case "$(uname -m)" in
    x86_64|aarch64|arm64) ;;
    *) die "Unsupported architecture: $(uname -m)";;
  esac
}

install_docker() {
  if command -v docker >/dev/null 2>&1; then ok "Docker present ($(docker --version | cut -d' ' -f3 | tr -d ,))."; return; fi
  log "Installing Docker…"
  curl -fsSL https://get.docker.com | sh
  systemctl enable --now docker || true
  ok "Docker installed."
}

check_compose() {
  docker compose version >/dev/null 2>&1 || die "Docker Compose v2 is required (comes with modern Docker)."
}

rand() { head -c "$1" /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "$1"; }

scaffold() {
  log "Preparing ${HARBORA_DIR}…"
  mkdir -p "$HARBORA_DIR/traefik/dynamic"
  cd "$HARBORA_DIR"

  # Fetch compose file (unless running from a local checkout that already has it).
  if [ ! -f docker-compose.yml ]; then
    curl -fsSL "$REPO_RAW/docker-compose.yml" -o docker-compose.yml
  fi

  if [ ! -f .env ]; then
    log "Generating configuration (.env)…"
    PANEL_DOMAIN="${PANEL_DOMAIN:-}"
    ACME_EMAIL="${ACME_EMAIL:-}"
    ROOT_DOMAIN="${ROOT_DOMAIN:-}"
    if [ -z "$PANEL_DOMAIN" ]; then read -rp "Panel domain (e.g. panel.example.com): " PANEL_DOMAIN; fi
    if [ -z "$ROOT_DOMAIN" ];  then read -rp "Apps root domain (e.g. example.com): " ROOT_DOMAIN; fi
    if [ -z "$ACME_EMAIL" ];   then read -rp "Email for Let's Encrypt: " ACME_EMAIL; fi

    cat > .env <<EOF
PANEL_DOMAIN=${PANEL_DOMAIN}
ROOT_DOMAIN=${ROOT_DOMAIN}
ACME_EMAIL=${ACME_EMAIL}
POSTGRES_USER=harbora
POSTGRES_DB=harbora
POSTGRES_PASSWORD=$(rand 32)
HARBORA_MASTER_KEY=$(rand 44)
EOF
    chmod 600 .env
    ok "Secrets generated and stored in ${HARBORA_DIR}/.env (mode 600)."
  else
    ok "Existing .env kept (secrets preserved)."
  fi

  touch traefik/dynamic/harbora.yml
}

start() {
  cd "$HARBORA_DIR"
  log "Pulling / building containers…"
  $COMPOSE pull || true
  $COMPOSE up -d
  ok "Harbora is starting."
}

show_next_steps() {
  cd "$HARBORA_DIR"; . ./.env
  echo
  ok "Installation complete."
  echo -e "  ${c_green}Panel:${c_reset}       https://${PANEL_DOMAIN}"
  echo -e "  ${c_green}First setup:${c_reset} https://${PANEL_DOMAIN}/setup"
  echo
  echo "Next steps:"
  echo "  1) Point an A record for ${PANEL_DOMAIN} (and *.${ROOT_DOMAIN}) at this server."
  echo "  2) Open the setup URL and create your owner account."
  echo "  3) Connect a Git repo, create an app, and deploy."
  echo
  echo "Manage:  cd ${HARBORA_DIR} && docker compose [ps|logs -f|restart]"
}

cmd_install() { require_root; check_os; install_docker; check_compose; scaffold; start; show_next_steps; }

cmd_update() {
  require_root; check_compose
  [ -d "$HARBORA_DIR" ] || die "Harbora is not installed at ${HARBORA_DIR}."
  cd "$HARBORA_DIR"
  log "Updating to the latest images…"
  curl -fsSL "$REPO_RAW/docker-compose.yml" -o docker-compose.yml
  $COMPOSE pull
  $COMPOSE up -d
  ok "Harbora updated."
}

cmd_uninstall() {
  require_root
  [ -d "$HARBORA_DIR" ] || die "Nothing to uninstall at ${HARBORA_DIR}."
  cd "$HARBORA_DIR"
  warn "This stops Harbora and removes its containers."
  read -rp "Also delete volumes (databases, backups)? [y/N] " del
  if [ "${del:-N}" = "y" ] || [ "${del:-N}" = "Y" ]; then
    $COMPOSE down -v
    warn "Volumes deleted."
  else
    $COMPOSE down
    ok "Containers removed; data volumes kept."
  fi
  echo "Config remains in ${HARBORA_DIR}. Remove it manually if you are done."
}

case "${1:-install}" in
  install)   cmd_install ;;
  update)    cmd_update ;;
  uninstall) cmd_uninstall ;;
  *) die "Unknown command '$1'. Use: install | update | uninstall" ;;
esac
