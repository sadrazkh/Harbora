# Node agent v1 — merge and migration notes

Branch: `feature/harbora-node-agent-v1`, developed in a separate git worktree so it never shared a
working tree with `feature/harbora-platform-expansion-v1`.

---

## What this branch touches

**Added** (nothing here existed before):

```
contracts/node-agent/v1/**
src/Harbora.NodeAgent.Contracts/**
src/Harbora.NodeAgent/**
tests/Harbora.NodeAgent.Tests/**
deploy/node-agent/**
docs/node-agent/**
examples/node-agent/**
.github/workflows/release-node-agent.yml
```

**Modified** — two files, both minimally:

| File | Change | Conflict risk |
|---|---|---|
| `Harbora.slnx` | Three `<Project>` lines added | Low; a textual conflict here resolves by keeping both sides' lines |
| — | | |

Nothing else in the repository was edited. In particular: no changes to `Harbora.Web`, `Harbora.Domain`,
`Harbora.Application`, `Harbora.Infrastructure`, `Harbora.Data`, `Harbora.Cli`, the existing
`Harbora.Agent`, any view, any migration, the template catalog, or the AI gateway.

---

## The existing `Harbora.Agent` is untouched and still works

`src/Harbora.Agent` is the current inbound HTTP agent: a bearer token, port 9700, a thin proxy over
the Docker API. Panels talk to it through `RemoteDockerEngine`.

It is deliberately left alone. Nodes running it keep working exactly as before, and the panel's
`ServerEngineFactory` path is unchanged. The two can coexist on the same fleet indefinitely — they
are different services, on different ports, with different credentials.

The difference between them, for whoever decides when to migrate:

| | `Harbora.Agent` (existing) | `Harbora.NodeAgent` (this branch) |
|---|---|---|
| Direction | Inbound: the panel connects to port 9700 | Outbound only: the node dials the panel |
| Firewall | Needs an inbound rule on the customer's server | None |
| Credential | A static bearer token, optional mTLS | Enrollment token → mTLS certificate, rotated |
| Command surface | The Docker API, effectively | 21 named verbs, no shell |
| Idempotency | None | Per-command, durable |
| Replay protection | None | Nonce + freshness window, durable |
| Rollback on failed health | No | Yes, automatic |
| Self-update | No | Yes, with rollback |
| Database access | Not modelled | TTL-bounded grants over an outbound tunnel |
| Audit | Panel-side only | Panel-side plus a local, append-only log |

---

## What the control plane has to implement before a node can enroll

This branch owns the node side and the contract. The panel side is **not** included, by design — it
lives in `Harbora.Web`, which this branch does not touch.

`contracts/node-agent/v1/` is the specification. Four things are needed:

### 1. `POST /api/node-agent/v1/enroll`

Bearer-authenticated by a short-lived, single-use enrollment token. Takes an `enrollmentRequest`
(CSR included), returns an `enrollmentResponse`: a signed certificate, the CA, a permanent node id,
and the granted scopes.

Needs a node CA. The private key never leaves the node, so the panel signs and never holds it.

**Watch out:** this endpoint is authenticated by a token, not by a session. Per
`docs/overhaul/`-era experience with Git webhooks, any DB read on this path must use
`IgnoreQueryFilters()` and pin the tenant explicitly — an anonymous HTTP request is scoped to
`Guid.Empty`, and every filtered read comes back empty while reporting success.

### 2. `POST /api/node-agent/v1/credential/renew`

mTLS-authenticated by the certificate being replaced. Returns a fresh one. A revoked node gets 403
with `credentialRevoked`.

### 3. `WSS /api/node-agent/v1/channel`

The persistent channel. The panel must:

- answer `node.hello` with `control.hello-ack` carrying the chosen protocol version, a resume token,
  the last sequence it durably holds, the granted scopes and the heartbeat interval
- keep per-node session state so a resume is possible, and set `resumeRejected` when it is not
- send `control.ack` with the highest sequence it has durably stored, so the node can trim its outbox
- issue commands as `commandEnvelope`s with a **fresh nonce per send** and a **stable idempotency key
  per logical operation** — that pairing is what makes a retry safe and a replay detectable

### 4. A TCP gateway (only if you publish databases)

Terminates mTLS, accepts a `tunnelRegistration` line, answers with a `tunnelRegistrationResponse`,
then multiplexes client sessions over the connection using the 9-byte frame header in
`TunnelProtocol.cs`. It enforces the IP allowlist and the connection caps, because it is the side
that sees the client's real address.

### Suggested panel-side sequencing

1. Enrollment + renewal endpoints and the node CA → nodes can enroll and stay enrolled
2. The channel with heartbeat and inventory → nodes appear and report
3. Command issuing for workloads → deployments move to the new path
4. The TCP gateway → database access
5. Migration of existing nodes

---

## Versioning

The node agent inherits the product version from `Directory.Build.props` rather than carrying its
own. That is a deliberate choice against the alternative: the panel already reports a single version
at `/api/v1/version`, and "your agent is older than your panel" is only a meaningful sentence while
both numbers come from the same place.

The consequence is that a node-agent release is tagged `node-agent-v<product-version>` and the
release workflow is separate, so a CLI release and a node-agent release move independently.

---

## Merge order with `feature/harbora-platform-expansion-v1`

The two branches are disjoint apart from `Harbora.slnx`. Either order works. If both add projects to
the solution, resolve by keeping every `<Project>` line from both sides — the file is a flat list and
the order does not matter.

---

## Known gaps, stated rather than hidden

| Gap | Consequence | Where it would go |
|---|---|---|
| Update signature verification | `agentUpdateRequest.signatureBase64` is in the contract; the agent enforces only the SHA-256 today. A checksum protects against corruption and a swapped file, not against a compromised release host | `AgentUpdater.ApplyAsync`, plus a pinned release public key in configuration |
| Rolling update with >1 replica | `UpgradeMode.RollingUpdate` is accepted and behaves as blue/green, which is correct for a single replica and not yet meaningful for several | `WorkloadDeployer` |
| Compose stacks | `workloadSpec.composeFile` is carried and validated, but the deployer expands only `containers[]` | `WorkloadDeployer`, reusing the panel's existing compose import |
| Snapshot shipping | `SnapshotVolume` writes to a node-local Docker volume and returns its checksum; moving it to a backup destination stays a control-plane concern | Control plane |
| Route publication | The node reports `host:port` for a route; Traefik still lives with the control plane. A node-local proxy is not attempted | Control plane |
| `RotateDatabaseAccessCredential` overlap | `overlapSeconds` is accepted and logged as unsupported — none of the four engines can hold two live passwords for one user | Contract note; would need a second user per rotation |
| Metrics endpoint auth | Loopback-only, no authentication. Anyone already on the box can read it — which is the same population that can read the state directory | Fine as is; documented |

---

## Rollback

Each phase is one commit, and each commit is independently revertible:

```
fad78b5  contract v1
b369822  agent core: identity, enrollment, channel, admission
0b87cfd  workload policy, deployment, runtime verbs
dd85809  database access, tunnels, Docker workspaces
0fb3ea1  self-update, drain
<this>   installer, uninstaller, systemd, release, docs
```

Reverting the whole branch removes the new directories and the three lines in `Harbora.slnx`. No
existing behaviour depends on any of it.
