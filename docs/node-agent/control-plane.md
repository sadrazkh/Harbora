# The control plane's half

What the panel does so that a node can enroll, stay connected, take commands and publish a database.

The node side is documented in [installation.md](installation.md) and [security.md](security.md);
the wire contract both halves implement is [`contracts/node-agent/v1/`](../../contracts/node-agent/v1/README.md).

---

## The pieces

| | Where | What it does |
|---|---|---|
| `NodeCertificateAuthority` | `Harbora.Infrastructure/Nodes` | Creates the node CA on first use, signs CSRs, issues the gateway's own TLS certificate |
| `NodeEnrollmentService` | `Harbora.Infrastructure/Nodes` | Mints single-use tokens, spends them, rotates and revokes credentials |
| `NodeChannelSession` | `Harbora.Infrastructure/Nodes` | One node's session: negotiation, resume, heartbeat, results, events |
| `NodeChannelRegistry` | `Harbora.Infrastructure/Nodes` | Which nodes are connected to this instance, and how to reach them |
| `NodeCommandService` | `Harbora.Infrastructure/Nodes` | Issues commands, records them, correlates the answers |
| `NodeHeartbeatMonitor` | `Harbora.Infrastructure/Nodes` | Marks nodes offline when they go quiet |
| `NodeTunnelGateway` | `Harbora.Infrastructure/Nodes` | Publishes database grants on public ports |
| `NodeAgentController` | `Harbora.Web/Controllers/Api` | `POST /api/node-agent/v1/enroll` and `…/credential/renew` |
| `NodeChannelEndpoint` | `Harbora.Web/Infrastructure` | `GET /api/node-agent/v1/channel` (WebSocket) |
| `NodeAdminApiController` | `Harbora.Web/Controllers/Api` | `/api/v1/nodes` — tokens, listing, revoke, drain, update |
| `NodesController` | `Harbora.Web/Controllers` | `/nodes` — the fleet screen and one node's detail |

Four new tables: `Nodes`, `NodeEnrollmentTokens`, `NodeCommands`, `NodeEvents`. Migration
`NodeAgentV1`, purely additive.

---

## Configuration

Under `NodeAgent:` in `appsettings.json`, or `NodeAgent__*` in `deploy/.env`:

```json
{
  "NodeAgent": {
    "PublicUrl": "https://panel.example.com",
    "MinimumAgentVersion": "0.2.0",
    "HeartbeatIntervalSeconds": 30,
    "EnrollmentTokenMinutes": 30,
    "TrustForwardedClientCertificate": true,
    "AutoRegisterAsServer": true,

    "TunnelGatewayUrl": "gw.example.com:8443",
    "GatewayListenPort": 8443,
    "GatewayPublicHost": "gw.example.com",
    "GatewayPublicPortStart": 41000,
    "GatewayPublicPortEnd": 41999
  }
}
```

| Setting | Notes |
|---|---|
| `PublicUrl` | What nodes keep using after enrollment, whatever URL the installer was given |
| `MinimumAgentVersion` | Nodes below it are told they are too old and refuse work themselves. Raising it is how a fleet is forced forward |
| `EnrollmentTokenMinutes` | Capped at 24 hours. The token's job is to survive a copy-paste, not to live in a wiki |
| `TrustForwardedClientCertificate` | **Off by default.** See the mTLS section — turning it on without the Traefik half removes authentication rather than adding it |
| `AutoRegisterAsServer` | On by default: an enrolled node becomes a deploy target. Off means nodes are attached by hand from the node's page. See [Scheduling onto a node](#scheduling-onto-a-node) |
| `GatewayListenPort` | `0` (the default) disables database tunnels entirely |

---

## Client certificates behind Traefik

The panel runs behind Traefik, which terminates TLS — so Traefik is what must request the node's
client certificate and pass it through. [`deploy/traefik/dynamic/node-agent.yml`](../../deploy/traefik/dynamic/node-agent.yml)
is a working configuration. Two things have to be true:

1. `clientAuthType: RequireAndVerifyClientCert` against the node CA, so a request with no
   certificate never reaches the panel.
2. `passTLSClientCert` with `pem: true`, so `X-Forwarded-Tls-Client-Cert` is **overwritten** on
   every request.

Only then set `TrustForwardedClientCertificate`. The flag is the operator asserting they did the
above; it is off by default because a certificate header any client can set is not authentication.

Export the CA for Traefik:

```sql
select value from "Settings" where "Key" = 'nodeagent.ca.certificate';
```

Kestrel with its own listener works too — `Connection.ClientCertificate` is checked first and no
header can override it. That path needs no flag and no Traefik configuration.

---

## Enrollment, end to end

1. An admin calls `POST /api/v1/nodes/tokens` (capability `servers.manage`). The response carries
   the plaintext token once and the exact install command to run.
