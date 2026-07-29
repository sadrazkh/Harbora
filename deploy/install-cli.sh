#!/usr/bin/env bash
#
# Harbora CLI installer (Linux / macOS). One command:
#   curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.sh | bash
#
# Downloads the self-contained `harbora` binary for your OS/arch from the latest GitHub release
# and installs it to a directory on your PATH. No .NET runtime required.
set -euo pipefail

REPO="${HARBORA_REPO:-sadrazkh/Harbora}"

case "$(uname -s)" in
  Linux)  os=linux ;;
  Darwin) os=osx ;;
  *) echo "Unsupported OS: $(uname -s). On Windows use install-cli.ps1." >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  aarch64|arm64) arch=arm64 ;;
  *) echo "Unsupported arch: $(uname -m)" >&2; exit 1 ;;
esac

asset="harbora-${os}-${arch}"
url="https://github.com/${REPO}/releases/latest/download/${asset}"

# Prefer a writable dir on PATH; fall back to ~/.local/bin.
if [ -w /usr/local/bin ]; then dest=/usr/local/bin
elif command -v sudo >/dev/null 2>&1 && [ -d /usr/local/bin ]; then dest=/usr/local/bin; SUDO=sudo
else dest="$HOME/.local/bin"; mkdir -p "$dest"; fi

echo "➜ Downloading $asset …"
tmp="$(mktemp)"
if ! curl -fsSL "$url" -o "$tmp"; then
  echo "✗ No published release found ($asset)." >&2
  echo "  Fix: tag a release so CI builds the binaries:" >&2
  echo "       git tag v0.1.0 && git push origin v0.1.0" >&2
  echo "  Or build from source (needs the .NET SDK):" >&2
  echo "       dotnet publish src/Harbora.Cli -c Release" >&2
  exit 1
fi
chmod +x "$tmp"

# A Harbora SERVER installs its own `harbora` command — the break-glass admin tool (doctor,
# reset-password, fix-key). Overwriting it would remove the platform's recovery tooling from the
# machine, and would do it silently. Install under a distinct name there instead.
name=harbora
if [ -f /opt/harbora/app/deploy/docker-compose.yml ] ||
   { [ -f "$dest/harbora" ] && grep -q 'Harbora server administration' "$dest/harbora" 2>/dev/null; }; then
  name=harbora-cli
  echo "! This machine runs a Harbora server, where 'harbora' is the admin/recovery command."
  echo "  Installing the deploy CLI as 'harbora-cli' so the recovery tool stays intact."
fi

${SUDO:-} mv "$tmp" "$dest/$name"

echo "✓ Installed to $dest/$name"
case ":$PATH:" in
  *":$dest:"*) : ;;
  *) echo "! Add to PATH:  export PATH=\"\$PATH:$dest\"  (add this to ~/.bashrc / ~/.zshrc)";;
esac
echo "Next:  $name login --server https://panel.example.com --token <token>"
