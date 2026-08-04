#!/usr/bin/env bash
#
# Build release binaries for the Harbora node agent.
#
#   ./build-release.sh              # both architectures into ./dist
#   ./build-release.sh linux-x64    # one
#
# Produces a self-contained single file per architecture plus a .sha256 next to it — the installer
# and the self-updater both refuse to install an artifact whose checksum they cannot match, so the
# checksum is part of the build, not an afterthought.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/Harbora.NodeAgent/Harbora.NodeAgent.csproj"
OUT="${OUT_DIR:-$ROOT/dist}"
BINARY="harbora-node-agent"

if [ $# -gt 0 ]; then RIDS=("$@"); else RIDS=(linux-x64 linux-arm64); fi

command -v dotnet >/dev/null 2>&1 || { echo "The .NET 10 SDK is required." >&2; exit 1; }

mkdir -p "$OUT"

for rid in "${RIDS[@]}"; do
  echo "➜ Building $rid…"

  work="$OUT/$rid"
  rm -rf "$work"

  # Self-contained: a node must not need a .NET runtime installed. Trimming is deliberately off —
  # the agent uses reflection-based configuration binding, and a trimmed build that fails to bind
  # would fail at startup on a customer's machine rather than here.
  dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:DebugType=none \
    -o "$work"

  artifact="$OUT/$BINARY-$rid"
  mv "$work/$BINARY" "$artifact"
  chmod 0755 "$artifact"
  rm -rf "$work"

  ( cd "$OUT" && sha256sum "$(basename "$artifact")" > "$(basename "$artifact").sha256" )

  echo "✓ $artifact"
  echo "  $(cat "$artifact.sha256")"
done

echo
echo "Artifacts in $OUT:"
ls -lh "$OUT" | tail -n +2
