namespace Harbora.Infrastructure.Dashboard;

/// <summary>How much a finding deserves to interrupt someone.</summary>
public enum AttentionLevel
{
    /// <summary>Something is down or unprotected right now.</summary>
    Critical = 0,
    /// <summary>Working, but heading somewhere bad.</summary>
    Warning = 1,
    /// <summary>Worth doing, nothing is wrong.</summary>
    Info = 2
}

/// <summary>What is wrong with a certificate, as a fact rather than a sentence.</summary>
public enum CertificateIssue
{
    /// <summary>Issuance failed; the argument is the summarised error, when there is one.</summary>
    IssueFailed = 0,
    /// <summary>Already expired; the argument is the date it did.</summary>
    Expired = 1,
    /// <summary>Due within the watch window; the argument is the number of days left.</summary>
    ExpiringSoon = 2
}

/// <summary>Which kind of delivery channel stopped working.</summary>
public enum ChannelKind
{
    Alert = 0,
    BackupDelivery = 1,

    /// <summary>
    /// An <c>EventSubscription</c> (P6, 2026-08-20 platform-options plan) — extends this enum rather
    /// than forking a second "broken channel" concept, per the plan's own instruction: a subscription
    /// whose deliveries keep failing surfaces through this same existing path into the dashboard
    /// Attention block, exactly like a broken <see cref="Alert"/> or <see cref="BackupDelivery"/>
    /// channel already does.
    /// </summary>
    EventSubscription = 2
}

/// <summary>
/// One thing worth a person's attention, and where to go about it.
///
/// Everything user-facing is a resource key plus arguments, not a finished sentence. The first
/// version composed English here, which put the most important copy in the panel — deploy failed,
/// disk filling, certificate expiring — in a language the person never chose. The rule decides
/// <em>what</em> to say; the view, which knows the culture, decides how it reads.
/// </summary>
public sealed record AttentionItem
{
    public required AttentionLevel Level { get; init; }

    /// <summary>Resource key for the headline; also its English text.</summary>
    public required string TitleKey { get; init; }
    public string[] TitleArgs { get; init; } = [];

    /// <summary>Resource key for the second line, when it is copy.</summary>
    public string? DetailKey { get; init; }
    public string[] DetailArgs { get; init; } = [];

    /// <summary>
    /// Verbatim second line — a summarised error message. Never localised: it is data from the
    /// failing thing, and rewriting it would sever it from the logs it came from.
    /// </summary>
    public string? DetailText { get; init; }

    public string? ActionKey { get; init; }
    public string? ActionUrl { get; init; }
}

/// <summary>Everything the rules need, already read from the database.</summary>
public sealed record AttentionFacts
{
    public IReadOnlyList<(string App, Guid DeploymentId, string? Error)> FailedDeployments { get; init; } = [];
    public IReadOnlyList<(string App, Guid AppId)> CrashedApps { get; init; } = [];
    public IReadOnlyList<(string Target, string? Error)> FailedBackups { get; init; } = [];

    /// <summary>
    /// Managed services (databases/caches) whose last provision attempt failed — P4, 2026-08-17
    /// app-environment-management design. Before this arm existed a failed database said nothing on
    /// the dashboard at all; it only ever showed up as a red pill on its own page, for whoever thought
    /// to look.
    /// </summary>
    public IReadOnlyList<(string Name, Guid ServiceId, string? Error)> FailedServices { get; init; } = [];

    /// <summary>Alert rules and backup channels that recorded a failure on their last attempt.</summary>
    public IReadOnlyList<(string Name, ChannelKind Kind, string Error)> BrokenChannels { get; init; } = [];

    /// <summary>
    /// A function whose two most recently completed invocations both failed — F4 (2026-08-21
    /// functions-and-services plan, "Function failures become visible"). "Repeated" is deliberately
    /// two in a row, not merely one: <see cref="Harbora.Domain.Notifications.EventKind.FunctionFailed"/>
    /// already tells anyone
    /// subscribed about a single failed run, and a lone failure is often a transient blip (the app was
    /// mid-deploy, a dependency hiccuped) that the next scheduled run quietly clears on its own —
    /// exactly the kind of one-off this dashboard's own rule says is decoration, not attention. Two
    /// failures with nothing successful between them is no longer a blip; it is a scheduled function
    /// that has stopped doing its job and, absent this arm, would keep failing silently — the "check
    /// that reports success for work it never did" defect class this plan exists to close.
    /// </summary>
    public IReadOnlyList<(string Function, string App, Guid AppId, Guid FunctionId, string? Error)>
        RepeatedFunctionFailures { get; init; } = [];

