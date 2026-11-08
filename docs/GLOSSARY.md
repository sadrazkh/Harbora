Last verified: 2026-11-08
Scope: domain types in `src/Harbora.Domain` and terms used across the code and docs at commit `b3fd974`.

# Glossary

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md). **[Verified]** every entry, against the type or file named.

| Term | Meaning here | Where |
|---|---|---|
| App | One deployable workload: a web service, private service, worker or cron job | `Domain/Apps` |
| Adminer | Temporary web database console the panel starts on demand | `Services/AdminerService` |
| Approval | A second person's sign-off on a deployment to a protected environment | `DeploymentApprovals` |
| Attach | Connecting a managed service or bucket to an app, which injects its variables on the next deploy | `AppManagedServices` |
| Backup module | The newer, flag-gated backup system, as opposed to the legacy engine | `src/Modules/Backup` |
| Buildpack | Generated Dockerfile for a project that has none | `Deployments/Buildpacks.cs` |
| Capability | A named permission such as `apps.deploy`, resolved from a role | `Domain/Authorization/Capabilities.cs` |
| Coalesce | When a new deploy request lands on an unsettled deployment instead of starting one | `DeploymentEngine.cs:91-98` |
| Control plane | The panel, seen from a node | `docs/node-agent/control-plane.md` |
| Cutover | Starting new replicas, wiring the route, then retiring old ones | `DeploymentPipeline.cs` |
| Destination | Where a legacy backup is stored: Local, S3 or SFTP | `BackupDestinations` |
| Drain | Stopping new work on a node before an update | `NodeAdminApiController` |
| Effective environment | Everything a container receives: own variables, groups, attached services, providers | `Apps/EffectiveEnvironmentBuilder` |
| Enrollment | A node's single-use exchange of a token for a certificate | `NodeEnrollmentService` |
| Entitlement | Whether a workspace may use a feature: Enabled, Locked or Hidden | `Domain/Features` |
| Environment | A stage inside a project, with its own private network | `Domain/Projects` |
| Fixture (proof) | A small real project launched by `deploy/launch-proof.sh` | `deploy/proof-fixtures` |
| Function app | An app whose source is generated from rows, not a repository | `Functions/FunctionProject.cs` |
| Harbora.yml | A project's committed deploy configuration | `examples/harbora.yml`, `Cli/ProjectConfig.cs` |
| Ingress tunnel | Outbound connection that carries customer traffic to a NAT'd node | `NodeTunnelGateway` |
| Inspector | The deterministic reader that works out what a project is | `Harbora.Shared/Inspection` |
| Instance size | A named CPU and memory tier such as nano | `InstanceSizes` |
| Island | A Vue component mounted into one spot of a Razor page | `Scripts/main.ts` |
| Launch | The one-button flow from source to verified app | `Infrastructure/Launch` |
| Ledger | The append-only list of money movements for a workspace | `BillingLedger` |
| Legacy agent | The deprecated inbound HTTP agent | `src/Harbora.Agent` |
| Managed service | A database or broker the platform runs | `ManagedServices` |
| Master key | The key that encrypts stored secrets | `HARBORA_MASTER_KEY`, `MasterKeyResolver` |
| Node | A server running Node Agent v1 | `Nodes` |
| Panel | The ASP.NET Core application | `src/Harbora.Web` |
| Panel mode | Simple or Advanced view | `Domain/Identity/PanelMode.cs` |
| Plan | A tier of limits, rates and entitlements | `Plans` |
| PITR | Point-in-time recovery for PostgreSQL | `PitrRestoreService` |
| Preview | A temporary environment for a branch or pull request | `Projects/PreviewSweeper` |
| Prepaid hour | The first running hour debited when a resource is created | `ResourceCreationBilling` |
| Proposal | What the inspector found, as shown on the confirm screen | `LaunchContracts.cs` |
| Provider | The operator selling the platform; also an AI, Git or email provider, by context | various |
| Reconciler | A service that settles half-finished state after a restart | `JobReconciler`, `DeploymentReconciler` |
| Release task | A one-off command run before cutover, such as a migration | `App.ReleaseCommand` |
| Replica | Either an extra container of an app, or a read-only database standby, by context | `DeploymentPipeline`, `PrimaryManagedServiceId` |
| Restore point | A database dump taken automatically before migrating | `UpgradeSafetyService` |
| Round | A batch of work led from one owner request, with its own ledger | `.superpowers/sdd/rN` |
| Server | A host the panel can place work on: local, a node, or a legacy agent target | `Servers` |
| Suspension | Stopping a workspace's workloads when its balance runs out | `BillingSuspension` |
| Verb | A named command the panel may send a node | `NodeCommandCatalog` |
| Verdict | A launch's final state, earned from its checks | `LaunchAdvance.VerdictOn` |
| Voucher | A single-use code that credits a wallet | `BillingVouchers` |
| Wallet | A workspace's balance | `Wallets` |
| Workspace | The tenant boundary | `Workspaces` |
