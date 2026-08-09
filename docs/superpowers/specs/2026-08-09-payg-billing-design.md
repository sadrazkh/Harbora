# Pay-as-you-go billing — design

**Date:** 2026-08-09 · **Status:** approved for planning · **Scope:** one implementation plan

A provider sells Harbora workspaces to customers. Each workspace holds a balance the provider tops
up from the admin panel. Every hour the platform charges the workspace for what it has reserved.
When the balance reaches zero the workspace's workloads stop and refuse to start again until it is
topped up.

## What already exists

This module is built on tenancy machinery that is already in place, and reuses it rather than
duplicating it:

| Piece | Where | Used for |
|---|---|---|
| `Workspace` | `Harbora.Domain/Identity/Workspace.cs` | the customer boundary; already carries `PlanId` and `IsSuspended` |
| `Plan` | `Harbora.Domain/Tenancy/Plan.cs` | caps on apps, services, memory, CPU, disk; `MonthlyPrice` is display-only today |
| `IQuotaService` | `Harbora.Application/Abstractions/IQuotaService.cs` | denies create/deploy past a cap; reports `WorkspaceUsage` |
| `PlanOverage` | `Harbora.Infrastructure/Tenancy/PlanOverage.cs` | which caps a workspace is already past |
| Durable job queue | `Harbora.Infrastructure/Jobs/` | per-kind retry policy, `ExclusiveWith` exclusion, startup gate |
| Alerts | existing alert types and delivery | low-balance warning |
| Postgres test lane | `tests/Harbora.Postgres.Tests/` | proving a unique index against real PostgreSQL |

`Plan.MonthlyPrice` stays as it is: a display figure. This module does not read it.

## Decisions

Recorded because each one closes an ambiguity that would otherwise be guessed at during
implementation.

1. **The balance belongs to the workspace, not the user.** Apps, databases and backups already
   belong to a workspace, and the tenancy filters, quotas and `IsSuspended` are all workspace-scoped.
   A user-level wallet would need a rule for which member pays when two share a workspace.
2. **Charging is on what is reserved, not what is measured.** A stopped app still occupies its
   volume. This also sidesteps `WorkspaceUsage.DiskUnmeasured` — volumes nobody has measured — which
   would otherwise silently under-bill.
3. **Every resource carries an hourly rate, and the plan's base rate is a per-hour minimum.**
   `charge(hour) = max(planBaseRate, Σ resource rates)`. This keeps "the plan is the floor" true
   while still giving every app a money line of its own, so "what did this app cost me" has a real
   answer.
4. **Whether a workspace may exceed its plan's caps is a flag on the plan.** Plans that allow it sell
   the excess; plans that do not keep today's hard wall through `IQuotaService`.
5. **A stopped workload is charged disk plus a small reserved-slot rate**, not zero and not the
   running rate.
6. **Bandwidth is part of the plan's allowance and is not metered.** See Known gaps.

## Data model

### `Wallet` (one per workspace)

| Field | Notes |
|---|---|
| `WorkspaceId` | unique |
| `BalanceMinor` | `long`, **integer minor units** (rial). Never `decimal`/`double` — repeated addition of a floating type bends a bill over time |
| `Currency` | ISO code, fixed per install |
| `LowBalanceHours` | warn when the balance is under this many hours of current burn |
| `ConcurrencyStamp` | EF concurrency token, so two writers cannot both win |

The wallet is a **cached total**. The ledger is the truth, so any drift is detectable — a reconcile
check compares `SUM(ledger.AmountMinor)` to `BalanceMinor`.

### `BillingLedgerEntry` (append-only)

| Field | Notes |
|---|---|
| `WorkspaceId` | |
| `OccurredAt`, `BillingHour` | `BillingHour` is the UTC hour this line pays for |
| `Kind` | `Charge`, `Credit`, `PlanMinimumTopUp`, `Adjustment` |
| `AmountMinor` | signed; credits positive, charges negative |
| `ResourceType` | `App`, `Service`, `Volume`, `PlanBase`, `None` |
| `ResourceId` | nullable |
| `ResourceName` | **copied, not joined** — a deleted app must still be readable on an old bill |
| `RunState` | `Running`, `Stopped`, `NotApplicable` |
| `RatePerHourMinor`, `Hours` | so the arithmetic on the line can be checked by eye |
| `Description`, `CreatedByUserId` | credits record who made them |

**Nothing updates or deletes a ledger row.** A correction is a new `Adjustment` line.

**Unique index** on `(WorkspaceId, ResourceType, ResourceId, BillingHour)` over **every line the tick
writes** — that is, `Kind IN (Charge, PlanMinimumTopUp)`. It deliberately does **not** cover `Credit`
or `Adjustment`, which are made by a person and may legitimately repeat within an hour.

