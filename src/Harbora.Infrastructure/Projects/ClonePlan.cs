﻿namespace Harbora.Infrastructure.Projects;

/// <summary>A volume as it exists on the source application.</summary>
public sealed record CloneSourceVolume(string MountPath, bool ReadOnly, long? SizeLimitBytes);

/// <summary>An application in the environment being copied.</summary>
public sealed record CloneSourceApp(
    Guid Id,
    string Name,
    string Slug,
    string? InstanceSizeKey,
    long MemoryLimitBytes,
    double CpuLimit,
    int DomainCount,
    IReadOnlyList<CloneSourceVolume> Volumes);

/// <summary>A managed service in the environment being copied.</summary>
public sealed record CloneSourceService(
    Guid Id,
    string Name,
    string? InstanceSizeKey,
    long MemoryLimitBytes,
    double CpuLimit,
    bool HasDatabaseName);

/// <summary>Everything the plan needs to decide names, and nothing it does not.</summary>
public sealed record CloneRequest(
    string DesiredName,
    Guid ProjectId,
    IReadOnlyCollection<string> TakenEnvironmentSlugs,
    IReadOnlyCollection<string> TakenAppSlugs,
    IReadOnlyCollection<string> TakenContainerNames,
    IReadOnlyList<CloneSourceApp> Apps,
    IReadOnlyList<CloneSourceService> Services);

public sealed record CloneVolumeSpec(string Name, string MountPath, bool ReadOnly, long? SizeLimitBytes);

public sealed record CloneAppSpec(
    Guid SourceId,
    string Name,
    string Slug,
    string? InstanceSizeKey,
    long MemoryLimitBytes,
    double CpuLimit,
    IReadOnlyList<CloneVolumeSpec> Volumes);

public sealed record CloneServiceSpec(
    Guid SourceId,
    string Name,
    string ContainerName,
    string VolumeName,
    string DatabaseName,
    string? InstanceSizeKey,
    long MemoryLimitBytes,
    double CpuLimit);

