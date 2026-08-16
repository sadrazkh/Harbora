using System.Globalization;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Billing;

/// <param name="ServerId">
/// Which host this resource was placed on, so the first hour is prepaid at the rate that host
/// charges. Null for a resource that has no host — a mail domain or mailbox, which carries its own
/// <paramref name="DirectRatePerHourMinor"/> snapshot instead.
///
/// <para>
/// Deliberately not defaulted. A price belongs to a (server, tier) pair now, and a call site that
/// forgot to say where it placed the workload would prepay the global rate while every hour after it
/// was charged the server's — a discrepancy visible only as a bill that does not reconcile against
/// its own first line.
/// </para>
/// </param>
public sealed record CreatedBillableResource(
    BilledResourceType Type,
    Guid Id,
    string Name,
    string? InstanceSizeKey,
    Guid? ServerId,
    long? DirectRatePerHourMinor = null);

/// <summary>A customer-facing refusal raised before a billable resource is persisted.</summary>
public sealed class CreationPaymentRequiredException(string reason, string reasonFa)
    : InvalidOperationException(reason)
{
    public string ReasonFa { get; } = reasonFa;
}

/// <summary>
/// Atomically persists newly-created workloads and prepays their first running hour. The charge
/// uses the same ledger identity as the hourly tick, so the tick sees that resource/hour as paid
/// and cannot debit it a second time.
/// </summary>
public sealed class ResourceCreationBilling(
    HarboraDbContext db,
    ISystemClock clock,
    IOptions<BillingOptions> options,
    WorkspaceBudgetService? budgets = null)
{
    private const int SaveAttempts = 3;
    public string Currency => options.Value.CurrencyOrDefault;

    public async Task<long> SaveAsync(
        Guid workspaceId,
        IReadOnlyCollection<CreatedBillableResource> resources,
        CancellationToken ct)
    {
        if (resources.Count == 0 || !options.Value.Enabled)
        {
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId, ct)
            ?? throw new CreationPaymentRequiredException(
                "The workspace no longer exists.",
                "این فضای کاری دیگر وجود ندارد.");

        // The provider workspace keeps the existing break-glass exemption: billing the control
        // plane out of its own ability to create a repair workload would lock the operator out.
        if (workspace.IsDefault)
        {
            await db.SaveChangesAsync(ct);
            return 0;
        }

        if (workspace.IsSuspended)
            throw new CreationPaymentRequiredException(
                "This workspace is suspended. No resource was created.",
                "این فضای کاری معلق است؛ هیچ منبعی ساخته نشد.");

        var keys = resources.Select(r => r.InstanceSizeKey)
            .Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sizes = await db.InstanceSizes.IgnoreQueryFilters().AsNoTracking()
            .Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, ct);

        // What the hosts these resources landed on charge for those tiers. Read for the same reason
        // the hourly pass reads it: a price belongs to a (server, tier) pair, and prepaying the first
        // hour at the global rate while every hour after it is charged the server's rate would leave
        // a bill that does not reconcile against its own first line.
        var serverIds = resources.Select(r => r.ServerId).OfType<Guid>().Distinct().ToList();
        var offers = await db.ServerInstanceOffers.IgnoreQueryFilters().AsNoTracking()
            .Where(o => serverIds.Contains(o.ServerId) && keys.Contains(o.InstanceSizeKey))
            .ToDictionaryAsync(o => (o.ServerId, o.InstanceSizeKey), ct);

        var priced = new List<(CreatedBillableResource Resource, long Rate)>(resources.Count);
        var total = 0L;
        foreach (var resource in resources)
        {
            if (resource.Type is BilledResourceType.MailDomain or BilledResourceType.Mailbox)
            {
                if (resource.DirectRatePerHourMinor is not { } direct || direct < 0)
                    throw Unpriced(resource.Name);
                try { total = checked(total + direct); }
                catch (OverflowException)
                {
                    throw new CreationPaymentRequiredException(
                        "The first-hour price is too large to charge safely. No resource was created.",
                        "هزینه ساعت اول بیش از حد مجاز است و با اطمینان قابل کسر نیست؛ هیچ منبعی ساخته نشد.");
                }
                priced.Add((resource, direct));
                continue;
            }

            if (resource.Type is not (BilledResourceType.App or BilledResourceType.Service))
                throw new ArgumentException("This resource type cannot be prepaid.", nameof(resources));

            if (string.IsNullOrWhiteSpace(resource.InstanceSizeKey)
                || !sizes.TryGetValue(resource.InstanceSizeKey, out var size))
                throw Unpriced(resource.Name);

            // Resolved through the host, exactly as the hourly pass resolves it: the server's rate if
            // it set one, otherwise the tier's. That is also what lets a provider sell a tier they
            // priced only on one box — which the tier-only lookup used to refuse as unpriced while
            // the meter went on charging it happily.
            ServerInstanceOffer? offer = null;
            if (resource.ServerId is { } serverId)
                offers.TryGetValue((serverId, size.Key), out offer);

            if (ServerRates.ForWorkload(size, offer, BilledRunState.Running) is not { } rate || rate < 0)
                throw Unpriced(resource.Name);

            try { total = checked(total + rate); }
            catch (OverflowException)
            {
                throw new CreationPaymentRequiredException(
                    "The first-hour price is too large to charge safely. No resource was created.",
                    "هزینه ساعت اول بیش از حد مجاز است و با اطمینان قابل کسر نیست؛ هیچ منبعی ساخته نشد.");
            }
            priced.Add((resource, rate));
        }

        var wallet = await db.Wallets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        EnsureAffordable(wallet, total, options.Value.CurrencyOrDefault);

        var budget = budgets ?? new WorkspaceBudgetService(db);
        if (!await budget.CanSpendAsync(workspaceId, total, clock.UtcNow, ct))
        {
            var state = await budget.GetAsync(workspaceId, clock.UtcNow, ct);
            throw new CreationPaymentRequiredException(
                $"This would exceed the workspace monthly spend limit of {Money(state.SpendLimitMinor!.Value)} {Currency}. No resource was created.",
                $"این ساخت از سقف هزینه ماهانهٔ {Money(state.SpendLimitMinor!.Value)} {Currency} عبور می‌کند؛ هیچ منبعی ساخته نشد.");
        }

        var hour = TopOfHour(clock.UtcNow);
        foreach (var (resource, rate) in priced.Where(x => x.Rate > 0))
        {
            db.BillingLedger.Add(new BillingLedgerEntry
            {
                WorkspaceId = workspaceId,
                BillingHour = hour,
                Kind = LedgerKind.Charge,
                AmountMinor = -rate,
                ResourceType = resource.Type,
                ResourceId = resource.Id,
                ResourceName = resource.Name,
                RunState = BilledRunState.Running,
                RatePerHourMinor = rate,
                Hours = 1,
                Description = string.Empty
            });
        }

        Apply(wallet!, -total);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return total;
            }
            catch (DbUpdateConcurrencyException) when (attempt < SaveAttempts)
            {
                var entry = db.Entry(wallet!);
                await entry.ReloadAsync(ct);
                if (entry.State == EntityState.Detached) throw;

                EnsureAffordable(wallet, total, options.Value.CurrencyOrDefault);
                Apply(wallet!, -total);
            }
        }
    }

    private static CreationPaymentRequiredException Unpriced(string name) => new(
        $"'{name}' has no running hourly price. Ask the provider to price its instance size; no resource was created.",
        $"برای «{name}» قیمت ساعتیِ حالت اجرا تنظیم نشده است؛ از مدیر بخواهید پلن آن را قیمت‌گذاری کند. هیچ منبعی ساخته نشد.");

    private static void EnsureAffordable(Wallet? wallet, long required, string fallbackCurrency)
    {
        var balance = wallet?.BalanceMinor ?? 0;
        var currency = wallet?.Currency ?? fallbackCurrency;

        // Even a deliberately free size requires a funded account. This is intentional: a zero
        // balance is not an active customer account and must not be able to allocate platform
        // capacity through whichever size happens to have a zero rate.
        if (balance > 0 && balance >= required) return;

        throw new CreationPaymentRequiredException(
            $"The balance is {Money(balance)} {currency}, but {Money(required)} {currency} is required for the first hour. Top up the workspace; no resource was created.",
            $"موجودی حساب {Money(balance)} {currency} است، اما برای ساعت اول {Money(required)} {currency} لازم است. ابتدا حساب فضای کاری را شارژ کنید؛ هیچ منبعی ساخته نشد.");
    }

    private static void Apply(Wallet wallet, long amount)
    {
        wallet.BalanceMinor += amount;
        wallet.ConcurrencyStamp = Guid.CreateVersion7();
    }

    private static string Money(long minor) =>
        (minor / 100m).ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static DateTimeOffset TopOfHour(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
