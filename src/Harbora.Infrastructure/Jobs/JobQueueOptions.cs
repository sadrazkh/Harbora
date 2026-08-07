namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// How much of the platform's background work may be in progress at once.
///
/// <para>
/// The queue ran one job at a time. Every deployment, backup, managed-service provision and cron run
/// on the whole install shared a single thread of execution, so one tenant's twenty-minute build was
/// twenty minutes during which nobody else's app could deploy, and a six-hour snapshot would have
/// been six hours of no background work at all. Nothing about the queue required that: the row has
/// carried <c>ClaimedBy</c> and a <c>ClaimStamp</c> concurrency token since it was written.
/// </para>
///
/// <para>
/// The default is deliberately small. A job here is usually a <c>docker build</c> — a whole
/// compiler, on the panel's own host, next to every tenant's running container — so the risk of
/// raising this is not the worker, it is the machine underneath it. Four is enough that a long build
/// stops being a platform-wide outage; a larger install with cores to spare can say so in
/// configuration.
/// </para>
/// </summary>
public sealed class JobQueueOptions
{
    public const string SectionName = "Jobs";

    /// <summary>
    /// The most jobs this process may run at the same time. <c>1</c> is exactly the behaviour the
    /// platform had before, and is the rollback path: an operator who suspects the queue can put the
    /// old worker back with one setting and a restart, without a deployment.
    /// </summary>
    public int MaxConcurrency { get; set; } = DefaultMaxConcurrency;

    /// <summary>
    /// Four, or the core count where that is smaller. Read at construction rather than baked in, so
    /// a one-core VPS — the install this product is most often on — does not start four builds.
    /// </summary>
    public static int DefaultMaxConcurrency => Math.Max(1, Math.Min(4, Environment.ProcessorCount));

    /// <summary>
    /// What the worker actually uses. Clamped at one, because a configured <c>0</c> would otherwise
    /// be a platform that accepts work, records it, and never runs any of it — a stopped queue that
    /// looks like a working one, which is the failure this whole phase exists to remove.
    /// </summary>
    public int EffectiveMaxConcurrency => Math.Max(1, MaxConcurrency);
}
