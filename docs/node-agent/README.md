# Harbora Node Agent

A headless Linux service that runs on customer and infrastructure servers and executes work on
behalf of the Harbora control plane.

The property everything else follows from: **installing a node opens no inbound port**. Enrollment,
commands and database tunnels are all connections the node dials itself.

## Documents

| | |
|---|---|
| [installation.md](installation.md) | Installing, configuring, updating, draining, uninstalling |
| [security.md](security.md) | What the control plane can and cannot do to a node, and why |
| [troubleshooting.md](troubleshooting.md) | Symptoms → causes → fixes |
| [merge-notes.md](merge-notes.md) | What this branch touches, what the panel still has to implement, known gaps |
| [changelog.md](changelog.md) | What each commit on this branch contains |
| [`contracts/node-agent/v1/`](../../contracts/node-agent/v1/README.md) | The wire contract. Normative |
| [`examples/node-agent/`](../../examples/node-agent/) | Development configuration and a worked enrollment |

## Shape of it

```
        ┌── control plane ──┐
        │                   │
        │  enroll  (HTTPS)  │◄──────┐
        │  channel (WSS)    │◄────┐ │   all dialled by the node
        │  gateway (TLS)    │◄──┐ │ │
        └───────────────────┘   │ │ │
                                │ │ │
   ┌────────────────────────────┴─┴─┴──────────────────────────────┐
   │ node agent                                                    │
   │                                                               │
   │  identity ─ enrollment ─ channel ─ admission ─ handlers        │
   │                                        │                      │
   │                            ┌───────────┴────────────┐         │
   │                        workloads              db grants       │
   │                            │                       │         │
   │                     policy → deploy          engine + tunnel  │
   │                            │                                  │
   │                    ┌───────┴────────┐                         │
   │                    │ IContainerRuntime │  ← the only seam      │
   │                    └───────┬────────┘     to the runtime      │
   └────────────────────────────┼──────────────────────────────────┘
                                ▼
                              Docker
```

## Source layout

| Directory | What lives there |
|---|---|
| `Identity/` | Key generation, CSR, certificate storage |
| `Enrollment/` | Enrollment and renewal against the control plane |
| `Transport/` | TLS settings, the WebSocket channel, backoff, the durable outbox |
| `Commands/` | Admission, the ledger, the dispatcher, one handler per verb |
| `Runtime/` | The container-runtime seam, workload policy, deployment, health, volumes, routes |
| `Database/` | Grant lifecycle and per-engine credential operations |
| `Tunnels/` | The outbound TCP gateway protocol |
| `Workspaces/` | The isolated Docker workspace |
| `Updates/` | Self-update with rollback, drain |
| `Observability/` | Structured logging, redaction, metrics, health |
| `Auditing/` | The node's own append-only log |
| `State/` | Atomic, owner-only JSON on disk |
| `Hosting/` | The worker, reconciler and sweepers |
