# `deploy/node-agent`

Everything needed to put a Harbora node agent on a Linux server.

| File | What it does |
|---|---|
| `install.sh` | One-command install: prerequisites, Docker, the binary, config, unit, enrollment |
| `uninstall.sh` | Removes the agent. Keeps workloads and data unless told otherwise |
| `harbora-node-agent.service` | The systemd unit, with the confinement the agent runs under |
| `build-release.sh` | Self-contained binaries for `linux-x64` and `linux-arm64`, each with a `.sha256` |

Full documentation: [`docs/node-agent/installation.md`](../../docs/node-agent/installation.md).

## Install

```bash
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/node-agent/install.sh \
  | bash -s -- --control-plane https://panel.example.com --token <enrollment-token> --name web-01
```

No inbound port is opened. The node dials the panel.

## Release

```bash
./build-release.sh                 # → ./dist
git tag node-agent-v0.2.0 && git push --tags   # → GitHub release via .github/workflows/release-node-agent.yml
```

The checksum is produced by the build rather than added afterwards, because both the installer and
the agent's own updater refuse an artifact they cannot verify.
