#!/usr/bin/env bash
#
# Harbora Node Agent — uninstaller.
#
#   bash uninstall.sh                 # remove the agent, keep every workload and volume running
#   bash uninstall.sh --stop-workloads  # also stop the containers Harbora deployed
#   bash uninstall.sh --purge-workloads # also remove those containers (volumes survive)
#   bash uninstall.sh --purge-data     # also delete node state, identity and snapshots
#   bash uninstall.sh --purge-volumes  # also DELETE the volumes — this destroys application data
#
# The default is the cautious one: the agent goes, the workloads stay. Removing the thing that
# manages containers is not the same decision as removing the containers, and only one of them
# throws away data.
set -euo pipefail

CONFIG_DIR="/etc/harbora-node"
DATA_DIR="/var/lib/harbora-node"
INSTALL_DIR="/usr/local/bin"
UNIT_NAME="harbora-node-agent.service"
BINARY="harbora-node-agent"
MANAGED_LABEL="io.harbora.managed=true"

STOP_WORKLOADS=0
PURGE_WORKLOADS=0
PURGE_DATA=0
PURGE_VOLUMES=0
ASSUME_YES=0

c_g='\033[0;32m'; c_b='\033[0;34m'; c_y='\033[1;33m'; c_r='\033[0;31m'; c_0='\033[0m'
log()  { echo -e "${c_b}➜${c_0} $*"; }
ok()   { echo -e "${c_g}✓${c_0} $*"; }
warn() { echo -e "${c_y}!${c_0} $*"; }
err()  { echo -e "${c_r}✗${c_0} $*" >&2; }
die()  { err "$*"; exit 1; }

usage() { sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0; }

while [ $# -gt 0 ]; do
  case "$1" in
    --stop-workloads)  STOP_WORKLOADS=1; shift;;
    --purge-workloads) PURGE_WORKLOADS=1; STOP_WORKLOADS=1; shift;;
    --purge-data)      PURGE_DATA=1; shift;;
    --purge-volumes)   PURGE_VOLUMES=1; PURGE_WORKLOADS=1; STOP_WORKLOADS=1; shift;;
    --yes|-y)          ASSUME_YES=1; shift;;
    -h|--help)         usage;;
    *) die "Unknown option: $1 (try --help)";;
  esac
done

[ "$(id -u)" -eq 0 ] || die "Run as root."

confirm() {
  [ "$ASSUME_YES" -eq 1 ] && return 0
  [ -t 0 ] || die "$1 Re-run with --yes to confirm non-interactively."

  read -r -p "$1 Type 'yes' to continue: " answer
  [ "$answer" = "yes" ] || die "Aborted."
}

managed_containers() {
  command -v docker >/dev/null 2>&1 || return 0
  docker ps -aq --filter "label=$MANAGED_LABEL" 2>/dev/null || true
}

managed_volumes() {
  command -v docker >/dev/null 2>&1 || return 0
  docker volume ls -q --filter "label=$MANAGED_LABEL" 2>/dev/null || true
}

stop_service() {
  if systemctl list-unit-files 2>/dev/null | grep -q "^$UNIT_NAME"; then
    log "Stopping $UNIT_NAME…"
    systemctl disable --now "$UNIT_NAME" >/dev/null 2>&1 || true
    rm -f "/etc/systemd/system/$UNIT_NAME"
    systemctl daemon-reload
    ok "Service removed."
  else
    warn "$UNIT_NAME is not installed."
  fi
}

handle_workloads() {
  local containers
  containers="$(managed_containers)"

  if [ -z "$containers" ]; then
    log "No Harbora-managed containers on this node."
    return 0
  fi

  local count
  count="$(printf '%s\n' "$containers" | wc -l | tr -d ' ')"

  if [ "$STOP_WORKLOADS" -eq 0 ]; then
    warn "$count Harbora-managed container(s) are left running. They keep serving; nothing manages them any more."
    warn "Stop them later with: docker ps -aq --filter label=$MANAGED_LABEL | xargs -r docker stop"
    return 0
  fi

  confirm "Stop $count Harbora-managed container(s)?"
  log "Stopping $count container(s)…"
  printf '%s\n' "$containers" | xargs -r docker stop >/dev/null
  ok "Containers stopped."

  if [ "$PURGE_WORKLOADS" -eq 1 ]; then
    log "Removing $count container(s)…"
    printf '%s\n' "$containers" | xargs -r docker rm -f >/dev/null
    ok "Containers removed."
  fi
}

handle_volumes() {
  [ "$PURGE_VOLUMES" -eq 1 ] || return 0

  local volumes
  volumes="$(managed_volumes)"
  [ -z "$volumes" ] && { log "No Harbora-managed volumes."; return 0; }

  local count
  count="$(printf '%s\n' "$volumes" | wc -l | tr -d ' ')"

  # The only irreversible step in this script, so it gets its own confirmation even under --yes
  # for the volume list itself.
  err "About to DELETE $count volume(s). This destroys the data of every application on this node:"
  printf '%s\n' "$volumes" | sed 's/^/    /'
  confirm "Delete these $count volume(s)? There is no undo."

  printf '%s\n' "$volumes" | xargs -r docker volume rm >/dev/null || warn "Some volumes were in use and were kept."
  ok "Volumes deleted."
}

handle_data() {
  if [ "$PURGE_DATA" -eq 0 ]; then
    warn "Node state kept at $DATA_DIR (identity, grants, workload records)."
    warn "Re-running the installer with a fresh token on this machine will create a SECOND node unless you remove it."
    return 0
  fi

  confirm "Delete $DATA_DIR and $CONFIG_DIR, including this node's identity?"

  # Overwrite the private key before unlinking. On the ext4 root most nodes run this is the
  # difference between a deleted key and a recoverable one.
  if [ -f "$DATA_DIR/identity/node.key.pem" ]; then
    shred -u "$DATA_DIR/identity/node.key.pem" 2>/dev/null || rm -f "$DATA_DIR/identity/node.key.pem"
  fi

  rm -rf "$DATA_DIR" "$CONFIG_DIR"
  ok "Node state and configuration removed."
}

remove_binary() {
  if [ -f "$INSTALL_DIR/$BINARY" ]; then
    rm -f "$INSTALL_DIR/$BINARY"
    ok "Binary removed from $INSTALL_DIR."
  fi
}

summary() {
  cat <<EOF

  Harbora node agent uninstalled.

  Still on this machine:
$([ "$PURGE_WORKLOADS" -eq 1 ] || echo "    · Harbora-managed containers (still running)")
$([ "$PURGE_VOLUMES" -eq 1 ] || echo "    · Harbora-managed volumes (application data)")
$([ "$PURGE_DATA" -eq 1 ] || echo "    · Node identity and state in $DATA_DIR")
    · Docker itself

  Remove the node from the panel as well, or it will show as permanently offline.

EOF
}

log "Uninstalling the Harbora node agent…"
stop_service
handle_workloads
handle_volumes
handle_data
remove_binary
summary
