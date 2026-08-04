#!/usr/bin/env bash
#
# Harbora Node Agent — installer.
#
# One command on any Linux server, as root:
#
#   curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/node-agent/install.sh \
#     | bash -s -- --control-plane https://panel.example.com --token <enrollment-token> --name web-01
#
# The node dials out; nothing here opens an inbound port. The enrollment token is short-lived and
# single-use, and the agent shreds it the moment it has a certificate.
#
# Options (all also readable from the environment, e.g. HARBORA_CONTROL_PLANE):
#   --control-plane URL     required — the Harbora panel
#   --token TOKEN           required — enrollment token created in the panel
#   --name NAME             node name (default: hostname)
#   --region REGION         placement hint
#   --environment ENV       placement hint, e.g. production
#   --labels k=v,k=v        placement hints
#   --version VERSION       agent release to install (default: latest)
#   --build-from-source     build with the .NET SDK instead of downloading a release
#   --allow-privileged      permit privileged workloads on this node (off by default)
#   --allow-docker-workspace  permit isolated tenant Docker workspaces (off by default)
#   --no-start              install without starting the service
set -euo pipefail

REPO_URL="${HARBORA_REPO_URL:-https://github.com/sadrazkh/Harbora}"
INSTALL_DIR="/usr/local/bin"
CONFIG_DIR="/etc/harbora-node"
DATA_DIR="/var/lib/harbora-node"
UNIT_NAME="harbora-node-agent.service"
BINARY="harbora-node-agent"

CONTROL_PLANE="${HARBORA_CONTROL_PLANE:-}"
TOKEN="${HARBORA_ENROLLMENT_TOKEN:-}"
NODE_NAME="${HARBORA_NODE_NAME:-$(hostname -s 2>/dev/null || hostname)}"
REGION="${HARBORA_REGION:-}"
ENVIRONMENT="${HARBORA_ENVIRONMENT:-}"
LABELS="${HARBORA_LABELS:-}"
AGENT_VERSION="${HARBORA_AGENT_VERSION:-latest}"
BUILD_FROM_SOURCE="${HARBORA_BUILD_FROM_SOURCE:-0}"
ALLOW_PRIVILEGED="false"
ALLOW_WORKSPACE="false"
START_SERVICE=1

c_g='\033[0;32m'; c_b='\033[0;34m'; c_y='\033[1;33m'; c_r='\033[0;31m'; c_0='\033[0m'
log()  { echo -e "${c_b}➜${c_0} $*"; }
ok()   { echo -e "${c_g}✓${c_0} $*"; }
warn() { echo -e "${c_y}!${c_0} $*"; }
err()  { echo -e "${c_r}✗${c_0} $*" >&2; }
die()  { err "$*"; exit 1; }

usage() { sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit 0; }

parse_args() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --control-plane) CONTROL_PLANE="$2"; shift 2;;
      --token)         TOKEN="$2"; shift 2;;
      --name)          NODE_NAME="$2"; shift 2;;
      --region)        REGION="$2"; shift 2;;
      --environment)   ENVIRONMENT="$2"; shift 2;;
      --labels)        LABELS="$2"; shift 2;;
      --version)       AGENT_VERSION="$2"; shift 2;;
      --build-from-source) BUILD_FROM_SOURCE=1; shift;;
      --allow-privileged)  ALLOW_PRIVILEGED="true"; shift;;
      --allow-docker-workspace) ALLOW_WORKSPACE="true"; shift;;
      --no-start)      START_SERVICE=0; shift;;
      -h|--help)       usage;;
      *) die "Unknown option: $1 (try --help)";;
    esac
  done
}

require_root() { [ "$(id -u)" -eq 0 ] || die "Run as root. / با دسترسی root اجرا کنید."; }

check_os() {
  [ "$(uname -s)" = "Linux" ] || die "The Harbora node agent runs on Linux only."
  command -v systemctl >/dev/null 2>&1 || die "systemd is required; this installer manages a systemd unit."

  case "$(uname -m)" in
    x86_64)         ARCH="x64";   REPORTED="amd64";;
    aarch64|arm64)  ARCH="arm64"; REPORTED="arm64";;
    *) die "Unsupported architecture $(uname -m). Harbora nodes run on amd64 or arm64.";;
  esac

  [ -f /etc/os-release ] && { . /etc/os-release; log "Detected ${PRETTY_NAME:-Linux} (${REPORTED})."; }
}