2. The installer calls `POST /api/node-agent/v1/enroll` with a CSR.
3. The panel spends the token, signs the CSR, and stores the certificate's thumbprint.
4. The node opens the channel with that certificate.

The CSR contributes a public key and a proof of possession, and nothing else. Subject, validity, key
usage and basic constraints are all set by the panel — a CSR asking for `CA:true` is signed as an
ordinary leaf, which is asserted in `NodeCertificateAuthorityTests`.

A machine that re-enrolls with the same fingerprint reuses its node id. Two node rows for one
machine would compete for the same containers while the panel showed both as healthy.

---

## The channel

One WebSocket per node, to one panel instance. On connect:

- the certificate must chain to the CA **and** be the current credential of a node that is not
  revoked — the chain alone would let any node's certificate open any node's session
- the node presents a resume token; a match resumes the session, anything else starts a new one with
  `resumeRejected` set
- the panel acknowledges sequences only after the frame is durable, because a node trims its outbox
  on that number and cannot get those frames back

`NodeCommandService` writes a `NodeCommands` row before the frame goes out. A command that was
issued is on record even if the panel dies before hearing back.

---

## Running more than one panel replica

A node holds one socket to one instance, and `NodeChannelRegistry` is per-instance. So:

- **Enrollment, renewal and the read-only node APIs** work on any replica.
- **Commands** only work on the replica holding that node's socket. Others answer
  `503` with "not connected to this panel instance".

Single-instance Harbora — which is what `deploy/docker-compose.yml` runs — is unaffected. For more
than one replica, either pin the node routes to one instance or add cross-instance routing; see the
gap list in [merge-notes.md](merge-notes.md).

---

## The TCP gateway

Off unless `GatewayListenPort` is set. When on:

1. Nodes dial in over mTLS and register a grant.
2. The gateway checks the grant against **the command the panel issued**, not against the
   registration frame — a node that could widen its own allowlist would make the allowlist
   decorative.
3. A public port is allocated and the endpoint returned to the node.
4. Client connections are admitted against the IP allowlist, the connection cap and the rate limit,
   enforced here because this is the only place the client's real address exists.

The gateway serves a certificate the node CA issued, so an internal hostname needs no public
certificate. Node certificates are client-auth only and the gateway's is server-auth only, so
neither can be used in the other's role.

Ports `GatewayListenPort` and the public range must be reachable from wherever customers connect.
This is the one part of the node design that opens inbound ports — on **Harbora's** infrastructure,
which is the point: the customer's server opens none.

---

## Scheduling onto a node

The platform schedules onto `Server` rows: `NodeCapacityService` reads that table, the scheduler
reads capacity from it, and an app carries a `ServerId`. A `Node` that exists only as a node is
enrolled, connected and commandable — and invisible to every one of those.

So a node projects itself into the model that already exists. `NodeServerLink` gives each node a
`Server` row and rewrites it from the node's own reports on connect, on every heartbeat and on
disconnect. Nothing else in the scheduler changed.

**How a node-backed server is recognised.** Its `AgentEndpoint` is null. That null is load-bearing:
`ServerEngineFactory` looks for a linked node *before* it falls back to the local engine, because
the old fallback read "not local, no endpoint" as "this machine" — which for a node-backed server
would deploy a customer's app onto the panel's own Docker daemon. A non-local server with neither an
endpoint nor a node now throws instead of landing somewhere convenient.

**What runs the workload.** `NodeWorkloadEngine` implements `IDockerEngine` over the node's verbs.
The pipeline's container name doubles as the node's workload id, so "stop the container I just got
back" and "retire the containers carrying this app's label" both land on the right workload without
the pipeline knowing anything changed.

Three things it cannot do, and says so by name rather than pretending:

| Call | Behaviour |
|---|---|
| `BuildImageAsync` | Throws `NodeCapabilityException`. There is no build verb; a build context is arbitrary code plus an arbitrary Dockerfile. Deploy from a prebuilt image or a template |
| `RunOneOffAsync` | Throws `NodeCapabilityException`. Release tasks and volume backups need it, and "run this container to completion" is a shell with extra steps |
| `GetStatsAsync` | Returns null, meaning *not measured*. Host pressure still arrives on the heartbeat; a fabricated zero would draw a flat line across a busy container |

`ListContainersAsync` is the opposite case: it **throws** when the node does not answer. An empty
list would tell the pipeline there is nothing to retire, and it would cut traffic over to the new
container while leaving the old one running.

**Images are pinned before they are sent.** A node refuses an unpinned reference, correctly — a tag
cannot express "deploy the thing that was tested". `ImageDigestResolver` turns a tag into
`repository@sha256:…`, asking the panel's own Docker first and the registry second, so every node in
a fleet gets the same bytes rather than each resolving `:latest` at whatever moment it pulled.

