using Harbora.Infrastructure.Tenancy;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// One eviction policy, and what choosing it costs — in a sentence rather than as a Redis constant.
///
/// <para>
/// The names are the ones Redis itself takes, because they are what a customer will read in every
/// other document about Redis and what <c>CONFIG GET maxmemory-policy</c> will hand back. But a list
/// of eight lower-case identifiers is not a choice anybody can make: the two that matter differ by
/// whether running out of room silently deletes a customer's data or silently stops their writes,
/// and neither of those is inferable from the string.
/// </para>
/// </summary>
/// <param name="Key">Exactly the token Redis accepts. Never localised.</param>
/// <param name="Consequence">What happens when the instance reaches <c>maxmemory</c>.</param>
/// <param name="SuitedTo">The workload this is the right answer for.</param>
public sealed record RedisEvictionChoice(
    string Key,
    string Label, string LabelFa,
    string Consequence, string ConsequenceFa,
    string SuitedTo, string SuitedToFa,
    bool Evicts);

/// <summary>
/// Per-instance <c>maxmemory</c> and <c>maxmemory-policy</c> for a managed Redis.
///
/// <para>
/// Every Redis this platform has ever started ran with <c>--appendonly yes</c> and nothing else,
/// which means <c>maxmemory 0</c> and Redis's compiled default of <c>noeviction</c>. That is exactly
/// right for a queue or a primary store — eviction there is silent data loss — and exactly wrong for
/// a cache, where the instance fills, starts answering <c>OOM command not allowed when used memory
/// &gt; 'maxmemory'</c>, and the customer's application begins failing in a way nothing in the panel
/// explains. One default cannot serve both, which is why this is chosen rather than assumed.
/// </para>
///
/// <para>
/// A plan class beside <see cref="CredentialRotationPlan"/>, <see cref="DatabaseTls"/> and
/// <c>ConnectionProbe</c> rather than a widening of <see cref="ServiceDefinition"/>: the catalogue
/// describes a service <i>type</i>, and this is a fact about one <i>instance</i>. The same reason
/// TLS's server arguments are assembled at the provisioning site rather than inside the Redis entry.
/// </para>
/// </summary>
public static class RedisMemoryPolicy
{
    public const string NoEviction = "noeviction";
    public const string AllKeysLru = "allkeys-lru";
    public const string AllKeysLfu = "allkeys-lfu";
    public const string VolatileLru = "volatile-lru";
    public const string VolatileTtl = "volatile-ttl";

    /// <summary>
    /// What Redis does when it has never been told otherwise, and therefore what every instance
    /// created before this shipped is running right now. Named rather than assumed, because
    /// "unconfigured" and "explicitly set to noeviction" produce the same behaviour and must not be
    /// stored as the same value — see <c>ManagedService.RedisEvictionPolicy</c>.
    /// </summary>
    public const string EngineDefault = NoEviction;

    /// <summary>
    /// Redis will not usefully run in less than this. A maxmemory of a few kilobytes is a Redis that
    /// refuses or evicts on its own bookkeeping before a customer's first key lands, which looks
    /// exactly like a broken instance.
    /// </summary>
    public const long MinimumBytes = 1024 * 1024;

    /// <summary>
    /// How much of the container's memory ceiling <c>maxmemory</c> may claim.
    ///
    /// <para>
    /// Not all of it, and the gap is not a safety-margin superstition. <c>maxmemory</c> counts the
    /// dataset; the process also holds client output buffers, replication backlog and allocator
    /// fragmentation on top of it — and every Redis here runs with <c>--appendonly yes</c>, so an AOF
    /// rewrite forks and the copy-on-write pages are charged to the same cgroup. A maxmemory set to
    /// the whole container limit means the kernel OOM-kills the container before Redis ever reaches
    /// the point of evicting, which is the precise failure this feature exists to prevent, arrived at
    /// from the other direction.
    /// </para>
    /// </summary>
    public const double UsableFraction = 0.8;