validate() {
  [ -n "$CONTROL_PLANE" ] || die "--control-plane is required (e.g. https://panel.example.com)."
  [ -n "$TOKEN" ]         || die "--token is required. Create an enrollment token in the panel: Servers → Add a node."
  [ -n "$NODE_NAME" ]     || die "--name is required."

  case "$CONTROL_PLANE" in
    https://*) ;;
    http://*)  die "The control plane URL must be https. The enrollment token travels on that connection.";;
    *)         die "--control-plane must be an absolute URL, e.g. https://panel.example.com";;
  esac
}

install_prereqs() {
  local missing=()
  for tool in curl tar; do command -v "$tool" >/dev/null 2>&1 || missing+=("$tool"); done
  [ ${#missing[@]} -eq 0 ] && return 0

  log "Installing ${missing[*]}…"
  if   command -v apt-get >/dev/null; then DEBIAN_FRONTEND=noninteractive apt-get update -qq && apt-get install -y -qq "${missing[@]}" >/dev/null
  elif command -v dnf     >/dev/null; then dnf install -y -q "${missing[@]}" >/dev/null
  elif command -v yum     >/dev/null; then yum install -y -q "${missing[@]}" >/dev/null
  elif command -v apk     >/dev/null; then apk add --no-cache "${missing[@]}" >/dev/null
  else die "Install ${missing[*]} and re-run."
  fi
}

install_docker() {
  if command -v docker >/dev/null 2>&1; then
    ok "Docker present ($(docker --version 2>/dev/null | awk '{print $3}' | tr -d ,))."
  else
    log "Installing Docker…"
    curl -fsSL https://get.docker.com | sh >/dev/null
    ok "Docker installed."
  fi

  systemctl enable --now docker >/dev/null 2>&1 || warn "Could not enable the docker service; check 'systemctl status docker'."
}

download_agent() {
  local url base
  base="$REPO_URL/releases"

  if [ "$AGENT_VERSION" = "latest" ]; then
    url="$base/latest/download/${BINARY}-linux-${ARCH}"
  else
    url="$base/download/${AGENT_VERSION}/${BINARY}-linux-${ARCH}"
  fi

  log "Downloading the agent from $url…"

  local staged="/tmp/${BINARY}.$$"
  if ! curl -fsSL --retry 3 -o "$staged" "$url"; then
    rm -f "$staged"
    return 1
  fi

  # Verify against the published checksums file when there is one. A release without checksums is
  # installable but says so — silently skipping the check would make the check meaningless.
  local sums="/tmp/${BINARY}.sha256.$$"
  if curl -fsSL --retry 2 -o "$sums" "${url}.sha256" 2>/dev/null; then
    local expected actual
    expected="$(awk '{print $1}' "$sums")"
    actual="$(sha256sum "$staged" | awk '{print $1}')"
    rm -f "$sums"

    if [ "$expected" != "$actual" ]; then
      rm -f "$staged"
      die "Checksum mismatch: expected $expected, got $actual. Nothing was installed."
    fi
    ok "Checksum verified."
  else
    warn "No published checksum for this release; the binary was not verified."
  fi

  install -m 0755 "$staged" "$INSTALL_DIR/$BINARY"
  rm -f "$staged"
  return 0
}

build_agent() {
  command -v dotnet >/dev/null 2>&1 || die "Building from source needs the .NET 10 SDK on this machine."

  local workdir="/tmp/harbora-node-src.$$"
  log "Building the agent from source (this takes a few minutes)…"

  rm -rf "$workdir"
  git clone --depth 1 "$REPO_URL" "$workdir" -q

  dotnet publish "$workdir/src/Harbora.NodeAgent/Harbora.NodeAgent.csproj" \
    -c Release -r "linux-$ARCH" --self-contained true \
    -o "$workdir/out" >/dev/null

  install -m 0755 "$workdir/out/$BINARY" "$INSTALL_DIR/$BINARY"
  rm -rf "$workdir"
  ok "Built and installed from source."
}

install_binary() {
  if [ "$BUILD_FROM_SOURCE" = "1" ]; then
    build_agent
  elif ! download_agent; then
    warn "No published release was reachable; falling back to building from source."
    build_agent
  fi

  local reported
  reported="$("$INSTALL_DIR/$BINARY" --version 2>/dev/null || echo unknown)"
  ok "Installed harbora-node-agent $reported at $INSTALL_DIR/$BINARY."
}

write_config() {
  install -d -m 0700 "$CONFIG_DIR"
  install -d -m 0700 "$DATA_DIR"

  # The token goes in its own file, which the agent shreds after enrollment. Keeping it out of
  # agent.conf means the config an operator edits later never contained a credential.
  local token_file="$CONFIG_DIR/enrollment.token"
  printf '%s' "$TOKEN" > "$token_file"
  chmod 0600 "$token_file"

  local labels_json="{}"
  if [ -n "$LABELS" ]; then
    labels_json="$(printf '%s' "$LABELS" | awk -F, '{
      printf "{";
      for (i = 1; i <= NF; i++) {
        split($i, kv, "=");
        printf "%s\"%s\": \"%s\"", (i > 1 ? ", " : ""), kv[1], kv[2];
      }
      printf "}";
    }')"
  fi

  cat > "$CONFIG_DIR/agent.conf" <<JSON
{
  "//": "Written by deploy/node-agent/install.sh. Safe to edit; restart the service afterwards.",
  "Logging": { "LogLevel": { "Default": "Information" } },
  "NodeAgent": {
    "ControlPlaneUrl": "$CONTROL_PLANE",
    "EnrollmentTokenFile": "$CONFIG_DIR/enrollment.token",
    "NodeName": "$NODE_NAME",
    "Region": "$REGION",
    "Environment": "$ENVIRONMENT",
    "Labels": $labels_json,
    "DataDirectory": "$DATA_DIR",
    "Security": {
      "AllowPrivilegedWorkloads": $ALLOW_PRIVILEGED,
      "AllowIsolatedDockerWorkspace": $ALLOW_WORKSPACE
    }
  }
}
JSON

  chmod 0600 "$CONFIG_DIR/agent.conf"
  ok "Configuration written to $CONFIG_DIR/agent.conf."

  [ "$ALLOW_PRIVILEGED" = "true" ] && warn "Privileged workloads are ENABLED on this node."
  [ "$ALLOW_WORKSPACE" = "true" ] && warn "Isolated Docker workspaces are ENABLED on this node; they run a nested daemon with elevated privileges."
  return 0
}

install_unit() {
  local unit_source
  unit_source="$(dirname "$0")/$UNIT_NAME"

  if [ -f "$unit_source" ]; then
    install -m 0644 "$unit_source" "/etc/systemd/system/$UNIT_NAME"
  else
    log "Fetching the systemd unit…"
    curl -fsSL -o "/etc/systemd/system/$UNIT_NAME" \
      "https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/node-agent/$UNIT_NAME"
    chmod 0644 "/etc/systemd/system/$UNIT_NAME"
  fi

  systemctl daemon-reload
  ok "systemd unit installed."
}

start_service() {
  [ "$START_SERVICE" -eq 1 ] || { warn "Not starting the service (--no-start). Run: systemctl enable --now $UNIT_NAME"; return 0; }

  systemctl enable "$UNIT_NAME" >/dev/null 2>&1
  systemctl restart "$UNIT_NAME"

  log "Waiting for the node to enroll…"

  for _ in $(seq 1 30); do
    if [ -f "$DATA_DIR/identity/node.crt.pem" ]; then
      ok "Node enrolled. It is now visible in the panel."
      return 0
    fi

    if ! systemctl is-active --quiet "$UNIT_NAME"; then
      err "The agent stopped. The last lines from the journal:"
      journalctl -u "$UNIT_NAME" -n 25 --no-pager || true
      die "Enrollment failed. Fix the problem above and re-run this installer with a fresh token."
    fi

    sleep 2
  done

  warn "The node has not enrolled yet. Watch it with: journalctl -u $UNIT_NAME -f"
}

summary() {
  cat <<EOF

  Harbora node agent installed.

    node name     $NODE_NAME
    control plane $CONTROL_PLANE
    binary        $INSTALL_DIR/$BINARY
    config        $CONFIG_DIR/agent.conf
    data          $DATA_DIR
    service       $UNIT_NAME

  Useful commands:

    systemctl status $UNIT_NAME
    journalctl -u $UNIT_NAME -f
    curl -s localhost:9701/healthz
    curl -s localhost:9701/metrics

  This node listens on no public port. Everything runs over connections it opened itself.

EOF
}

main() {
  parse_args "$@"
  require_root
  check_os
  validate
  install_prereqs
  install_docker
  install_binary
  write_config
  install_unit
  start_service
  summary
}

main "$@"
