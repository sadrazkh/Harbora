# Harbora node agent — troubleshooting

Start here:

```bash
systemctl status harbora-node-agent
journalctl -u harbora-node-agent -n 100 --no-pager
curl -s localhost:9701/healthz
```

The agent logs one JSON object per line. To read them as a human:

```bash
journalctl -u harbora-node-agent -o cat | jq -r '"\(.ts) \(.level) \(.msg)"'
```

---

## The node does not appear in the panel

| What the journal says | What it means | Fix |
|---|---|---|
| `Enrollment cannot succeed: EnrollmentTokenExpired` | The token timed out before the installer used it | Create a new token in the panel and re-run the installer |
| `EnrollmentTokenAlreadyUsed` | The token was already spent — often by an earlier install attempt on this machine | Create a new token. If the node is already enrolled, `/var/lib/harbora-node/identity/node.crt.pem` exists and there is nothing to do |
| `EnrollmentTokenInvalid` | Wrong token, or the wrong control plane | Check `ControlPlaneUrl` in `/etc/harbora-node/agent.conf` |
| `Could not reach the control plane` | Network, DNS, or the panel is down | `curl -sI https://panel.example.com` from the node |
| `Architecture 'x' is not supported` | Not amd64 or arm64 | Nothing to fix; Harbora nodes need one of those |
| `The control plane speaks protocol vN` | The panel is newer than the agent | Update the agent |
| Nothing at all | The service is not running | `systemctl status harbora-node-agent` |

The agent retries a network failure forever and stops immediately on a token failure. That
difference is deliberate: one will pass on its own, the other never will.

---

## The node is online but shows as degraded

`healthz` and the journal both say why. The usual causes:

| Reason in the log | Meaning |
|---|---|
| `disk free …` | Under 10% free or under 2 GiB. The node still serves; it should not accept a deploy that pulls a large image |
| `memory free …` | Under 5% free |
| `load …` | Load per core above 2.0 |
| `credential expires …` | The certificate is within a week of expiry. Renewal should already be happening; if it is not, the journal says why |
| `container runtime unavailable` | Docker is down. `systemctl status docker` |

Degraded is a real state, not a nicer word for unhealthy: the containers already running are fine.

---

## A deploy is refused

The panel shows the error code. The interesting ones:

| Code | Meaning | Fix |
|---|---|---|
| `imageNotPinned` | The spec did not carry a `sha256` digest | The control plane must resolve the tag to a digest before sending |
| `policyDenied` | The spec asked for something this node will not do — a host path dressed as a volume name, a denied capability, privileged mode with the flag off | Read the message; it names the field |
| `unauthorized` | Privileged mode without `node:admin`, or a tenant mismatch between the command and the spec | |
| `unsupportedArchitecture` | The workload does not list this node's architecture | |
| `agentTooOld` | The workload or manifest needs a newer agent | Update the node |
| `validationFailed` | Shape, ranges, or a missing manifest-required variable | The message names it |
| `insufficientResources` | No free host port in the configured range | Widen `Ports` in `agent.conf`, or publish fewer ports |

## A deploy rolled back

```
"deployment.rolled-back" — <workload>: <container>: <reason>
```

The new release did not become healthy within its grace period, so the previous one was restored.
Common reasons:

- `the container exited during startup` — the application crashed. `StreamLogs` from the panel, or
  `docker logs harbora-<name>-<container>-<release>` on the node
- `still not healthy after Ns` — the health check never passed. Either the app is slow to start
  (raise `startPeriodSeconds`) or the probe is pointed at the wrong port or path
- `<n> consecutive probe failures` — the probe reached the app and did not like the answer

If there was no previous release, the result says so: the workload is down and needs a fixed spec.

---

## A database grant does not work

