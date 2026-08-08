# Node Agent contract — changelog

Versioning: the directory name (`v1`) is the **major** version and appears in every URL. Additive
changes are recorded here and do not move the directory. A breaking change creates `v2/` and both
are served until every node has moved.

## v1.3.0 — per-workload resource statistics

Additive. The verb is advertised through `supportedCommands`, which the agent derives from the
handlers it has actually wired up, so an older node simply does not offer it and a control plane
that consults capabilities — as this contract requires — does not send it. There is no capability
flag for it: the command name in `supportedCommands` *is* the flag.

- **`GetWorkloadStats`** (`workloads:read`) — a point-in-time CPU, memory and network reading for
  one workload's containers.
- **`workloadStats`** — the response shape. Every figure is nullable and is **absent** when the
  runtime did not report it, never zero: a control plane that reads a missing value as zero draws an
  idle application, which is the opposite of what an unmeasured one means.

Added because a node had no way to answer "how much is this application using". `GetStatsAsync`
returned null by design — the contract had no verb for it — so an application on a node charted an
empty graph, which reads as silence rather than as an unanswered question.

The CPU percentage is computed from the same formula on both sides (`ContainerCpu` in the C#
mirror). Two copies of it means the same container reads differently depending on where it runs, and
the difference gets blamed on the node. Both counters at zero — an idle container's first sample —
is 0/0, and rather than write a `NaN` that JSON cannot carry, the whole response fails.

---

## v1.2.0 — HTTP ingress over an outbound tunnel

Additive. A node that does not implement it reports `supportsHttpIngressTunnel: false` and is never
sent `ConfigureIngress`; a gateway that receives no `purpose` reads the registration as a database
tunnel, exactly as before.

- **`ConfigureIngress`** (`routes:write`) — turn the node's ingress tunnel on or off.
- **`tunnelRegistration.purpose`** — `database` (default) or `ingress`. `grantId` is now nullable,
  because an ingress tunnel serves the node's published ports rather than one named grant.
- **`open` frames carry a target on an ingress tunnel** — the host port, four bytes big-endian.
  Empty on a database tunnel, as before.
- **`nodeCapabilities.supportsHttpIngressTunnel`**.

Added because a node dialling out from behind NAT could be enrolled, commanded and deployed to — and
then every HTTP route the control plane wired up timed out, because the published host port existed
only on the node's own machine. The tunnel reverses the direction the same way the database tunnel
already does.

The `open` payload is a port and nothing else. A host field would let the gateway name any address
the node can reach, which is a port-forward into the customer's private network wearing a tunnel's
clothes. The node dials loopback, and only a port it allocated itself for a workload that asked to
publish it — so an ingress tunnel reaches exactly what the control plane already deployed there, and
the verb grants no capability that the deploy did not.

---

## v1.1.0 — ListWorkloads

Additive, and therefore still v1: an older node that does not implement the verb reports it as
absent in its capabilities, and a control plane that consults `supportedCommands` — as the contract
requires — simply does not send it.

- **`ListWorkloads`** (`workloads:read`) — enumerate a tenant's workloads on a node.

Added because the control plane cannot retire the containers a previous release left behind without
knowing what is there, and guessing names is not knowing. It is read-only and strictly weaker than
repeating `GetWorkloadStatus` for every id the caller already holds, so it grants no capability that
did not already exist — it only removes the need to have memorised the ids.

---

## v1.0.0 — first release

Initial contract. Covers:

- **Enrollment** — single-use short-lived token → CSR → signed certificate; permanent node id
- **Credential renewal** — mTLS-authenticated rotation, with revocation as an explicit outcome
- **Channel** — outbound WSS, versioned frame envelope, protocol negotiation, resume-after-reconnect
- **Heartbeat** — liveness plus the volatile parts of node state, including certificate expiry
- **Inventory & capabilities** — what the node is and what this build can actually do
- **Command envelope** — allowlist, idempotency key, nonce + freshness window, required scope,
  timeout, cancellation, correlation id, audit metadata
- **The allowlist itself, 21 verbs** — `DeployWorkload`, `UpdateWorkload`, `StopWorkload`,
  `StartWorkload`, `RestartWorkload`, `DeleteWorkload`, `GetWorkloadStatus`, `StreamLogs`,
  `CreateNetwork`, `DeleteNetwork`, `CreateVolume`, `SnapshotVolume`, `RestoreVolume`,
  `CreateDatabaseAccessGrant`, `RevokeDatabaseAccessGrant`, `RotateDatabaseAccessCredential`,
  `RegisterHttpRoute`, `RegisterTcpRoute`, `RemoveRoute`, `DrainNode`, `UpdateAgent`.
  Written out because a verb that reaches the schema without reaching this file is a capability
  nobody reviewed; `DocumentationDriftTests` fails the build when one does
- **Command ack / progress / result** — exactly one terminal result per accepted command
- **Workload specification** — containers pinned by digest, named volumes only, no host bind mounts,
  privileged/host-namespace flags that default off and are refused without an explicit host flag
- **Versioned app manifest** — per-architecture digests, env/secret schemas, backup and restore
  policies, upgrade strategy, migration notes, minimum node version, dependencies
- **Database access grant** — temporary (TTL-bounded) and persistent (explicitly confirmed) modes,
  IP allowlist, connection caps, credential rotation, revocation
- **Tunnel state and registration** — outbound TCP gateway model
- **Node update** — verified download, drain-first, rollback outcome
- **Error model** — structured codes with a retryable flag
- **Audit metadata** — actor, tenant, source IP, reason

### Known gaps, deliberately left for a later minor version

- No streaming *upload* frame: build contexts still go through the existing panel path.
- `snapshotVolume` writes to node-local storage; shipping the artifact to a backup destination stays
  a control-plane concern.
- No batch command frame. One envelope, one command — batching would complicate idempotency for a
  saving that has not been measured.
