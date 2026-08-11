# Node agent v1 — merge and migration notes

Branch: `feature/harbora-node-agent-v1`, developed in a separate git worktree so it never shared a
working tree with `feature/harbora-platform-expansion-v1`.

---

## What this branch touches

**Added** (nothing here existed before):

```
contracts/node-agent/v1/**                   the wire contract
src/Harbora.NodeAgent.Contracts/**           its C# mirror, dependency-free
src/Harbora.NodeAgent/**                     the agent
src/Harbora.Domain/Nodes/**                  Node, tokens, command and event records
src/Harbora.Infrastructure/Nodes/**          CA, enrollment, channel, commands, gateway
src/Harbora.Web/Controllers/{Nodes,Api/Node*}.cs  the panel screens and the APIs
src/Harbora.Web/Views/Nodes/**               the fleet list and one node's detail
src/Harbora.Web/ViewModels/NodeViewModels.cs
src/Harbora.Web/Infrastructure/Node*.cs      client-certificate resolution, channel endpoint
src/Harbora.Data/Migrations/*_NodeAgentV1.*  four new tables
tests/Harbora.NodeAgent.Tests/**             the agent's suite
tests/Harbora.Tests/Node*Tests.cs            the control plane's
tests/Harbora.NodeIngress.Tests/**           both at once: real sockets, real mTLS, real HTTP
deploy/node-agent/**                         installer, uninstaller, unit, release script
deploy/traefik/dynamic/node-agent.yml        mTLS routing for the node endpoints
docs/node-agent/**                           installation, security, control plane, troubleshooting
examples/node-agent/**                       dev config and a worked enrollment
.github/workflows/{ci,release}-node-agent.yml
```

