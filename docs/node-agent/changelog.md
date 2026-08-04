# Node agent v1 — what each commit contains

Branch `feature/harbora-node-agent-v1`, off `origin/master` at `c6ff869`. Each commit is
independently revertible and leaves the build and the test suite green.

---

## 1. `fad78b5` — Node agent contract v1: schema, C# mirror and conformance tests

The wire contract, published as JSON Schema with a dependency-free C# mirror.

**Added**

```
contracts/node-agent/v1/README.md
contracts/node-agent/v1/CHANGELOG.md
contracts/node-agent/v1/node-agent.v1.schema.json
contracts/node-agent/v1/openapi.yaml
contracts/node-agent/v1/examples/command-envelope.deploy-workload.json
contracts/node-agent/v1/examples/app-manifest.postgres.json
contracts/node-agent/v1/examples/database-access-grant.temporary.json
src/Harbora.NodeAgent.Contracts/Harbora.NodeAgent.Contracts.csproj
src/Harbora.NodeAgent.Contracts/{NodeContract,NodeErrorCode,NodeCommands,Frames,
  CommandEnvelope,Enrollment,Inventory,WorkloadSpec,AppManifest,DatabaseAccess,
  Tunnel,NodeUpdate,CommandPayloads}.cs
tests/Harbora.NodeAgent.Tests/Harbora.NodeAgent.Tests.csproj
tests/Harbora.NodeAgent.Tests/{RepoPaths,ContractSchemaTests,ContractExampleTests}.cs
```

**Modified** — `Harbora.slnx` (two project entries).

Covers brief sections 9 and 13. 47 tests.

---

## 2. `b369822` — Node agent core: identity, enrollment, mTLS channel, command admission

The service itself: it enrolls, stays connected, and refuses anything the contract does not permit.

**Added**

```
src/Harbora.NodeAgent/Harbora.NodeAgent.csproj
src/Harbora.NodeAgent/{Program,NodeAgentOptions,AgentVersion,appsettings.json}
src/Harbora.NodeAgent/Identity/NodeIdentityStore.cs
src/Harbora.NodeAgent/Enrollment/{EnrollmentClient,EnrollmentService}.cs
src/Harbora.NodeAgent/Transport/{ControlPlaneTls,MessageTransport,ReconnectPolicy,
  ChannelOutbox,ControlChannel}.cs
src/Harbora.NodeAgent/Commands/{CommandContext,CommandLedger,CommandDispatcher}.cs
src/Harbora.NodeAgent/Runtime/{IContainerRuntime,DockerContainerRuntime}.cs
src/Harbora.NodeAgent/Inventory/{HostFacts,InventoryCollector}.cs
src/Harbora.NodeAgent/Observability/{StructuredLogging,NodeMetrics,NodeHealthEvaluator,
  MetricsEndpoint}.cs
src/Harbora.NodeAgent/Security/SecretRedactor.cs
src/Harbora.NodeAgent/State/{JsonFileStore,NodeState}.cs
src/Harbora.NodeAgent/Auditing/NodeAuditLog.cs
src/Harbora.NodeAgent/Hosting/{NodeAgentWorker,ChannelResponder,LedgerSweeper}.cs
tests/Harbora.NodeAgent.Tests/Fakes/{TestDoubles,TestFactories}.cs
tests/Harbora.NodeAgent.Tests/{Enrollment,ControlChannel,CommandAdmission,
  SecretRedaction,NodeHealthAndVersion}Tests.cs
```

**Modified** — `Harbora.slnx` (one project entry).

Covers sections 4, 5 (enrollment half), 6, 7 (admission half), 12. 180 tests.

**Defect found while testing:** an operator's cancel was reported to the control plane as a timeout,
because both surface as `OperationCanceledException` on the same linked token. The panel would have
retried something a human deliberately stopped.

---

## 3. `0b87cfd` — Workload policy, deployment with rollback, and the sixteen runtime verbs

**Added**

```
src/Harbora.NodeAgent/Runtime/{WorkloadPolicy,WorkloadRegistry,HealthProbe,
  WorkloadDeployer,VolumeArchiver,RouteRegistry}.cs
src/Harbora.NodeAgent/Commands/ImplementedCommands.cs
src/Harbora.NodeAgent/Commands/Handlers/{Workload,Infrastructure,Route}Handlers.cs
src/Harbora.NodeAgent/Hosting/{ChannelEventPublisher,StateReconciler}.cs
tests/Harbora.NodeAgent.Tests/{WorkloadPolicy,Deployment,CommandHandler}Tests.cs
```

**Modified** — `Program.cs`, `NodeAgentOptions.cs`, `InventoryCollector.cs`, `CommandContext.cs`,
`CommandDispatcher.cs`, `NodeAgentWorker.cs`.

Covers sections 7 (handlers), 8, 9. 292 tests.

**Three defects found while testing:**

1. Release ids were truncated version-7 GUIDs, which are timestamp-prefixed — two releases seconds
   apart shared a prefix, and since the id is part of the container name, a new release collided
   with the one it was meant to run alongside.
2. `StatusAsync` collapsed "containers are gone" into "stopped", so the reconciler would have tried
   to start containers that no longer existed.
3. Log chunks were sent fire-and-forget from a `Progress<T>` callback, which posts asynchronously —
   lines could overtake each other and the final marker.

---

## 4. `dd85809` — External database access over an outbound tunnel, and isolated Docker workspaces

**Added**

