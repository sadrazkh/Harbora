# Node Agent contract — changelog

Versioning: the directory name (`v1`) is the **major** version and appears in every URL. Additive
changes are recorded here and do not move the directory. A breaking change creates `v2/` and both
are served until every node has moved.

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
