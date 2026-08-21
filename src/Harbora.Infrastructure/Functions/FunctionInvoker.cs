using System.Diagnostics;
using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Domain.Jobs;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// The platform's own door into a running function app.
///
/// <para>
/// Two halves, deliberately: <see cref="QueueAsync"/> writes down what should happen and hands the
/// job to the durable queue; <see cref="ExecuteAsync"/> makes the request. A scheduled call that was
/// due at the moment the panel restarted is then still made, which is the difference between a cron
/// feature and a cron feature people trust.
/// </para>
///
/// <para>
/// F4 (2026-08-21 functions-and-services plan, "Function failures become visible") wants this class to
/// publish <see cref="EventKind.FunctionFailed"/> the same direct way <c>DeploymentPipeline</c> and
/// <c>BackupEngine</c> publish their own events — but unlike those two, this class already sits at the
/// bottom of <see cref="IEventPublisher"/>'s own dependency chain:
/// <c>EventDispatcher</c> (the <see cref="IEventPublisher"/>) needs <c>INotificationService</c> for its
/// Telegram delivery path, <c>NotificationService</c> needs <c>IFunctionEventBus</c> to tell functions
/// about the same platform happenings people hear about, and <c>FunctionEventBus</c> needs
/// <see cref="IFunctionInvoker"/> — this class — to actually run an event-triggered function. A direct
/// constructor dependency on <see cref="IEventPublisher"/> here would close that loop into a genuine DI
/// cycle (caught the hard way: any page that resolves <c>WorkspaceAccountService</c> 500'd). Taking
/// <see cref="IServiceScopeFactory"/> instead and resolving <see cref="IEventPublisher"/> from a fresh
/// scope inside <see cref="CompleteAsync"/> defers that resolution past constructor-time graph
/// validation — the same isolated-scope idiom <c>NotificationService.EnqueueDeliveryAsync</c> and
/// <c>EventDispatcher.PublishAsync</c> themselves already use, for the same reason: this must persist
/// regardless of what the caller's own unit of work does next.
/// </para>
/// </summary>
public sealed class FunctionInvoker(
    HarboraDbContext db,
    IHttpClientFactory httpFactory,
    ISecretProtector protector,
    IJobQueue jobs,
    IFeatureGate features,
    IServiceScopeFactory scopeFactory,
    ILogger<FunctionInvoker> logger) : IFunctionInvoker
{
    /// <summary>
    /// How long the platform waits for a function it called itself. Long enough for real work, short
    /// enough that a wedged handler cannot pile up invocations faster than they drain.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<Guid?> QueueAsync(
        Guid functionId, FunctionTrigger trigger, FunctionEvent? evt, CancellationToken ct)
    {
        var invocation = await CreateInvocationAsync(functionId, trigger, evt, body: null, ct);
        if (invocation is null) return null;

        // Exclusive on the function rather than on the invocation: two calls of one function must not
        // overlap, or a handler that takes longer than its own schedule quietly runs twice at once.
        await jobs.EnqueueExclusiveAsync(
            JobKind.FunctionInvoke, invocation.Id, invocation.FunctionId, invocation.WorkspaceId, ct);
        return invocation.Id;
    }

    public async Task<FunctionInvocation?> InvokeNowAsync(
        Guid functionId, FunctionTrigger trigger, FunctionEvent? evt, string? body, CancellationToken ct)
    {
        var invocation = await CreateInvocationAsync(functionId, trigger, evt, body, ct);
        if (invocation is null) return null;

        // Deliberately not the durable job queue: F2's queue consumer needs the verdict synchronously
        // to decide ack/nack/dead-letter, on its own schedule rather than the job worker's — see this
        // method's own doc on IFunctionInvoker. Still the exact same execution this invocation would
        // have gotten through the queue: same address resolution, same secret, same 60s timeout, same
        // failure-event publish.
        await ExecuteAsync(invocation.Id, ct);

        return await db.FunctionInvocations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == invocation.Id, ct);
    }

    /// <summary>
    /// The guard checks and row-creation both <see cref="QueueAsync"/> and <see cref="InvokeNowAsync"/>
    /// need before they diverge on how the call actually gets made.
    /// </summary>
    private async Task<FunctionInvocation?> CreateInvocationAsync(
        Guid functionId, FunctionTrigger trigger, FunctionEvent? evt, string? body, CancellationToken ct)
    {
        var fn = await db.FunctionDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == functionId, ct);
        if (fn is null || !fn.IsEnabled) return null;

        var app = await db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == fn.AppId, ct);
        if (app is null) return null;

        // An entitlement that was revoked must stop the code that is already deployed, not only the
        // page that creates more of it. Anything else means a cancelled customer's schedules keep
        // running until somebody notices.
        var verdict = await features.EvaluateAsync(app.WorkspaceId, PlatformFeatures.Functions, ct);
        if (!verdict.IsEnabled)
        {
            logger.LogInformation(
                "Skipping {Trigger} invocation of function {Slug}: workspace {Workspace} is not entitled to functions.",
                trigger, fn.Slug, app.WorkspaceId);
            return null;
        }

        if (app.ActiveDeploymentId is null)
        {
            logger.LogInformation(
                "Skipping {Trigger} invocation of function {Slug}: {App} has never been published.",
                trigger, fn.Slug, app.Slug);
            return null;
        }

        var invocation = new FunctionInvocation
        {
            FunctionId = fn.Id,
            AppId = app.Id,
            WorkspaceId = app.WorkspaceId,
            Trigger = trigger,
            EnvelopeJson = FunctionProject.InvokeEnvelope(TriggerWord(trigger), evt, body)
        };
        db.FunctionInvocations.Add(invocation);
        await db.SaveChangesAsync(ct);
        return invocation;
    }

    public async Task ExecuteAsync(Guid invocationId, CancellationToken ct)
    {
        var invocation = await db.FunctionInvocations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == invocationId, ct);
        if (invocation is null || invocation.CompletedAt is not null) return;

        var fn = await db.FunctionDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == invocation.FunctionId, ct);
        var app = await db.Apps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == invocation.AppId, ct);

        if (fn is null || app is null)
        {
            await CompleteAsync(invocation, fn, app, null, false, "The function no longer exists.", 0, ct);
            return;
        }

        var address = await ResolveAddressAsync(app, ct);
        if (address is null)
        {
            await CompleteAsync(invocation, fn, app, null, false,
                "The function app is not reachable from the panel — it may be stopped.", 0, ct);
            return;
        }

        var secret = SafeUnprotect(app.FunctionInvokeSecret);
        if (string.IsNullOrEmpty(secret))
        {
            // The host refuses an unsigned call, so an app with no secret would fail with a 401 that
            // reads like a bug. Say the real thing: it has not been published since the secret existed.
            await CompleteAsync(invocation, fn, app, null, false,
                "This app has no invoke secret. Publish it again to issue one.", 0, ct);
            return;
        }

        var url = $"{address}{FunctionProject.InvokePathPrefix}{fn.Slug}";
        var started = Stopwatch.GetTimestamp();
        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = Timeout;

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(invocation.EnvelopeJson ?? "{}", Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation(FunctionProject.SecretHeader, secret);

            using var response = await client.SendAsync(request, ct);
            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var ok = (int)response.StatusCode < 400;

            await CompleteAsync(invocation, fn, app, (int)response.StatusCode, ok,
                ok ? null : $"The function answered {(int)response.StatusCode}.", elapsed, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // The timeout and a refused connection are both "no answer", and telling them apart is
            // the difference between "your function is slow" and "your app is down".
            var reason = ex is TaskCanceledException && !ct.IsCancellationRequested
                ? $"No answer within {Timeout.TotalSeconds:0}s."
                : "Could not reach the function app.";
            await CompleteAsync(invocation, fn, app, null, false, reason, elapsed, ct);
        }
    }

    /// <summary>
    /// Where the panel can reach this app's host, or null when nothing can.
    ///
    /// <para>
    /// The same two answers the deployment pipeline's health probe uses, and for the same reason: on
    /// the local engine an app answers to its slug on the shared network, and on a remote node it
    /// answers on the port that node published. Anything else here would be a third opinion about an
    /// address, which is how a feature ends up working in one topology and silently not in the other.
    /// </para>
    /// </summary>
    private async Task<string?> ResolveAddressAsync(Domain.Apps.App app, CancellationToken ct)
    {
        var port = app.ContainerPort <= 0 ? FunctionProject.DefaultPort : app.ContainerPort;
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == app.ServerId, ct);

        if (server is null || server.IsLocal)
            return app.PrivateAddressState == PrivateAddressOutcome.Registered
                ? $"http://{app.Slug}:{port}"
                // No alias means the container answers to no stable name, and the name it does answer
                // to changes every deployment. Refusing is better than guessing at one.
                : null;

        return app.PublishedHostPort is { } published
            ? $"http://{server.Hostname}:{published}"
            : null;
    }

    private async Task CompleteAsync(
        FunctionInvocation invocation, FunctionDefinition? fn, Domain.Apps.App? app,
        int? status, bool ok, string? error, int elapsedMs, CancellationToken ct)
    {
        invocation.StatusCode = status;
        invocation.Succeeded = ok;
        invocation.Error = error is { Length: > 900 } ? error[..900] : error;
        invocation.DurationMs = elapsedMs;
        invocation.CompletedAt = DateTimeOffset.UtcNow;
        invocation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // F4 (2026-08-21 functions-and-services plan, "Function failures become visible"): the same
        // "enqueue only, right where the row is already marked failed" shape DeploymentPipeline,
        // BackupEngine, MetricsCollector and ManagedServiceEngine already use for their own failure
        // events. A scheduled function that fails at 3am and tells nobody is the same defect class as
        // a check that reports success for work it never did — this is the seam that stops it being
        // silent. Never called out inline: IEventPublisher.PublishAsync only ever writes durable rows
        // and enqueues a job, and never throws on its own, so this can never turn an invocation that
        // already finished (successfully or not) into something that fails a second time here.
        //
        // Resolved from a fresh scope rather than taken as a constructor dependency — see the class
        // doc for the DI cycle that closes if IEventPublisher is injected here directly.
        if (!ok)
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(
                invocation.WorkspaceId, EventKind.FunctionFailed,
                new Dictionary<string, string>
                {
                    ["function"] = fn?.Name ?? "", ["app"] = app?.Name ?? "", ["error"] = invocation.Error ?? ""
                }, ct);
        }
    }

    private string? SafeUnprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return protector.Unprotect(ciphertext); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A function app's invoke secret could not be decrypted.");
            return null;
        }
    }

    internal static string TriggerWord(FunctionTrigger trigger) => trigger switch
    {
        FunctionTrigger.Http => "http",
        FunctionTrigger.Cron => "cron",
        FunctionTrigger.Queue => "queue",
        _ => "event"
    };
}

/// <summary>Runs one queued invocation. Registered as an <see cref="IJobHandler"/>.</summary>
public sealed class FunctionInvokeJobHandler(IFunctionInvoker invoker) : IJobHandler
{
    public JobKind Kind => JobKind.FunctionInvoke;

    public Task ExecuteAsync(Guid targetId, CancellationToken ct) => invoker.ExecuteAsync(targetId, ct);
}