**Tenancy on the node.** Every panel-scheduled workload is deployed under one tenant,
`harbora-platform`, not under the workspace. `IDockerEngine` is server-scoped — the metrics sweep
lists every container on a machine, a backup reaches whichever workspace owns the volume, a cutover
retires containers the current request has no workspace context for — so a per-workspace tenant
would be a lookup the interface cannot express, and the first caller to get it wrong would silently
see nothing. Workspace isolation is unaffected: it is enforced in the panel by the query filter that
decides which app a request may act on at all, and on the node by the per-workspace networks the
pipeline already names.

**Reaching the app.** There is no shared overlay between panel and node, so every container's port
is published to a per-deployment host port and the proxy routes to `hostname:port`. The hostname is
the node's own reported address, preferring a globally routable IPv4 one. A hostname an operator
typed is kept; a raw address is refreshed, so a node that changes address does not keep receiving
traffic at the old one.

> A node behind NAT enrolls, connects and deploys successfully — and its HTTP routes time out. The
> control channel is outbound-only by design; ingress is not, yet. See the gap table in
> [merge-notes.md](merge-notes.md).

**Turning it off.** `NodeAgent:AutoRegisterAsServer` (default `true`) decides whether enrolling a
node also makes it a deploy target. Set it to `false` on an install where nodes exist for something
else — publishing a database, say — and attach individual nodes from the node's page instead.
Detaching is refused while anything is placed on the node, because removing the `Server` row under a
running app leaves the panel showing it as deployed with no way to reach, stop or delete it.

---

## Operating

### From the panel

**Platform → Nodes** (capability `servers.manage`) lists the fleet with its live state, mints an
enrollment token and shows the exact install command once — the panel stores only a hash, so that
response is the single moment the token exists anywhere readable.

A node's page carries its inventory, granted scopes, capabilities, recent commands and recent
events, plus the three operations: drain, update the agent, revoke the credential. Two details worth
knowing when reading it:

- A node can show **online but not connected to this instance**. The row is what the database last
  recorded; the flag is whether this replica holds its socket. They disagree exactly when a command
  from here would fail, so the page says so instead of letting the 503 explain it later.
- **Privileged mode enabled** is shown as a warning rather than a tick. It means the machine's owner
  turned on a switch that lets a node-admin command run a privileged container.

### From the API

```bash
# Mint a token and get the install command back
curl -sX POST https://panel.example.com/api/v1/nodes/tokens \
  -H "Authorization: Bearer $HARBORA_TOKEN" -H 'Content-Type: application/json' \
  -d '{"nodeName":"web-01","region":"eu-central","environment":"production"}' | jq -r .install

# The fleet
curl -s https://panel.example.com/api/v1/nodes -H "Authorization: Bearer $HARBORA_TOKEN" \
  | jq -r '.[] | [.nodeId, .name, .status, .health, (.connected|tostring), .agentVersion] | @tsv'

# One node, with its recent commands and events
curl -s https://panel.example.com/api/v1/nodes/nd_xxx -H "Authorization: Bearer $HARBORA_TOKEN"

# Drain before maintenance, and put it back afterwards
curl -sX POST .../api/v1/nodes/nd_xxx/drain -d '{"drain":true,"reason":"kernel upgrade"}'
curl -sX POST .../api/v1/nodes/nd_xxx/drain -d '{"drain":false}'

# Update an agent (the checksum is mandatory)
curl -sX POST .../api/v1/nodes/nd_xxx/update-agent -d '{
  "targetVersion":"0.3.0",
  "downloadUrl":"https://github.com/sadrazkh/Harbora/releases/download/node-agent-v0.3.0/harbora-node-agent-linux-x64",
  "sha256":"…"}'

# Withdraw a credential. The node is not asked to cooperate.
curl -sX POST .../api/v1/nodes/nd_xxx/revoke -d '{"reason":"decommissioned"}'
```

There is deliberately **no** endpoint that forwards an arbitrary command to a node. The node's
allowlist is what makes a compromised panel survivable, and a passthrough here would move the
boundary from "twenty-one verbs" to "twenty-one verbs plus whatever that endpoint accepts".

---

## What to watch

| Symptom | Likely cause |
|---|---|
| A node enrolls but never connects | Traefik is not configured for mTLS on the channel route, or `TrustForwardedClientCertificate` is off. The panel logs which |
| `certificate … is not the current credential of any node` | A superseded certificate after a rotation. The node renews and reconnects on its own |
| Nodes flap between online and offline | The channel is being closed by a proxy idle timeout shorter than the 20-second keep-alive |
| Commands answer `503` | The node is connected to a different panel replica |
| `The node CA is present but could not be decrypted` | `HARBORA_MASTER_KEY` changed. Every node has to be re-enrolled against a new CA |