    /// <summary>
    /// Domains whose certificate is missing, expired, or close to it. The argument depends on the
    /// issue — see <see cref="CertificateIssue"/>. Structured on purpose: the first version carried
    /// prose here and the rule sniffed it for the word "expired" to pick a severity, which is a
    /// translation away from always choosing Warning.
    /// </summary>
    public IReadOnlyList<(string Host, CertificateIssue Issue, string? Argument)> CertificateProblems { get; init; } = [];

    public double DiskUsedRatio { get; init; }

    /// <summary>Apps that exist but have never had a successful deployment.</summary>
    public IReadOnlyList<(string App, Guid AppId)> NeverDeployed { get; init; } = [];

    public bool HasAnyApp { get; init; }
    public bool HasAnyBackupSchedule { get; init; }

    /// <summary>
    /// The newer release tag, when the update check is on and found one strictly ahead of the
    /// running build. Null when the check is off, found nothing newer, or the account is not an
    /// operator — a customer has no install button to reach.
    /// </summary>
    public string? UpdateAvailableTag { get; init; }
}

/// <summary>
/// Turns the workspace's state into the short list a dashboard should open with.
///
/// The rule this follows, and the reason it is a separate testable class: <b>nothing appears here that
/// a person cannot act on</b>. A count of total deployments is not attention — it is decoration. Every
/// item below names something that is wrong or about to be, and where to go about it.
///
/// Ordering is by how much it hurts, then by how recent, so the first line is always the one worth
/// reading first.
/// </summary>
public static class AttentionRules
{
    /// <summary>
    /// Disk is a platform-wide problem: past this, deploys and backups start failing.
    ///
    /// <para>
    /// This is <see cref="Build"/>'s fallback default, used only when no ratio is supplied. The value
    /// an installation actually sees comes from <c>Monitoring:DiskWarnRatio</c>
    /// (<see cref="Harbora.Infrastructure.Monitoring.MonitoringOptions"/>), passed in by
    /// <see cref="AttentionService"/> — the same figure the disk-warning alert and the monitoring
    /// page's own banner read, so all three agree once it is configured.
    /// </para>
    /// </summary>
    public const double DiskWarnRatio = 0.85;

    /// <summary>Beyond this the list stops being a list and becomes a wall.</summary>
    public const int MaxItems = 8;

    // The vocabulary, named so the localisation guard can walk it. A key added to Build and not
    // here escapes the guard, which is why Build only ever uses these constants.
    public const string DeployFailedTitle = "{0}: deploy failed";
    public const string DeployFailedDetail = "The deployment did not finish.";
    public const string DeployFailedAction = "Open the deployment";
    public const string CrashedTitle = "{0} is not running";
    public const string CrashedDetail = "Its container keeps stopping or restarting.";
    public const string CrashedAction = "Open the app";
    public const string CertificateExpiredTitle = "{0}: certificate expired";
    public const string CertificateAttentionTitle = "{0}: certificate needs attention";
    public const string CertificateExpiredDetail = "The certificate expired on {0}.";
    public const string CertificateExpiringDetail = "The certificate expires in {0} days and has not renewed yet.";
    public const string CertificateFailedDetail = "The certificate could not be issued.";
    public const string CertificateAction = "Check the domain";
    public const string BackupFailedTitle = "Backup failed: {0}";
    public const string BackupFailedDetail = "The backup did not complete.";
    public const string BackupsAction = "Open backups";
    public const string ServiceFailedTitle = "{0}: database failed to provision";
    public const string ServiceFailedDetail = "It did not come up.";
    public const string ServiceFailedAction = "Open the database";
    public const string ChannelTitle = "{0} is not delivering";
    public const string ChannelAlertDetail = "Alert channel: {0}";
    public const string ChannelBackupDetail = "Backup delivery: {0}";
    public const string ChannelEventDetail = "Event subscription: {0}";
    public const string AlertsAction = "Open alerts";
    public const string EventSubscriptionsAction = "Open event subscriptions";
    public const string FunctionFailedTitle = "{0} ({1}) keeps failing";
    public const string FunctionFailedDetail = "Its last two runs both failed.";
    public const string FunctionFailedAction = "Open the function";
    public const string DiskTitle = "Disk is filling up";
    public const string DiskDetail = "{0}% used. Builds and backups fail once it is full.";
    public const string MonitoringAction = "Open monitoring";
    public const string NeverDeployedTitle = "{0} has never been deployed";
    public const string NeverDeployedDetail = "It exists but nothing is running yet.";
    public const string NeverDeployedAction = "Deploy it";
    public const string NoBackupsTitle = "No scheduled backups";
    public const string NoBackupsDetail = "Nothing here is being backed up automatically.";
    public const string NoBackupsAction = "Set one up";
    public const string UpdateTitle = "Harbora {0} has been released";
    public const string UpdateDetail = "This panel runs an older build. Updating is a step on the server.";
    public const string UpdateAction = "How to update";