**Modified** — seven files, each by an appended block. The full list with conflict risk is
in [Additional files touched outside the node's own tree](#additional-files-touched-outside-the-nodes-own-tree)
below.

Untouched: every view and Vue island, the template catalog, the AI gateway, the CLI, the existing
`Harbora.Agent`, and every pre-existing domain entity.

---

## The existing `Harbora.Agent` is deprecated, and still works

> **Deprecated as of v0.2.0.** Node Agent v1 is the way to add a node. The legacy HTTP agent is
> frozen: it receives fixes for security and for outright breakage, and nothing else. **Support
> continues for at least two minor versions** after v0.2.0 — an end-of-life release will not be
> named before then, and when it is named it will be named here first. Nodes already running it keep
> working; no upgrade is forced on anybody.
>
> Two things an operator should know before choosing it for something new. It ships with **no
> automated tests at all** — `src/Harbora.Agent` is a single `Program.cs` and no test project
> references it, which is why it is frozen rather than developed. And a server reached through it
> **cannot back up its own volumes or databases**: `BackupEngine` refuses in front of the work
> rather than writing an archive onto a disk the panel cannot read. See
> [disaster-recovery.md](../disaster-recovery.md).

`src/Harbora.Agent` is the inbound HTTP agent: a bearer token, port 9700, a thin proxy over the
Docker API. Panels talk to it through `RemoteDockerEngine`.

Its code is deliberately left alone. Nodes running it keep working exactly as before, and the
panel's `ServerEngineFactory` path is unchanged. The two coexist on the same fleet — they are
different services, on different ports, with different credentials.

The difference between them, for whoever decides when to migrate:

| | `Harbora.Agent` (existing) | `Harbora.NodeAgent` (this branch) |
|---|---|---|
| Direction | Inbound: the panel connects to port 9700 | Outbound only: the node dials the panel |
| Firewall | Needs an inbound rule on the customer's server | None |
| Credential | A static bearer token, optional mTLS | Enrollment token → mTLS certificate, rotated |
| Command surface | The Docker API, effectively | 25 named verbs, no arbitrary shell |
| Idempotency | None | Per-command, durable |
| Replay protection | None | Nonce + freshness window, durable |
| Rollback on failed health | No | Yes, automatic |
| Self-update | No | Yes, with rollback |
| Database access | Not modelled | TTL-bounded grants over an outbound tunnel |
| Audit | Panel-side only | Panel-side plus a local, append-only log |

---

## The control plane side is implemented

Originally this branch shipped the node and the contract only. The control-plane half now ships with
it — see [control-plane.md](control-plane.md).

Implemented:

| Contract surface | Where |
|---|---|
| `POST /api/node-agent/v1/enroll` | `NodeAgentController` → `NodeEnrollmentService` |
| `POST /api/node-agent/v1/credential/renew` | same |
| `WSS /api/node-agent/v1/channel` | `NodeChannelEndpoint` → `NodeChannelSession` |
| TCP gateway | `NodeTunnelGateway` |
| Node CA | `NodeCertificateAuthority` — created on first use, key encrypted with the platform master key |
| Command issue and correlation | `NodeCommandService` |
| Admin API | `NodesController` at `/api/v1/nodes`, capability `servers.manage` |

### Additional files touched outside the node's own tree

| File | Change | Conflict risk |
|---|---|---|
| `Harbora.slnx` | Three `<Project>` lines | Low — keep both sides' lines |
| `src/Harbora.Data/HarboraDbContext.cs` | Four `DbSet`s and their configuration, appended | **Medium** — both changes are additions to the same two regions; resolve by keeping both |
| `src/Harbora.Data/Migrations/` | One new migration, `NodeAgentV1` | **Medium** — if the other branch also adds a migration, both apply, but the model snapshot conflicts and needs regenerating with `dotnet ef migrations add` after the merge |
| `src/Harbora.Infrastructure/DependencyInjection.cs` | One block of registrations | Low |
| `src/Harbora.Infrastructure/Harbora.Infrastructure.csproj` | One `<ProjectReference>` | Low |
| `src/Harbora.Web/Program.cs` | `UseWebSockets`, one service registration, `MapNodeChannel()` | Low |
| `tests/Harbora.Tests/Harbora.Tests.csproj` | One `<ProjectReference>` | Low |
| `src/Harbora.Infrastructure/Navigation/NavigationMap.cs` | One sidebar item | Low |
| `src/Harbora.Web/Views/Shared/Design/_Sidebar.cshtml` | One label pair | Low |
| `deploy/traefik/dynamic/node-agent.yml` | New file | None |

Still untouched: every view, the template catalog, the AI gateway, the CLI, the existing
`Harbora.Agent`, and every existing domain entity.

### Resolving the migration conflict, if there is one

Two branches adding migrations produce a conflicting `HarboraDbContextModelSnapshot.cs`. The
migrations themselves do not conflict — they create different tables. After merging:

```bash
git checkout --theirs src/Harbora.Data/Migrations/HarboraDbContextModelSnapshot.cs
dotnet ef migrations remove --project src/Harbora.Data --startup-project src/Harbora.Web   # if needed
dotnet ef migrations add MergeSnapshot --project src/Harbora.Data --startup-project src/Harbora.Web
```

Regenerating is safer than hand-merging a snapshot: the snapshot is generated output, and a
hand-edited one that disagrees with the model produces migrations that are wrong in ways EF cannot
detect until they run against a real database.

## Versioning

The node agent inherits the product version from `Directory.Build.props` rather than carrying its
own. That is a deliberate choice against the alternative: the panel already reports a single version
at `/api/v1/version`, and "your agent is older than your panel" is only a meaningful sentence while
both numbers come from the same place.

The consequence is that a node-agent release is tagged `node-agent-v<product-version>` and the
release workflow is separate, so a CLI release and a node-agent release move independently.

---

## Merge order with `feature/harbora-platform-expansion-v1`

Either order works; the conflicts are all additive. Expect to resolve:

- **`Harbora.slnx`** — keep every `<Project>` line from both sides. The file is a flat list.
- **`HarboraDbContext.cs`** — keep both sides' `DbSet`s and both sides' `OnModelCreating` blocks.
- **`HarboraDbContextModelSnapshot.cs`** — regenerate rather than hand-merge; see above.
- **`DependencyInjection.cs`, `Program.cs`** — keep both sides' registrations.

Merging this branch second is slightly easier, because regenerating the EF snapshot is the last
thing either branch needs and doing it once is less work than doing it twice.

---

## Known gaps, stated rather than hidden

| Gap | Consequence | Where it would go |
|---|---|---|
| Update signature verification | `agentUpdateRequest.signatureBase64` is in the contract; the agent enforces only the SHA-256 today. A checksum protects against corruption and a swapped file, not against a compromised release host | `AgentUpdater.ApplyAsync`, plus a pinned release public key in configuration |
| Rolling update with >1 replica | `UpgradeMode.RollingUpdate` is accepted and behaves as blue/green, which is correct for a single replica and not yet meaningful for several | `WorkloadDeployer` |
| Compose stacks | `workloadSpec.composeFile` is carried and validated, but the deployer expands only `containers[]` | `WorkloadDeployer`, reusing the panel's existing compose import |
| Route publication | The node reports `host:port` for a route; Traefik still lives with the control plane. A node-local proxy is not attempted | Control plane |
| `RotateDatabaseAccessCredential` overlap | `overlapSeconds` is accepted and logged as unsupported — none of the four engines can hold two live passwords for one user | Contract note; would need a second user per rotation |
| Metrics endpoint auth | Loopback-only, no authentication. Anyone already on the box can read it — which is the same population that can read the state directory | Fine as is; documented |
| Multi-replica command routing | `NodeChannelRegistry` is per-instance, so a command only works on the replica holding that node's socket; others answer 503. Single-instance Harbora is unaffected | A shared routing layer, or pinning node routes to one replica |
| Ingress tunnel bandwidth | Every request to an app on a tunnelled node passes through the panel. That is the trade the tunnel is, and it is stated on the node page — but a panel serving a busy tunnelled fleet is carrying traffic it does not carry for a direct one | Nothing to fix; capacity planning |
| Ingress tunnel across panel replicas | `NodeIngressRegistry` is per-instance, like `NodeChannelRegistry`. A node's tunnel lands on one replica, and only that replica's listeners can serve it. Single-instance Harbora is unaffected | The same shared routing layer the command path would need |
| Building on a v1 node | The contract has no build verb, deliberately: a build context is arbitrary code plus an arbitrary Dockerfile. An app deployed from a Git source cannot be placed on a v1 node, and `NodeWorkloadEngine.BuildImageAsync` refuses by name rather than failing obscurely | Build centrally, push to a registry, let the node pull by digest |
| Release tasks on a v1 node | A release task needs a one-off container, which is a shell with extra steps and so is not a verb. `RunOneOffAsync` throws `NodeCapabilityException` instead of skipping the task quietly | A narrow `RunReleaseTask` verb whose command comes from the app manifest rather than from the request |

Closed since this table was written: **snapshot shipping and volume backups on a v1 node**. The
node snapshots into its private archive volume and exchanges the artifact only with its configured
control plane through a one-use, one-direction bearer ticket. The panel verifies the node checksum,
then applies its normal encryption and Local/S3/SFTP storage path; destination credentials never
leave the panel. Restore verifies the checksum before an extract-first, rollback-capable swap.

Also closed: **per-container statistics from a v1 node**. `GetStatsAsync`
returned null, so the metrics collector had host pressure from the heartbeat and no per-app CPU or
memory. `GetWorkloadStats` is that verb, added in contract v1.3.0 and dispatched by
`NodeWorkloadEngine.GetStatsAsync`.

---

## Rollback

Each phase is one commit, and each commit is independently revertible:

```
fad78b5  contract v1
b369822  agent core: identity, enrollment, channel, admission
0b87cfd  workload policy, deployment, runtime verbs
dd85809  database access, tunnels, Docker workspaces
0fb3ea1  self-update, drain
6046e21  installer, uninstaller, systemd, release, docs
47f9a5d  container integration tests, CI gate
<this>   control plane: CA, enrollment, channel, gateway, admin API
```

Reverting the first seven commits removes the new directories and the three lines in `Harbora.slnx`.
Reverting the eighth additionally undoes the DbContext, migration, DI and Program.cs changes listed
above. No existing behaviour depends on any of it: with the node subsystem reverted, the panel
behaves exactly as it did on `c6ff869`, and `NodeAgent:GatewayListenPort` defaulting to 0 means even
an un-reverted install opens no new port until an operator configures one.
