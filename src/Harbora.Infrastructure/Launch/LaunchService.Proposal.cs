using Harbora.Application.Abstractions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Domain.Settings;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Configuration;
using Harbora.Infrastructure.Services;
using Harbora.Shared.Inspection;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Launch;

/// <summary>What the confirm screen is drawn from, and what it would cost. Nothing in this file writes.</summary>
public sealed partial class LaunchService
{
    /// <summary>A name <see cref="DatabaseProvisioningService.Slugify"/> turns into a container and a database name, and Postgres cuts an identifier at 63.</summary>
    private const int MaxDerivedNameStem = 40;

    public async Task<LaunchProposal?> ProposeAsync(Guid launchId, CancellationToken ct)
    {
        var launch = await db.AppLaunches.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == launchId && l.Status == LaunchStatus.AwaitingConfirmation, ct);
        if (launch is null) return null;

        var inspection = LaunchJson.ReadInspection(launch.InspectionJson);
        var name = launch.SourceKind == LaunchSourceKind.Git
            ? LaunchSource.NameFromCloneUrl(launch.CloneUrl ?? "")
            : LaunchSource.NameFromArchive(launch.ArchivePath ?? "");

        var place = await PlacementAsync(launch.WorkspaceId, ct);

        // Only what is certain is ticked: creating a database costs money, and a guess is not consent.
        var services = inspection.Services.Where(s => s.Certain)
            .Select(s => AttachEnvRoles.TypeFor(s.Kind))
            .Distinct()
            .Select(type => new LaunchServiceChoice(type, Aliases: AliasesFor(inspection, type)))
            .ToList();
        var aliased = services.SelectMany(s => s.Aliases ?? []).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        var fields = inspection.EnvVars
            .Where(v => v.Disposition != EnvVarDisposition.ProvidedByAttach)
            .Select(v => new LaunchEnvField(
                v.Name, v.Disposition,
                v.Disposition == EnvVarDisposition.KeepExample ? v.ExampleValue : null,
                IsSecretByDefault(v), v.SourceFile))
            .ToList();

        var defaults = new LaunchChoices
        {
            Name = name,
            Kind = inspection.Kind == DetectedAppKind.Worker ? ServiceKind.Worker : ServiceKind.Web,
            ContainerPort = inspection.Port ?? 80,
            Command = inspection.StartCommand,
            ReleaseCommand = inspection.ReleaseCommand,
            RootDirectory = inspection.RootDirectory,
            EnvironmentId = place.Environment?.Id,
            InstanceSizeKey = await DefaultSizeKeyAsync(launch.WorkspaceId, ct),
            Services = services,
            // An alias of a database that is ticked has nothing to ask: the attachment answers it.
            EnvVars = fields.Where(f => !aliased.Contains(f.Key))
                .Select(f => new LaunchEnvChoice(f.Key, f.SuggestedValue, f.IsSecret))
                .ToList()
        };

        // The operator's catalogue, not the shipped one: offering an engine they withdrew would only
        // earn a refusal one click later.
        var catalog = await ServiceCatalogReader.EffectiveAsync(db, engine, ct);
        var options = catalog
            .Select(entry => new LaunchServiceOption(
                entry.Type, entry.DisplayName, entry.DisplayNameFa,
                inspection.Services.FirstOrDefault(s => AttachEnvRoles.TypeFor(s.Kind) == entry.Type)))
            .ToList();