```
src/Harbora.NodeAgent/Security/LocalSecretVault.cs
src/Harbora.NodeAgent/Database/{DatabaseEngineOperations,DatabaseAccessManager}.cs
src/Harbora.NodeAgent/Tunnels/{TunnelProtocol,GatewayTunnel,TunnelSupervisor}.cs
src/Harbora.NodeAgent/Workspaces/DockerWorkspaceProvisioner.cs
src/Harbora.NodeAgent/Commands/Handlers/DatabaseAccessHandlers.cs
src/Harbora.NodeAgent/Hosting/GrantSweeper.cs
tests/Harbora.NodeAgent.Tests/{DatabaseAccess,TunnelAndWorkspace}Tests.cs
tests/Harbora.NodeAgent.Tests/Fakes/DuplexStreams.cs
```

**Modified** — `NodeAgentOptions.cs`, `WorkloadDeployer.cs`, `Program.cs`.

Covers sections 10 and 11. 356 tests.

---

## 5. `0fb3ea1` — Agent self-update with rollback, node drain, and update metrics

**Added**

```
src/Harbora.NodeAgent/Updates/{AgentUpdater,DrainCoordinator}.cs
src/Harbora.NodeAgent/Commands/Handlers/NodeAdminHandlers.cs
tests/Harbora.NodeAgent.Tests/UpdateAndDrainTests.cs
```

**Modified** — `NodeAgentWorker.cs`, `Program.cs`.

Covers section 5 (update half). 373 tests.

---

## 6. Installer, uninstaller, systemd unit, release pipeline and documentation

**Added**

```
deploy/node-agent/{install.sh,uninstall.sh,build-release.sh,README.md}
deploy/node-agent/harbora-node-agent.service
.github/workflows/release-node-agent.yml
docs/node-agent/{README,installation,security,troubleshooting,merge-notes,changelog}.md
examples/node-agent/{agent.development.json,enrolling-a-node.md}
tests/Harbora.NodeAgent.Tests/DeploymentArtifactTests.cs
```

**Modified** — `Program.cs` (a `--version` flag, which the installer and the updater both use).

Covers section 15. 412 tests.

---

## 7. Container-backed integration tests and the node-agent CI gate

**Added**

```
tests/Harbora.NodeAgent.Tests/DockerIntegrationTests.cs
.github/workflows/ci-node-agent.yml
```

The only tests that need a real daemon: they exercise `DockerContainerRuntime` against Docker with
throwaway containers, which is the one thing the fake runtime cannot validate. Where no daemon is
reachable they report an explicit skip with a reason rather than passing vacuously — and the CI
workflow fails the build if they skip on a runner that *does* have Docker, so the gate cannot
quietly stop testing anything.

The workflow also cross-publishes both release architectures, because a build that only works on the
developer's framework-dependent path is a release that fails at tag time.

Covers section 14's integration-container requirement. 412 passing, 17 environment-gated.

---

## 8. The control plane

The panel's half of the contract, so a node has something to enroll with.

**Added**

```
src/Harbora.Domain/Nodes/{Node,NodeEnrollmentToken,NodeCommandRecord}.cs
src/Harbora.Data/Migrations/*_NodeAgentV1.*
src/Harbora.Infrastructure/Nodes/{NodeAgentControlPlaneOptions,NodeCertificateAuthority,
  NodeEnrollmentService,NodeConnection,NodeChannelSession,NodeCommandService,
  NodeHeartbeatMonitor,NodeTunnelGateway}.cs
src/Harbora.Web/Controllers/Api/{NodeAgentController,NodesController}.cs
src/Harbora.Web/Infrastructure/{NodeClientCertificate,NodeChannelEndpoint}.cs
deploy/traefik/dynamic/node-agent.yml
docs/node-agent/control-plane.md
tests/Harbora.Tests/{NodeEnrollmentTests,NodeControlPlaneTests}.cs
```

**Modified** — `HarboraDbContext.cs` (four DbSets and their configuration), `DependencyInjection.cs`
(registrations), `Harbora.Infrastructure.csproj` and `Harbora.Tests.csproj` (a project reference each),
`Harbora.Web/Program.cs` (WebSockets, one registration, one endpoint), `Harbora.slnx`,
`docs/node-agent/{README,merge-notes}.md`, `contracts/node-agent/v1/README.md` (tunnel framing),
`src/Harbora.NodeAgent.Contracts/Tunnel.cs` and `src/Harbora.NodeAgent/Tunnels/TunnelProtocol.cs`
(the frame type moved into the contract, where a wire format belongs).

Covers the control-plane side of sections 5, 6, 7, 10 and 13.

412 + 17 gated on the agent's suite; 1213 on the panel's, up from 1145.

---

## 9. The node screens

**Added**

```
src/Harbora.Web/Controllers/NodesController.cs
src/Harbora.Web/ViewModels/NodeViewModels.cs
src/Harbora.Web/Views/Nodes/{Index,Detail}.cshtml
tests/Harbora.Tests/NodeUiTests.cs
```

**Modified** — `NavigationMap.cs` and `_Sidebar.cshtml` (one item and its label pair);
`Controllers/Api/NodesController.cs` renamed to `NodeAdminApiController` so the panel's own
`NodesController` can own the controller name MVC resolves links by. The API route is unchanged.

Bilingual, logical direction classes throughout, and held to the same honesty gate as the rest of
the panel: a measurement goes through `Design/_Metric`, and "never heartbeat" reads as *never*
rather than as a long time ago.

1246 tests on the panel's suite, up from 1213.