    /// <summary>
    /// The choices offered, and only these five.
    ///
    /// <para>
    /// Redis accepts eight. <c>allkeys-random</c> and <c>volatile-random</c> are omitted deliberately:
    /// neither is ever the better answer than its LRU counterpart for a workload somebody had to be
    /// talked through, and offering them turns a decision into a menu.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<RedisEvictionChoice> Choices =
    [
        new(NoEviction,
            "Never evict", "هرگز حذف نکن",
            "Writes are refused once the instance is full. Nothing already stored is ever deleted.",
            "با پر شدن نمونه، نوشتن رد می‌شود. هیچ چیزی که ذخیره شده حذف نمی‌شود.",
            "A queue, a session store you cannot rebuild, or any primary store. This is what Redis does when nobody chooses.",
            "صف، جایی که نمی‌توان دوباره ساختش، یا هر ذخیره‌گاه اصلی. این همان کاری است که Redis بدون انتخاب انجام می‌دهد.",
            Evicts: false),

        new(AllKeysLru,
            "Evict the least recently used key", "حذف کلیدی که دیرتر از همه استفاده شده",
            "Any key may be deleted to make room, whether or not it has an expiry. Writes never fail for want of memory.",
            "هر کلیدی ممکن است برای باز کردن جا حذف شود، چه انقضا داشته باشد چه نه. نوشتن هرگز به خاطر حافظه شکست نمی‌خورد.",
            "A cache. Everything in it can be fetched again from the source it was cached from.",
            "یک cache. هر چیزی در آن را می‌توان دوباره از منبعش گرفت.",
            Evicts: true),

        new(AllKeysLfu,
            "Evict the least frequently used key", "حذف کلیدی که کمتر از همه استفاده شده",
            "Any key may be deleted, choosing the ones asked for least often rather than longest ago.",
            "هر کلیدی ممکن است حذف شود؛ آن‌هایی که کمتر درخواست شده‌اند، نه آن‌هایی که دیرتر.",
            "A cache with a small hot set that must survive an occasional sweep over everything else.",
            "یک cache با مجموعهٔ داغ کوچک که باید از یک پویش گاه‌به‌گاه روی بقیه جان سالم به در ببرد.",
            Evicts: true),

        new(VolatileLru,
            "Evict expiring keys only", "فقط حذف کلیدهای انقضادار",
            "Only keys that already carry an expiry may be deleted. Once none are left, writes are refused.",
            "فقط کلیدهایی که از پیش انقضا دارند حذف می‌شوند. وقتی چیزی نماند، نوشتن رد می‌شود.",
            "One instance holding both cached and permanent data, where the permanent keys carry no expiry.",
            "یک نمونه که هم داده cache و هم داده دائمی دارد و کلیدهای دائمی انقضا ندارند.",
            Evicts: true),

        new(VolatileTtl,
            "Evict the soonest to expire", "حذف نزدیک‌ترین به انقضا",
            "Among keys that carry an expiry, the ones closest to expiring go first. Once none are left, writes are refused.",
            "از میان کلیدهای انقضادار، نزدیک‌ترین‌ها به انقضا اول حذف می‌شوند. وقتی چیزی نماند، نوشتن رد می‌شود.",
            "A cache whose entries already have meaningful lifetimes you would rather Redis respected.",
            "یک cache که ورودی‌هایش عمر معناداری دارند و ترجیح می‌دهید Redis به آن احترام بگذارد.",
            Evicts: true),
    ];

