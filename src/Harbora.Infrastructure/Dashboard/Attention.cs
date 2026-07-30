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

/// <summary>One thing worth a person's attention, and where to go about it.</summary>
public sealed record AttentionItem(
    AttentionLevel Level, string Title, string Detail, string? ActionText = null, string? ActionUrl = null);

/// <summary>Everything the rules need, already read from the database.</summary>
public sealed record AttentionFacts
{
    public IReadOnlyList<(string App, Guid DeploymentId, string? Error)> FailedDeployments { get; init; } = [];
    public IReadOnlyList<(string App, Guid AppId)> CrashedApps { get; init; } = [];
    public IReadOnlyList<(string Target, string? Error)> FailedBackups { get; init; } = [];

    /// <summary>Alert rules and backup channels that recorded a failure on their last attempt.</summary>
    public IReadOnlyList<(string Name, string Kind, string Error)> BrokenChannels { get; init; } = [];

    /// <summary>Domains whose certificate is missing, expired, or close to it.</summary>
    public IReadOnlyList<(string Host, string Problem)> CertificateProblems { get; init; } = [];

    public double DiskUsedRatio { get; init; }

    /// <summary>Apps that exist but have never had a successful deployment.</summary>
    public IReadOnlyList<(string App, Guid AppId)> NeverDeployed { get; init; } = [];

    public bool HasAnyApp { get; init; }
    public bool HasAnyBackupSchedule { get; init; }
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
    /// <summary>Disk is a platform-wide problem: past this, deploys and backups start failing.</summary>
    public const double DiskWarnRatio = 0.85;

    /// <summary>Beyond this the list stops being a list and becomes a wall.</summary>
    public const int MaxItems = 8;

    public static IReadOnlyList<AttentionItem> Build(AttentionFacts facts)
    {
        var items = new List<AttentionItem>();

        foreach (var (app, deploymentId, error) in facts.FailedDeployments)
            items.Add(new(AttentionLevel.Critical,
                $"{app}: deploy failed",
                Summarise(error) ?? "The deployment did not finish.",
                "Open the deployment", $"/deployments/details/{deploymentId}"));

        foreach (var (app, appId) in facts.CrashedApps)
            items.Add(new(AttentionLevel.Critical,
                $"{app} is not running",
                "Its container keeps stopping or restarting.",
                "Open the app", $"/apps/details/{appId}"));

        foreach (var (host, problem) in facts.CertificateProblems)
            items.Add(new(
                // An expired certificate is a broken site; one that is merely due is not, yet.
                problem.Contains("expired", StringComparison.OrdinalIgnoreCase)
                    ? AttentionLevel.Critical : AttentionLevel.Warning,
                $"{host}: certificate {(problem.Contains("expired", StringComparison.OrdinalIgnoreCase) ? "expired" : "needs attention")}",
                problem, "Check the domain", "/domains"));

        foreach (var (target, error) in facts.FailedBackups)
            items.Add(new(AttentionLevel.Critical,
                $"Backup failed: {target}",
                Summarise(error) ?? "The backup did not complete.",
                "Open backups", "/backups"));

        foreach (var (name, kind, error) in facts.BrokenChannels)
            items.Add(new(AttentionLevel.Warning,
                $"{name} is not delivering",
                // A channel that fails silently is the reason nobody hears about any of the above.
                $"{kind}: {Summarise(error)}",
                kind == "Backup delivery" ? "Open backups" : "Open alerts",
                kind == "Backup delivery" ? "/backups" : "/monitoring"));

        if (facts.DiskUsedRatio >= DiskWarnRatio)
            items.Add(new(AttentionLevel.Warning,
                "Disk is filling up",
                $"{facts.DiskUsedRatio * 100:0}% used. Builds and backups fail once it is full.",
                "Open monitoring", "/monitoring"));

        foreach (var (app, appId) in facts.NeverDeployed)
            items.Add(new(AttentionLevel.Info,
                $"{app} has never been deployed",
                "It exists but nothing is running yet.",
                "Deploy it", $"/apps/details/{appId}"));

        // Onboarding, and only while it is true. A workspace with apps and no backup schedule is one
        // bad day from having nothing; a workspace with no apps has nothing to protect yet.
        if (facts.HasAnyApp && !facts.HasAnyBackupSchedule)
            items.Add(new(AttentionLevel.Info,
                "No scheduled backups",
                "Nothing here is being backed up automatically.",
                "Set one up", "/backups"));

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
