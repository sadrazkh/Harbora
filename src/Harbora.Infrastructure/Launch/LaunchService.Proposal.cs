using Harbora.Application.Abstractions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Servers;
using Harbora.Domain.Services;
using Harbora.Domain.Settings;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Configuration;
using Harbora.Infrastructure.Services;
using Harbora.Shared;
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
        return launch is null ? null : await BuildProposalAsync(launch, LaunchJson.ReadStored(launch.InspectionJson).Current, ct);
    }

    public async Task<ServiceOutcome<LaunchProposal>> ChooseRootAsync(Guid launchId, string candidate, CancellationToken ct)
    {
        var launch = await db.AppLaunches.AsNoTracking().FirstOrDefaultAsync(l => l.Id == launchId, ct);
        if (launch is null) return ServiceOutcome<LaunchProposal>.Refused(NotFound);
        if (launch.Status != LaunchStatus.AwaitingConfirmation)
            return ServiceOutcome<LaunchProposal>.Refused(NoLongerConfirmable);

        var stored = LaunchJson.ReadStored(launch.InspectionJson);
        var wanted = AppRootDirectory.Normalise(candidate);
        if (!stored.Candidates.ContainsKey(wanted))
        {
            var folders = stored.Root.RootCandidates;
            return ServiceOutcome<LaunchProposal>.Refused(new ServiceRefusal(
                $"'{wanted}' is not one of the folders found here: {string.Join(", ", folders)}.",
                $"«{wanted}» جزو پوشه‌هایی که اینجا پیدا شد نیست: {string.Join("، ", folders)}.",
                nameof(LaunchChoices.RootDirectory)));
        }

        var chosen = stored with { Chosen = wanted };
        var written = LaunchJson.Write(chosen);

        // Asked again on the row: a choice arriving just behind a confirm must not rewrite what it read.
        var recorded = false;
        await UpdateAsync(launchId, l =>
        {
            if (l.Status != LaunchStatus.AwaitingConfirmation) return;
            l.InspectionJson = written;
            recorded = true;
        }, ct);
        if (!recorded) return ServiceOutcome<LaunchProposal>.Refused(NoLongerConfirmable);

        return ServiceOutcome<LaunchProposal>.Success(await BuildProposalAsync(launch, chosen.Current, ct));
    }

    private async Task<LaunchProposal> BuildProposalAsync(AppLaunch launch, SourceInspection inspection, CancellationToken ct)
    {
        var name = launch.SourceKind == LaunchSourceKind.Git
            ? LaunchSource.NameFromCloneUrl(launch.CloneUrl ?? "")
            : LaunchSource.NameFromArchive(launch.ArchivePath ?? "");

        var place = await PlacementAsync(launch.WorkspaceId, ct);

        // Only what is certain is ticked: creating a database costs money, and a guess is not consent.
        var ticked = inspection.Services.Where(s => s.Certain)
            .Select(s => AttachEnvRoles.TypeFor(s.Kind))
            .Distinct()
            .ToList();
        var services = ticked
            .Select(type => new LaunchServiceChoice(type, Aliases: AliasesFor(inspection, type, ticked)))
            .ToList();
        var aliased = services.SelectMany(s => s.Aliases ?? []).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        // Every name, including the ones an attachment sets: somebody who unticks Postgres because their
        // database is elsewhere has to have somewhere to put its address. The screen sorts by disposition.
        var fields = inspection.EnvVars.Concat(UnnamedConnections(inspection))
            .Select(v => new LaunchEnvField(
                v.Name, v.Disposition,
                v.Disposition == EnvVarDisposition.KeepExample ? v.ExampleValue : null,
                IsSecretByDefault(v), v.SourceFile,
                v.Disposition is EnvVarDisposition.ProvidedByAttach or EnvVarDisposition.AliasOfAttach && v.Service is { } kind
                    ? AttachEnvRoles.TypeFor(kind)
                    : null))
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
            // An alias of a database that is ticked has nothing to ask: the attachment answers it. An
            // attach name is a question only once its engine is unticked, and then only with a value in
            // it, which is the person's to give: a blank one here would be named as "not set".
            EnvVars = fields.Where(f => f.Disposition != EnvVarDisposition.ProvidedByAttach && !aliased.Contains(f.Key))
                .Select(f => new LaunchEnvChoice(f.Key, f.SuggestedValue, f.IsSecret))
                .ToList()
        };

        // The operator's catalogue, not the shipped one, whose versions they can change. Every shipped
        // engine stays in it: nothing an operator can do today takes one out.
        var catalog = await ServiceCatalogReader.EffectiveAsync(db, engine, ct);
        var options = catalog
            .Select(entry => new LaunchServiceOption(
                entry.Type, entry.DisplayName, entry.DisplayNameFa,
                inspection.Services.FirstOrDefault(s => AttachEnvRoles.TypeFor(s.Kind) == entry.Type)))
            .ToList();

        // Across every workspace, and asked now rather than worked out on the page, or launching one
        // repository twice previews the address of the app that is already there.
        var slug = await AppProvisioningService.UniqueSlugAsync(db, AppProvisioningService.Slugify(name), ct);

        return new LaunchProposal(launch.Id, inspection, defaults, name, fields, options, slug);
    }

    /// <summary>
    /// A variable only a person can answer is usually a third party's credential, so the safer wrong
    /// guess is to mask it; a name that plainly is one is masked and anything else is left readable.
    /// A generated one is always secret, and the example file's own value never makes anything secret.
    ///
    /// <para>
    /// A connection name goes the other way round, secret unless it plainly holds a harmless part: what
    /// somebody types for <c>DATABASE_URL</c> or <c>MONGODB_URI</c> has their database's password in it,
    /// and neither name says so. What this answers is what <see cref="LaunchEnvField.IsSecret"/> carries.
    /// </para>
    /// </summary>
    private static bool IsSecretByDefault(DetectedEnvVar variable) => variable.Disposition switch
    {
        EnvVarDisposition.GeneratedSecret => true,
        EnvVarDisposition.ProvidedByAttach or EnvVarDisposition.AliasOfAttach => !IsPlainConnectionPart(variable),
        EnvVarDisposition.NeedsAnswer => variable.Role == EnvAliasRole.Url
            || SecretNameParts.Any(part => variable.Name.Contains(part, StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    private static readonly string[] SecretNameParts = ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASS", "CREDENTIAL"];

    /// <summary>How an attach name, which carries no role of its own, says it holds a host, a port, a user or a database's name.</summary>
    private static readonly string[] PlainPartEndings = ["HOST", "PORT", "USER", "USERNAME", "DATABASE", "DB", "NAME"];

    private static bool IsPlainConnectionPart(DetectedEnvVar variable) => variable.Role is { } role
        ? role is EnvAliasRole.Host or EnvAliasRole.Port or EnvAliasRole.User or EnvAliasRole.Database
        : PlainPartEndings.Any(ending => variable.Name.EndsWith(ending, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A project with <c>pg</c> in its dependencies and no example file names no variable at all, so
    /// somebody who unticks Postgres would have nowhere to give their own database's address until the app's
    /// page. Each detected service with no name of its own gets the one name its attachment would set for
    /// the whole address, from the same table that alias resolution reads.
    /// </summary>
    private static IEnumerable<DetectedEnvVar> UnnamedConnections(SourceInspection inspection)
    {
        var given = inspection.EnvVars.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var service in inspection.Services.Where(s => inspection.EnvVars.All(v => v.Service != s.Kind)))
            if (AttachEnvRoles.SourceKeyFor(AttachEnvRoles.TypeFor(service.Kind), EnvAliasRole.Url) is { } address && given.Add(address))
                yield return new DetectedEnvVar(address, EnvVarDisposition.ProvidedByAttach, null, service.Kind, null,
                    service.Evidence.FirstOrDefault()?.File ?? "");
    }

    /// <summary>
    /// Every spelling in the example env file of something this engine's attachment provides, turned
    /// into the attach variable it reads from. A role this engine has no value for stays an ordinary
    /// question on the screen rather than an alias of nothing.
    ///
    /// <para>
    /// A name like <c>DB_HOST</c> belongs to whichever database the app has, so when exactly one is
    /// ticked it is that one's, whatever the inspector could or could not guess: a plain PDO app has no
    /// library to detect, and the note on its screen tells the person to tick one. With two ticked it
    /// stays a question, because nothing says which.
    /// </para>
    /// </summary>
    private static IReadOnlyList<AttachEnvAlias> AliasesFor(
        SourceInspection inspection, ManagedServiceType type, IReadOnlyCollection<ManagedServiceType> ticked)
    {
        var theOneDatabase = ticked.Where(IsDatabase).Distinct().Count() == 1 && IsDatabase(type);

        return inspection.EnvVars
            .Where(v => v.Role is not null && (
                (v.Disposition == EnvVarDisposition.AliasOfAttach
                 && v.Service is { } service && AttachEnvRoles.TypeFor(service) == type)
                || (theOneDatabase && AttachEnvNames.IsGenericDatabaseName(v.Name))))
            .Select(v => AttachEnvRoles.SourceKeyFor(type, v.Role!.Value) is { } source ? new AttachEnvAlias(v.Name, source) : null)
            .OfType<AttachEnvAlias>()
            .ToList();
    }

    /// <summary>The engines a generic <c>DB_</c> name can mean; a cache or a broker beside one does not make it unclear.</summary>
    private static bool IsDatabase(ManagedServiceType type) => type is ManagedServiceType.PostgreSql
        or ManagedServiceType.MySql or ManagedServiceType.MariaDb or ManagedServiceType.MongoDb;

    /// <summary>
    /// What a service is really attached with. One ticked by hand arrives with none, and gets what the
    /// example file says it is read under. One from the screen's defaults keeps its own, and those were
    /// worked out with everything certain ticked: unticking one of two databases leaves the other as the
    /// only one a <c>DB_HOST</c> can mean. What a person types for such a name is the app's own variable,
    /// which outranks anything an attachment sets, so this never overrides an answer.
    /// </summary>
    private static IReadOnlyList<AttachEnvAlias> SettledAliases(
        LaunchServiceChoice service, SourceInspection inspection, IReadOnlyCollection<ManagedServiceType> ticked)
    {
        var worked = AliasesFor(inspection, service.Type, ticked);
        return service.Aliases is not { } given
            ? worked
            : given.Concat(worked.Where(a => AttachEnvNames.IsGenericDatabaseName(a.Name))).DistinctBy(a => a.Name).ToList();
    }

    /// <summary>
    /// The size the chooser would open on. <c>SizePickerService</c> is what really answers this for a
    /// form, but it lives in the web project and this does not: the rules below are its own, asked in
    /// its own order, and <c>LaunchSizeDefaultParityTests</c> holds the two together. Never "no limit":
    /// that is the one thing a create must not pick for somebody.
    ///
    /// <para>
    /// The platform's default goes first and is held to the same rules, on any host. Taken unchecked it
    /// could be a tier nobody priced, and the screen would then open on a refusal it had chosen by itself.
    /// </para>
    /// </summary>
    private async Task<string?> DefaultSizeKeyAsync(Guid workspaceId, CancellationToken ct)
    {
        var platformDefault = await db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Key == SettingKeys.DefaultInstanceSize)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

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

        // Nothing charges the provider's own workspace, so a price is no gate there: see the chooser.
        var priceIsAGate = billing.Value.Enabled && !await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == workspaceId).Select(w => w.IsDefault).FirstOrDefaultAsync(ct);

        bool Selectable(Server server, InstanceSize size)
        {
            var reading = capacities.FirstOrDefault(c => c.ServerId == server.Id);
            if (reading is { IsOnline: false }) return false;

            var offer = offers.FirstOrDefault(o => o.ServerId == server.Id
                && string.Equals(o.InstanceSizeKey, size.Key, StringComparison.OrdinalIgnoreCase));

            if (allowedKeys.Count > 0 && !allowedKeys.Contains(size.Key)) return false;
            if (!ServerRates.OffersNewWork(offer)) return false;
            if (reading is not null && !reading.CanFit(size.MemoryBytes, size.CpuCores)) return false;
            return !priceIsAGate || ServerRates.ForWorkload(size, offer, BilledRunState.Running) is not null;
        }

        var preferred = sizes.FirstOrDefault(s => string.Equals(s.Key, platformDefault, StringComparison.OrdinalIgnoreCase));
        if (preferred is not null && servers.Any(server => Selectable(server, preferred))) return preferred.Key;

        foreach (var server in servers)
            foreach (var size in sizes)
                if (Selectable(server, size))
                    return size.Key;

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

        return new LaunchQuote(lines, quote.FirstHourMinor, quote.BalanceMinor, quote.Currency, quote.Exempt, quote.Refusal, quote.MoreCreditWouldHelp);
    }

    /// <summary>
    /// The one host everything in this launch lands on, because an app and its database must share one.
    /// With none chosen the scheduler is asked once, for all of it together and inside the plan's pool,
    /// instead of placing the first database wherever suits it alone and then discovering that the app
    /// does not fit beside it, or that a database's placement knows nothing of a plan's pool. The
    /// answer is written into what is confirmed, so a retry stays on the same host.
    ///
    /// <para>
    /// A chosen host is asked the same question, and the form always posts one. Each create checks only
    /// itself, so on a small host every one of them would pass here, the first database would be created
    /// and charged, and the next refused: a partial launch that trying again cannot help. The choice stays
    /// on a refusal, so that what the screen goes on to quote is still about the host it shows. An id that
    /// is no server is read the way the create services read it, as no choice at all.
    /// </para>
    /// </summary>
    private async Task<SettledServer> SettleServerAsync(Guid workspaceId, LaunchChoices choices, CancellationToken ct)
    {
        var (memory, cpu) = await AllOfItAsync(choices, ct);

        if (choices.ServerId is { } chosen && await db.Servers.AsNoTracking().AnyAsync(s => s.Id == chosen, ct))
        {
            var fits = await scheduler.CheckAsync(chosen, memory, cpu, ct);
            return new SettledServer(chosen, fits.Ok ? null : NoRoom(fits));
        }

        var plan = await PlanForAsync(workspaceId, ct);
        var pool = string.IsNullOrWhiteSpace(plan?.NodePool) ? null : plan!.NodePool;

        var placed = await scheduler.PlaceAsync(memory, cpu, pool, ct);
        return placed.Ok
            ? new SettledServer(placed.ServerId, null)
            : new SettledServer(null, NoRoom(placed));
    }

    private static ServiceRefusal NoRoom(PlacementResult placement) => new(
        placement.Reason ?? "No server has room for everything this launch creates.",
        placement.ReasonFa ?? placement.Reason ?? "هیچ سروری برای همهٔ چیزهایی که این راه‌اندازی می‌سازد جا ندارد.");

    /// <summary>What every database and the app ask for together, all at the one size a launch gives them.</summary>
    private async Task<(long Memory, double Cpu)> AllOfItAsync(LaunchChoices choices, CancellationToken ct)
    {
        var size = string.IsNullOrWhiteSpace(choices.InstanceSizeKey)
            ? null
            : await db.InstanceSizes.AsNoTracking().FirstOrDefaultAsync(s => s.Key == choices.InstanceSizeKey, ct);
        var count = choices.Services.Count + 1;
        return ((size?.MemoryBytes ?? 0) * count, (size?.CpuCores ?? 0) * count);
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
            var wanted = WantedServiceName(choices, service);
            var name = wanted;
            for (var n = 2; !taken.Add(DatabaseProvisioningService.Slugify(name)) || await ContainerNameTakenAsync(name, ct); n++)
                name = $"{wanted}-{n}";
            names.Add(name);
        }

        return names;
    }

    private static string WantedServiceName(LaunchChoices choices, LaunchServiceChoice service) =>
        string.IsNullOrWhiteSpace(service.Name) ? DerivedServiceName(choices.Name, service.Type) : service.Name.Trim();

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
        "این راه‌اندازی دیگر قابل تأیید نیست. از اول شروع کن.");

    /// <summary>A second submit of a screen that has already been accepted: not a refusal, it simply has nothing left to do.</summary>
    private static bool AlreadyConfirmed(LaunchStatus status) => status is LaunchStatus.Creating
        or LaunchStatus.WaitingForServices or LaunchStatus.Deploying or LaunchStatus.Verifying
        or LaunchStatus.Succeeded or LaunchStatus.SucceededWithProblems or LaunchStatus.Failed;

    private async Task<IReadOnlyList<ServiceRefusal>> CheckChoicesAsync(
        AppLaunch launch, LaunchChoices choices, SettledServer server, CancellationToken ct)
    {
        var refusals = new List<ServiceRefusal>();
        var stored = LaunchJson.ReadStored(launch.InspectionJson);
        var inspection = stored.Current;

        foreach (var variable in choices.EnvVars.Where(v => !EnvConfigFileEditor.IsValidKey(v.Key ?? "")))
            refusals.Add(new ServiceRefusal(
                $"'{variable.Key}' cannot be a variable name: use letters, digits and underscores, and do not start with a digit.",
                $"«{variable.Key}» نمی‌تواند نام متغیر باشد: از حرف، عدد و زیرخط استفاده کن و با عدد شروع نکن.",
                nameof(LaunchChoices.EnvVars)));

        // No tier at all is an install that has defined none, where there is nothing to choose. On any
        // other install a blank here would create an unlimited workload nobody asked for.
        if (string.IsNullOrWhiteSpace(choices.InstanceSizeKey) && await db.InstanceSizes.AsNoTracking().AnyAsync(s => s.IsEnabled, ct))
            refusals.Add(new ServiceRefusal("Choose a size.", "یک اندازه انتخاب کن.", nameof(LaunchChoices.InstanceSizeKey)));

        // A folder named only in this form was never chosen, and what is about to be created was worked
        // out from a different one: the wrong database, the wrong variables and the wrong port, with
        // nothing to say so. Any other folder has to come through ChooseRootAsync, which reads its answers.
        if (stored.Root.RootCandidates.Count > 0)
        {
            var folders = stored.Root.RootCandidates;
            if (stored.Chosen is null)
                refusals.Add(new ServiceRefusal(
                    $"This holds several apps. Choose which folder to launch: {string.Join(", ", folders)}.",
                    $"این کد چند اپ دارد. انتخاب کن کدام پوشه راه بیفتد: {string.Join("، ", folders)}.",
                    nameof(LaunchChoices.RootDirectory)));
            else if (!string.IsNullOrWhiteSpace(choices.RootDirectory)
                     && AppRootDirectory.Normalise(choices.RootDirectory) != AppRootDirectory.Normalise(inspection.RootDirectory))
                refusals.Add(new ServiceRefusal(
                    $"This was worked out for the folder {stored.Chosen}. To launch another one, choose it from the list: {string.Join(", ", folders)}.",
                    $"این پیشنهاد برای پوشهٔ {stored.Chosen} آماده شده است. برای راه انداختن پوشهٔ دیگر، آن را از فهرست انتخاب کن: {string.Join("، ", folders)}.",
                    nameof(LaunchChoices.RootDirectory)));
        }

        // The app form's rule, said before anything exists: a Compose app reads its source from a
        // repository row, and an upload has none, so otherwise this arrives as a build that cannot find
        // anything to build.
        if (launch.SourceKind == LaunchSourceKind.Upload && inspection.Stack == DetectedStack.Compose)
            refusals.Add(new ServiceRefusal(
                "A project that runs only from a Docker Compose file cannot be launched from an upload yet. Push it to a Git repository and launch that, or add a Dockerfile.",
                "پروژه‌ای که فقط با فایل Docker Compose اجرا می‌شود فعلاً از راه آپلود راه نمی‌افتد. آن را در یک مخزن گیت بگذار و از آنجا راه بینداز، یا یک Dockerfile اضافه کن."));

        if (server.Refusal is { } placement) refusals.Add(placement);
        var serverId = server.ServerId;

        var names = await ServiceNamesAsync(choices, ct);
        for (var i = 0; i < choices.Services.Count; i++)
            refusals.AddRange(await databases.ValidateCreateAsync(
                DatabaseRequest(launch, choices, i, names[i], serverId, null), ct));

        refusals.AddRange(await apps.ValidateCreateAsync(AppRequest(launch, choices, inspection, serverId, null, null), ct));

        // Each of those asks the plan about itself alone, and on a plan with one slot left every one of
        // them is true. Asked as a batch, so a launch that cannot all fit stops here rather than at its
        // second create, with the first already charged. Only when nothing above has spoken: a plan that
        // is already full would otherwise say so twice in two different sentences.
        if (refusals.Count == 0)
        {
            var (memory, cpu) = await AllOfItAsync(choices, ct);
            var batch = await quota.CanAddWorkloadsAsync(launch.WorkspaceId,
                new WorkloadQuotaDelta(Apps: 1, Services: choices.Services.Count, MemoryBytes: memory, CpuCores: cpu), ct);
            if (!batch.Allowed)
                refusals.Add(AppRefusals.Quota(batch, "Plan quota exceeded.", "سقف پلن پر شده است."));
        }

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