        return new LaunchProposal(launchId, inspection, defaults, name, fields, options);
    }

    /// <summary>
    /// A variable only a person can answer is usually a third party's credential, so the safer wrong
    /// guess is to mask it; a name that plainly is one is masked and anything else is left readable.
    /// A generated one is always secret, and the example file's own value never makes anything secret.
    /// </summary>
    private static bool IsSecretByDefault(DetectedEnvVar variable) => variable.Disposition switch
    {
        EnvVarDisposition.GeneratedSecret => true,
        EnvVarDisposition.NeedsAnswer or EnvVarDisposition.AliasOfAttach =>
            SecretNameParts.Any(part => variable.Name.Contains(part, StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    private static readonly string[] SecretNameParts = ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASS", "CREDENTIAL"];

    /// <summary>
    /// Every spelling in the example env file of something this engine's attachment provides, turned
    /// into the attach variable it reads from. A role this engine has no value for stays an ordinary
    /// question on the screen rather than an alias of nothing.
    /// </summary>
    private static IReadOnlyList<AttachEnvAlias> AliasesFor(SourceInspection inspection, ManagedServiceType type) =>
        inspection.EnvVars
            .Where(v => v.Disposition == EnvVarDisposition.AliasOfAttach
                        && v.Service is { } service && AttachEnvRoles.TypeFor(service) == type
                        && v.Role is not null)
            .Select(v => AttachEnvRoles.SourceKeyFor(type, v.Role!.Value) is { } source ? new AttachEnvAlias(v.Name, source) : null)
            .OfType<AttachEnvAlias>()
            .ToList();

    /// <summary>
    /// The size the chooser would open on. <c>SizePickerService</c> is what really answers this for a
    /// form, but it lives in the web project and this does not: the rules below are its own, asked in
    /// its own order, and <c>LaunchSizeDefaultParityTests</c> holds the two together. Never "no limit":
    /// that is the one thing a create must not pick for somebody.
    /// </summary>
    private async Task<string?> DefaultSizeKeyAsync(Guid workspaceId, CancellationToken ct)
    {
        var platformDefault = await db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Key == SettingKeys.DefaultInstanceSize)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(platformDefault)) return platformDefault;

        var plan = await PlanForAsync(workspaceId, ct);
        var allowedKeys = (plan?.AllowedSizeKeys ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sizes = await db.InstanceSizes.AsNoTracking()
            .Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct);
        var offers = await db.ServerInstanceOffers.AsNoTracking().ToListAsync(ct);
        var servers = await db.Servers.AsNoTracking()
            .OrderByDescending(s => s.IsLocal).ThenBy(s => s.Name).ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(plan?.NodePool))
            servers = servers.Where(s => string.Equals(s.Pool, plan!.NodePool, StringComparison.OrdinalIgnoreCase)).ToList();

        var capacities = await capacity.GetAllAsync(ct);

        foreach (var server in servers)
        {
            var reading = capacities.FirstOrDefault(c => c.ServerId == server.Id);
            if (reading is { IsOnline: false }) continue;

            foreach (var size in sizes)
            {
                var offer = offers.FirstOrDefault(o => o.ServerId == server.Id
                    && string.Equals(o.InstanceSizeKey, size.Key, StringComparison.OrdinalIgnoreCase));

                if (allowedKeys.Count > 0 && !allowedKeys.Contains(size.Key)) continue;
                if (!ServerRates.OffersNewWork(offer)) continue;
                if (reading is not null && !reading.CanFit(size.MemoryBytes, size.CpuCores)) continue;
                if (billing.Value.Enabled && ServerRates.ForWorkload(size, offer, BilledRunState.Running) is null) continue;

                return size.Key;
            }
        }

        return null;
    }

    /// <summary>The workspace's plan, or the default one: the same resolution the hourly pass makes.</summary>
    private async Task<Plan?> PlanForAsync(Guid workspaceId, CancellationToken ct)
    {
        var planId = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == workspaceId).Select(w => w.PlanId).FirstOrDefaultAsync(ct);

        return planId is { } id
            ? await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, ct);
    }

    // ---- what it costs -------------------------------------------------------------------------

    public async Task<LaunchQuote> QuoteAsync(Guid launchId, LaunchChoices choices, CancellationToken ct)
    {
        var launch = await db.AppLaunches.AsNoTracking().FirstOrDefaultAsync(l => l.Id == launchId, ct);
        if (launch is null) return new LaunchQuote([], 0, null, creationBilling.Currency, false, NotFound);

        var server = await SettleServerAsync(launch.WorkspaceId, choices, ct);
        return await QuoteOnAsync(launch.WorkspaceId, choices, server.ServerId, ct);
    }

    private async Task<LaunchQuote> QuoteOnAsync(Guid workspaceId, LaunchChoices choices, Guid? serverId, CancellationToken ct)
    {
        var names = await ServiceNamesAsync(choices, ct);

        // Priced through the host everything lands on, and through the function that charges it.
        var resources = new List<CreatedBillableResource>(choices.Services.Count + 1);
        for (var i = 0; i < choices.Services.Count; i++)
            resources.Add(new(BilledResourceType.Service, Guid.Empty, names[i], choices.InstanceSizeKey, serverId));
        resources.Add(new(BilledResourceType.App, Guid.Empty, choices.Name, choices.InstanceSizeKey, serverId));

        var quote = await creationBilling.QuoteAsync(workspaceId, resources, ct);

        var lines = quote.Lines.Select(line => line.Resource.Type == BilledResourceType.App
                ? new LaunchQuoteLine($"App {line.Resource.Name}", $"اپ {line.Resource.Name}", line.HourlyMinor)
                : new LaunchQuoteLine($"Database {line.Resource.Name}", $"دیتابیس {line.Resource.Name}", line.HourlyMinor))
            .ToList();

        return new LaunchQuote(lines, quote.FirstHourMinor, quote.BalanceMinor, quote.Currency, quote.Exempt, quote.Refusal);
    }

    /// <summary>
    /// The one host everything in this launch lands on, because an app and its database must share one.
    /// With none chosen the scheduler is asked once, for all of it together and inside the plan's pool,
    /// instead of placing the first database wherever suits it alone and then discovering that the app
    /// does not fit beside it, or that a database's placement knows nothing of a plan's pool. The
    /// answer is written into what is confirmed, so a retry stays on the same host.
    /// </summary>
    private async Task<SettledServer> SettleServerAsync(Guid workspaceId, LaunchChoices choices, CancellationToken ct)
    {
        if (choices.ServerId is { } chosen) return new SettledServer(chosen, null);

        var size = string.IsNullOrWhiteSpace(choices.InstanceSizeKey)
            ? null
            : await db.InstanceSizes.AsNoTracking().FirstOrDefaultAsync(s => s.Key == choices.InstanceSizeKey, ct);
        var count = choices.Services.Count + 1;
        var plan = await PlanForAsync(workspaceId, ct);
        var pool = string.IsNullOrWhiteSpace(plan?.NodePool) ? null : plan!.NodePool;

        var placed = await scheduler.PlaceAsync((size?.MemoryBytes ?? 0) * count, (size?.CpuCores ?? 0) * count, pool, ct);
        return placed.Ok
            ? new SettledServer(placed.ServerId, null)
            : new SettledServer(null, new ServiceRefusal(
                placed.Reason ?? "No server has room for everything this launch creates.",
                placed.ReasonFa ?? placed.Reason ?? "هیچ سروری برای همهٔ چیزهایی که این راه‌اندازی می‌سازد جا ندارد."));
    }

    private sealed record SettledServer(Guid? ServerId, ServiceRefusal? Refusal);

    /// <summary>
    /// Each database's name: the person's, else <c>{app}-{engine}</c>, suffixed until it is free. Across
    /// every workspace, because the container name it becomes is unique across the platform: the
    /// service's own check looks only in this workspace, and would leave the collision to the index.
    /// </summary>
    private async Task<IReadOnlyList<string>> ServiceNamesAsync(LaunchChoices choices, CancellationToken ct)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>(choices.Services.Count);

        foreach (var service in choices.Services)
        {
            var wanted = string.IsNullOrWhiteSpace(service.Name) ? DerivedServiceName(choices.Name, service.Type) : service.Name.Trim();
            var name = wanted;
            for (var n = 2; !taken.Add(DatabaseProvisioningService.Slugify(name)) || await ContainerNameTakenAsync(name, ct); n++)
                name = $"{wanted}-{n}";
            names.Add(name);
        }

        return names;
    }

    private Task<bool> ContainerNameTakenAsync(string name, CancellationToken ct)
    {
        var container = DatabaseProvisioningService.ContainerPrefix + DatabaseProvisioningService.Slugify(name);
        return db.ManagedServices.IgnoreQueryFilters().AsNoTracking().AnyAsync(s => s.ContainerName == container, ct);
    }

    private static string DerivedServiceName(string appName, ManagedServiceType type)
    {
        var stem = appName.Trim();
        if (stem.Length > MaxDerivedNameStem) stem = stem[..MaxDerivedNameStem];
        return $"{stem}-{type.ToString().ToLowerInvariant()}";
    }

    // ---- what it would refuse ------------------------------------------------------------------

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateConfirmAsync(Guid launchId, LaunchChoices choices, CancellationToken ct)
    {
        var launch = await db.AppLaunches.AsNoTracking().FirstOrDefaultAsync(l => l.Id == launchId, ct);
        if (launch is null) return [NotFound];
        if (launch.Status != LaunchStatus.AwaitingConfirmation)
            return AlreadyConfirmed(launch.Status) ? [] : [NoLongerConfirmable];

        return await CheckChoicesAsync(launch, choices, await SettleServerAsync(launch.WorkspaceId, choices, ct), ct);
    }

    private static readonly ServiceRefusal NoLongerConfirmable = new(
        "This launch can no longer be confirmed. Start again from the beginning.",
        "این راه‌اندازی دیگر قابل تأیید نیست. از اول شروع کنید.");

    /// <summary>A second submit of a screen that has already been accepted: not a refusal, it simply has nothing left to do.</summary>
    private static bool AlreadyConfirmed(LaunchStatus status) => status is LaunchStatus.Creating
        or LaunchStatus.WaitingForServices or LaunchStatus.Deploying or LaunchStatus.Verifying
        or LaunchStatus.Succeeded or LaunchStatus.SucceededWithProblems or LaunchStatus.Failed;

    private async Task<IReadOnlyList<ServiceRefusal>> CheckChoicesAsync(
        AppLaunch launch, LaunchChoices choices, SettledServer server, CancellationToken ct)
    {
        var refusals = new List<ServiceRefusal>();
        var inspection = LaunchJson.ReadInspection(launch.InspectionJson);

        foreach (var variable in choices.EnvVars.Where(v => !EnvConfigFileEditor.IsValidKey(v.Key ?? "")))
            refusals.Add(new ServiceRefusal(
                $"'{variable.Key}' cannot be a variable name: use letters, digits and underscores, and do not start with a digit.",
                $"«{variable.Key}» نمی‌تواند نام متغیر باشد: از حرف، عدد و زیرخط استفاده کنید و با عدد شروع نکنید.",
                nameof(LaunchChoices.EnvVars)));

        // No tier at all is an install that has defined none, where there is nothing to choose. On any
        // other install a blank here would create an unlimited workload nobody asked for.
        if (string.IsNullOrWhiteSpace(choices.InstanceSizeKey) && await db.InstanceSizes.AsNoTracking().AnyAsync(s => s.IsEnabled, ct))
            refusals.Add(new ServiceRefusal("Choose a size.", "یک اندازه انتخاب کنید.", nameof(LaunchChoices.InstanceSizeKey)));

        if (inspection.RootCandidates.Count > 0 && string.IsNullOrWhiteSpace(choices.RootDirectory))
            refusals.Add(new ServiceRefusal(
                $"This holds several apps. Choose which folder to launch: {string.Join(", ", inspection.RootCandidates)}.",
                $"این کد چند اپ دارد. انتخاب کنید کدام پوشه راه بیفتد: {string.Join("، ", inspection.RootCandidates)}.",
                nameof(LaunchChoices.RootDirectory)));

        // The app form's rule, said before anything exists: a Compose app reads its source from a
        // repository row, and an upload has none, so otherwise this arrives as a build that cannot find
        // anything to build.
        if (launch.SourceKind == LaunchSourceKind.Upload && inspection.Stack == DetectedStack.Compose)
            refusals.Add(new ServiceRefusal(
                "A project that runs only from a Docker Compose file cannot be launched from an upload yet. Push it to a Git repository and launch that, or add a Dockerfile.",
                "پروژه‌ای که فقط با فایل Docker Compose اجرا می‌شود فعلاً از راه آپلود راه نمی‌افتد. آن را در یک مخزن گیت بگذارید و از آنجا راه بیندازید، یا یک Dockerfile اضافه کنید."));

        if (server.Refusal is { } placement) refusals.Add(placement);
        var serverId = server.ServerId;

        var names = await ServiceNamesAsync(choices, ct);
        for (var i = 0; i < choices.Services.Count; i++)
            refusals.AddRange(await databases.ValidateCreateAsync(
                DatabaseRequest(launch, choices, i, names[i], serverId, null), ct));

        refusals.AddRange(await apps.ValidateCreateAsync(AppRequest(launch, choices, inspection, serverId, null, null), ct));

        if ((await QuoteOnAsync(launch.WorkspaceId, choices, serverId, ct)).Refusal is { } money)
            refusals.Add(money);

        // Each create asks about the plan on its own behalf, so one full plan reads as four refusals.
        return refusals.DistinctBy(r => r.Message).ToList();
    }

    private DatabaseCreateRequest DatabaseRequest(
        AppLaunch launch, LaunchChoices choices, int index, string name, Guid? serverId, string? clientIp) =>
        new(launch.WorkspaceId, currentUser.UserId ?? launch.UserId, name, choices.Services[index].Type,
            string.IsNullOrWhiteSpace(choices.Services[index].Version) ? null : choices.Services[index].Version,
            choices.EnvironmentId, choices.InstanceSizeKey, serverId, IpAddress: clientIp);

    private AppCreateRequest AppRequest(
        AppLaunch launch, LaunchChoices choices, SourceInspection inspection, Guid? serverId, string? token, string? clientIp) => new()
    {
        WorkspaceId = launch.WorkspaceId,
        UserId = currentUser.UserId ?? launch.UserId,
        ClientIp = clientIp,
        Name = choices.Name,
        Slug = choices.Slug,
        SourceType = launch.SourceKind == LaunchSourceKind.Upload ? AppSourceType.Upload
            : inspection.Stack == DetectedStack.Compose ? AppSourceType.DockerCompose
            : AppSourceType.GitRepository,
        CloneUrl = launch.SourceKind == LaunchSourceKind.Git ? launch.CloneUrl : null,
        // What the source was actually read from, so a repository on master does not get the form's main.
        GitRef = launch.SourceKind == LaunchSourceKind.Git ? launch.ResolvedBranch ?? launch.GitRef : null,
        GitToken = token,
        RootDirectory = choices.RootDirectory,
        ContainerPort = choices.ContainerPort,
        EnvironmentId = choices.EnvironmentId,
        Kind = choices.Kind,
        ReleaseCommand = choices.ReleaseCommand,
        StartCommand = choices.Command,
        ServerId = serverId,
        InstanceSizeKey = choices.InstanceSizeKey,
        // The tick deploys it, once its databases answer.
        DeployNow = false
    };
}
