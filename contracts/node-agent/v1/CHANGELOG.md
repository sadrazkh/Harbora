# Node Agent contract — changelog

Versioning: the directory name (`v1`) is the **major** version and appears in every URL. Additive
changes are recorded here and do not move the directory. A breaking change creates `v2/` and both
are served until every node has moved.

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
