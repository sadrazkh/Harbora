# Installing a Harbora node

A node is any Linux server that runs your workloads. Installing the agent on it is one command, and
it opens **no inbound port** — everything the agent does runs over connections it dials itself.

---

## Before you start

| | |
|---|---|
| OS | Linux with systemd (Debian/Ubuntu, RHEL/Fedora, Alpine with OpenRC → not supported) |
| Architecture | `amd64` or `arm64` |
| RAM | 1 GB minimum for the agent plus whatever the workloads need |
| Outbound access | HTTPS to your panel, and TCP to the Harbora gateway if you publish databases |
| Inbound access | **none** |
| Docker | installed by the installer if missing |

---

## 1. Create an enrollment token

In the panel: **Servers → Add a node**. The token is short-lived and single-use. It is spent by the
first successful enrollment and worthless afterwards — including to whoever finds it in your shell
history.

## 2. Run the installer

As root on the node:

```bash
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/node-agent/install.sh \
  | bash -s -- --control-plane https://panel.example.com --token hbr_enroll_xxx --name web-01
```

Options:

| Flag | Meaning |
|---|---|
| `--control-plane URL` | Your panel. Must be `https` |
| `--token TOKEN` | The enrollment token |
| `--name NAME` | Node name in the panel (default: hostname) |
| `--region REGION` | Placement hint, e.g. `eu-central` |
| `--environment ENV` | Placement hint, e.g. `production` |
| `--labels k=v,k=v` | Placement hints |
| `--version VERSION` | A specific agent release (default: latest) |
| `--build-from-source` | Build with the .NET SDK instead of downloading |
| `--allow-privileged` | Permit privileged workloads. **Off by default** — see [security.md](security.md) |
| `--allow-docker-workspace` | Permit tenant Docker workspaces. **Off by default** |
| `--no-start` | Install without starting |

The installer verifies the downloaded binary against the release's published checksum. If a release
has no checksum it says so rather than skipping the check quietly.

## 3. Watch it enroll

The installer waits for enrollment and prints the journal if it fails. To follow along yourself:

```bash
journalctl -u harbora-node-agent -f
```

You are looking for `Enrolled as node <id>`. The node then appears in the panel.

---

## What the installer put where

| Path | What it is |
|---|---|
| `/usr/local/bin/harbora-node-agent` | The binary. Self-contained; no .NET runtime needed |
| `/etc/harbora-node/agent.conf` | Configuration, `0600`. Safe to edit; restart afterwards |
| `/etc/harbora-node/enrollment.token` | The token, `0600`. **Deleted by the agent once spent** |
| `/var/lib/harbora-node/identity/` | The node's private key and certificate, `0600` |
| `/var/lib/harbora-node/state/` | Node id, resume position, command ledger, workload records, grants |
| `/var/lib/harbora-node/snapshots/` | Volume archives |
| `/var/lib/harbora-node/audit/` | The node's own audit log |
| `/etc/systemd/system/harbora-node-agent.service` | The unit |

The agent runs as root because it drives the Docker daemon — which is root-equivalent by definition
— and because a self-update replaces its own binary. The systemd unit confines it: `ProtectSystem=strict`,
`ProtectHome`, `PrivateTmp`, `NoNewPrivileges`, and only three writable paths.

---

## Configuration

`/etc/harbora-node/agent.conf` is plain JSON. Everything in it can also be set as an environment
variable with the `HARBORA_NODE_` prefix (`HARBORA_NODE_NodeAgent__HeartbeatIntervalSeconds=15`),
which is what `systemctl edit harbora-node-agent` is for.

```json
{
  "NodeAgent": {
    "ControlPlaneUrl": "https://panel.example.com",
    "NodeName": "web-01",
    "Region": "eu-central",
    "Environment": "production",
    "Labels": { "tier": "premium" },
    "HeartbeatIntervalSeconds": 30,
    "MaxConcurrentCommands": 4,
    "MaintenanceImage": "docker.io/library/busybox:1.36",
    "ArtifactTransferImage": "docker.io/curlimages/curl:8.10.1",
    "Ports": { "Start": 30000, "End": 32767 },
    "Metrics": { "Enabled": true, "BindAddress": "127.0.0.1", "Port": 9701 },
    "Security": {
      "AllowPrivilegedWorkloads": false,
      "AllowIsolatedDockerWorkspace": false
    }
  }
}
```

Settings worth knowing about:

- **`Metrics.BindAddress`** must be loopback. The agent refuses to start otherwise — the metrics
  endpoint describes the node in enough detail to be worth not publishing, and "no inbound port" is
  a property of the install rather than a default.
- **`MaintenanceImage`** is the image used for the agent's own helper containers (volume archiving,
  checksums). Pin it by digest in production: `repo@sha256:…`.
- **`ArtifactTransferImage`** supplies curl for the one-use HTTPS backup relay. It can contact only
  the configured `ControlPlaneUrl`; object-storage and SFTP credentials stay in the panel. Pin this
  image by digest in production too.
- **`Ports`** is the range host ports are allocated from for workloads that must be reachable across
  nodes. Ports below 1024 are refused.

---

## Updating

Normally the panel does it: **Servers → the node → Update agent**. The node downloads the release,
verifies its checksum, swaps the binary and restarts. If the version that comes back is not the one
that was installed, the node restores the previous binary itself and reports a rollback.

By hand, on the node:

```bash
curl -fsSL https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/node-agent/install.sh \
  | bash -s -- --control-plane https://panel.example.com --token <fresh-token> --name web-01
```

An already-enrolled node keeps its identity; the token is only used when there is no certificate yet.

---

## Draining a node

Before a reboot or a kernel upgrade, take the node out of service from the panel (**Drain**), or
check whether it is draining locally:

```bash
curl -s localhost:9701/healthz
```

A draining node refuses new deploys and keeps existing workloads running unless you asked for them to
be stopped. The flag survives a restart — a node that forgot it was draining would accept the very
deploy you drained it to avoid.

---

## Uninstalling

```bash
# Remove the agent; every workload and volume keeps running:
bash /path/to/uninstall.sh

# Also stop and remove the containers Harbora deployed (volumes survive):
bash uninstall.sh --purge-workloads

# Also delete node identity and state:
bash uninstall.sh --purge-data

# Also DELETE the volumes. This destroys application data:
bash uninstall.sh --purge-volumes
```

The default is the cautious one. Removing the thing that manages containers is not the same decision
as removing the containers, and only one of them throws data away.

Remove the node in the panel too, or it shows as permanently offline.

---

## Building from source

```bash
git clone https://github.com/sadrazkh/Harbora
cd Harbora
./deploy/node-agent/build-release.sh            # both architectures into ./dist
./deploy/node-agent/build-release.sh linux-arm64
```

Each artifact gets a `.sha256` beside it, because both the installer and the agent's own updater
refuse an artifact they cannot verify.

---

## Development

`examples/node-agent/` has a configuration for running the agent against a local panel without
installing anything:

```bash
dotnet run --project src/Harbora.NodeAgent -- \
  --NodeAgent:ControlPlaneUrl=http://localhost:5000 \
  --NodeAgent:Security:AllowInsecureControlPlane=true \
  --NodeAgent:DataDirectory=/tmp/harbora-node-dev \
  --NodeAgent:EnrollmentToken=dev-token
```

`AllowInsecureControlPlane` is validated as development-only: the agent refuses a plain-http control
plane without it, because the enrollment token travels on that connection.
