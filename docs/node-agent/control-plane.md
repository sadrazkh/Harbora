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
| `NodesController` | `Harbora.Web/Controllers/Api` | `/api/v1/nodes` — tokens, listing, revoke, drain, update |

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

## Operating

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
