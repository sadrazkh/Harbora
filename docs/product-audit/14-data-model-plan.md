# 14 — Data Model Plan

Current model: 63 tables + 1 owned type, 46 Npgsql migrations, GUIDv7 ids, EF global query filters for tenancy (24 filtered entities, deny-by-default `Guid.Empty` scope), belt-and-braces explicit predicates, deliberate denormalized `WorkspaceId` on `Deployment`/`Environment` so filters don't hide orphans from reconcilers. **No soft delete anywhere; no migrations proposed by this audit — planning only.**

## 1. Verdicts on the existing model
- **Keep:** tenancy filter mechanism + `SystemWorkspaceScope` for background work + `IgnoreQueryFilters` discipline (206 uses, each purposeful); instance-size values copied onto App/Service at creation (tier edits don't retro-change running apps); hash-only tokens; encrypted-at-rest secrets; append-only audits.
- **Debt to schedule:** `App.EnvironmentId`/`ManagedService.EnvironmentId` nullable "during the transition" → finish (07 §2.1). Dead columns (03 P3 list) → drop in one cleanup migration per area **when that area is next touched**, never as a standalone churn migration. Three parallel plan tables (`Plan`/`StoragePlan`/`AiPlan`) → acceptable now; converge only if/when billing arrives (P3). Two audit shapes (`AuditLog` vs `DatabaseAccessAudit`) → leave; unify into notification/incident reporting at the read layer.
- **Add indexes:** `MetricRollups(ServerId,Name,ResourceRef,Period,PeriodStart)` (R-34); partial unique on active backup snapshots per target (R-02/28); `Notification(UserId,ReadAt,CreatedAt)` when 09 lands.
- **Retention column policy:** every new table with unbounded growth declares retention in its plan row here and is wired to the Phase-2 sweeper (R-14).

## 2. Mission entity checklist → disposition

| Mission entity | Exists as | Plan |
|---|---|---|
| User / Workspace / Membership / Role | `User`, `Workspace`, `WorkspaceMember`, enums + `ProjectGrant` | Keep. Add optional `AppId` to `ProjectGrant` for per-app grants (05 §B5). |
| Node | `Node` (+`NodeEnrollmentToken`, `NodeCommandRecord`, `NodeEventRecord`) and legacy `Server` | Keep both until legacy sunset; `Server` remains the placement/capacity row (v1 nodes link via `NodeServerLink`). |
| Application / Environment | `App`, `Project`, `Environment` | Finish EnvironmentId requiredness. |
| Build / Deployment / Release | `Deployment` (build+release merged, immutable, config snapshot) | Keep merged — splitting Build/Release adds nothing at this scale; `ImageTag` is the artifact ref, `ConfigJson` the release snapshot. |
| Domain / Certificate | `DomainName`, `Certificate` (observed mirror; Traefik owns truth) | Keep observer model; add `Certificate` writer for `Revoked` or drop the enum member. |
| DatabaseService | `ManagedService` + `DatabaseAccessGrant/Audit` | Add `ProvisionError` text; keep grants. |
| Volume | `Volume` | Add `Protected` flag (07 §3.2). |
| Backup / Restore | dual systems (legacy 4 + module 4 + owned RetentionPolicy) | Convergence per 08 §A1; add active-uniqueness; assign `SafetySnapshotRef`. |
| Metric / Alert | `MonitoringMetric`, `MetricRollup`, `Alert` | Split `Alert` → `NotificationChannelIntegration` + `AlertRule` (09 §4.3); add `AlertIncident` (08 §B2.4): id, ruleRef/eventType, scope refs, openedAt, resolvedAt, lastNotifiedAt — the incident timeline row. |
| Notification / NotificationPreference / NotificationDelivery | **none today** | New per 09 §4.1: `NotificationEvent` (dedupKey unique-in-window, retention 90 d), `Notification` (rendered per-recipient, retention 180 d or user-capped), `NotificationPreference`, `NotificationDelivery` (retention 90 d), quiet-hours fields. Workspace-scoped where the event is; user-scoped rows unfiltered-but-user-keyed (same pattern as `ApiToken`). |
| EmailService / EmailDomain / EmailSender / EmailCredential / EmailTemplate / EmailMessage / EmailDelivery / EmailEvent / EmailSuppression / EmailUsage / ProviderConnection | **none today** | New module (10 §4). Ownership: `EmailService` → Environment (hard requirement — prod/staging isolation); domains/senders → workspace-owned, service-linked; messages/deliveries env-scoped; suppression workspace-scoped; usage `(WorkspaceId, ServiceId, Period)` unique. Retention: messages 30 d (dev-inbox 7 d), events 90 d, usage forever. Never show in UI: credential secrets after first reveal, raw provider webhook payload bodies (log-level only), full recipient lists in shared views (mask). Indexes: `EmailMessage(ServiceId, CreatedAt)`, `EmailDelivery(MessageId)`, `EmailSuppression(WorkspaceId, Address)` unique. |
| Integration | per-feature rows (GitProvider, channel integrations) | No generic table — deliberate (13 §1). |
| ApiToken / Activity / UsageRecord / ResourceLimit / Plan / CreditTransaction | ApiToken ✅, AuditLog ✅, UsageRecord ✅, Plan+InstanceSize ✅; CreditTransaction ❌ | `CreditTransaction`/invoicing = P3 with billing; extend `UsageRecord` coverage to services/volumes/buckets first (05 §C). |

## 3. Status enums — additions only
`RestoreJobStatus` gains explicit transition map; new `NotificationDeliveryStatus {Pending, Sent, Failed, Suppressed}`; new `EmailDeliveryStatus {Queued, Sent, Delivered, Bounced, Complained, Failed, Suppressed}`; new `EmailServiceMode {Sandbox, Live, DevInbox}`. Existing enums: never renumber (wire-format rule the codebase already follows).

## 4. Ownership & scoping rules (restate for new work)
- Every tenant-visible row carries `WorkspaceId` (denormalized if reachable only via joins) + global filter + explicit predicate in controllers.
- Environment-scoped resources also carry `EnvironmentId` (required for new tables from day one — learn from the App transition).
- Background services use `SystemWorkspaceScope` and must pass ids explicitly (memory: tenant filter kills sessionless work — webhooks/sweepers reading an empty DB and reporting success is a known failure class; the codebase's `IgnoreQueryFilters` discipline in collectors exists for this reason).
- Platform rows (nodes, plans, settings, templates) stay unfiltered with the documented rationale block pattern (`HarboraDbContext.cs:590-598`).

## 5. Migration discipline (unchanged, restated)
One migration per feature PR; `MigrationConsistencyTests` guards model drift and designer pairing; never `--no-build` after model edits (team memory: stale-assembly migrations); pre-upgrade dump remains the boot-time gate. Column drops for dead fields ride the next feature migration of their area.
