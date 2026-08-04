# Harbora Node Agent contract — v1

The normative wire contract between a Harbora **node** and the **control plane** (the panel).

`node-agent.v1.schema.json` is the source of truth. `src/Harbora.NodeAgent.Contracts` mirrors it in
C#, and `ContractSchemaTests` fails the build if the two drift apart — so neither side can quietly
change a field name and discover it in production.

---

## Design constraints this contract encodes

| Constraint | How the contract enforces it |
|---|---|
| A node install must open **no inbound port** | Every channel is dialled outbound by the node: enrollment (HTTPS), the command channel (WSS), the database tunnel (TLS to the gateway) |
| The control plane must not be able to run arbitrary code on a customer's server | `commandName` is a closed enum. There is no `RunShell` member, and no command takes a shell string |
| A retried command must not act twice | `idempotencyKey` on every envelope; the node — the only party that knows whether the first attempt landed — decides |
| A captured command must not be replayable | `nonce` + `issuedAt`, checked against a 5-minute freshness window |
| "Deploy what was tested" | `imageRef.digest` is required. A mutable tag cannot express it |
| A database must not become internet-facing | No spec can bind to a host address; external access exists only as a gateway tunnel with an IP allowlist and a TTL |
| Secrets must not leak | Secrets travel in a dedicated `secrets` array, never in `command`, and the node never echoes a value back |

---

## Transport

```
                    ┌────────────────────────── control plane ─────────────────────────┐
  node (outbound)   │                                                                  │
  ────────────────► │  POST  {base}/api/node-agent/v1/enroll           (bearer: token)  │  once
  ────────────────► │  POST  {base}/api/node-agent/v1/credential/renew (mTLS)           │  periodic
  ═══════════════►  │  WSS   {base}/api/node-agent/v1/channel          (mTLS)           │  persistent
  ────────────────► │  TLS   {gateway}/api/node-agent/v1/tunnel        (mTLS)           │  per grant
                    └──────────────────────────────────────────────────────────────────┘
```

Enrollment is the only exchange authenticated by the enrollment token. Everything after it is
authenticated by the certificate issued in response, so a token that leaks from a shell history or a
CI log is worthless once spent.

### Frames

Every message on the channel is one JSON `controlFrame`. `sequence` is per-sender and monotonic
within a session.

| Direction | `type` | Payload `$def` |
|---|---|---|
| node → cp | `node.hello` | `nodeHello` |
| node → cp | `node.resume` | `nodeHello` (with `resumeToken`) |
| node → cp | `node.heartbeat` | `nodeHeartbeat` |
| node → cp | `node.inventory` | `nodeInventory` |
| node → cp | `command.ack` | `commandAck` |
| node → cp | `command.progress` | `commandProgress` |
| node → cp | `command.result` | `commandResult` |
| node → cp | `log.chunk` | `logChunk` |
| node → cp | `node.event` | `nodeEvent` |
| node → cp | `node.pong` | *(none)* |
| cp → node | `control.hello-ack` | `controlHelloAck` |
| cp → node | `control.command` | `commandEnvelope` |
| cp → node | `control.cancel` | `commandCancel` |
| cp → node | `control.credential-rotated` | *(none — the node renews out of band)* |
| cp → node | `control.ack` | `{ "sequence": <int> }` |
| cp → node | `control.ping` | *(none)* |

### Session lifecycle

```
node                                             control plane
 │── node.hello {supportedProtocolVersions,   ──►│
 │              resumeToken?, lastReceivedSeq}   │
 │◄─ control.hello-ack {protocolVersion,      ───│
 │       resumeToken, lastReceivedSequence,      │
 │       grantedScopes, minimumAgentVersion}     │
 │── node.inventory (only if resume rejected) ──►│
 │                                               │
 │◄─ control.command {commandEnvelope}        ───│
 │── command.ack                              ──►│
 │── command.progress …                       ──►│
 │── command.result                           ──►│
 │                                               │
 │── node.heartbeat (every N seconds)         ──►│
```

**Protocol negotiation.** The node offers `supportedProtocolVersions`; the control plane picks one
and states it in `hello-ack`. If the chosen version is not in the node's list, the node closes the
connection and stays in a degraded state — heartbeating, refusing commands — rather than acting on
frames it may be misreading.

**Resume.** On reconnect the node sends its `resumeToken` and the highest control-plane sequence it
durably processed. The control plane replays only what follows. If it cannot (`resumeRejected`), the
node resends its full inventory and its unacknowledged frames.