This is what makes a retried tick harmless: the second attempt collides and changes nothing. Scoping
it to `Charge` alone would leave the `PlanMinimumTopUp` line free to be written twice — which is the
same double-charge the index exists to prevent, arriving through the one line that has no resource
behind it. The top-up line therefore carries `ResourceType = PlanBase` and a null `ResourceId`, so it
has a stable key to collide on.

It must be proven in the Postgres lane, because a partial unique index is behaviour only a real
database has.

### `Plan` gains

- `BaseRatePerHourMinor` — the floor
- `AllowsOverage` — whether `IQuotaService` walls at the caps or sells past them
- overage rates per unit-hour: CPU core, GB memory, GB disk, extra app, extra service

### `InstanceSize` gains

- `RunningRatePerHourMinor`
- `StoppedRatePerHourMinor` — the reserved-slot part; disk is charged separately per GB

## The hourly tick

A job on the existing durable queue, `ExclusiveWith` keyed to the billing hour.

1. Opens a **`SystemWorkspaceScope`**. Without it the EF tenancy filters return nothing, the tick
   charges no one, and reports success — the failure mode this platform has already been bitten by.
2. Charges the hour that has **ended**. Never the future.
3. For each workspace: enumerate billable resources, read each one's run state from the platform's
   own record of it, resolve its rate, write one ledger line each.
4. If `Σ resource rates < planBaseRate`, write one `PlanMinimumTopUp` line for the difference, so the
   ledger sums exactly to what was deducted and the customer can see why.
5. Decrement the wallet once, under the concurrency token.
6. If the balance crosses to `≤ 0`, suspend (below).

**Missed hours.** If the panel was down, the tick backfills the hours it missed, oldest first, up to
`Billing:MaxBackfillHours` (default **72**). Reaching that bound is a `LogWarning` naming the
workspace and the hours dropped, never a silent skip — skipping quietly would mean free hosting for
the outage, the same class of defect as a sweeper that reports success having swept nothing.

Backfill uses each hour's own `BillingHour` key, so it is idempotent by the same index and a tick
that dies halfway resumes where it stopped.

## Suspension and resumption

`Workspace.IsSuspended` already exists and blocks new deploys. It is **extended, not duplicated**:

- `SuspendedReason` — `Manual` or `NoBalance`. Without it, a top-up would silently clear an
  administrator's deliberate suspension.
- On suspension, record which workloads were **running at that moment**. On resumption only those
  are started. An app the customer had deliberately stopped must not come back and start spending
  again.

**One guard, not four.** Every path that can start a workload — deploy, manual start, scheduled
cron, preview environment, template deployment — calls a single `IBillingGate.CanStart(workspaceId)`.
Two copies of a placement rule have already drifted apart silently in this codebase; this rule does
not get a second copy.

**Low-balance alert** fires before the balance reaches zero, using the existing alert machinery, so
the first thing a customer learns is not that their site is down.

## Admin and customer surfaces

- **Admin:** credit a workspace (amount, note) — writes a `Credit` ledger line and clears a
  `NoBalance` suspension if the balance is now positive. Sees balance, burn rate and hours of runway.
- **Customer:** balance, hours of runway at the current rate, and a per-resource breakdown —
  "this app: 10 h running, 14 h stopped, this much" — which is a `GROUP BY` over the ledger.

## Testing

The tests that matter are the ones that go red when a guard is deleted. Each of these is a mutation
the implementation must not survive:

| Delete this | This must go red |
|---|---|
| the `SystemWorkspaceScope` | a tick over N workspaces writes N sets of lines, not zero |
| the unique index | running one hour's tick twice deducts once |
| the guard from any one start path | a test that enumerates every start path and asserts each calls the gate |
| the stopped-rate branch | 10 h running + 14 h stopped produces two different rates |
| the `PlanMinimumTopUp` line | `SUM(ledger)` equals the wallet movement |
| `PlanMinimumTopUp` from the unique index | running one hour's tick twice writes one top-up line, not two |
| the `SuspendedReason` check | a top-up does not clear a manual suspension |
| the running-at-suspension record | resumption starts only what was running |

Plus: money is integer-only end to end; the unique index is exercised in the Postgres lane against a
real database; the admin credit page and the customer balance page are covered in the HTTP lane.

## Known gaps, stated rather than hidden

- **Bandwidth is not metered anywhere in Harbora.** A plan may state a traffic allowance, but nothing
  measures or enforces it. Plan copy must not imply otherwise. Metering traffic is a separate project
  — Traefik metrics or access logs, attribution to a workspace, storage and rollups.
- **Disk is billed on allocation, not measurement.** That is the decision, and it is also what makes
  billing predictable; but it means a customer who allocates 100 GB and uses 1 GB pays for 100.
- **Suspended workspaces keep their data.** How long a suspended workspace is retained before its
  volumes are reclaimed is a policy decision, not part of this module.
