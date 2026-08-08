# Harbora node agent — security model

A node is usually a customer's own server. The agent runs on it as root and takes instructions from
a remote control plane, so the honest question is not "is this secure" but **"what exactly can the
control plane do to this machine, and what happens if the control plane is wrong, compromised, or
coerced?"**

This document answers that.

---

## The one-sentence version

The control plane can ask the node to perform any of twenty-one named operations on
digest-pinned images, in named volumes, under declared limits — and nothing else. There is no verb
that runs a command, no field that carries a host path, and no configuration that turns those back
on for a tenant.

---

## Trust boundaries

```
   admin ──────► control plane ──────► node agent ──────► Docker ──────► containers
                       │                    │
                   (untrusted            (this is
                    from the node's       the boundary
                    point of view)        that matters)
```

The node treats the control plane as **authenticated but not trusted**. Authentication proves the
frames came from the panel. It does not prove the panel is behaving, so every command is validated
against local policy afterwards.

---

## Identity and transport

| Property | How |
|---|---|
| No inbound port | Every channel is dialled by the node: enrollment (HTTPS), commands (WSS), tunnels (TLS) |
| Private key never leaves the node | Enrollment sends a CSR; the panel signs the public half |
| Panel database compromise cannot impersonate a node | Same reason — it holds no private key |
| Enrollment token is single-use | Spent at first enrollment; the agent shreds the token file |
| Mutual TLS after enrollment | Node certificate on every later connection |
| Rotation is routine | Renewal starts at two thirds of the certificate lifetime |
| Revocation is terminal and loud | A revoked node stops and says so rather than retrying quietly |
| Server trust | System store first, then the CA received at enrollment. A **name mismatch is never** rescued by the private CA |

The metrics endpoint is the only socket the agent listens on. It is bound to loopback, and a
non-loopback bind address fails configuration validation — not merely a default.

---

## Command admission

Every command passes these checks in order, and the first failure is the answer. Nothing parses a
payload it has not already decided it is willing to act on.

1. **Protocol version** — a frame from an unnegotiated version is dropped, not guessed at
2. **Allowlist** — the verb must be in the closed catalog
3. **Freshness** — `issuedAt` within ±5 minutes
4. **Nonce** — not seen before; nonces are on disk, so a restart does not reopen the window
5. **Scope** — the declared scope must match the verb's requirement *and* be one the node was
   enrolled with
6. **Drain** — a draining node refuses new mutating work
7. **Idempotency** — a completed key replays its stored result rather than re-executing
8. **Payload validation** — shape, ranges, policy
9. **Execution** — bounded by a timeout, cancellable, audited

### There is no remote shell

The catalog has twenty-four verbs and none of them executes a command supplied by the caller. The
contract test `Allowlist_contains_no_arbitrary_execution_verb` asserts this rather than trusting it
to stay true.

Where the agent does run programs — a `tar` for a volume snapshot, `psql` to create a database user
— it passes an **argv array**, never a string a shell would parse, and the values it interpolates
are ones it generated itself from a restricted alphabet.

---

## Workload policy

Refusals are per-spec and reported all at once, so fixing a template does not take one deploy per
mistake.

| Rule | Why |
|---|---|
| Images must be pinned by `sha256` digest | A mutable tag cannot express "deploy what was tested" |
| The pulled digest is read back and compared | Pulling by digest makes substitution hard; checking makes it checked |
| Volume names must be plain names | **The most important check here.** Docker's bind syntax is `source:target`, so a "volume name" of `/var/run/docker.sock` would become a host bind mount of the Docker socket — the whole machine, through a field that looks like a label |
| No field can express a host bind mount | `mountSpec` has no `hostPath`, `source` or `bind` member |
| Mount paths are normalised before comparison | `/var/run/../run/docker.sock` is the same string to the policy as the obvious spelling |
| Cannot mount over `/`, `/proc`, `/sys`, `/dev`, `/boot`, or `/etc` itself | `/etc/nginx/conf.d` is fine; replacing all of `/etc` is not |
| Containers must declare a memory limit | One tenant's leak must not take down every other tenant on the box |
| A PID limit is applied | A fork bomb in one container must not take the node down |
| `CAP_DROP=ALL` and `no-new-privileges` by default | |
| `SYS_ADMIN`, `SYS_PTRACE`, `NET_ADMIN` and friends are refused | |
| Host ports must fall inside the configured range, ≥1024 | |
| Every resource is labelled with its tenant | A command carrying another tenant's workload id resolves to nothing |

### Privileged mode

Refused unless **both**:

1. the machine's owner set `Security:AllowPrivilegedWorkloads` on this node, **and**
2. the command carries `node:admin` scope.

Two locks, one key each. Either alone would let a tenant-facing spec reach it. Turning the flag on is
recorded in the node's audit log at startup, so a machine that has it enabled says so on every boot.

