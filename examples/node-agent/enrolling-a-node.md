# Enrolling a node — a worked example

What actually happens on the wire, in order, with real shapes. Useful when implementing the
control-plane side or when a node will not enroll and you want to know which step it stopped at.

The normative definitions are in [`contracts/node-agent/v1/`](../../contracts/node-agent/v1/README.md).

---

## 0. An admin mints a token

In the panel: **Servers → Add a node**. The panel stores a hash of the token, an expiry a few minutes
out, and a single-use flag.

```
hbr_enroll_9f2c41d7b83e4a5c
```

## 1. The installer runs on the node

```bash
curl -fsSL .../deploy/node-agent/install.sh | bash -s -- \
  --control-plane https://panel.example.com \
  --token hbr_enroll_9f2c41d7b83e4a5c \
  --name web-01 --region eu-central --environment production
```

It writes the token to `/etc/harbora-node/enrollment.token` (mode `0600`) and starts the service. The
token is never written into `agent.conf`, so the file an operator edits later never contained a
credential.

## 2. The agent generates a key and a CSR

An ECDSA P-256 key at `/var/lib/harbora-node/identity/node.key.pem`, mode `0600`. It never leaves
the machine — the panel only ever sees the CSR.

The subject is built structurally, not by formatting a string, so a node name containing a comma
cannot add its own RDNs.

## 3. `POST /api/node-agent/v1/enroll`

```http
POST /api/node-agent/v1/enroll HTTP/1.1
Host: panel.example.com
Authorization: Bearer hbr_enroll_9f2c41d7b83e4a5c
Content-Type: application/json
```

```json
{
  "enrollmentToken": "hbr_enroll_9f2c41d7b83e4a5c",
  "nodeName": "web-01",
  "certificateSigningRequestPem": "-----BEGIN CERTIFICATE REQUEST-----\nMIIBFz...\n-----END CERTIFICATE REQUEST-----\n",
  "agentVersion": "0.2.0",
  "supportedProtocolVersions": [1],
  "region": "eu-central",
  "environment": "production",
  "labels": { "tier": "premium" },
  "machineFingerprint": "9c1f…",
  "inventory": {
    "nodeName": "web-01",
    "hostname": "web-01",
    "osName": "Debian GNU/Linux",
    "osVersion": "12",
    "kernelVersion": "6.1.0-18-amd64",
    "architecture": "amd64",
    "containerRuntime": "docker",
    "containerRuntimeVersion": "27.3.1",
    "cpuCores": 4,
    "totalMemoryBytes": 8318558208,
    "totalDiskBytes": 214748364800,
    "freeDiskBytes": 187904819200,
    "ipAddresses": ["203.0.113.10"],
    "availablePortRange": { "start": 30000, "end": 32767 },
    "usedPorts": [22, 80, 443],
    "storage": { "totalBytes": 214748364800, "freeBytes": 187904819200, "dataRoot": "/var/lib/harbora-node" }
  },
  "capabilities": {
    "agentVersion": "0.2.0",
    "supportedProtocolVersions": [1],
    "supportedCommands": ["DeployWorkload", "…"],
    "supportedDatabaseEngines": ["postgresql", "mysql", "mongodb", "redis"],
    "supportsComposeStacks": true,
    "supportsRollingUpdate": true,
    "supportsVolumeSnapshots": true,
    "supportsTcpTunnel": true,
    "supportsIsolatedDockerWorkspace": true,
    "privilegedModeEnabled": false,
    "supportsSelfUpdate": true
  }
}
```

`machineFingerprint` is a hash of the machine id, not the machine id itself — enough to recognise a
re-enrollment of the same host without handing the panel one more thing worth stealing.

## 4. The panel answers

```json
{
  "nodeId": "nd_01JQ8F3K2M",
  "certificatePem": "-----BEGIN CERTIFICATE-----\n…\n-----END CERTIFICATE-----\n",
  "caCertificatePem": "-----BEGIN CERTIFICATE-----\n…\n-----END CERTIFICATE-----\n",
  "certificateNotAfter": "2026-11-02T09:14:00Z",
  "controlPlaneUrl": "https://panel.example.com",
  "tunnelGatewayUrl": "gw.harbora.example.com:8443",
  "protocolVersion": 1,
  "grantedScopes": [
    "workloads:read", "workloads:write", "networks:write", "volumes:write",
    "database-access:write", "routes:write", "node:admin"
  ],
  "minimumAgentVersion": "0.2.0",
  "heartbeatIntervalSeconds": 30
}
```