**Reconnect.** Exponential backoff from 1s to 5min with full jitter. Jitter is not decoration: a
control-plane restart disconnects every node at once, and without it they would all return in the
same instant.

---

## Command payloads

The `payload` of a `commandEnvelope`, by verb:

| Command | Scope | Payload `$def` | Result `$def` |
|---|---|---|---|
| `DeployWorkload` | `workloads:write` | `deployWorkloadRequest` | `deployWorkloadResult` |
| `UpdateWorkload` | `workloads:write` | `deployWorkloadRequest` | `deployWorkloadResult` |
| `StopWorkload` | `workloads:write` | `workloadRequest` | `acknowledgedResult` |
| `StartWorkload` | `workloads:write` | `workloadRequest` | `acknowledgedResult` |
| `RestartWorkload` | `workloads:write` | `workloadRequest` | `acknowledgedResult` |
| `DeleteWorkload` | `workloads:write` | `deleteWorkloadRequest` | `acknowledgedResult` |
| `GetWorkloadStatus` | `workloads:read` | `workloadRequest` | `workloadStatus` |
| `StreamLogs` | `workloads:read` | `streamLogsRequest` | `acknowledgedResult` (+ `log.chunk` frames) |
| `CreateNetwork` | `networks:write` | `networkRequest` | `acknowledgedResult` |
| `DeleteNetwork` | `networks:write` | `networkRequest` | `acknowledgedResult` |
| `CreateVolume` | `volumes:write` | `volumeRequest` | `acknowledgedResult` |
| `SnapshotVolume` | `volumes:write` | `snapshotVolumeRequest` | `snapshotVolumeResult` |
| `RestoreVolume` | `volumes:write` | `restoreVolumeRequest` | `acknowledgedResult` |
| `CreateDatabaseAccessGrant` | `database-access:write` | `databaseAccessGrantSpec` | `databaseAccessGrantState` |
| `RevokeDatabaseAccessGrant` | `database-access:write` | `revokeDatabaseAccessRequest` | `databaseAccessGrantState` |
| `RotateDatabaseAccessCredential` | `database-access:write` | `rotateDatabaseAccessRequest` | `databaseAccessGrantState` |
| `RegisterHttpRoute` | `routes:write` | `registerHttpRouteRequest` | `routeResult` |
| `RegisterTcpRoute` | `routes:write` | `registerTcpRouteRequest` | `routeResult` |
| `RemoveRoute` | `routes:write` | `removeRouteRequest` | `acknowledgedResult` |
| `DrainNode` | `node:admin` | `drainNodeRequest` | `drainNodeResult` |
| `UpdateAgent` | `node:admin` | `agentUpdateRequest` | `agentUpdateResult` |

### Admission order

A command is checked in this order, and the first failure is the answer. The order matters: nothing
parses a payload that has not already been authorised.

1. **Protocol version** — frame `v` equals the negotiated version → else `unsupportedProtocolVersion`
2. **Allowlist** — `command` is in the catalog → else `unknownCommand`
3. **Freshness** — `issuedAt` within ±5 min → else `replayRejected`
4. **Nonce** — not seen before → else `replayRejected`
5. **Scope** — `requiredScope` matches the catalog's requirement *and* is granted → else `unauthorized`
6. **Drain** — mutating non-admin commands refused while draining → else `nodeDraining`
7. **Idempotency** — key already completed → replay the stored result, do not re-execute
8. **Payload validation** — shape, ranges, policy → else `validationFailed` / `policyDenied`
9. **Execute**, bounded by `timeoutSeconds` and cancellable

---

## Compatibility rules

Within major version 1:

- **Additive only.** New optional fields and new enum members may appear at any time.
- **Unknown fields are ignored**, never rejected. Every object is `additionalProperties: true`.
- **Unknown enum members degrade**, never throw: an unrecognised `errorCode` is `unknown`, an
  unrecognised `nodeEvent.kind` is passed through as an opaque string.
- **No field ever changes meaning or type.** A semantic change gets a new field and a deprecation
  note here.
- **Removing a field, tightening a constraint or adding a required field is a v2 change.**

A node and a control plane that disagree on the major version do not talk. That is the point: half
a protocol is worse than none.

---

## Files

| File | What it is |
|---|---|
| `node-agent.v1.schema.json` | JSON Schema 2020-12 for every message. Normative |
| `openapi.yaml` | The two REST endpoints (enroll, renew) in OpenAPI 3.1 |
| `CHANGELOG.md` | Every change to this contract, with the version it landed in |
| `examples/` | Valid instances used by the contract tests |