    public static RedisEvictionChoice? Find(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : Choices.FirstOrDefault(c => c.Key == key);

    /// <summary>Whether this key is one Harbora offers. An unknown one is refused, never passed through.</summary>
    public static bool IsKnown(string? key) => Find(key) is not null;

    /// <summary>
    /// The largest <c>maxmemory</c> this container may be given, or null when the container has no
    /// memory ceiling at all and there is therefore nothing to measure against.
    ///
    /// <para>
    /// Null is not zero and must not be rendered as one. A managed service created before instance
    /// sizes existed carries <c>MemoryLimitBytes == 0</c>, which means unlimited — the honest answer
    /// there is "Harbora cannot tell you the ceiling", not a fabricated figure.
    /// </para>
    /// </summary>
    public static long? Ceiling(long containerLimitBytes) =>
        containerLimitBytes <= 0 ? null : (long)(containerLimitBytes * UsableFraction);

    /// <summary>
    /// Why this pair cannot be applied, in a sentence naming the setting and the figure, or null when
    /// it can. Every branch names what refused and what to do about it — "operation failed" is not an
    /// answer anybody can act on.
    /// </summary>
    /// <param name="policy">A key from <see cref="Choices"/>, or null/empty for "not configured".</param>
    /// <param name="maxMemoryBytes">Zero for "no cap", which is what Redis's own <c>maxmemory 0</c> means.</param>
    /// <param name="containerLimitBytes">The instance's own memory ceiling; zero when it has none.</param>
    public static string? WhyRefused(string? policy, long maxMemoryBytes, long containerLimitBytes, bool isFa)
    {
        var chosen = string.IsNullOrWhiteSpace(policy) ? null : policy.Trim();

        if (chosen is not null && !IsKnown(chosen))
        {
            return isFa
                ? $"سیاست «{chosen}» یکی از گزینه‌هایی نیست که Harbora ارائه می‌دهد."
                : $"'{chosen}' is not one of the eviction policies Harbora offers.";
        }

        if (maxMemoryBytes < 0)
        {
            return isFa
                ? "سقف حافظه نمی‌تواند منفی باشد."
                : "The memory cap cannot be negative.";
        }

        // A cap with no policy beside it silently inherits noeviction, which is the opposite of what
        // somebody capping a cache wants and is invisible from the panel.
        if (maxMemoryBytes > 0 && chosen is null)
        {
            return isFa
                ? "وقتی سقف حافظه تعیین می‌کنید باید سیاست حذف را هم انتخاب کنید؛ در غیر این صورت Redis به noeviction برمی‌گردد و با پر شدن، نوشتن را رد می‌کند."
                : "Choosing a memory cap means choosing an eviction policy too. Without one Redis falls back to noeviction and refuses writes once it is full.";
        }

        // The mirror image, and the more dangerous of the two: with no maxmemory Redis never evicts
        // whatever its policy says, so a panel showing "allkeys-lru" beside no cap would be reporting
        // a setting that has no effect on anything.
        if (maxMemoryBytes == 0 && Find(chosen) is { Evicts: true })
        {
            return isFa
                ? "یک سیاست حذف بدون سقف حافظه هیچ اثری ندارد: Redis تا وقتی maxmemory تعیین نشده باشد هیچ کلیدی را حذف نمی‌کند. سقف حافظه را تعیین کنید یا «هرگز حذف نکن» را انتخاب کنید."
                : "An eviction policy with no memory cap does nothing: Redis evicts nothing at all until maxmemory is set. Set a memory cap, or choose 'never evict'.";
        }

        if (maxMemoryBytes > 0 && maxMemoryBytes < MinimumBytes)
        {
            return isFa
                ? $"سقف حافظه باید دست‌کم {ByteSize.Format(MinimumBytes)} باشد."
                : $"The memory cap must be at least {ByteSize.Format(MinimumBytes)}.";
        }

        if (maxMemoryBytes > 0 && Ceiling(containerLimitBytes) is { } ceiling && maxMemoryBytes > ceiling)
        {
            return isFa
                ? $"سقف حافظه {ByteSize.Format(maxMemoryBytes)} از چیزی که این نمونه می‌تواند استفاده کند بیشتر است. "
                  + $"با سقف کانتینر {ByteSize.Format(containerLimitBytes)}، بیشترین مقدار مجاز {ByteSize.Format(ceiling)} است — "
                  + "بقیه برای بازنویسی AOF و بافرهای خود Redis کنار گذاشته می‌شود، وگرنه کانتینر پیش از آنکه Redis چیزی حذف کند kill می‌شود."
                : $"A memory cap of {ByteSize.Format(maxMemoryBytes)} is more than this instance can use. "
                  + $"With a container limit of {ByteSize.Format(containerLimitBytes)} the most that can be set is {ByteSize.Format(ceiling)} — "
                  + "the rest is left for the AOF rewrite and Redis's own buffers, or the container is killed before Redis ever evicts anything.";
        }

        return null;
    }

    /// <summary>
    /// The extra <c>redis-server</c> arguments this instance's settings add, appended to the ones the
    /// catalogue already builds. Empty when nothing has been chosen — which is what makes every
    /// instance created before this shipped keep exactly the command line it has today.
    /// </summary>
    public static IReadOnlyList<string> CommandArguments(string? policy, long maxMemoryBytes)
    {
        var args = new List<string>();

        // The order redis-server reads them in does not matter, but keeping the policy first mirrors
        // LiveArguments below, where it does.
        if (Find(policy) is { } chosen) args.AddRange(["--maxmemory-policy", chosen.Key]);
        if (maxMemoryBytes > 0) args.AddRange(["--maxmemory", maxMemoryBytes.ToString()]);

        return args;
    }

    /// <summary>
    /// The <c>redis-cli</c> invocation that applies these settings to an instance that is already
    /// running, or null when nothing has been chosen and there is therefore nothing to apply.
    ///
    /// <para>
    /// Redis takes both of these live — which is what makes this feature honest rather than a stored
    /// intention. The password goes through <c>REDISCLI_AUTH</c> and never onto the command line, for
    /// the reason <c>ConnectionProbe</c> already states.
    /// </para>
    ///
    /// <para>
    /// Two statements rather than one multi-parameter <c>CONFIG SET</c>: the catalogue still offers
    /// <c>6-alpine</c>, and setting several parameters in one call is a Redis 7 addition that a 6
    /// instance answers with an error. And the policy is set <b>before</b> the cap, not after: the
    /// other order leaves a window in which a full instance is over its brand-new maxmemory while
    /// still holding whatever policy it had, which for a cache being configured for the first time
    /// means refusing the customer's writes for as long as the two commands are apart.
    /// </para>
    /// </summary>
    public static RedisConfigPlan? LiveApply(ServiceCreds creds, string? policy, long maxMemoryBytes)
    {
        var chosen = Find(policy);
        if (chosen is null && maxMemoryBytes <= 0) return null;

        var cli = $"redis-cli -h {Shell(creds.Host)} -p {creds.Port}";
        var statements = new List<string>();

        if (chosen is not null)
            statements.Add($"{cli} CONFIG SET maxmemory-policy {Shell(chosen.Key)}");

        statements.Add($"{cli} CONFIG SET maxmemory {Shell(maxMemoryBytes.ToString())}");

        // `set -e` so the second statement is not reported as the outcome of the pair when the first
        // one already failed — a non-zero exit here is what marks the change as not yet applied, and
        // it has to mean "at least one of these was refused".
        return new RedisConfigPlan(
            ["sh", "-c", "set -e; " + string.Join("; ", statements)],
            new Dictionary<string, string> { ["REDISCLI_AUTH"] = creds.Password });
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

/// <summary>
/// A command that changes a running Redis's configuration, and the environment carrying the password
/// it needs. Shaped like <see cref="RotationPlan"/> and <c>ProbePlan</c>, and run the same way.
/// </summary>
public sealed record RedisConfigPlan(IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Env);

/// <summary>
/// Why a requested <c>maxmemory</c>/<c>maxmemory-policy</c> pair was refused, carried in both
/// languages — the <see cref="Harbora.Application.Abstractions.QuotaRefusedException"/> idiom, reused
/// so the controller that catches this need not show English only.
/// </summary>
public sealed class RedisMemoryPolicyRefusedException(string reason, string? reasonFa)
    : InvalidOperationException(reason)
{
    public string? ReasonFa { get; } = reasonFa;
}