| Symptom | Cause |
|---|---|
| `tunnelUnavailable` on creation | The node has no gateway configured, or cannot reach it. `TunnelGatewayUrl` comes from enrollment; check outbound TCP to it |
| `credentialRotationFailed` mentioning admin credentials | The node mints grants with the database's own admin login, read from the deployed spec. A database deployed outside Harbora, or one whose spec has no `POSTGRES_PASSWORD`/`MYSQL_ROOT_PASSWORD`/`MONGO_INITDB_ROOT_PASSWORD`/`REDIS_PASSWORD`, cannot be granted access this way |
| Connection refused at the published endpoint | The gateway's IP allowlist. The grant is enforced where the client's real address is visible |
| The grant vanished | It expired. Grants are swept every 15 seconds and the expiry is enforced by the node |
| `validationFailed` about the allowlist | `0.0.0.0/0` is refused. An allowlist that allows everything is not an allowlist |

Grants on the node:

```bash
jq '.grants[] | {grantId, engine, state, expiresAt, username}' /var/lib/harbora-node/state/grants.json
```

The passwords in that file are encrypted; the rest is safe to read.

---

## An agent update failed

The node resolves an in-flight update on its next start:

```
Resolved the pending agent update: RolledBack (1.2.0 → 1.1.0)
```

| Outcome | Meaning |
|---|---|
| `Updated` | The version that came back matched. Marker cleared |
| `RolledBack` | It did not match; the previous binary was restored and the service restarted |
| `Failed` | It did not match *and* the previous binary was gone. **The node needs manual repair** |

Manual repair:

```bash
systemctl stop harbora-node-agent
curl -fsSL -o /usr/local/bin/harbora-node-agent \
  https://github.com/sadrazkh/Harbora/releases/latest/download/harbora-node-agent-linux-x64
chmod +x /usr/local/bin/harbora-node-agent
rm -f /var/lib/harbora-node/state/pending-update.json
systemctl start harbora-node-agent
```

`updateVerificationFailed` at the start of an update means the checksum did not match. Nothing was
installed — that is the check working.

---

## The channel keeps reconnecting

```
Control channel failed; will reconnect.
Reconnecting in 8.3s (attempt 4).
```

Backoff runs from 1s to 5 minutes with full jitter. The attempt counter only resets on a **completed
handshake**, so a control plane that accepts the TCP connection and then drops it does not become a
tight loop.

If the delay never grows, the handshake is succeeding and something else closes the connection —
usually a proxy in front of the panel with a short idle timeout. The agent sends a WebSocket keepalive
every 20 seconds; a proxy timing out faster than that needs its timeout raised.

---

## Commands are refused with `nodeDraining`

The node was drained and stayed that way — the flag is persisted deliberately, so a reboot does not
silently put a drained node back in service. Undrain it from the panel, or:

```bash
jq '.draining, .drainReason' /var/lib/harbora-node/state/node.json
```

---

## Reading the node's own audit log

```bash
jq -r '[.at, .action, .outcome, (.actorName // "-"), (.detail // "")] | @tsv' \
  /var/lib/harbora-node/audit/node-audit.log | column -t -s $'\t'
```

This is what the node actually did, as opposed to what the panel asked for. It survives the panel
being unreachable, which is when you most want it.

---

## Metrics

```bash
curl -s localhost:9701/metrics
```

Worth watching:

| Metric | Meaning |
|---|---|
| `harbora_node_channel_connected` | 1 while the control channel is up |
| `harbora_node_health{state="…"}` | One gauge per state |
| `harbora_node_disk_pressure` etc. | The pressure flags behind a degraded verdict |
| `harbora_node_certificate_remaining_seconds` | Time until the credential expires |
| `harbora_node_deployments_rolled_back_total` | A climbing count is a bad release, not a bad node |
| `harbora_node_database_grants_active` | |
| `harbora_node_tunnels_active` | |
| `harbora_node_commands_total{command,status}` | |

The endpoint is loopback-only by design. Scrape it with a local exporter, or over an SSH tunnel.

---

## Collecting everything for a bug report

```bash
{
  harbora-node-agent --version
  uname -a
  docker version --format '{{.Server.Version}}'
  systemctl status harbora-node-agent --no-pager
  journalctl -u harbora-node-agent -n 300 --no-pager
  curl -s localhost:9701/metrics
  jq 'del(.grants[].protectedPassword)' /var/lib/harbora-node/state/grants.json 2>/dev/null
} > /tmp/harbora-node-report.txt 2>&1
```

The journal and the metrics are already redacted. The `jq` removes the encrypted grant passwords
anyway — belt and braces on the one file that holds any.
