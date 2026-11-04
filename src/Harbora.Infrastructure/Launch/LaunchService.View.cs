using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Verification;
using Harbora.Shared.Inspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harbora.Infrastructure.Launch;

/// <summary>A launch as a page shows it. A read through the workspace filter, and nothing secret on it.</summary>
public sealed partial class LaunchService
{
    public async Task<LaunchView?> GetAsync(Guid launchId, CancellationToken ct)
    {
        var launch = await db.AppLaunches.AsNoTracking().FirstOrDefaultAsync(l => l.Id == launchId, ct);
        return launch is null ? null : await BuildViewAsync(launch, ct);
    }

    /// <summary>For a caller who has just shown the launch is theirs.</summary>
    private async Task<LaunchView> ViewAsync(Guid launchId, CancellationToken ct) =>
        await BuildViewAsync(await db.AppLaunches.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == launchId, ct), ct);

    private async Task<LaunchView> BuildViewAsync(AppLaunch launch, CancellationToken ct)
    {
        var choices = LaunchJson.ReadChoices(launch.ChoicesJson);
        var serviceIds = LaunchJson.ReadIds(launch.ServiceIdsJson);

        var services = await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.WorkspaceId == launch.WorkspaceId && serviceIds.Contains(s.Id))
            .Select(s => new LaunchServiceState(s.Id, s.Name, s.Status, s.ErrorMessage))
            .ToListAsync(ct);

        var app = launch.AppId is { } appId
            ? await db.Apps.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.Id == appId && a.WorkspaceId == launch.WorkspaceId)
                .Select(a => new { a.Id, a.Name, a.Status })
                .FirstOrDefaultAsync(ct)
            : null;

        var deployment = launch.DeploymentId is { } deploymentId
            ? await db.Deployments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Id == deploymentId && d.WorkspaceId == launch.WorkspaceId)
                .Select(d => new LaunchDeploymentState(d.Status, d.ErrorMessage))
                .FirstOrDefaultAsync(ct)
            : null;

        // An address is only worth offering once something is there to answer it.
        var deployed = launch.Status is LaunchStatus.Verifying or LaunchStatus.Succeeded or LaunchStatus.SucceededWithProblems
            || launch.FailedStep == LaunchStepKeys.Verify;

        string? publicUrl = null;
        IReadOnlyList<AppCheck> checks = [];
        if (app is not null && deployed)
        {
            var domain = await db.Domains.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.AppId == app.Id && d.IsPrimary)
                .Select(d => new { d.Host, d.SslEnabled })
                .FirstOrDefaultAsync(ct);
            if (domain is not null) publicUrl = $"{(domain.SslEnabled ? "https" : "http")}://{domain.Host}";

            // Resolved here and not injected: nothing before a launch's checks has any use for one.
            if (serviceProvider.GetService<IAppVerifier>() is { } verifier)
                checks = (await verifier.GetLatestAsync(app.Id, ct))?.Checks ?? [];
        }

        var unset = app is not null && choices is not null
            ? await UnsetVariablesAsync(app.Id, choices, LaunchJson.ReadInspection(launch.InspectionJson), ct)
            : [];

        // From the rows and never from a message: this is what somebody is paying for while they read
        // the page, and a sentence written when the launch failed goes on saying so after a delete.
        var created = services
            .Select(s => new LaunchCreatedThing("database", s.Name, $"/databases/{s.Id}", s.Status == ServiceStatus.Running))
            .ToList();
        if (app is not null)
            created.Add(new LaunchCreatedThing("app", app.Name, $"/apps/details/{app.Id}", app.Status == AppStatus.Running));

        var now = clock.UtcNow;
        return new LaunchView(
            launch.Id,
            launch.Status,
            launch.SourceKind,
            SourceLabelOf(launch, choices),
            app?.Id,
            app?.Name ?? choices?.Name,
            publicUrl,
            launch.DeploymentId,
            launch.FailureMessage,
            launch.FailureMessageFa,
            StepsOf(launch, choices, services, app?.Id, deployment, unset, checks),
            checks,
            LaunchAdvance.CanRetry(launch, now),
            LaunchAdvance.IsTerminal(launch.Status),
            AppGone: launch.AppId is not null && app is null,
            Created: created);
    }

    /// <summary>
    /// What this launch was told about and the app does not have: an answer left blank, or one a retry
    /// could not recall. Said on the timeline, because an app with a missing key often starts anyway.
    /// An alias is not one of these, and nor is a name a ticked engine's attachment sets: those are
    /// answered. Whereas a name the example file expects from an attachment, with that engine unticked
    /// and no value given, is answered by nothing, and is said even though it was never asked.
    /// </summary>
    private async Task<IReadOnlyList<string>> UnsetVariablesAsync(
        Guid appId, LaunchChoices choices, SourceInspection inspection, CancellationToken ct)
    {
        var set = await db.EnvironmentVariables.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.AppId == appId).Select(v => v.Key).ToListAsync(ct);
        var aliased = choices.Services.SelectMany(s => s.Aliases ?? []).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        var ticked = choices.Services.Select(s => s.Type).ToHashSet();
        var attached = AttachEnvNames.All
            .Where(names => ticked.Contains(AttachEnvRoles.TypeFor(names.Key)))
            .SelectMany(names => names.Value)
            .ToHashSet(StringComparer.Ordinal);

        return choices.EnvVars.Select(v => v.Key)
            .Concat(inspection.EnvVars.Where(v => v.Disposition == EnvVarDisposition.ProvidedByAttach).Select(v => v.Name))
            .Where(key => !set.Contains(key) && !aliased.Contains(key) && !attached.Contains(key))
            .Distinct()
            .ToList();
    }

    private static string SourceLabelOf(AppLaunch launch, LaunchChoices? choices)
    {
        if (launch.SourceKind == LaunchSourceKind.Git) return launch.CloneUrl ?? "";

        // A consumed archive's path is gone with it; by then the app's name says the same thing.
        return launch.ArchivePath is { } path ? LaunchSource.LabelForArchive(path) : choices?.Name ?? "zip";
    }

    private static IReadOnlyList<LaunchStepView> StepsOf(
        AppLaunch launch, LaunchChoices? choices, IReadOnlyList<LaunchServiceState> services, Guid? appId,
        LaunchDeploymentState? deployment, IReadOnlyList<string> unset, IReadOnlyList<AppCheck> checks)
    {
        // Whatever a launch that ended early never reached. A failed one may still get there on a
        // retry, so those steps are only waiting.
        var neverReached = launch.Status is LaunchStatus.InspectFailed or LaunchStatus.Abandoned
            ? LaunchStepState.Skipped
            : LaunchStepState.Waiting;

        var wantedServices = choices?.Services.Count ?? services.Count;
        var failure = (launch.FailureMessage, launch.FailureMessageFa);

        LaunchStepView Failed(string key, string title, string titleFa, string? link = null) =>
            new(key, title, titleFa, LaunchStepState.Failed, failure.FailureMessage, failure.FailureMessageFa, link);

        const string sourceTitle = "Reading your code", sourceTitleFa = "خواندن کد";
        var source = launch.Status switch
        {
            LaunchStatus.Inspecting => new LaunchStepView(LaunchStepKeys.Source, sourceTitle, sourceTitleFa, LaunchStepState.Running),
            LaunchStatus.InspectFailed => Failed(LaunchStepKeys.Source, sourceTitle, sourceTitleFa),
            _ => new LaunchStepView(LaunchStepKeys.Source, sourceTitle, sourceTitleFa, LaunchStepState.Done)
        };

        const string servicesTitle = "Databases", servicesTitleFa = "دیتابیس‌ها";
        var failedService = services.FirstOrDefault(s => s.Status == ServiceStatus.Failed) ?? services.FirstOrDefault(s => s.Status != ServiceStatus.Running);
        var serviceLink = failedService is not null ? $"/databases/{failedService.Id}" : null;
        LaunchStepView servicesStep;
        if (launch.FailedStep == LaunchStepKeys.Services)
        {
            servicesStep = Failed(LaunchStepKeys.Services, servicesTitle, servicesTitleFa, serviceLink);
        }
        else if (launch.Status is LaunchStatus.Inspecting or LaunchStatus.InspectFailed or LaunchStatus.AwaitingConfirmation or LaunchStatus.Abandoned)
        {
            servicesStep = new(LaunchStepKeys.Services, servicesTitle, servicesTitleFa, neverReached);
        }
        else if (wantedServices == 0)
        {
            servicesStep = new(LaunchStepKeys.Services, servicesTitle, servicesTitleFa, LaunchStepState.Skipped,
                "This app needs none.", "این اپ دیتابیسی لازم ندارد.");
        }
        else if (launch.Status is LaunchStatus.Creating or LaunchStatus.WaitingForServices)
        {
            var waiting = string.Join(", ", services.Where(s => s.Status != ServiceStatus.Running).Select(s => s.Name));
            servicesStep = new(LaunchStepKeys.Services, servicesTitle, servicesTitleFa, LaunchStepState.Running,
                waiting.Length == 0 ? null : $"Starting {waiting}.", waiting.Length == 0 ? null : $"در حال راه‌اندازی {waiting}.", serviceLink);
        }
        else if (services.FirstOrDefault(s => s.Status == ServiceStatus.Failed) is { } broken)
        {
            // A launch that stopped at the app, with its database failing behind it, did not get its
            // database. How far the launch got is one thing and what the rows say is another.
            servicesStep = new(LaunchStepKeys.Services, servicesTitle, servicesTitleFa, LaunchStepState.Failed,
                $"The database {broken.Name} could not be started: {broken.ErrorMessage ?? "It gave no reason."}",
                $"دیتابیس «{broken.Name}» بالا نیامد: {broken.ErrorMessage ?? "دلیلی گزارش نکرد."}",
                serviceLink);
        }
        else if (services.Any(s => s.Status == ServiceStatus.Provisioning))
        {
            var starting = string.Join(", ", services.Where(s => s.Status == ServiceStatus.Provisioning).Select(s => s.Name));
            servicesStep = new(LaunchStepKeys.Services, servicesTitle, servicesTitleFa, LaunchStepState.Running,
                $"Starting {starting}.", $"در حال راه‌اندازی {starting}.", serviceLink);
        }
        else
        {
            servicesStep = new(LaunchStepKeys.Services, servicesTitle, servicesTitleFa, LaunchStepState.Done);
        }

        const string appTitle = "Creating the app", appTitleFa = "ساختن اپ";
        var appLink = appId is { } id ? $"/apps/details/{id}" : null;
        string? unsetDetail = null, unsetDetailFa = null;
        if (unset.Count > 0)
        {
            var named = string.Join(", ", unset);
            unsetDetail = $"Not set: {named}. Set them on the app's page if it needs them.";
            unsetDetailFa = $"تنظیم نشده: {named}. اگر اپ لازمشان دارد، در صفحهٔ اپ تنظیمشان کنید.";
        }

        LaunchStepView appStep;
        if (launch.FailedStep == LaunchStepKeys.App)
            appStep = Failed(LaunchStepKeys.App, appTitle, appTitleFa, appLink);
        else if (launch.Status == LaunchStatus.Creating)
            appStep = new(LaunchStepKeys.App, appTitle, appTitleFa,
                services.Count >= wantedServices ? LaunchStepState.Running : LaunchStepState.Waiting, LinkPath: appLink);
        else if (appId is not null)
            appStep = new(LaunchStepKeys.App, appTitle, appTitleFa, LaunchStepState.Done, unsetDetail, unsetDetailFa, appLink);
        else
            appStep = new(LaunchStepKeys.App, appTitle, appTitleFa, neverReached);

        const string deployTitle = "Deploying", deployTitleFa = "استقرار";
        var deployLink = launch.DeploymentId is { } deploymentId ? $"/deployments/details/{deploymentId}" : null;
        LaunchStepView deployStep;
        if (launch.FailedStep == LaunchStepKeys.Deploy)
        {
            deployStep = Failed(LaunchStepKeys.Deploy, deployTitle, deployTitleFa, deployLink);
        }
        else if (launch.Status == LaunchStatus.Deploying)
        {
            var pending = deployment?.Status == DeploymentStatus.PendingApproval;
            deployStep = new(LaunchStepKeys.Deploy, deployTitle, deployTitleFa, LaunchStepState.Running,
                pending ? "This environment is protected: it is waiting for somebody to approve the deployment." : null,
                pending ? "این محیط محافظت‌شده است: استقرار منتظر تأیید یک نفر است." : null,
                deployLink);
        }
        else if (launch.Status is LaunchStatus.Verifying or LaunchStatus.Succeeded or LaunchStatus.SucceededWithProblems
                 || launch.FailedStep == LaunchStepKeys.Verify)
        {
            deployStep = new(LaunchStepKeys.Deploy, deployTitle, deployTitleFa, LaunchStepState.Done, LinkPath: deployLink);
        }
        else
        {
            deployStep = new(LaunchStepKeys.Deploy, deployTitle, deployTitleFa, neverReached);
        }

        const string verifyTitle = "Checking that it works", verifyTitleFa = "بررسی سالم بودن";
        var verifyStep = launch.Status switch
        {
            _ when launch.FailedStep == LaunchStepKeys.Verify => Failed(LaunchStepKeys.Verify, verifyTitle, verifyTitleFa),
            LaunchStatus.Verifying => new LaunchStepView(LaunchStepKeys.Verify, verifyTitle, verifyTitleFa, LaunchStepState.Running),
            LaunchStatus.Succeeded => new LaunchStepView(LaunchStepKeys.Verify, verifyTitle, verifyTitleFa, LaunchStepState.Done),
            // It finished, and what it found is not good: never the green treatment. Neither sentence
            // says it is running, because this is also where a run that could not find out ends up.
            LaunchStatus.SucceededWithProblems => checks.Any(c => c.Status is AppCheckStatus.Failed or AppCheckStatus.Warning)
                ? new LaunchStepView(LaunchStepKeys.Verify, verifyTitle, verifyTitleFa, LaunchStepState.Failed,
                    "The app is deployed, but a check found a problem.", "اپ مستقر شد، ولی یکی از بررسی‌ها مشکلی پیدا کرد.")
                : new LaunchStepView(LaunchStepKeys.Verify, verifyTitle, verifyTitleFa, LaunchStepState.Failed,
                    "The app is deployed, but Harbora could not check that it is running.", "اپ مستقر شد، ولی هاربورا نتوانست بررسی کند که در حال اجراست."),
            _ => new LaunchStepView(LaunchStepKeys.Verify, verifyTitle, verifyTitleFa, neverReached)
        };

        return [source, servicesStep, appStep, deployStep, verifyStep];
    }
}