/// <summary>
/// What copying an environment would create, decided before anything is created.
///
/// Copying is the ordinary way people get a staging environment, and doing it by hand means
/// recreating a dozen services and getting one of them subtly wrong. The whole risk of doing it
/// automatically is in the names: two docker volumes with one name are one volume, and a clone that
/// reuses a container name takes over the container production is serving from.
///
/// So every name is decided here, up front, against what is already taken <b>and against what this
/// same plan has already claimed</b> — two applications called <c>api</c> and <c>api-2</c> cloned
/// into <c>staging</c> must not both land on <c>api-2-staging</c>.
///
/// Three things are deliberately not copied, and the plan reports the one that is visible:
/// <list type="bullet">
///   <item>Domains. A hostname points at one place; cloning it would either steal production's
///   traffic or fail at the router. The copy gets its own automatic subdomain.</item>
///   <item>Volume contents. The copy gets volumes with the same mounts and nothing in them —
///   silently duplicating a customer database into a less guarded environment is not a thing to do
///   as a side effect of pressing a button.</item>
///   <item>Deployment history, previews and measured sizes. They describe what happened to the
///   original, and carrying them over would make the copy claim a past it does not have.</item>
/// </list>
/// </summary>
public sealed record ClonePlan(
    Guid ProjectId,
    string EnvironmentName,
    string EnvironmentSlug,
    IReadOnlyList<CloneAppSpec> Apps,
    IReadOnlyList<CloneServiceSpec> Services,
    int DomainsLeftBehind)
{
    /// <summary>Memory the whole package asks for, so quota is answered once rather than per item.</summary>
    public long MemoryBytes =>
        Apps.Sum(a => a.MemoryLimitBytes) + Services.Sum(s => s.MemoryLimitBytes);

    public double CpuCores =>
        Apps.Sum(a => a.CpuLimit) + Services.Sum(s => s.CpuLimit);

    public int ResourceCount => Apps.Count + Services.Count;

    public static ClonePlan Of(CloneRequest request)
    {
        var environmentSlug = Unique(
            ProjectService.Slugify(request.DesiredName) is { Length: > 0 } s ? s : "environment",
            request.TakenEnvironmentSlugs);

        var name = string.IsNullOrWhiteSpace(request.DesiredName)
            ? environmentSlug
            : request.DesiredName.Trim();

        // Claimed as we go: the taken lists describe the database, and the plan has to be unique
        // against itself as well or two rows race for one name at insert time.
        var appSlugs = new HashSet<string>(request.TakenAppSlugs, StringComparer.OrdinalIgnoreCase);

        // Uniqueness for a service is on its container name, because that is what the platform
        // actually collides on and what the create form checks. It is carried as the slug the name
        // is built from, so the suffix that resolves a collision reaches the database name too —
        // otherwise the second copy gets a distinct container and the first copy's database name.
        var serviceSlugs = new HashSet<string>(
            request.TakenContainerNames.Select(StripContainerPrefix), StringComparer.OrdinalIgnoreCase);

        var apps = new List<CloneAppSpec>();
        foreach (var app in request.Apps)
        {
            // Claimed after it is handed out. Two source applications cannot share a slug in one
            // workspace today, but this is a public rule and it does not get to assume its caller
            // deduplicated — the cost of being wrong is two containers with one name.
            var slug = Unique(Suffixed(app.Slug, environmentSlug, "app"), appSlugs);
            appSlugs.Add(slug);

            apps.Add(new CloneAppSpec(
                app.Id, app.Name, slug, app.InstanceSizeKey, app.MemoryLimitBytes, app.CpuLimit,
                app.Volumes.Select(v => new CloneVolumeSpec(
                    // The same shape the create form produces, so a cloned volume is not a
                    // different kind of thing from one made by hand.
                    $"harbora-vol-{slug}-{ProjectService.Slugify(v.MountPath.Trim('/'))}",
                    v.MountPath, v.ReadOnly, v.SizeLimitBytes)).ToList()));
        }

        var services = new List<CloneServiceSpec>();
        foreach (var service in request.Services)
        {
            var slug = Unique(
                Suffixed(ProjectService.Slugify(service.Name), environmentSlug, "service"), serviceSlugs);
            serviceSlugs.Add(slug);

            var container = ContainerPrefix + slug;

            services.Add(new CloneServiceSpec(
                service.Id, service.Name, container, $"{container}-data",
                service.HasDatabaseName ? slug.Replace('-', '_') : string.Empty,
                service.InstanceSizeKey, service.MemoryLimitBytes, service.CpuLimit));
        }

        return new ClonePlan(request.ProjectId, name, environmentSlug, apps, services,
            request.Apps.Sum(a => a.DomainCount));
    }

    /// <summary>
    /// The variable names an attach owns, so copying an application's configuration does not carry
    /// over a password and a hostname that point at the original environment's database.
    ///
    /// This is the failure the whole feature would otherwise ship with: the copy comes up, connects
    /// to production's database, and everything looks like it worked.
    /// </summary>
    /// <param name="services">Each cloned service's name and the keys it writes on attach.</param>
    public static IReadOnlySet<string> AttachOwnedKeys(
        IEnumerable<(string Name, IReadOnlyCollection<string> Keys)> services)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, keys) in services)
        {
            var prefix = Harbora.Infrastructure.Services.AttachKeys.PrefixFor(name);
            foreach (var key in keys)
            {
                owned.Add(key);
                owned.Add(prefix + key);
            }
        }

        return owned;
    }

    /// <summary>
    /// Whether an application in the source environment was attached to a service there.
    ///
    /// There is no attachment table — an attach writes variables — so this reads the one signal
    /// that is always written: the service's own prefixed set. Guessing from the unprefixed names
    /// instead would report every application as attached to every database.
    /// </summary>
    public static bool IsAttachedTo(IEnumerable<string> appVariableKeys, string serviceName)
    {
        var prefix = Harbora.Infrastructure.Services.AttachKeys.PrefixFor(serviceName);
        return appVariableKeys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>The clone's slug for a source slug, keeping the 40-character DNS ceiling.</summary>
    private static string Suffixed(string source, string environmentSlug, string fallback)
    {
        var head = string.IsNullOrEmpty(source) ? fallback : source;
        var combined = $"{head}-{environmentSlug}";

        // Slugify enforces the ceiling; going through it again also refuses anything the source
        // slug should not have contained in the first place.
        var slug = ProjectService.Slugify(combined);
        return slug.Length == 0 ? fallback : slug;
    }

    /// <summary>The prefix every managed service's container name carries.</summary>
    public const string ContainerPrefix = "harbora-svc-";

    private static string StripContainerPrefix(string container) =>
        container.StartsWith(ContainerPrefix, StringComparison.OrdinalIgnoreCase)
            ? container[ContainerPrefix.Length..]
            : container;

    private static string Unique(string candidate, IReadOnlyCollection<string> taken)
    {
        if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;

        for (var n = 2; ; n++)
        {
            var next = $"{candidate}-{n}";
            if (!taken.Contains(next, StringComparer.OrdinalIgnoreCase)) return next;
        }
    }
}
