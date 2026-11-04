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

/// <summary>What creating some resources would cost, worked out without creating them.</summary>
/// <param name="Lines">Each resource with its hourly rate, null where nobody priced it.</param>
/// <param name="FirstHourMinor">What leaves the balance on creation; zero when <paramref name="Exempt"/>.</param>
/// <param name="BalanceMinor">The workspace's balance, or null when it has no wallet.</param>
/// <param name="Exempt">Billing is off, or this is the provider's own workspace: nothing is charged.</param>
/// <param name="Refusal">Why the creation would be refused, or null when it would be paid for.</param>
public sealed record CreationQuote(
    IReadOnlyList<(CreatedBillableResource Resource, long? HourlyMinor)> Lines,
    long FirstHourMinor,
    long? BalanceMinor,
    string Currency,
    bool Exempt,
    ServiceRefusal? Refusal);

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
            ?? throw WorkspaceGone();

        // The provider workspace keeps the existing break-glass exemption: billing the control
        // plane out of its own ability to create a repair workload would lock the operator out.
        if (workspace.IsDefault)
        {
            await db.SaveChangesAsync(ct);
            return 0;
        }

        if (workspace.IsSuspended)
            throw Suspended();

        var rates = await RatesAsync(resources, ct);

        var priced = new List<(CreatedBillableResource Resource, long Rate)>(resources.Count);
        var total = 0L;
        foreach (var resource in resources)
        {
            if (resource.Type is not (BilledResourceType.MailDomain or BilledResourceType.Mailbox
                or BilledResourceType.App or BilledResourceType.Service))
                throw new ArgumentException("This resource type cannot be prepaid.", nameof(resources));

            if (rates[resource] is not { } rate)
                throw Unpriced(resource.Name);

            total = AddToTotal(total, rate);
            priced.Add((resource, rate));
        }

        var wallet = await db.Wallets.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        EnsureAffordable(wallet, total, options.Value.CurrencyOrDefault);
        await EnsureWithinBudgetAsync(workspaceId, total, ct);

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

    /// <summary>
    /// What <see cref="SaveAsync"/> would do with these resources, saying instead of doing: the rate it
    /// would charge each, the total it would take, and the refusal it would give. Read through the same
    /// rate resolution and the same gates rather than a second copy of them, because a quote that can
    /// disagree with the charge is a bill that surprises somebody. Changes nothing.
    /// </summary>
    public async Task<CreationQuote> QuoteAsync(
        Guid workspaceId, IReadOnlyCollection<CreatedBillableResource> resources, CancellationToken ct)
    {
        var rates = await RatesAsync(resources, ct);
        var lines = resources.Select(r => (Resource: r, HourlyMinor: rates[r])).ToList();
        var wallet = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        var balance = wallet?.BalanceMinor;
        var currency = wallet?.Currency ?? options.Value.CurrencyOrDefault;

        // In SaveAsync's own order, so the refusal named here is the one it would give.
        if (resources.Count == 0 || !options.Value.Enabled)
            return new CreationQuote(lines, 0, balance, currency, Exempt: true, null);

        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (workspace is { IsDefault: true })
            return new CreationQuote(lines, 0, balance, currency, Exempt: true, null);

        var total = 0L;
        try
        {
            if (workspace is null) throw WorkspaceGone();
            if (workspace.IsSuspended) throw Suspended();

            foreach (var line in lines)
            {
                if (line.HourlyMinor is not { } rate) throw Unpriced(line.Resource.Name);
                total = AddToTotal(total, rate);
            }

            EnsureAffordable(wallet, total, options.Value.CurrencyOrDefault);
            await EnsureWithinBudgetAsync(workspaceId, total, ct);
            return new CreationQuote(lines, total, balance, currency, Exempt: false, null);
        }
        catch (CreationPaymentRequiredException ex)
        {
            return new CreationQuote(lines, total, balance, currency, Exempt: false,
                new ServiceRefusal(ex.Message, ex.ReasonFa));
        }
    }

    /// <summary>
    /// What one running hour of each resource costs, null where nobody priced it. The one resolution
    /// of a first-hour rate: <see cref="SaveAsync"/> charges from this and <see cref="QuoteAsync"/>
    /// reports from it.
    /// </summary>
    private async Task<Dictionary<CreatedBillableResource, long?>> RatesAsync(
        IReadOnlyCollection<CreatedBillableResource> resources, CancellationToken ct)
    {
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

        var rates = new Dictionary<CreatedBillableResource, long?>(resources.Count);
        foreach (var resource in resources)
        {
            if (resource.Type is BilledResourceType.MailDomain or BilledResourceType.Mailbox)
            {
                rates[resource] = resource.DirectRatePerHourMinor is { } direct && direct >= 0 ? direct : null;
                continue;
            }

            if (string.IsNullOrWhiteSpace(resource.InstanceSizeKey)
                || !sizes.TryGetValue(resource.InstanceSizeKey, out var size))
            {
                rates[resource] = null;
                continue;
            }

            // Resolved through the host, exactly as the hourly pass resolves it: the server's rate if
            // it set one, otherwise the tier's. That is also what lets a provider sell a tier they
            // priced only on one box — which the tier-only lookup used to refuse as unpriced while
            // the meter went on charging it happily.
            ServerInstanceOffer? offer = null;
            if (resource.ServerId is { } serverId)
                offers.TryGetValue((serverId, size.Key), out offer);

            rates[resource] = ServerRates.ForWorkload(size, offer, BilledRunState.Running) is { } rate && rate >= 0
                ? rate
                : null;
        }

        return rates;
    }

    private static long AddToTotal(long total, long rate)
    {
        try { return checked(total + rate); }
        catch (OverflowException)
        {
            throw new CreationPaymentRequiredException(
                "The first-hour price is too large to charge safely. No resource was created.",
                "هزینه ساعت اول بیش از حد مجاز است و با اطمینان قابل کسر نیست؛ هیچ منبعی ساخته نشد.");
        }
    }

    private async Task EnsureWithinBudgetAsync(Guid workspaceId, long total, CancellationToken ct)
    {
        var budget = budgets ?? new WorkspaceBudgetService(db);
        if (await budget.CanSpendAsync(workspaceId, total, clock.UtcNow, ct)) return;

        var state = await budget.GetAsync(workspaceId, clock.UtcNow, ct);
        throw new CreationPaymentRequiredException(
            $"This would exceed the workspace monthly spend limit of {Money(state.SpendLimitMinor!.Value)} {Currency}. No resource was created.",
            $"این ساخت از سقف هزینه ماهانهٔ {Money(state.SpendLimitMinor!.Value)} {Currency} عبور می‌کند؛ هیچ منبعی ساخته نشد.");
    }

    private static CreationPaymentRequiredException WorkspaceGone() => new(
        "The workspace no longer exists.",
        "این فضای کاری دیگر وجود ندارد.");

    private static CreationPaymentRequiredException Suspended() => new(
        "This workspace is suspended. No resource was created.",
        "این فضای کاری معلق است؛ هیچ منبعی ساخته نشد.");

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
