namespace Harbora.Domain.Common;

/// <summary>Coarse RBAC roles. Fine-grained project permissions layer on top via WorkspaceMember.</summary>
public enum SystemRole
{
    Owner = 0,    // full control, created at first-run setup
    Admin = 1,    // manage everything except billing/owner transfer
    Member = 2,   // developer: create/deploy/manage own apps, databases, routes, git
    Viewer = 3,   // read-only
    // Appended (value 4) so existing persisted values stay stable. Ops role: day-2 operations
    // (restart/stop/start apps, run backups) but NOT create/delete/deploy or platform management.
    Operator = 4
}

public enum WorkspaceRole
{
    Admin = 0,
    Member = 1,
    Viewer = 2,
    // Appended so persisted roles keep their meaning. Day-2 operations without deploy/create.
    Operator = 3
}

/// <summary>How an application is built/sourced.</summary>
/// <summary>
/// What a deployable unit is for. Everything that exists today is <see cref="Web"/>, which is why it
/// is 0 — the column backfills to the current behaviour without touching a single row's meaning.
/// </summary>
public enum ServiceKind
{
    /// <summary>Serves HTTP; can have domains and a public URL.</summary>
    Web = 0,
    /// <summary>Reachable only from inside the project's network — no domain, no public port.</summary>
    Private = 1,
    /// <summary>Long-running process with no inbound traffic.</summary>
    Worker = 2,
    /// <summary>Runs on a schedule and exits.</summary>
    Cron = 3,
    /// <summary>Runs once before a release is switched live; a failure keeps the current version.</summary>
    ReleaseTask = 4,
    /// <summary>Built to static files and served by the proxy.</summary>
    Static = 5
}

public enum AppSourceType
{
    GitRepository = 0,
    Dockerfile = 1,
    DockerCompose = 2,
    PrebuiltImage = 3,
    StaticSite = 4,
    Template = 5,
    // Appended (value 6) so existing persisted values stay stable. Source arrives by upload from a
    // developer's machine (`harbora deploy`) rather than being pulled by the server — the app is
    // created first and code is pushed to it afterwards, with no Git remote in between.
    Upload = 6,
    // Appended (value 7). The source is rows in this database: a function app's code is typed into
    // the panel, and publishing generates the whole build context from those rows. There is no
    // checkout, no upload and no third machine that has to be reachable.
    InlineCode = 7
}

public enum AppStatus
{
    Created = 0,
    Deploying = 1,
    Running = 2,
    Stopped = 3,
    Failed = 4,
    Crashed = 5
}

public enum DeploymentStatus
{
    Queued = 0,
    Building = 1,
    Pushing = 2,
    Deploying = 3,
    Succeeded = 4,
    Failed = 5,
    Cancelled = 6,
    RolledBack = 7,
    // Appended (value 8) so existing persisted values stay stable. Sits logically between
    // Deploying and Succeeded: the new container is up and being health-probed before cutover.
    HealthChecking = 8
}

public enum DeploymentTrigger
{
    Manual = 0,
    GitPush = 1,
    GitTag = 2,
    Webhook = 3,
    Cli = 4,
    Rollback = 5,
    Schedule = 6
}

public enum GitProviderType
{
    GitHub = 0,
    GitLab = 1,
    Gitea = 2,
    Bitbucket = 3,
    Custom = 4
}

public enum LogStream
{
    Build = 0,
    Runtime = 1,
    System = 2
}

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4
}

public enum CertificateStatus
{
    Pending = 0,
    Issued = 1,
    Expired = 2,
    Failed = 3,
    Revoked = 4
}

public enum RouteType
{
    HostBased = 0,
    PathBased = 1,
    Redirect = 2
}

public enum ManagedServiceType
{
    PostgreSql = 0,
    MySql = 1,
    MariaDb = 2,
    Redis = 3,
    MongoDb = 4,

    // Message brokers. An environment that holds several apps and several databases usually holds
    // one of these too, and until now the only way to get one was to deploy it as an ordinary
    // application and wire it up by hand.
    //
    // They are managed services rather than templates for the same reason a database is: Harbora
    // generates the credentials, puts it on the environment's private network, and injects the
    // connection into whatever attaches to it.
    RabbitMq = 5,
    Nats = 6
}

public enum ServiceStatus
{
    Provisioning = 0,
    Running = 1,
    Stopped = 2,
    Failed = 3
}