Failure answers are typed, and the agent treats them differently:

| Status | `code` | Agent behaviour |
|---|---|---|
| 401 | `enrollmentTokenInvalid` | stops — retrying cannot help |
| 409 | `enrollmentTokenAlreadyUsed` | stops |
| 410 | `enrollmentTokenExpired` | stops |
| 422 | `validationFailed` | stops |
| 5xx | `internal` | retries with backoff |

## 5. The agent stores the identity and shreds the token

```
/var/lib/harbora-node/identity/node.key.pem              0600
/var/lib/harbora-node/identity/node.crt.pem              0600
/var/lib/harbora-node/identity/control-plane-ca.crt.pem  0600
/var/lib/harbora-node/state/node.json                    0600
```

`/etc/harbora-node/enrollment.token` is overwritten with random bytes and deleted.

## 6. The channel opens

```
wss://panel.example.com/api/node-agent/v1/channel      (client certificate: the one just issued)
```

Node → panel:

```json
{
  "v": 1,
  "type": "node.hello",
  "id": "01924f7c2d317a4e9b106f0c2a1d8e33",
  "sentAt": "2026-08-04T09:14:07.881Z",
  "payload": {
    "nodeId": "nd_01JQ8F3K2M",
    "agentVersion": "0.2.0",
    "supportedProtocolVersions": [1],
    "resumeToken": null,
    "lastReceivedSequence": 0,
    "inventory": { "…": "as above" },
    "capabilities": { "…": "as above" }
  }
}
```

Panel → node:

```json
{
  "v": 1,
  "type": "control.hello-ack",
  "id": "…",
  "payload": {
    "protocolVersion": 1,
    "resumeToken": "sess_7f21a0",
    "serverTime": "2026-08-04T09:14:07.902Z",
    "lastReceivedSequence": 0,
    "heartbeatIntervalSeconds": 30,
    "grantedScopes": ["…"],
    "resumeRejected": false
  }
}
```

The node stores the resume token and presents it on the next connect. After that it heartbeats:

```json
{
  "v": 1,
  "type": "node.heartbeat",
  "id": "…",
  "payload": {
    "nodeId": "nd_01JQ8F3K2M",
    "agentVersion": "0.2.0",
    "health": "healthy",
    "load1": 0.42,
    "freeMemoryBytes": 5100273664,
    "freeDiskBytes": 187904819200,
    "runningWorkloads": 0,
    "draining": false,
    "certificateExpiresAt": "2026-11-02T09:14:00Z"
  }
}
```

## 7. A first command

```json
{
  "v": 1,
  "type": "control.command",
  "id": "…",
  "sequence": 1,
  "payload": {
    "commandId": "01924f7c2d317a4e9b106f0c2a1d8e34",
    "command": "GetWorkloadStatus",
    "idempotencyKey": "status:wl-7f2a91:2026-08-04T09:15",
    "nonce": "1f8c2b7d9e4a5c60",
    "issuedAt": "2026-08-04T09:15:00.000Z",
    "correlationId": "01924f7c2d317a4e9b106f0c2a1d8e34",
    "requiredScope": "workloads:read",
    "audit": { "actorName": "ops@example.com", "tenantId": "ws-acme", "sourceIp": "203.0.113.44" },
    "payload": { "workloadId": "wl-7f2a91", "tenantId": "ws-acme" }
  }
}
```

The node answers `command.ack`, then exactly one `command.result`.

Two things the control plane must get right here:

- **a fresh `nonce` on every send**, including a retry. A resent envelope with the same nonce is
  rejected as a replay
- **a stable `idempotencyKey` per logical operation**. That is what makes the retry safe: the node
  replays the original result instead of doing the work twice

---

## Renewal, later

At two thirds of the certificate's lifetime the node calls
`POST /api/node-agent/v1/credential/renew` over mTLS with a new CSR for the **same key**. Starting
early is the point: a renewal that begins at the last moment has one attempt, and the first attempt
is the one most likely to hit the outage that caused the delay.

If the panel answers 403 `credentialRevoked`, the agent stops and says so. It does not keep retrying
a credential an admin deliberately withdrew.
