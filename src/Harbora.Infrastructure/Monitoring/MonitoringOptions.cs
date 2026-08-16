namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Four operational numbers the collector and the monitoring dashboard used to carry as constants:
/// when a node's disk counts as a problem, how often that warning may repeat, how long a standing
/// per-app threshold breach waits before it is reported again, and how stale a workspace's backups
/// have to be before the dashboard says so.
///
/// <para>
/// Bound the way the other options sections are — see <c>DependencyInjection.AddHarboraInfrastructure</c>
/// — under <see cref="SectionName"/>, and left unset in the shipped <c>appsettings.json</c> so these
/// C# defaults stay the single source of truth for what an installation that configures nothing
/// actually does.
/// </para>
/// </summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>
    /// Fraction of a node's disk that must be in use before it counts as a problem.
    ///
    /// <para>
    /// One number, several readers — not the same trap as <see cref="BackupStalenessHours"/> below,
    /// because every reader means the exact same thing by it: the disk-warning alert
    /// (<see cref="MetricsCollector"/>), the home dashboard's Attention panel
    /// (<see cref="Harbora.Infrastructure.Dashboard.AttentionRules"/>), and the monitoring page's own
    /// banner (<c>MonitoringDashboardViewModel.DiskWarning</c>) all watch the same disk.used /
    /// disk.total samples for the same "disk is becoming a platform-wide problem" fact, so all three
    /// read this one figure. An installation that raises it should see every one of those agree.
    /// </para>
    /// </summary>
    public double DiskWarnRatio { get; set; } = 0.85;

    /// <summary>
    /// How long between disk-warning alerts for the same node, so a full disk nags once per interval
    /// rather than once per collector tick.
    /// </summary>
    public double DiskAlertIntervalHours { get; set; } = 1;

    internal TimeSpan DiskAlertInterval => TimeSpan.FromHours(Math.Max(0, DiskAlertIntervalHours));

    /// <summary>
    /// How long a per-application threshold breach that is still standing waits before it is reported
    /// again. Too short floods the channel with the same fact every collector tick; too long lets a
    /// standing problem go quiet.
    /// </summary>
    public double ThresholdRepeatAfterHours { get; set; } = ThresholdRule.RepeatAfter.TotalHours;

    internal TimeSpan ThresholdRepeatAfter => TimeSpan.FromHours(Math.Max(0, ThresholdRepeatAfterHours));

    /// <summary>
    /// How long since a workspace's last successful backup before the monitoring dashboard's own
    /// warning banner says so.
    ///
    /// <para>
    /// <b>This is one of three backup-staleness numbers in the codebase, and they are not the same
    /// number wearing three hats — collapsing them would be a bug dressed as tidying:</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>This one</b> — <c>MonitoringController.Index</c> — is about a person
    /// looking at a dashboard: "has anything succeeded recently enough that I should stop worrying?"
    /// </description></item>
    /// <item><description><see cref="Harbora.Infrastructure.Backups.VerificationSchedule.StaleAfter"/>
    /// (seven days, left as a constant) is about whether a stored restore verdict can still be
    /// trusted, and drives an actual restore into a scratch database — a much more expensive question
    /// than a dashboard glance, asked far less often on purpose.</description></item>
    /// <item><description><see cref="Harbora.Infrastructure.Tenancy.StorageMeasurer.StaleAfter"/>
    /// (twenty-four hours, left as a constant) is about whether a volume's measured size can still be
    /// trusted for quota and billing — a technical cache lifetime, not a judgement about the backup
    /// itself.</description></item>
    /// </list>
    /// </summary>
    public double BackupStalenessHours { get; set; } = 48;

    /// <summary>
    /// Public, unlike its two siblings above: <c>MonitoringController</c> lives in
    /// <c>Harbora.Web</c>, a different assembly from this one, and needs the clamped
    /// <see cref="TimeSpan"/> the same way <c>MetricsCollector</c> needs
    /// <see cref="DiskAlertInterval"/> and <see cref="ThresholdRepeatAfter"/> — both of which stay
    /// internal because their only reader is inside this assembly.
    /// </summary>
    public TimeSpan BackupStaleness => TimeSpan.FromHours(Math.Max(0, BackupStalenessHours));

    /// <summary>
    /// The bounded backstop close (2026-08-16 monitoring-alerting spec §M4, decision 2): an incident
    /// nobody acknowledges, and whose condition is never observed clearing — the shape of a deploy or
    /// backup failure, which never resolves on its own — closes anyway once it has stood open this
    /// long, rather than staying open for ever. A condition that keeps recurring is unaffected: each
    /// tick that still observes it refreshes <c>AlertIncident.LastObservedAt</c>, but the clock this
    /// bound is measured against is <c>OpenedAt</c>, deliberately — see <c>IncidentService.ExpireStaleAsync</c>.
    /// </summary>
    public double IncidentAutoExpireDays { get; set; } = 14;

    internal TimeSpan IncidentAutoExpireAfter => TimeSpan.FromDays(Math.Max(1, IncidentAutoExpireDays));
}