/// <summary>
/// <b>These numbers are hashed and stored, not just stored.</b>
///
/// <para>
/// Enum wire values are frozen platform-wide, and this one has a second reason on top of the usual
/// column round-trip: <c>BackupRunIdentity.ExclusionKeyFor</c> hashes <c>(int)type</c> together with
/// the target reference, and the resulting <c>Guid</c> is persisted in <c>Job.ExclusiveWith</c> — the
/// value the queue compares to decide that two backups of one target must not run at the same time.
/// </para>
/// <para>
/// So inserting a member in the middle, or reordering these, does not merely mislabel existing rows.
/// It changes what every <c>Pending</c> backup job in the database is exclusive WITH: a queued row
/// keeps the key computed from the old number, the process that dequeues it computes the new one,
/// and the two no longer collide. The guard does not fail loudly — it stops matching, and two
/// snapshots of one target run beside each other, which is the failure the key exists to prevent.
/// Append only.
/// </para>
/// </summary>
public enum BackupType
{
    Database = 0,
    Volume = 1,
    AppConfig = 2,
    FullPlatform = 3,
    Service = 4
}

public enum BackupStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Expired = 4
}

public enum BackupDestinationType
{
    Local = 0,
    S3 = 1,
    /// <summary>Appended: existing rows keep the values they were stored with.</summary>
    Sftp = 2
}

public enum AlertChannel
{
    Email = 0,
    Telegram = 1,
    Discord = 2,
    Webhook = 3
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>The trigger that fired a notification; matched against an Alert's opt-in flags.</summary>
public enum AlertEvent
{
    DeployFailed = 0,
    AppCrashed = 1,
    SslExpiring = 2,
    DiskWarning = 3,
    BackupFailed = 4,
    Test = 5,
    /// <summary>Appended so existing persisted values keep their meaning.</summary>
    ThresholdBreached = 6,
    /// <summary>
    /// The balance is worth fewer than the workspace's chosen number of hours at what the last
    /// charged hour actually cost. Appended, for the same reason as the value above it.
    ///
    /// <para>
    /// Unlike its neighbours this one carries no per-rule opt-in flag on <c>Alert</c>, and that is a
    /// decision rather than an omission — see <c>NotificationService.Matches</c>, which says why.
    /// </para>
    /// </summary>
    LowBalance = 7
}

/// <summary>
/// Which figure a per-application threshold watches. <see cref="CpuPercent"/> and
/// <see cref="MemoryPercent"/> are each a percentage of that application's own allocation rather than
/// of the host: an app is over its limit when it is over <em>its</em> limit, and a host-relative
/// figure would say nothing on a big server and everything on a small one.
/// </summary>
public enum AlertMetric
{
    CpuPercent = 0,
    MemoryPercent = 1,

    /// <summary>
    /// Appended, and the odd one out: a restart count has no allocation to be a percentage of, so
    /// <c>Alert.ThresholdPercent</c> is repurposed for this metric as a plain restart count and
    /// <c>Alert.SustainedMinutes</c> as the rolling window it is counted over — "more than N restarts
    /// in the last M minutes" rather than "held above N% for the whole of M minutes". See
    /// <c>MetricsCollector.EvaluateThresholdsAsync</c> for where that split actually happens.
    /// </summary>
    RestartRate = 2
}

/// <summary>
/// Which of the three ways an <c>AlertIncident</c> stopped firing (2026-08-16
/// monitoring-alerting spec §M4, decision 2 — no single one of the three is complete on its own).
/// </summary>
public enum IncidentClosedReason
{
    /// <summary>Whatever raised the condition observed it clear on its own — a threshold dropping
    /// back under the line, a crashed app coming back, disk freeing up, a certificate renewing.</summary>
    Resolved = 0,

    /// <summary>A person closed it by hand. The only way a deploy or backup failure ever closes:
    /// neither one "recovers" — the next deploy or backup succeeding is a different fact about a
    /// different attempt — so the fix, if there was one, happened somewhere this panel cannot see.</summary>
    Acknowledged = 1,

    /// <summary>Nobody touched it and nothing observed it clear, so the bounded backstop closed it
    /// rather than leaving it open for ever.</summary>
    Expired = 2
}

public enum ServerStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
    Degraded = 3
}

public enum TokenType
{
    Api = 0,
    Cli = 1
}