The same applies to host networking and the host PID namespace, which are privilege by another name.

---

## Secrets

| Where a secret could leak | What prevents it |
|---|---|
| Log lines | Redaction happens at the single logging exit, not at each call site — call sites can be careful, but every call site being careful forever cannot be relied on |
| A workload's own output | Pattern redaction catches connection strings, bearer tokens and PEM private keys the agent was never told about |
| A record's `ToString()` | `SecretSpec` and `DatabaseAccessGrantState` override it |
| A container's command line | Secrets are injected as environment entries or tmpfs files, never as arguments |
| The container's writable layer | File-mounted secrets go to a tmpfs, which is not committed, exported or pushed |
| A database client's command line | Passwords travel in `PGPASSWORD` / `MYSQL_PWD` / `REDISCLI_AUTH` or on stdin. A process's command line is world-readable in `/proc` |
| An engine's error output | Stderr that echoed a statement containing a credential is summarised rather than forwarded |
| The state file | Grant passwords are AES-GCM encrypted under a key derived from the node's private key |
| A status read | A grant password is returned exactly once, on creation and on rotation |

Erasing the node identity makes every stored secret unreadable. A re-enrolled node cannot read what
the previous one held — the correct outcome, not an inconvenience.

---

## External database access

The node **never** binds a database to `0.0.0.0`. It dials the Harbora TCP gateway outbound and the
gateway publishes the endpoint.

Consequences worth stating:

- the customer's firewall gains no inbound rule
- the public IP belongs to Harbora, not to the customer
- revoking access is closing a socket the node owns, not hoping a port got unbound
- the gateway sees the client's real address, so the IP allowlist is enforced where it can be

| Control | Temporary grant | Persistent grant |
|---|---|---|
| TTL | required, 1 minute – 7 days, enforced by the node | not applicable |
| Operator confirmation | not required | **required** |
| IP allowlist | recommended (a grant without one is logged as such) | **required** |
| `0.0.0.0/0` | refused | refused |
| Connection caps | yes | yes |
| Read-only option | yes | yes |
| Rotation | yes | yes |
| Revocation | immediate | immediate |

Expiry is enforced on the node. A control plane that goes away mid-grant must not leave the door
open, and the only party that can guarantee it closes is the one holding the socket. A node that was
powered off through a grant's expiry closes it on the way back up rather than resuming it.

Credentials are minted with the database's **own admin login**, read from the workload spec the node
itself deployed. No second credential store, and nothing extra sent at grant time.

---

## The Docker Ready App

The host's `/var/run/docker.sock` is **never** shared with a tenant. Handing it over is handing over
root on the machine and every other tenant's containers, because the socket has no notion of who is
asking.

A tenant gets their own daemon in a container, on their own network, capped at limits the node owns
rather than the spec supplies. A nested daemon needs privileges the ordinary policy refuses, so the
path is gated by:

- **its own feature flag** — `Security:AllowIsolatedDockerWorkspace`, deliberately not the general
  privileged switch. An operator who enabled privileged mode for one internal workload has not
  thereby agreed to run untrusted tenant code in a nested daemon
- **a `node:admin` command**
- **a warning in the journal and an entry in the audit log**

If any of the three is missing the workspace is refused rather than downgraded. A half-isolated
Docker workspace is worse than none, because it looks like the safe thing.

Also enforced for a workspace: no host networking, no host PID namespace, no published host ports,
no egress by default, and exactly one mount — its own volume.

---

## Audit

The node keeps its own append-only audit log at `/var/lib/harbora-node/audit/node-audit.log`, `0600`,
rotated at 32 MiB. Redacted on write.

Local is the point. The panel already logs what it asked for; this records what the node actually
did, and it survives the panel being unreachable, wrong, or the thing under investigation.

Every entry carries the actor, tenant, source IP and reason the control plane supplied, plus the
correlation and idempotency keys. Recorded actions include command admission and completion,
enrollment, credential renewal, grant creation/rotation/revocation/expiry, drain, agent update, and
workspace provisioning.

---

## What is deliberately not solved here

- **A malicious admin in the control plane** can still deploy a workload, delete one, or mint a
  database grant. That is what an admin is. The node limits *how* — no shell, no host mounts, no
  socket — and records everything.
- **A compromised Docker daemon** is a compromised node. The agent is a client of it.
- **Rootless workloads** are not enforced. `User` is honoured when a spec sets it; the node does not
  refuse a container that runs as root inside its own namespace.
- **Signature verification for agent updates** is contract-supported (`signatureBase64`) but the
  agent currently enforces only the SHA-256. See [merge-notes.md](merge-notes.md).

---

## Reporting

Security issues in the node agent: open a private security advisory on the repository rather than a
public issue.