    /// <summary>Every key the rules can emit. The guard that keeps them translated walks this.</summary>
    public static readonly IReadOnlyList<string> AllKeys =
    [
        DeployFailedTitle, DeployFailedDetail, DeployFailedAction,
        CrashedTitle, CrashedDetail, CrashedAction,
        CertificateExpiredTitle, CertificateAttentionTitle,
        CertificateExpiredDetail, CertificateExpiringDetail, CertificateFailedDetail, CertificateAction,
        BackupFailedTitle, BackupFailedDetail, BackupsAction,
        ServiceFailedTitle, ServiceFailedDetail, ServiceFailedAction,
        ChannelTitle, ChannelAlertDetail, ChannelBackupDetail, ChannelEventDetail, AlertsAction, EventSubscriptionsAction,
        FunctionFailedTitle, FunctionFailedDetail, FunctionFailedAction,
        DiskTitle, DiskDetail, MonitoringAction,
        NeverDeployedTitle, NeverDeployedDetail, NeverDeployedAction,
        NoBackupsTitle, NoBackupsDetail, NoBackupsAction,
        UpdateTitle, UpdateDetail, UpdateAction
    ];

    /// <param name="diskWarnRatio">
    /// The configured fraction of disk that counts as a problem. Defaults to <see cref="DiskWarnRatio"/>
    /// for a caller that does not pass one; <see cref="AttentionService"/> always passes the
    /// installation's configured value.
    /// </param>
    public static IReadOnlyList<AttentionItem> Build(AttentionFacts facts, double diskWarnRatio = DiskWarnRatio)
    {
        var items = new List<AttentionItem>();

        foreach (var (app, deploymentId, error) in facts.FailedDeployments)
            items.Add(new()
            {
                Level = AttentionLevel.Critical,
                TitleKey = DeployFailedTitle, TitleArgs = [app],
                DetailText = Summarise(error),
                DetailKey = Summarise(error) is null ? DeployFailedDetail : null,
                ActionKey = DeployFailedAction, ActionUrl = $"/deployments/details/{deploymentId}"
            });

        foreach (var (app, appId) in facts.CrashedApps)
            items.Add(new()
            {
                Level = AttentionLevel.Critical,
                TitleKey = CrashedTitle, TitleArgs = [app],
                DetailKey = CrashedDetail,
                ActionKey = CrashedAction, ActionUrl = $"/apps/details/{appId}"
            });

        foreach (var (host, issue, argument) in facts.CertificateProblems)
            items.Add(issue switch
            {
                // An expired certificate is a broken site; one that is merely due is not, yet.
                CertificateIssue.Expired => new()
                {
                    Level = AttentionLevel.Critical,
                    TitleKey = CertificateExpiredTitle, TitleArgs = [host],
                    DetailKey = CertificateExpiredDetail, DetailArgs = [argument ?? "?"],
                    ActionKey = CertificateAction, ActionUrl = "/domains"
                },
                CertificateIssue.ExpiringSoon => new()
                {
                    Level = AttentionLevel.Warning,
                    TitleKey = CertificateAttentionTitle, TitleArgs = [host],
                    DetailKey = CertificateExpiringDetail, DetailArgs = [argument ?? "?"],
                    ActionKey = CertificateAction, ActionUrl = "/domains"
                },
                _ => new AttentionItem
                {
                    Level = AttentionLevel.Warning,
                    TitleKey = CertificateAttentionTitle, TitleArgs = [host],
                    DetailText = argument,
                    DetailKey = argument is null ? CertificateFailedDetail : null,
                    ActionKey = CertificateAction, ActionUrl = "/domains"
                }
            });

        foreach (var (target, error) in facts.FailedBackups)
            items.Add(new()
            {
                Level = AttentionLevel.Critical,
                TitleKey = BackupFailedTitle, TitleArgs = [target],
                DetailText = Summarise(error),
                DetailKey = Summarise(error) is null ? BackupFailedDetail : null,
                ActionKey = BackupsAction, ActionUrl = "/backups"
            });

        foreach (var (name, serviceId, error) in facts.FailedServices)
            items.Add(new()
            {
                Level = AttentionLevel.Critical,
                TitleKey = ServiceFailedTitle, TitleArgs = [name],
                DetailText = Summarise(error),
                DetailKey = Summarise(error) is null ? ServiceFailedDetail : null,
                ActionKey = ServiceFailedAction, ActionUrl = $"/databases/{serviceId}"
            });

        foreach (var (function, app, appId, functionId, error) in facts.RepeatedFunctionFailures)
            items.Add(new()
            {
                Level = AttentionLevel.Critical,
                TitleKey = FunctionFailedTitle, TitleArgs = [function, app],
                DetailText = Summarise(error),
                DetailKey = Summarise(error) is null ? FunctionFailedDetail : null,
                ActionKey = FunctionFailedAction, ActionUrl = $"/functions/{appId}/{functionId}"
            });

        foreach (var (name, kind, error) in facts.BrokenChannels)
            items.Add(new()
            {
                Level = AttentionLevel.Warning,
                // A channel that fails silently is the reason nobody hears about any of the above.
                TitleKey = ChannelTitle, TitleArgs = [name],
                DetailKey = kind switch
                {
                    ChannelKind.BackupDelivery => ChannelBackupDetail,
                    ChannelKind.EventSubscription => ChannelEventDetail,
                    _ => ChannelAlertDetail
                },
                DetailArgs = [Summarise(error) ?? string.Empty],
                ActionKey = kind switch
                {
                    ChannelKind.BackupDelivery => BackupsAction,
                    ChannelKind.EventSubscription => EventSubscriptionsAction,
                    _ => AlertsAction
                },
                ActionUrl = kind switch
                {
                    ChannelKind.BackupDelivery => "/backups",
                    ChannelKind.EventSubscription => "/notifications/webhooks",
                    _ => "/monitoring"
                }
            });

        if (facts.DiskUsedRatio >= diskWarnRatio)
            items.Add(new()
            {
                Level = AttentionLevel.Warning,
                TitleKey = DiskTitle,
                DetailKey = DiskDetail, DetailArgs = [$"{facts.DiskUsedRatio * 100:0}"],
                ActionKey = MonitoringAction, ActionUrl = "/monitoring"
            });

        foreach (var (app, appId) in facts.NeverDeployed)
            items.Add(new()
            {
                Level = AttentionLevel.Info,
                TitleKey = NeverDeployedTitle, TitleArgs = [app],
                DetailKey = NeverDeployedDetail,
                ActionKey = NeverDeployedAction, ActionUrl = $"/apps/details/{appId}"
            });

        // News, not a problem: the panel keeps working perfectly on an older build. Info level, and
        // only for the operator who can actually do something about it.
        if (facts.UpdateAvailableTag is { Length: > 0 } tag)
            items.Add(new()
            {
                Level = AttentionLevel.Info,
                TitleKey = UpdateTitle, TitleArgs = [tag],
                DetailKey = UpdateDetail,
                ActionKey = UpdateAction, ActionUrl = "/admin/settings"
            });

        // Onboarding, and only while it is true. A workspace with apps and no backup schedule is one
        // bad day from having nothing; a workspace with no apps has nothing to protect yet.
        if (facts.HasAnyApp && !facts.HasAnyBackupSchedule)
            items.Add(new()
            {
                Level = AttentionLevel.Info,
                TitleKey = NoBackupsTitle,
                DetailKey = NoBackupsDetail,
                ActionKey = NoBackupsAction, ActionUrl = "/backups"
            });

        return items
            .OrderBy(i => (int)i.Level)
            .Take(MaxItems)
            .ToList();
    }

    /// <summary>
    /// The first sentence of an error, which is the part a person reads. The full text stays on the
    /// page it came from — a dashboard that reprints a stack trace is not a dashboard.
    /// </summary>
    public static string? Summarise(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;

        var text = error.Trim().ReplaceLineEndings(" ");
        var stop = text.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0) text = text[..(stop + 1)];

        return text.Length <= 160 ? text : text[..160].TrimEnd() + "…";
    }
}
