# Pay-as-you-go Billing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A provider tops up a workspace's balance from the admin panel; the platform charges that workspace hourly for what it has reserved; at zero the workloads stop and refuse to start until it is topped up.

**Architecture:** An append-only ledger is the source of truth and a cached wallet balance is derived from it. An hourly job on the existing durable Postgres job queue computes one ledger line per billable resource, made idempotent by a partial unique index on `(WorkspaceId, ResourceType, ResourceId, BillingHour)`. Rate resolution and hour planning are pure rule classes with no database, so the arithmetic is testable without a container. Enforcement extends the existing `Workspace.IsSuspended` rather than duplicating it, behind a single start gate every start path calls.

**Tech Stack:** .NET 10, ASP.NET MVC, EF Core + Npgsql, xUnit + FluentAssertions, Testcontainers (`tests/Harbora.Postgres.Tests`).

**Spec:** `docs/superpowers/specs/2026-08-09-payg-billing-design.md`

## Global Constraints

- **Zero build warnings.** Not "no new warnings" — zero. `dotnet build Harbora.slnx -c Debug` must report `0 Warning(s)`.
- **Baseline:** at the time of writing, `dotnet test Harbora.slnx -c Debug` is 3,209 + 498 + 15 = **3,722 passing, 0 failing**, 17 Docker-gated skips + 50 Postgres-lane skips. No task may reduce the passing count.
- **Money is `long` minor units everywhere.** Never `decimal`, never `double`, never `float`. Repeated addition of a floating type bends a bill over time.
- **Never generate a migration with `--no-build`.** A migration built against a stale assembly captures the old model. `MigrationConsistencyTests` catches it, but do not rely on that.
- **Never renumber an existing enum value.** Rows hold these by value. Append only.
- **Test names read as sentences** — `A_retried_tick_charges_once`, not `TestRetry`. Read neighbouring test files before writing.
- **Commit messages are narrative** — a sentence about what changed in the world, not a `feat:` prefix. Read `git log --oneline -20` for the register. End every message with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Land a document before the test that reads it, or squash the pair.** A commit must not claim a verification that could not have run at that commit.
- **`docs/product-audit/19-do-not-change-list.md`** lists 30 protected behaviours. Read it before starting. If a change would touch one, stop and report.
- **Bilingual UI.** Every user-facing string added to a view needs its Persian counterpart, following the pattern in neighbouring views (`isFa`).
- **Design tokens only in views.** No hardcoded hex colours; use `var(--…)` from `Scripts/app.css`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Harbora.Domain/Billing/Wallet.cs` | cached balance per workspace |
| `src/Harbora.Domain/Billing/BillingLedgerEntry.cs` | append-only ledger row + its enums |
| `src/Harbora.Domain/Tenancy/Plan.cs` *(modify)* | base rate, overage flag, overage rates |
| `src/Harbora.Domain/Tenancy/InstanceSize.cs` *(modify)* | running and stopped hourly rates |
| `src/Harbora.Domain/Identity/Workspace.cs` *(modify)* | `SuspendedReason` |
| `src/Harbora.Domain/Apps/App.cs` *(modify)* | `WasRunningAtSuspension` |
| `src/Harbora.Infrastructure/Billing/BillingRates.cs` | **pure**: resource + size + plan + run state → hourly rate |
| `src/Harbora.Infrastructure/Billing/BillingHourPlan.cs` | **pure**: workspace snapshot → the lines for one hour |
| `src/Harbora.Infrastructure/Billing/BillingOptions.cs` | `Billing:` configuration section |
| `src/Harbora.Infrastructure/Billing/BillingTick.cs` | the hourly job: scope, idempotency, backfill, wallet write |
| `src/Harbora.Infrastructure/Billing/BillingSuspension.cs` | suspend/resume, running-at-suspension snapshot |
| `src/Harbora.Application/Abstractions/IBillingGate.cs` | the one start gate |
| `src/Harbora.Infrastructure/Billing/BillingGate.cs` | its implementation |
| `src/Harbora.Infrastructure/Billing/WalletService.cs` | credit, balance, runway |
| `tests/Harbora.Tests/Billing/*.cs` | unit + HTTP coverage |
| `tests/Harbora.Postgres.Tests/BillingIndexTests.cs` | the partial unique index against real PostgreSQL |

Rate arithmetic and hour planning are deliberately **pure classes with no `DbContext`**, following `PlanOverage` and `RetentionRule` in this codebase: the money maths is then testable without a container, and the job is left with only orchestration.

---

### Task 1: Money, wallet and ledger

**Files:**
- Create: `src/Harbora.Domain/Billing/Wallet.cs`
- Create: `src/Harbora.Domain/Billing/BillingLedgerEntry.cs`
- Modify: `src/Harbora.Data/HarboraDbContext.cs`
- Create: `src/Harbora.Data/Migrations/<timestamp>_Billing.cs` (generated)
- Test: `tests/Harbora.Tests/Billing/LedgerShapeTests.cs`

**Interfaces:**
- Consumes: `BaseEntity` (`Id`, `CreatedAt`, `UpdatedAt`), `IWorkspaceScope`
- Produces: `Wallet`, `BillingLedgerEntry`, `LedgerKind`, `BilledResourceType`, `BilledRunState`; `HarboraDbContext.Wallets`, `HarboraDbContext.BillingLedger`

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/Billing/LedgerShapeTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Domain.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

public class LedgerShapeTests
{
    [Fact]
    public void A_charge_is_negative_and_a_credit_is_positive()
    {
        // The sign lives in AmountMinor, not in the Kind, so SUM(AmountMinor) is the balance and
        // nothing has to know which kinds subtract.
        var charge = new BillingLedgerEntry { Kind = LedgerKind.Charge, AmountMinor = -500 };
        var credit = new BillingLedgerEntry { Kind = LedgerKind.Credit, AmountMinor = 500 };

        (charge.AmountMinor + credit.AmountMinor).Should().Be(0);
    }

    [Fact]
    public void A_ledger_line_keeps_the_resource_name_it_was_written_with()
    {
        // Copied, never joined. An app deleted next month must still be readable on this month's
        // bill, and a join to a deleted row gives a blank line where a name should be.
        var line = new BillingLedgerEntry
        {
            ResourceType = BilledResourceType.App,
            ResourceId = Guid.CreateVersion7(),
            ResourceName = "shop-api",
        };

        line.ResourceName.Should().Be("shop-api");
    }

    [Fact]
    public void Money_is_a_whole_number_of_minor_units()
    {
        // Guards the one decision that cannot be walked back later: a bill assembled from a
        // floating type drifts by fractions that compound over thousands of hourly lines.
        typeof(BillingLedgerEntry).GetProperty(nameof(BillingLedgerEntry.AmountMinor))!
            .PropertyType.Should().Be<long>();
        typeof(Wallet).GetProperty(nameof(Wallet.BalanceMinor))!
            .PropertyType.Should().Be<long>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~LedgerShapeTests"`
Expected: FAIL — build error, `The type or namespace name 'Billing' does not exist in the namespace 'Harbora.Domain'`.

- [ ] **Step 3: Create the entities**

Create `src/Harbora.Domain/Billing/BillingLedgerEntry.cs`:

```csharp
using Harbora.Domain.Common;

namespace Harbora.Domain.Billing;

/// <summary>
/// What a ledger line is. Appended, never renumbered — rows hold these by value.
/// </summary>
public enum LedgerKind
{
    /// <summary>An hour of one reserved resource, written by the tick.</summary>
    Charge = 0,
    /// <summary>Money put in by an administrator.</summary>
    Credit = 1,
    /// <summary>
    /// The difference between the plan's hourly minimum and the sum of the hour's resource lines,
    /// so the ledger totals exactly what left the wallet and the customer can see why.
    /// </summary>
    PlanMinimumTopUp = 2,
    /// <summary>A correction. Nothing is ever edited or deleted; a mistake gets an opposing line.</summary>
    Adjustment = 3
}

/// <summary>What a line is for. Appended, never renumbered.</summary>
public enum BilledResourceType
{
    None = 0,
    App = 1,
    Service = 2,
    Volume = 3,
    /// <summary>The plan-minimum line. Carries a null <c>ResourceId</c>.</summary>
    PlanBase = 4
}

/// <summary>Whether the resource was running for the hour being charged.</summary>
public enum BilledRunState
{
    NotApplicable = 0,
    Running = 1,
    Stopped = 2
}

/// <summary>
/// One line of one workspace's bill. <b>Append-only.</b> Nothing updates or deletes a row here: a
/// correction is a new <see cref="LedgerKind.Adjustment"/> line, so "why did my balance move" is a
/// query rather than a reconstruction.
///
/// <para>
/// <see cref="ResourceName"/> is copied rather than joined on purpose. An app deleted next month
/// must still be readable on this month's bill, and a join to a row that is gone renders a blank
/// where the customer is looking for a name.
/// </para>
/// </summary>
public class BillingLedgerEntry : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>The UTC hour this line pays for — always an hour that has already ended.</summary>
    public DateTimeOffset BillingHour { get; set; }

    public LedgerKind Kind { get; set; }

    /// <summary>
    /// Signed minor units: charges negative, credits positive. The sign lives here rather than in
    /// <see cref="Kind"/> so the balance is <c>SUM(AmountMinor)</c> and no reader needs a table of
    /// which kinds subtract.
    /// </summary>
    public long AmountMinor { get; set; }

    public BilledResourceType ResourceType { get; set; }
    public Guid? ResourceId { get; set; }

    /// <summary>Copied at write time. See the note on the class.</summary>
    public string ResourceName { get; set; } = string.Empty;

    public BilledRunState RunState { get; set; }

    /// <summary>The rate and the hours, kept so the arithmetic on the line can be checked by eye.</summary>
    public long RatePerHourMinor { get; set; }
    public int Hours { get; set; } = 1;

    public string Description { get; set; } = string.Empty;

    /// <summary>Set on credits and adjustments so a person's money movement has a person on it.</summary>
    public Guid? CreatedByUserId { get; set; }
}
```

Create `src/Harbora.Domain/Billing/Wallet.cs`:

```csharp
using Harbora.Domain.Common;

namespace Harbora.Domain.Billing;

/// <summary>
/// A workspace's spendable balance.
///
/// <para>
/// This is a <b>cached total</b>, not the truth. The truth is <c>SUM(BillingLedgerEntry.AmountMinor)</c>,
/// which is why a reconcile check can prove the two agree — a balance with no ledger behind it can
/// only be trusted, and money should be checkable instead.
/// </para>
/// </summary>
public class Wallet : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Whole minor units. Never a floating type — see the note in the plan's constraints.</summary>
    public long BalanceMinor { get; set; }

    /// <summary>ISO 4217 code. One currency per install.</summary>
    public string Currency { get; set; } = "IRR";

    /// <summary>
    /// Warn when the balance is worth less than this many hours at the current burn rate. Zero
    /// disables the warning, as a zero does on every other limit in this platform.
    /// </summary>
    public int LowBalanceHours { get; set; } = 24;

    /// <summary>
    /// Concurrency token. The tick and an administrator's credit can land in the same second, and
    /// last-write-wins on a balance loses somebody's money.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.CreateVersion7();
}
```

- [ ] **Step 4: Register both in the DbContext**

In `src/Harbora.Data/HarboraDbContext.cs`, add the two `DbSet`s beside the existing ones:

```csharp
    public DbSet<Harbora.Domain.Billing.Wallet> Wallets => Set<Harbora.Domain.Billing.Wallet>();
    public DbSet<Harbora.Domain.Billing.BillingLedgerEntry> BillingLedger => Set<Harbora.Domain.Billing.BillingLedgerEntry>();
```

And in `OnModelCreating`, beside the other entity blocks:

```csharp
        b.Entity<Harbora.Domain.Billing.Wallet>(e =>
        {
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            e.HasIndex(x => x.WorkspaceId).IsUnique();
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
        });

        b.Entity<Harbora.Domain.Billing.BillingLedgerEntry>(e =>
        {
            e.HasQueryFilter(x => IgnoreWorkspaceFilter || x.WorkspaceId == CurrentWorkspaceId);
            e.Property(x => x.ResourceName).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(400);

            // Reading a bill: one workspace, newest first.
            e.HasIndex(x => new { x.WorkspaceId, x.BillingHour });

            // Reading one resource's history: "what did this app cost me".
            e.HasIndex(x => new { x.WorkspaceId, x.ResourceType, x.ResourceId });

            // The idempotency key. Covers BOTH kinds the tick writes: scoping it to Charge alone
            // would leave PlanMinimumTopUp free to be written twice by a retried tick, which is the
            // same double-charge this index exists to prevent, arriving through the one line with no
            // resource behind it. Credit and Adjustment are made by a person and may legitimately
            // repeat within an hour, so they are outside the filter.
            //
            // PlanMinimumTopUp rows carry a null ResourceId by design (BilledResourceType.PlanBase's
            // doc comment) — there is no resource behind that line. Postgres's default treats two
            // NULLs as distinct, which would let a retried tick write the plan-minimum line twice
            // right through this index. AreNullsDistinct(false) closes that: NULLS NOT DISTINCT
            // (PG15+) makes the two NULL ResourceIds collide like any other equal value.
            e.HasIndex(x => new { x.WorkspaceId, x.ResourceType, x.ResourceId, x.BillingHour })
                .IsUnique()
                .AreNullsDistinct(false)
                .HasFilter("\"Kind\" IN (0, 2)");
        });
```

- [ ] **Step 5: Generate the migration**

Run, **without** `--no-build`:

```bash
dotnet ef migrations add Billing --project src/Harbora.Data --startup-project src/Harbora.Web
```

Expected: `Done. To undo this action, use 'ef migrations remove'`, and a new pair of files under `src/Harbora.Data/Migrations/`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~LedgerShapeTests|FullyQualifiedName~MigrationConsistency"`
Expected: PASS, including `MigrationConsistencyTests` — which proves the model snapshot matches the migration.

- [ ] **Step 7: Full suite and build**

Run: `dotnet build Harbora.slnx -c Debug` → `0 Warning(s) 0 Error(s)`
Run: `dotnet test Harbora.slnx -c Debug --no-build` → at least 3,725 passing, 0 failing.

- [ ] **Step 8: Commit**

```bash
git add src/Harbora.Domain/Billing src/Harbora.Data tests/Harbora.Tests/Billing
git commit -F - <<'MSG'
Give a workspace a balance and somewhere to write down why it moved

The ledger is append-only and the wallet is a cached total of it, so a
balance is checkable rather than merely trusted. Each line copies the name
of the resource it charges instead of joining to it: an app deleted next
month still has to read as a name on this month's bill.

The unique index covers both kinds the hourly tick writes, not just Charge.
Leaving the plan-minimum line outside it would let a retried tick write that
one twice - the same double-charge the index exists to prevent, arriving
through the only line with no resource behind it.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

### Task 2: Rate resolution

**Files:**
- Create: `src/Harbora.Infrastructure/Billing/BillingRates.cs`
- Modify: `src/Harbora.Domain/Tenancy/InstanceSize.cs`
- Modify: `src/Harbora.Domain/Tenancy/Plan.cs`
- Modify: `src/Harbora.Data/HarboraDbContext.cs` (nothing new; the migration in step 5 picks up the columns)
- Test: `tests/Harbora.Tests/Billing/BillingRatesTests.cs`

**Interfaces:**
- Consumes: `InstanceSize`, `Plan`, `BilledRunState`
- Produces: `BillingRates.ForWorkload(InstanceSize size, BilledRunState state)` → `long`; `BillingRates.ForVolume(long bytes, long ratePerGbHourMinor)` → `long`; `BillingRates.GibibytesCeiling(long bytes)` → `long`

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/Billing/BillingRatesTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

public class BillingRatesTests
{
    private static InstanceSize Size(long running, long stopped) => new()
    {
        Key = "small",
        RunningRatePerHourMinor = running,
        StoppedRatePerHourMinor = stopped,
    };

    [Fact]
    public void A_running_workload_is_charged_its_running_rate()
    {
        BillingRates.ForWorkload(Size(1000, 100), BilledRunState.Running).Should().Be(1000);
    }

    [Fact]
    public void A_stopped_workload_is_charged_the_reserved_rate_not_the_running_one()
    {
        // The customer stopped it but did not delete it, so the slot, the image and the volume are
        // still theirs. Charging the running rate would bill for CPU nobody is using; charging zero
        // would let a workspace park a hundred gigabytes for free.
        BillingRates.ForWorkload(Size(1000, 100), BilledRunState.Stopped).Should().Be(100);
    }

    [Fact]
    public void A_size_with_no_rates_costs_nothing_rather_than_throwing()
    {
        // Sizes existed before this module did. An unpriced one must read as free until somebody
        // prices it, because the alternative is a tick that dies on one row and bills nobody.
        BillingRates.ForWorkload(new InstanceSize { Key = "legacy" }, BilledRunState.Running)
            .Should().Be(0);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(1073741824L, 1L)]       // exactly 1 GiB
    [InlineData(1073741825L, 2L)]       // one byte over rounds up
    [InlineData(5368709120L, 5L)]       // 5 GiB
    public void Disk_is_charged_by_the_gibibyte_rounded_up(long bytes, long expectedGib)
    {
        BillingRates.GibibytesCeiling(bytes).Should().Be(expectedGib);
    }

    [Fact]
    public void A_volume_costs_its_rounded_up_gibibytes_times_the_rate()
    {
        // 3 GiB + 1 byte at 250/GiB-hour = 4 × 250.
        BillingRates.ForVolume(3L * 1024 * 1024 * 1024 + 1, ratePerGbHourMinor: 250).Should().Be(1000);
    }

    [Fact]
    public void Rounding_up_never_overflows_on_an_absurd_volume()
    {
        // long.MaxValue bytes is not a real disk, but arithmetic that overflows on it is a crash
        // in a job that must not stop billing everyone else.
        var act = () => BillingRates.GibibytesCeiling(long.MaxValue);
        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~BillingRatesTests"`
Expected: FAIL — `'InstanceSize' does not contain a definition for 'RunningRatePerHourMinor'`.

- [ ] **Step 3: Add the rate columns**

In `src/Harbora.Domain/Tenancy/InstanceSize.cs`, after `DiskBytes`:

```csharp
    /// <summary>
    /// What one hour of this size costs while it is running, in minor units. Zero means free, which
    /// is what every size built before billing existed reads as until somebody prices it.
    /// </summary>
    public long RunningRatePerHourMinor { get; set; }

    /// <summary>
    /// What one hour costs while the workload is stopped but not deleted — the reserved slot, the
    /// image and the port. Disk is charged separately per gibibyte, so this is only the slot.
    /// </summary>
    public long StoppedRatePerHourMinor { get; set; }
```

In `src/Harbora.Domain/Tenancy/Plan.cs`, after `MonthlyPrice`:

```csharp
    /// <summary>
    /// The floor. A workspace on this plan pays at least this much per hour, whatever it is running
    /// — including nothing. <see cref="MonthlyPrice"/> is unrelated and remains display-only.
    /// </summary>
    public long BaseRatePerHourMinor { get; set; }

    /// <summary>
    /// Whether this plan sells capacity past its own caps. False keeps today's behaviour, where
    /// <c>IQuotaService</c> refuses; true lets the tenant past and charges the overage rates below.
    /// </summary>
    public bool AllowsOverage { get; set; }

    /// <summary>Charged per unit-hour beyond the matching cap. Only read when <see cref="AllowsOverage"/>.</summary>
    public long OverageCpuCoreHourMinor { get; set; }
    public long OverageMemoryGbHourMinor { get; set; }
    public long OverageDiskGbHourMinor { get; set; }

    /// <summary>Charged per gibibyte-hour of allocated volume, inside the caps as well as past them.</summary>
    public long DiskGbHourMinor { get; set; }
```

- [ ] **Step 4: Write the rate resolver**

Create `src/Harbora.Infrastructure/Billing/BillingRates.cs`:

```csharp
using Harbora.Domain.Billing;
using Harbora.Domain.Tenancy;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// What one hour of one thing costs. Pure arithmetic with no database, for the same reason
/// <c>PlanOverage</c> and <c>RetentionRule</c> are: the money maths is then provable without a
/// container, and the job that calls it is left with only orchestration.
/// </summary>
public static class BillingRates
{
    private const long BytesPerGibibyte = 1024L * 1024 * 1024;

    /// <summary>
    /// The hourly rate for one workload. A size nobody has priced costs nothing rather than
    /// throwing: sizes existed before this module did, and a tick that dies on one unpriced row
    /// bills nobody at all that hour.
    /// </summary>
    public static long ForWorkload(InstanceSize size, BilledRunState state) => state switch
    {
        BilledRunState.Running => size.RunningRatePerHourMinor,
        BilledRunState.Stopped => size.StoppedRatePerHourMinor,
        _ => 0
    };

    /// <summary>
    /// Gibibytes, rounded up, because a customer holding one byte over a boundary is holding the
    /// whole next gibibyte as far as the disk is concerned.
    ///
    /// <para>
    /// Written as a division rather than <c>bytes + BytesPerGibibyte - 1</c> so an absurd figure
    /// cannot overflow: the tick must not crash on one bad row and stop billing everyone else.
    /// </para>
    /// </summary>
    public static long GibibytesCeiling(long bytes)
    {
        if (bytes <= 0) return 0;

        var whole = bytes / BytesPerGibibyte;
        return bytes % BytesPerGibibyte == 0 ? whole : whole + 1;
    }

    /// <summary>What an allocated volume costs for one hour.</summary>
    public static long ForVolume(long bytes, long ratePerGbHourMinor) =>
        GibibytesCeiling(bytes) * ratePerGbHourMinor;
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~BillingRatesTests"`
Expected: PASS, 8 tests.

- [ ] **Step 6: Extend the migration**

The columns added in step 3 are new. Generate a follow-on migration (not `--no-build`):

```bash
dotnet ef migrations add BillingRates --project src/Harbora.Data --startup-project src/Harbora.Web
```

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~MigrationConsistency"` → PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Harbora.Domain/Tenancy src/Harbora.Infrastructure/Billing src/Harbora.Data tests/Harbora.Tests/Billing
git commit -F - <<'MSG'
Put a price on an hour of a size, and on a gibibyte of disk

Rates live on the instance size and the plan; working out what an hour costs
is a pure function over them, so the arithmetic is provable without a
database - the same shape PlanOverage and RetentionRule already have here.

Two decisions worth stating. A size nobody has priced costs nothing rather
than throwing, because sizes existed before billing did and a tick that dies
on one unpriced row bills nobody that hour. And disk rounds up by gibibyte
through a division rather than an add, so an absurd figure cannot overflow
the job.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

### Task 4b: Bill a managed database for the disk it holds

**Added after Task 4's review found it. Dispatch after Task 5.**

A managed service — Postgres, MySQL, Redis and the rest — carries its own `InstanceSizeKey`,
`VolumeName` and `StorageBytes` on `ManagedService`, and has **no relation to the `Volume` table**,
which is keyed by `AppId` alone. `BillingTick` reads volumes by `AppId`, so a managed service's
storage is structurally unreachable from it.

The consequence: a workspace pays the instance-size rate for its database and then holds unlimited
storage for nothing. That is the largest revenue hole on the branch, and it is not a bug in Task 4 —
its brief said "apps and services with their instance sizes, and its volumes", and a service's own
storage was never named.

**Files:**
- Modify: `src/Harbora.Infrastructure/Billing/BillingTick.cs`
- Test: `tests/Harbora.Tests/Billing/BillingTickTests.cs`

- [ ] **Step 1: Write the failing test.** A managed service with storage produces a disk line. Assert
  the amount, and assert the line's `ResourceType`/`ResourceId` — Task 3 found that charge lines whose
  identity is never asserted let a hard-coded id pass every test, and Task 1's index is keyed on
  exactly those columns.

- [ ] **Step 2: Decide the ledger key and say why.** This is the real work. A managed service's disk
  needs a `(ResourceType, ResourceId)` that (a) cannot collide with an app volume's line for the same
  hour, and (b) stays stable across ticks so the unique index keeps a retry harmless. Consider whether
  it deserves its own `BilledResourceType` member — **appended, never renumbered** — rather than
  reusing `Volume` with a service id, and argue the choice. A reader of the bill has to be able to
  tell "my database's disk" from "my app's disk".

- [ ] **Step 3: Use `StorageBytes` as reserved, not measured.** Item 18 of the do-not-change list
  keeps unmeasured disk from being read as zero. Task 4 already treats an unmeasured volume exactly
  like an unpriced one — no line, reported. Follow that, and say in your report whether
  `StorageBytes` is reserved or measured, because the bill has to say which.

- [ ] **Step 4: Charge a stopped database too.** The agreed rate model is that a stopped workload
  still pays for disk, because the data is still on the disk. A stopped managed service is the
  clearest case of that.

### Task 2b: Tell "free" apart from "nobody set a price"

**Added after Task 3, which found the ambiguity. Dispatch before Task 4 — the tick is where it has
to become loud, so the schema has to change first.**

Task 2 made all seven rate columns non-nullable `long`, defaulting to `0`. Task 3 then asked what a
zero-value ledger line means and found the honest answer is: nobody can tell. `0` currently says both
"this resource is deliberately free" and "no human has priced this yet", and those are different
facts with opposite correct responses.

The failure that follows is this project's own signature, moved up to the business layer: an operator
adds an instance size, forgets to price it, every workload on that size runs free for ever, and every
hourly tick reports success. Task 8b will make a free tier a legitimate configuration, which removes
the last chance to guess from context.

`null` is the answer. It is what nullable is for — `null` means no answer has been given, `0` means
the answer is zero.

**Files:**
- Modify: `src/Harbora.Domain/Tenancy/Plan.cs`, `src/Harbora.Domain/Tenancy/InstanceSize.cs`
- Modify: `src/Harbora.Infrastructure/Billing/BillingRates.cs`
- Migration: generated, altering the seven columns to nullable
- Test: `tests/Harbora.Tests/Billing/BillingRatesTests.cs`

- [ ] **Step 1: Write the failing test.** An unpriced rate and a deliberately-free rate must be
  distinguishable by a caller. A zero rate still yields a zero charge; an unset rate must be
  reportable as unset rather than silently costing nothing.

- [ ] **Step 2: Make the seven columns `long?`.** Five on `Plan`, two on `InstanceSize`. The
  migration alters existing columns to nullable, which is additive and safe on live data — but every
  existing row currently holds `0`, and after this change `0` means *free*. Say in your report
  whether the migration should rewrite those zeros to `null`, and argue it. My reading: on this
  branch nothing has ever been priced and no row is deliberately free, so rewriting to `null` is the
  truthful state — but check whether any seeder or test fixture depends on zero and say so.

- [ ] **Step 3: Give `BillingRates` a way to say "unset".** Do not return `0` for it. Choose the
  shape — a nullable return, a result record, whatever fits the three existing methods — and justify
  the choice in your report. Keep money `long`.

- [ ] **Step 4: Update Task 2's tests** so they still pin what they pinned, and add the two cases
  this distinction creates: unset stays unset, and an explicit zero stays a zero charge.

- [ ] **Step 5:** Task 4 will decide what the tick *does* with an unset rate; that is not yours.
  Your job is that it can tell. Say in your report what you think the tick should do and why, so
  Task 4 inherits a reasoned position rather than a blank.

### Task 3: Planning one hour

**Files:**
- Create: `src/Harbora.Infrastructure/Billing/BillingHourPlan.cs`
- Test: `tests/Harbora.Tests/Billing/BillingHourPlanTests.cs`

**Interfaces:**
- Consumes: `BillingRates`, `BilledResourceType`, `BilledRunState`, `LedgerKind`
- Produces:
  - `record BillableResource(BilledResourceType Type, Guid Id, string Name, BilledRunState State, long RatePerHourMinor)`
  - `record PlannedLine(BilledResourceType Type, Guid? Id, string Name, BilledRunState State, long RatePerHourMinor, long AmountMinor, LedgerKind Kind)`
  - `BillingHourPlan.For(IReadOnlyList<BillableResource> resources, long planBaseRatePerHourMinor)` → `IReadOnlyList<PlannedLine>`

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/Billing/BillingHourPlanTests.cs`:

```csharp
using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Infrastructure.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

public class BillingHourPlanTests
{
    private static BillableResource App(string name, long rate, BilledRunState state = BilledRunState.Running) =>
        new(BilledResourceType.App, Guid.CreateVersion7(), name, state, rate);

    [Fact]
    public void Every_resource_gets_its_own_line_so_a_bill_can_be_read_per_app()
    {
        var lines = BillingHourPlan.For([App("api", 600), App("web", 400)], planBaseRatePerHourMinor: 0);

        lines.Where(l => l.Kind == LedgerKind.Charge).Should().HaveCount(2);
        lines.Should().Contain(l => l.Name == "api" && l.AmountMinor == -600);
        lines.Should().Contain(l => l.Name == "web" && l.AmountMinor == -400);
    }

    [Fact]
    public void When_the_resources_come_to_less_than_the_plan_the_difference_is_its_own_line()
    {
        // The plan is a floor. Writing the shortfall as a line of its own is what makes the ledger
        // total exactly what left the wallet — and lets the customer see the words "plan minimum"
        // instead of an unexplained gap between their apps and their balance.
        var lines = BillingHourPlan.For([App("api", 600)], planBaseRatePerHourMinor: 1000);

        lines.Should().HaveCount(2);
        lines.Sum(l => l.AmountMinor).Should().Be(-1000);

        var topUp = lines.Single(l => l.Kind == LedgerKind.PlanMinimumTopUp);
        topUp.AmountMinor.Should().Be(-400);
        topUp.Type.Should().Be(BilledResourceType.PlanBase);
        topUp.Id.Should().BeNull("the top-up has no resource behind it, and needs a stable key to collide on");
    }

    [Fact]
    public void When_the_resources_exceed_the_plan_there_is_no_top_up_line()
    {
        var lines = BillingHourPlan.For([App("api", 600), App("web", 900)], planBaseRatePerHourMinor: 1000);

        lines.Should().OnlyContain(l => l.Kind == LedgerKind.Charge);
        lines.Sum(l => l.AmountMinor).Should().Be(-1500);
    }

    [Fact]
    public void A_workspace_with_nothing_running_still_pays_the_plan_floor()
    {
        var lines = BillingHourPlan.For([], planBaseRatePerHourMinor: 1000);

        lines.Should().ContainSingle();
        lines[0].Kind.Should().Be(LedgerKind.PlanMinimumTopUp);
        lines[0].AmountMinor.Should().Be(-1000);
    }

    [Fact]
    public void A_free_plan_with_nothing_running_produces_no_lines_at_all()
    {
        // Writing a row of zero every hour for every dormant workspace is how a ledger becomes the
        // biggest table on the install without ever holding a number.
        BillingHourPlan.For([], planBaseRatePerHourMinor: 0).Should().BeEmpty();
    }

    [Fact]
    public void A_stopped_resource_is_still_a_line_carrying_its_stopped_state()
    {
        var lines = BillingHourPlan.For([App("api", 100, BilledRunState.Stopped)], planBaseRatePerHourMinor: 0);

        lines.Should().ContainSingle();
        lines[0].State.Should().Be(BilledRunState.Stopped);
        lines[0].AmountMinor.Should().Be(-100);
    }

    [Fact]
    public void A_resource_priced_at_zero_writes_no_line()
    {
        BillingHourPlan.For([App("legacy", 0)], planBaseRatePerHourMinor: 0).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~BillingHourPlanTests"`
Expected: FAIL — `The name 'BillingHourPlan' does not exist in the current context`.

- [ ] **Step 3: Write the planner**

Create `src/Harbora.Infrastructure/Billing/BillingHourPlan.cs`:

```csharp
using Harbora.Domain.Billing;

namespace Harbora.Infrastructure.Billing;

/// <summary>One thing a workspace held for an hour, with the rate already resolved.</summary>
public sealed record BillableResource(
    BilledResourceType Type,
    Guid Id,
    string Name,
    BilledRunState State,
    long RatePerHourMinor);

/// <summary>One ledger line, worked out but not yet written.</summary>
public sealed record PlannedLine(
    BilledResourceType Type,
    Guid? Id,
    string Name,
    BilledRunState State,
    long RatePerHourMinor,
    long AmountMinor,
    LedgerKind Kind);

/// <summary>
/// The lines for one workspace for one hour. Pure — no database, no clock — so every rule below is
/// provable directly rather than by running a job and reading a table.
/// </summary>
public static class BillingHourPlan
{
    /// <summary>
    /// Charge each resource at its own rate, then, if the total falls short of the plan's hourly
    /// floor, add one line for the difference.
    ///
    /// <para>
    /// The shortfall is a line rather than an adjustment to the others because a bill has to add up
    /// in front of the person paying it: every app shows what it actually cost, and the gap between
    /// that and the floor is labelled as what it is.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PlannedLine> For(
        IReadOnlyList<BillableResource> resources,
        long planBaseRatePerHourMinor)
    {
        var lines = new List<PlannedLine>(resources.Count + 1);
        var total = 0L;

        foreach (var r in resources)
        {
            // A rate of zero writes nothing. A row of zero every hour for every unpriced resource is
            // how a ledger becomes the largest table on the install without ever holding a number.
            if (r.RatePerHourMinor <= 0) continue;

            total += r.RatePerHourMinor;
            lines.Add(new PlannedLine(
                r.Type, r.Id, r.Name, r.State, r.RatePerHourMinor,
                AmountMinor: -r.RatePerHourMinor, LedgerKind.Charge));
        }

        var shortfall = planBaseRatePerHourMinor - total;
        if (shortfall > 0)
        {
            lines.Add(new PlannedLine(
                BilledResourceType.PlanBase,
                // Null on purpose: the index that makes a retried tick harmless keys on
                // (workspace, type, id, hour), and this line needs a stable key to collide on.
                Id: null,
                Name: "Plan minimum",
                BilledRunState.NotApplicable,
                RatePerHourMinor: planBaseRatePerHourMinor,
                AmountMinor: -shortfall,
                LedgerKind.PlanMinimumTopUp));
        }

        return lines;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~BillingHourPlanTests"`
Expected: PASS, 7 tests.

- [ ] **Step 5: Prove the top-up line bites**

Temporarily delete the `if (shortfall > 0)` block, re-run the same filter, and confirm
`When_the_resources_come_to_less_than_the_plan_the_difference_is_its_own_line`,
`A_workspace_with_nothing_running_still_pays_the_plan_floor` both FAIL. Restore the block and
re-run to green. Record the two failure messages in the task report.

- [ ] **Step 6: Commit**

```bash
git add src/Harbora.Infrastructure/Billing tests/Harbora.Tests/Billing
git commit -F - <<'MSG'
Work out an hour's lines without touching a database

Each resource is charged at its own rate, and where the total falls short of
the plan's floor the difference becomes a line of its own rather than being
folded into the others. A bill has to add up in front of the person paying
it: every app shows what it actually cost, and the gap to the floor is
labelled as what it is.

A rate of zero writes no line. A row of zero every hour for every unpriced
resource is how a ledger becomes the largest table on an install without ever
holding a number.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

### Task 4: The hourly tick

**Files:**
- Create: `src/Harbora.Infrastructure/Billing/BillingOptions.cs`
- Create: `src/Harbora.Infrastructure/Billing/BillingTick.cs`
- Modify: `src/Harbora.Infrastructure/DependencyInjection.cs`
- Modify: `src/Harbora.Web/appsettings.json`
- Test: `tests/Harbora.Tests/Billing/BillingTickTests.cs`

**Interfaces:**
- Consumes: `BillingHourPlan.For`, `BillingRates`, `IServiceScopeFactory`, `ISystemClock`, `HarboraDbContext`
- Produces: `BillingTick.ChargeHourAsync(DateTimeOffset hour, CancellationToken ct)` → `Task<BillingTickResult>`; `record BillingTickResult(int WorkspacesCharged, int LinesWritten, int HoursBackfilled, IReadOnlyList<string> Failures)`

- [ ] **Step 1: Write the failing test**

Create `tests/Harbora.Tests/Billing/BillingTickTests.cs`. Follow the in-memory context helper used by neighbouring infrastructure tests — read `tests/Harbora.Tests/` for the existing `NewContext()` pattern before writing, and reuse it rather than inventing a second one.

```csharp
using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Identity;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Billing;

public class BillingTickTests
{
    private static readonly DateTimeOffset Hour = new(2026, 8, 9, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Every_workspace_is_charged_not_just_the_one_in_scope()
    {
        // The guard on the trap this platform has already been bitten by: the EF tenancy filters
        // are session-scoped, so work that runs without a session sees an EMPTY database, charges
        // nobody, and reports success. Two workspaces in, two workspaces charged, or this is that.
        await using var db = Harness.SystemContext();
        var a = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant-a", ratePerHour: 500);
        var b = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant-b", ratePerHour: 700);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        result.WorkspacesCharged.Should().Be(2);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == a)).BalanceMinor.Should().Be(-500);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == b)).BalanceMinor.Should().Be(-700);
    }

    [Fact]
    public async Task A_retried_tick_charges_once()
    {
        // The durable queue retries. Without the unique index behind this, a retry is a second bill.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);
        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-500);
        (await db.BillingLedger.CountAsync(l => l.WorkspaceId == ws)).Should().Be(1);
    }

    [Fact]
    public async Task The_wallet_equals_the_sum_of_its_ledger()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour, default);
        await Harness.Tick(db).ChargeHourAsync(Hour.AddHours(1), default);

        var wallet = await db.Wallets.SingleAsync(w => w.WorkspaceId == ws);
        var ledger = await db.BillingLedger.Where(l => l.WorkspaceId == ws).SumAsync(l => l.AmountMinor);
        wallet.BalanceMinor.Should().Be(ledger);
    }

    [Fact]
    public async Task An_hour_that_has_not_ended_is_not_charged()
    {
        // Charging forward means a customer pays for an hour they might spend stopped.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var future = Harness.Clock.UtcNow.AddHours(2);
        var result = await Harness.Tick(db).ChargeHourAsync(future, default);

        result.LinesWritten.Should().Be(0);
    }

    [Fact]
    public async Task A_missed_hour_is_backfilled_and_the_bound_is_reported()
    {
        // A panel that was down for a day must not have hosted for free, and must not silently
        // decide how much free hosting is acceptable either.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();

        var tick = Harness.Tick(db, maxBackfillHours: 3);
        var result = await tick.CatchUpAsync(lastChargedHour: Hour.AddHours(-10), default);

        result.HoursBackfilled.Should().Be(3);
        result.Failures.Should().ContainSingle(f => f.Contains("backfill"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~BillingTickTests"`
Expected: FAIL — `The name 'Harness' does not exist`. Write the harness alongside the tick in step 3; keep it in the test project.

- [ ] **Step 3: Write the options**

Create `src/Harbora.Infrastructure/Billing/BillingOptions.cs`:

```csharp
namespace Harbora.Infrastructure.Billing;

/// <summary>
/// How the hourly charge behaves. Every value here is deliberately visible to an operator, because
/// each one decides how somebody's money moves.
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>
    /// Off by default. Billing is a commercial decision, and an install that upgrades into it
    /// without being asked would start charging tenants who were never told there was a price.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How many missed hours one catch-up will pay for. A panel that was down must not have hosted
    /// for free, but it must not silently decide how much free hosting is acceptable either —
    /// reaching this bound is a warning naming what was dropped, never a quiet skip.
    /// </summary>
    public int MaxBackfillHours { get; set; } = 72;
}
```

- [ ] **Step 4: Write the tick**

Create `src/Harbora.Infrastructure/Billing/BillingTick.cs`. The essential shape:

```csharp
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Billing;

/// <summary>What one pass did. Returned so a caller can assert on it rather than read a log.</summary>
public sealed record BillingTickResult(
    int WorkspacesCharged,
    int LinesWritten,
    int HoursBackfilled,
    IReadOnlyList<string> Failures);

/// <summary>
/// Charges every workspace for one hour that has already ended.
///
/// <para>
/// <b>Runs unscoped, deliberately.</b> The EF tenancy filters are driven by the request's workspace,
/// so work with no session behind it sees an empty database — it would charge nobody and report
/// success. The scope is taken explicitly here and a test counts the workspaces charged, because
/// that failure is invisible in a log.
/// </para>
/// </summary>
public sealed class BillingTick(
    IServiceScopeFactory scopeFactory,
    IOptions<BillingOptions> options,
    ISystemClock clock,
    ILogger<BillingTick> logger)
{
    /// <summary>
    /// One hour, every workspace. Idempotent: the unique index on
    /// (WorkspaceId, ResourceType, ResourceId, BillingHour) means a second attempt at the same hour
    /// collides and changes nothing, which is what makes a retry from the durable queue harmless.
    /// </summary>
    public async Task<BillingTickResult> ChargeHourAsync(DateTimeOffset hour, CancellationToken ct)
    {
        // Implementation notes for the engineer:
        //
        // 1. `using var scope = scopeFactory.CreateScope();` then resolve HarboraDbContext. The
        //    parameterless-scope constructor already defaults to SystemWorkspaceScope.Instance —
        //    confirm that is what you get here, and assert it in the test rather than assuming.
        // 2. Refuse an hour that has not ended: `if (hour >= clock.UtcNow.Hour floor) return empty`.
        // 3. For each workspace: load its plan, its apps and services with their instance sizes,
        //    and its volumes. Map each to a BillableResource using BillingRates.
        //    Run state comes from AppStatus: Running => BilledRunState.Running, Stopped/Failed/
        //    Crashed => BilledRunState.Stopped. Created/Deploying are NOT charged — nothing is
        //    reserved yet.
        // 4. Call BillingHourPlan.For(resources, plan.BaseRatePerHourMinor).
        // 5. Insert the lines. Catch the unique-violation (PostgresException SqlState 23505) and
        //    treat it as "already charged", NOT as a failure — qualify the catch on 23505 exactly,
        //    the way the rest of this codebase does; a bare DbUpdateException catch would swallow
        //    a real write error.
        // 6. Decrement the wallet once, under its ConcurrencyStamp. On DbUpdateConcurrencyException,
        //    reload and retry the wallet write only — the ledger lines are already durable.
        // 7. A failure on one workspace is recorded in Failures and the loop continues. One tenant
        //    must not stop the platform billing everyone else.
        throw new NotImplementedException("See the notes above; implement then delete this line.");
    }

    /// <summary>
    /// Pay for every hour between <paramref name="lastChargedHour"/> and now, oldest first, up to
    /// <c>Billing:MaxBackfillHours</c>. Reaching the bound is a warning naming the hours dropped.
    /// </summary>
    public async Task<BillingTickResult> CatchUpAsync(DateTimeOffset lastChargedHour, CancellationToken ct)
    {
        throw new NotImplementedException("Loop ChargeHourAsync oldest-first; bound by MaxBackfillHours; LogWarning at the bound.");
    }
}
```

**The `NotImplementedException` lines are scaffolding for this step only.** Replace them with the real implementation before running step 5 — a plan step that leaves one behind is a plan failure.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~BillingTickTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Prove the tenancy guard bites**

Change the tick to resolve its context from a `FixedWorkspaceScope(Guid.Empty)` instead of the system scope, re-run, and confirm `Every_workspace_is_charged_not_just_the_one_in_scope` FAILS with `Expected result.WorkspacesCharged to be 2, but found 0`. Restore, re-run green, and record the failure message in the task report. **This is the single most important mutation in the plan** — it is the failure that reports success.

- [ ] **Step 7: Register it**

In `src/Harbora.Infrastructure/DependencyInjection.cs`, beside the retention registration:

```csharp
        services.Configure<Billing.BillingOptions>(config.GetSection(Billing.BillingOptions.SectionName));
        services.AddScoped<Billing.BillingTick>();
```

In `src/Harbora.Web/appsettings.json`, add a `Billing` block with a comment in the style of the neighbouring `Retention` block, stating that `Enabled` is false by default and why.

- [ ] **Step 8: Full suite and commit**

Run: `dotnet build Harbora.slnx -c Debug` → 0 warnings.
Run: `dotnet test Harbora.slnx -c Debug --no-build` → 0 failing.

```bash
git add src/Harbora.Infrastructure/Billing src/Harbora.Infrastructure/DependencyInjection.cs src/Harbora.Web/appsettings.json tests/Harbora.Tests/Billing
git commit -F - <<'MSG'
Charge every workspace for the hour that just ended

The pass runs unscoped on purpose and a test counts the workspaces it
charged. The tenancy filters are driven by the request's workspace, so work
with no session behind it reads an empty database, charges nobody and reports
success - which is invisible in a log and is the trap this platform has
already been caught by once.

A retry is harmless: the unique index makes the second attempt at an hour
collide and change nothing, and the 23505 catch is qualified on that exact
code so a real write error still surfaces. A failure on one tenant is
recorded and stepped over, because one broken workspace must not stop the
platform billing the others.

Billing ships disabled. An install that upgraded into it unasked would start
charging tenants who were never told there was a price.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

### Task 5: Suspension, and remembering what was running

**Files:**
- Modify: `src/Harbora.Domain/Identity/Workspace.cs`
- Modify: `src/Harbora.Domain/Apps/App.cs`
- Create: `src/Harbora.Infrastructure/Billing/BillingSuspension.cs`
- Test: `tests/Harbora.Tests/Billing/BillingSuspensionTests.cs`

**Interfaces:**
- Consumes: `Workspace.IsSuspended`, `AppStatus`
- Produces: `enum SuspensionReason { None = 0, Manual = 1, NoBalance = 2 }`; `BillingSuspension.SuspendAsync(Guid workspaceId, CancellationToken ct)`; `BillingSuspension.ResumeAsync(Guid workspaceId, CancellationToken ct)`

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Billing;

public class BillingSuspensionTests
{
    [Fact]
    public async Task Suspending_for_no_balance_stops_the_running_apps()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 100);
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);

        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Stopped);
    }

    [Fact]
    public async Task Resuming_starts_only_what_was_running_when_it_was_suspended()
    {
        // The one that matters to a customer. An app they deliberately stopped last week must not
        // come back and start spending the money they just put in.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithTwoApps(db, running: "api", stopped: "worker");
        await db.SaveChangesAsync();

        await Harness.Suspension(db).SuspendAsync(ws, default);
        await Harness.Suspension(db).ResumeAsync(ws, default);

        var apps = await db.Apps.Where(a => a.WorkspaceId == ws).ToListAsync();
        apps.Single(a => a.Slug == "api").Status.Should().Be(AppStatus.Running);
        apps.Single(a => a.Slug == "worker").Status.Should().Be(AppStatus.Stopped,
            "the customer stopped this one themselves, and a top-up is not a request to start it");
    }

    [Fact]
    public async Task A_top_up_does_not_lift_a_suspension_an_administrator_made()
    {
        // Without a reason on the suspension, paying a bill would quietly undo an operator's
        // deliberate act — which is the sort of thing nobody notices until it matters.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedSuspendedWorkspace(db, SuspensionReason.Manual);
        await db.SaveChangesAsync();

        await Harness.Suspension(db).ResumeAsync(ws, default);

        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~BillingSuspensionTests"`
Expected: FAIL — `The name 'SuspensionReason' does not exist`.

- [ ] **Step 3: Add the state**

In `src/Harbora.Domain/Identity/Workspace.cs`:

```csharp
    /// <summary>
    /// Why this workspace is suspended. Without it a top-up would quietly lift an operator's
    /// deliberate suspension, which is the sort of thing nobody notices until it matters.
    /// </summary>
    public SuspensionReason SuspendedReason { get; set; } = SuspensionReason.None;
}

/// <summary>Why a workspace is suspended. Appended, never renumbered.</summary>
public enum SuspensionReason
{
    None = 0,
    /// <summary>An operator suspended it. A payment does not lift this.</summary>
    Manual = 1,
    /// <summary>The balance reached zero. A payment lifts this.</summary>
    NoBalance = 2
}
```

In `src/Harbora.Domain/Apps/App.cs`:

```csharp
    /// <summary>
    /// Whether this app was running at the moment its workspace was suspended, so resumption starts
    /// what the outage stopped and nothing else. An app the customer had stopped themselves must not
    /// come back and start spending the money they just put in.
    /// </summary>
    public bool WasRunningAtSuspension { get; set; }
```

- [ ] **Step 4: Write `BillingSuspension`**

Create `src/Harbora.Infrastructure/Billing/BillingSuspension.cs` implementing:
- `SuspendAsync`: set `IsSuspended = true`, `SuspendedReason = NoBalance`; for each app currently `AppStatus.Running`, set `WasRunningAtSuspension = true` and stop it through the existing app-stop path (find it — do not shell out to Docker directly; the platform already has a stop route and it must be the one used).
- `ResumeAsync`: return without doing anything unless `SuspendedReason == NoBalance`; clear the flags; start only apps with `WasRunningAtSuspension`, clearing the marker as each starts.

- [ ] **Step 5: Run the tests, generate the migration, run the full suite**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~BillingSuspensionTests"` → PASS.
Run: `dotnet ef migrations add BillingSuspension --project src/Harbora.Data --startup-project src/Harbora.Web` (not `--no-build`).
Run: `dotnet build Harbora.slnx -c Debug` → 0 warnings; `dotnet test Harbora.slnx -c Debug --no-build` → 0 failing.

- [ ] **Step 6: Commit**

```bash
git add src/Harbora.Domain src/Harbora.Infrastructure/Billing src/Harbora.Data tests/Harbora.Tests/Billing
git commit -F - <<'MSG'
Remember what was running before the money ran out

Suspension already existed and blocked new deploys; it now also stops what is
running, and records which apps were running when it did. Resumption starts
only those. An app the customer stopped themselves last week must not come
back and start spending the money they just put in.

A suspension now carries its reason, so paying a bill lifts the one the
balance caused and leaves the one an operator made deliberately - which is
the sort of thing nobody notices until it matters.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

### Task 6: One gate, and every path through it

**Files:**
- Create: `src/Harbora.Application/Abstractions/IBillingGate.cs`
- Create: `src/Harbora.Infrastructure/Billing/BillingGate.cs`
- Modify: every start path (enumerated in step 3)
- Test: `tests/Harbora.Tests/Billing/BillingGateTests.cs`

**Interfaces:**
- Produces: `IBillingGate.CanStartAsync(Guid workspaceId, CancellationToken ct)` → `Task<QuotaCheck>` (reuses the existing `QuotaCheck` record from `IQuotaService`, so refusal messages render the same way everywhere)

- [ ] **Step 1: Write the failing test — including the one that counts the paths**

```csharp
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests.Billing;

public class BillingGateTests
{
    [Fact]
    public async Task A_workspace_with_no_balance_cannot_start_anything()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithBalance(db, balanceMinor: 0);
        await db.SaveChangesAsync();

        var check = await Harness.Gate(db).CanStartAsync(ws, default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().NotBeNullOrWhiteSpace("a refusal a customer cannot read is a support ticket");
    }

    [Fact]
    public async Task A_workspace_in_credit_can_start()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithBalance(db, balanceMinor: 5_000);
        await db.SaveChangesAsync();

        (await Harness.Gate(db).CanStartAsync(ws, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Every_path_that_can_start_a_workload_asks_the_gate()
    {
        // The rule this codebase has already been bitten by: ReservedHosts had ONE call site while
        // four paths minted hosts. A gate with five call sites and six callers is a customer with
        // no balance getting free hosting through the sixth.
        //
        // Add a path here when you add a path in the product. The failure message names the file.
        string[] starters =
        [
            "src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs",
            "src/Harbora.Web/Controllers/AppsController.cs",
            "src/Harbora.Infrastructure/Projects/PreviewEnvironmentService.cs",
            "src/Harbora.Infrastructure/Templates/TemplateDeploymentService.cs",
            "src/Harbora.Infrastructure/Cron/CronRunner.cs",
        ];

        foreach (var path in starters)
        {
            var full = Path.Combine(Harness.RepoRoot, path);
            File.Exists(full).Should().BeTrue($"{path} is on the starter list but not on disk — " +
                "if it moved, update this list; if it was deleted, remove it");

            File.ReadAllText(full).Should().Contain("CanStartAsync",
                $"{path} can start a workload, so it must ask IBillingGate first. " +
                "Without it a workspace with no balance gets free hosting through this path.");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~BillingGateTests"`
Expected: FAIL on all three — the interface does not exist, and no starter contains `CanStartAsync`.

- [ ] **Step 3: Confirm the starter list against the code before writing it**

Before implementing, `grep` the repository for every path that transitions an app to running — deploy, manual start, preview environment, template deployment, scheduled cron. **The five paths above are the list as understood when this plan was written; verify it.** If you find a sixth, add it to both the product code and the test's array, and say so in the task report. If one of the five turns out not to start anything, remove it from the array with the evidence.

- [ ] **Step 4: Write the gate**

Create `src/Harbora.Application/Abstractions/IBillingGate.cs`:

```csharp
namespace Harbora.Application.Abstractions;

/// <summary>
/// The one place that decides whether a workspace may start a workload right now.
///
/// <para>
/// One implementation, one interface, and a test that counts the call sites. Two copies of a
/// placement rule have already drifted apart silently in this codebase; a billing rule with a
/// second copy is a customer getting free hosting through whichever copy nobody updated.
/// </para>
/// </summary>
public interface IBillingGate
{
    Task<QuotaCheck> CanStartAsync(Guid workspaceId, CancellationToken ct);
}
```

Create `src/Harbora.Infrastructure/Billing/BillingGate.cs` returning `QuotaCheck.Ok` when billing is disabled, when the workspace is not suspended and the balance is positive; and `QuotaCheck.Deny` with a bilingual, readable reason otherwise.

- [ ] **Step 5: Wire every path**

Add the `CanStartAsync` call to each file in the verified list, refusing before anything is started. Follow each file's existing refusal style — return the same shape its neighbouring quota refusal returns, so the message surfaces where a quota message already does.

- [ ] **Step 6: Run the tests, then prove the enumeration bites**

Run the filter → PASS.
Then delete the `CanStartAsync` call from **one** starter, re-run, and confirm the enumeration test fails naming that file. Restore, re-run green, and record the failure message.

- [ ] **Step 7: Full suite and commit**

```bash
git add src/Harbora.Application src/Harbora.Infrastructure src/Harbora.Web tests/Harbora.Tests/Billing
git commit -F - <<'MSG'
Ask one gate before starting anything, and count the askers

Five paths can put a workload into the running state, and a billing rule that
lives in four of them is a customer with no balance getting free hosting
through the fifth. There is one interface, one implementation, and a test
that reads each starter and fails naming the file that stopped asking.

The refusal reuses QuotaCheck so it renders where a quota refusal already
does, rather than inventing a second way for the platform to say no.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

### Task 7: The plan's overage flag

**Files:**
- Modify: `src/Harbora.Infrastructure/Tenancy/QuotaService.cs`
- Test: `tests/Harbora.Tests/Billing/OverageTests.cs`

**Interfaces:**
- Consumes: `Plan.AllowsOverage`, `IQuotaService.CanAddAppAsync`, `QuotaCheck`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task A_plan_that_does_not_sell_overage_still_refuses_past_its_cap()
{
    // Today's behaviour, kept. A free plan must stay a wall.
    var check = await Harness.Quota(db).CanAddAppAsync(freePlanWorkspace, "small", null, default);
    check.Allowed.Should().BeFalse();
}

[Fact]
public async Task A_plan_that_sells_overage_lets_the_tenant_past_its_cap()
{
    var check = await Harness.Quota(db).CanAddAppAsync(payGoWorkspace, "small", null, default);
    check.Allowed.Should().BeTrue("the plan sells the excess; the tick will charge for it");
}

[Fact]
public async Task Overage_does_not_let_a_suspended_workspace_past()
{
    // Selling capacity past a cap is not the same as selling it to somebody who is not paying.
    var check = await Harness.Quota(db).CanAddAppAsync(suspendedPayGoWorkspace, "small", null, default);
    check.Allowed.Should().BeFalse();
}
```

Expand each into a full test using the harness, following the shape of the existing quota tests in `tests/Harbora.Tests/`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Harbora.Tests/Harbora.Tests.csproj --filter "FullyQualifiedName~OverageTests"`
Expected: FAIL — the second test denies, because today every plan is a wall.

- [ ] **Step 3: Make the cap conditional**

In `QuotaService`, when a cap would be breached, allow it when `plan.AllowsOverage` **and** the workspace is not suspended. Keep the existing refusal message for the hard-cap case unchanged — it is what tenants already see.

- [ ] **Step 4: Run the tests, full suite, commit**

```bash
git commit -F - <<'MSG'
Let a plan sell what it caps, if that is what it is for

A cap is now either a wall or a price line, decided on the plan rather than
platform-wide, so a free tier stays a wall while a pay-as-you-go tier lets
the tenant past and the tick charges for it.

Selling capacity past a cap is not the same as selling it to somebody who is
not paying, so a suspended workspace is refused either way.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

### Task 8: Admin credit, and the customer's bill

**Files:**
- Create: `src/Harbora.Infrastructure/Billing/WalletService.cs`
- Modify: `src/Harbora.Web/Controllers/TenantsController.cs` (credit action)
- Create: `src/Harbora.Web/Views/Billing/Index.cshtml` (customer balance + breakdown)
- Test: `tests/Harbora.Tests/Billing/WalletServiceTests.cs`, `tests/Harbora.Tests/Http/BillingPageHttpTests.cs`

**Interfaces:**
- Produces: `WalletService.CreditAsync(Guid workspaceId, long amountMinor, string note, Guid byUserId, CancellationToken ct)` → `Task<long>` (new balance); `WalletService.BreakdownAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)` → `Task<IReadOnlyList<ResourceCost>>`; `record ResourceCost(BilledResourceType Type, Guid? Id, string Name, int RunningHours, int StoppedHours, long TotalMinor)`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task A_credit_is_a_ledger_line_with_a_person_on_it()
{
    var balance = await Harness.Wallet(db).CreditAsync(ws, 100_000, "card payment", adminId, default);

    balance.Should().Be(100_000);
    var line = await db.BillingLedger.SingleAsync(l => l.Kind == LedgerKind.Credit);
    line.AmountMinor.Should().Be(100_000);
    line.CreatedByUserId.Should().Be(adminId);
    line.Description.Should().Be("card payment");
}

[Fact]
public async Task A_credit_that_clears_the_debt_lifts_a_no_balance_suspension()
{
    await Harness.Wallet(db).CreditAsync(suspendedWs, 100_000, "top-up", adminId, default);

    (await db.Workspaces.SingleAsync(w => w.Id == suspendedWs)).IsSuspended.Should().BeFalse();
}

[Fact]
public async Task The_breakdown_separates_the_hours_an_app_ran_from_the_hours_it_sat_stopped()
{
    // The question the customer actually asks: this app was up for ten hours and idle for
    // fourteen, so what did each cost me.
    var costs = await Harness.Wallet(db).BreakdownAsync(ws, from, to, default);

    var api = costs.Single(c => c.Name == "api");
    api.RunningHours.Should().Be(10);
    api.StoppedHours.Should().Be(14);
    api.TotalMinor.Should().Be(10 * 1000 + 14 * 100);
}

[Fact]
public async Task A_deleted_app_still_appears_on_the_bill_it_was_charged_on()
{
    // The reason ResourceName is copied rather than joined.
    db.Apps.Remove(await db.Apps.SingleAsync(a => a.Slug == "api"));
    await db.SaveChangesAsync();

    var costs = await Harness.Wallet(db).BreakdownAsync(ws, from, to, default);
    costs.Should().Contain(c => c.Name == "api");
}
```

- [ ] **Step 2: Run to verify they fail**

Expected: FAIL — `WalletService` does not exist.

- [ ] **Step 3: Implement `WalletService`**

`CreditAsync` writes the ledger line and moves the wallet in one `SaveChangesAsync`, then calls `BillingSuspension.ResumeAsync` when the balance is now positive. `BreakdownAsync` groups the ledger by `(ResourceType, ResourceId, ResourceName)` and counts hours per `RunState` — a `GROUP BY`, which is the whole reason each line carries its resource.

- [ ] **Step 4: Add the admin credit action and the customer page**

Follow the existing controller and view conventions. Bilingual strings, design tokens only, and the credit form is a POST with antiforgery like every other mutating form in this panel.

- [ ] **Step 5: Write the HTTP-lane test**

In `tests/Harbora.Tests/Http/`, following the existing lane: the billing page returns 200 for a member and the admin credit endpoint refuses a non-admin.

- [ ] **Step 6: Run everything and commit**

```bash
git commit -F - <<'MSG'
Put money in, and show where it went

A credit is a ledger line with a person and a note on it, and when it clears
the debt it lifts the suspension the balance caused - and only that one.

The breakdown answers the question a customer actually asks: this app ran for
ten hours and sat stopped for fourteen, and here is what each cost. It is a
GROUP BY, which is the whole reason every line carries the resource it
charged and a copy of its name - an app deleted last week still reads as a
name on the bill it was charged on.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

### Task 8b: An admin can actually set the prices

**Added after Task 2, which found the gap. Dispatch after Task 8 and before Task 9.**

Task 2 gave `Plan` five rate columns and `InstanceSize` two. Nothing in Tasks 1–10 as originally
written ever sets one. `PlansController.CreatePlan` and `CreateSize` take explicit scalar parameter
lists — not model binding — and neither list mentions a rate, so the columns are unreachable from
the admin UI and every one of them stays `0`.

Follow that through: the tick runs hourly, every rate resolves to zero, Task 3 drops zero-value
lines, the ledger stays empty, no balance ever moves, nobody is ever suspended, and every run
reports success. The feature would ship charging nobody and saying it worked — the failure this
whole codebase has spent two phases learning to recognise, arrived at through the plan rather than
through the code.

**Files:**
- Modify: `src/Harbora.Web/Controllers/PlansController.cs`
- Modify: the plan and size forms under `src/Harbora.Web/Views/Plans/`
- Test: `tests/Harbora.Tests/Billing/RateAdminTests.cs`

- [ ] **Step 1: Write the failing test.** A rate set through the controller action must come back
  from the database. Assert on all seven columns — five on `Plan`, two on `InstanceSize` — because a
  parameter list that silently omits one is exactly how this gap was created. The test must name the
  columns explicitly rather than looping over reflection, so adding an eighth rate later fails loudly
  instead of being quietly uncovered.

- [ ] **Step 2: Extend both parameter lists and both forms.** Money is entered by a person, so accept
  it in major units in the form and convert once at the boundary to `long` minor units; do not put a
  `decimal` on the entity. Note `CreatePlan` already takes a `decimal monthlyPrice` — that is
  pre-existing display pricing, unrelated to these rates. Leave it alone and do not follow its
  pattern.

- [ ] **Step 3: Refuse a negative rate**, and say so in the form rather than storing it. A negative
  rate is a machine that pays customers to run workloads.

- [ ] **Step 4: A rate of zero must remain expressible and must mean free**, not "unset" — an
  operator may legitimately want a free tier. Confirm Task 3's zero-line dropping still reads as
  "this line costs nothing" and not as "this rate is missing", and say in your report which it is.

- [ ] **Step 5: Audit the change.** Price changes are the most disputable thing an admin does. Follow
  the auditing pattern already used by the surrounding admin actions.

### Task 9: Warn before the lights go out

**Files:**
- Modify: `src/Harbora.Infrastructure/Billing/BillingTick.cs`
- Test: `tests/Harbora.Tests/Billing/LowBalanceAlertTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task A_workspace_within_its_warning_window_gets_one_alert_not_one_an_hour()
{
    // A customer who is warned twenty times stops reading the warnings.
    await Harness.Tick(db).ChargeHourAsync(hour, default);
    await Harness.Tick(db).ChargeHourAsync(hour.AddHours(1), default);

    (await db.Alerts.CountAsync(a => a.Type == AlertType.LowBalance)).Should().Be(1);
}

[Fact]
public async Task A_top_up_re_arms_the_warning()
{
    // Otherwise a customer is warned once, ever, and the second time they run down they get nothing.
    await Harness.Wallet(db).CreditAsync(ws, 1_000_000, "top-up", adminId, default);
    await Harness.Tick(db).ChargeHourAsync(laterHour, default);

    (await db.Alerts.CountAsync(a => a.Type == AlertType.LowBalance)).Should().Be(2);
}
```

- [ ] **Step 2–4:** Run to fail; implement using the existing alert type and delivery (add the alert type by **appending** to the enum, never renumbering); run to pass.

- [ ] **Step 5: Commit**

```bash
git commit -F - <<'MSG'
Say it once, before the site goes down

The warning fires when the balance is worth less than the workspace's chosen
number of hours, and fires once - a customer warned twenty times stops
reading warnings. A top-up re-arms it, or the second time they run down they
would get nothing at all.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

### Task 10: Prove the index against a real database, and tell the operator

**Files:**
- Create: `tests/Harbora.Postgres.Tests/BillingIndexTests.cs`
- Modify: `deploy/RUNBOOK.md`
- Modify: `src/Harbora.Web/appsettings.json` (comment only)

- [ ] **Step 1: Write the Postgres-lane facts**

Read `tests/Harbora.Postgres.Tests/PartialUniqueIndexTests.cs` first and follow its shape exactly — `[PostgresFact]`, the lane fixture, and its honest-skip behaviour.

```csharp
[PostgresFact]
public async Task Two_charges_for_the_same_resource_and_hour_cannot_both_exist()
{
    // Only a real database has a unique index. In-memory accepts both rows and the retry test
    // above would pass while production double-charges.
    await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("billing_idempotency"));

    db.BillingLedger.Add(Line(ws, BilledResourceType.App, appId, hour));
    await db.SaveChangesAsync();

    db.BillingLedger.Add(Line(ws, BilledResourceType.App, appId, hour));

    (await Refusal(db)).ConstraintName.Should().Be("IX_BillingLedger_WorkspaceId_ResourceType_ResourceId_BillingHour");
}

[PostgresFact]
public async Task Two_plan_minimum_lines_for_the_same_hour_cannot_both_exist()
{
    // The line with no resource behind it — the one a Charge-only filter would have let through
    // twice. Its ResourceId is null, so this also proves the null is part of the key.
}

[PostgresFact]
public async Task Two_credits_in_the_same_hour_are_both_allowed()
{
    // A person may legitimately top up twice in an hour, which is why the filter names the two
    // kinds the tick writes rather than covering everything.
}

[PostgresFact]
public async Task The_filter_covers_exactly_the_kinds_the_tick_writes()
{
    var definition = await IndexDefinitionAsync(connectionString, "IX_BillingLedger_...");
    FilteredKinds(definition).Should().BeEquivalentTo(new[] { 0, 2 });
}
```

- [ ] **Step 2: Note honestly that you cannot run them**

There is no Docker on the development machine. Say so in the task report, and name the command that does run them:
`dotnet test tests/Harbora.Postgres.Tests/Harbora.Postgres.Tests.csproj -c Release` on a Docker host, or the `postgres` job in `.github/workflows/ci.yml`.

- [ ] **Step 3: Write the operator's release note**

Add to `deploy/RUNBOOK.md` under "What changes for you in this release", in the voice of the notes already there:
- billing ships **disabled**; nothing charges anyone until `Billing:Enabled` is set
- what happens when it is enabled: hourly charging, and workloads stopping at zero
- **that a plan's traffic allowance is not measured or enforced by anything**, so plan copy must not imply it is

- [ ] **Step 4: Full suite and commit**

```bash
git commit -F - <<'MSG'
Prove the index on a database that has one, and say what changes

In-memory accepts both rows, so the retry test passes while production
double-charges. These facts run against real PostgreSQL: two charges for one
resource-hour collide, two plan-minimum lines collide, and two credits in an
hour do not - a person may legitimately top up twice.

Not executed here: this machine has no Docker. On a host that has one,
dotnet test tests/Harbora.Postgres.Tests/Harbora.Postgres.Tests.csproj -c Release

The release note says billing ships disabled, what turning it on does, and
that a plan's traffic allowance is a promise nothing measures - which is the
sort of thing an operator should read from us rather than from a customer.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
MSG
```

---

## Self-Review

**Spec coverage.** Every section of the spec maps to a task: wallet and ledger → 1; rates on size and plan → 2; the `max(base, Σ)` rule and the top-up line → 3; the tick, `SystemWorkspaceScope`, idempotency and backfill → 4; suspension, reason and the running-at-suspension record → 5; the single start gate → 6; the per-plan overage flag → 7; admin credit, resume-on-credit and the per-resource breakdown → 8; the low-balance alert → 9; the Postgres-lane proof of the index and the operator release note including the bandwidth gap → 10.

**Placeholder scan.** Task 4 step 4 deliberately contains `NotImplementedException` scaffolding with implementation notes and an explicit instruction to replace it before the next step; every other step carries the code it needs. Tasks 7, 8 and 9 give test bodies as fragments to be expanded against the shared harness rather than repeating fifty lines of seeding — the harness is defined once in Task 4 and named in each.

**Type consistency.** `QuotaCheck` is reused from `IQuotaService` rather than a new result type. `BilledRunState` is used by `BillingRates`, `BillingHourPlan` and `BillingLedgerEntry` with the same three members throughout. `BillableResource` (input) and `PlannedLine` (output) are distinct records and are not interchanged. `CanStartAsync` is spelled the same in the interface, the implementation, the five call sites and the enumeration test.

**One gap the implementer must close, deliberately left open:** Task 6 step 3 requires verifying the five-path starter list against the code rather than trusting it. The list is what was true when this plan was written; the product may have grown a sixth.
