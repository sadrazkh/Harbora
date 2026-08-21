using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// The panel-side bridge decision 2 of the 2026-08-21 functions-and-services plan settled on: a
/// <see cref="FunctionTrigger.Queue"/> function is consumed here, on the panel, and every delivery
/// calls the function through <see cref="IFunctionInvoker.InvokeNowAsync"/> — the same signed door
/// every other trigger uses — rather than an amqp client baked into the three generated hosts.
///
/// <para>
/// <b>Restart survival</b> follows <see cref="Deployments.PanelNetworkRebinder"/>'s worked shape:
/// nothing here trusts in-memory state to have survived a restart. <see cref="ReconcileAsync"/> reads
/// which functions should be consumed right now straight from the database — unfiltered, since this
/// runs with no session and a filtered read would find nothing and silently consume nothing at all,
/// the tenant-filter trap this codebase keeps re-learning — and starts or stops workers to match. Run
/// once at boot (after a warm-up, mirroring <see cref="FunctionCronScheduler"/>) and then on a timer,
/// because unlike a container restart, a function's queue trigger can also change without one.
/// </para>
///
/// <para>
/// <b>A message never vanishes silently.</b> <see cref="HandleAsync"/> acks on success; on a first
/// failure (the broker's own <c>Redelivered</c> flag is false) it nack-requeues for exactly one more
/// try; on a second failure it parks a <see cref="FunctionQueueDeadLetter"/> row — visible on the
/// function's own page — and drops the message from the queue.
/// </para>
///
/// <para>
/// <b>A broker that is down is not silence.</b> A connection or declare failure is recorded on
/// <see cref="FunctionDefinition.QueueLastError"/>, the same field/shape
/// <c>EventSubscription.LastError</c> already uses, which <c>AttentionService</c> reads into the
/// dashboard's existing broken-channel path (extended, not forked, per the plan's own instruction).
/// </para>
///
/// <para>
/// <b>Tenancy, both directions.</b> <see cref="ReconcileAsync"/> only ever starts a worker for a
/// function this table itself says is Queue-triggered and enabled — it cannot skip a workspace's own
/// queue functions, because it does not filter by workspace at all (it is a platform-wide background
/// service; every workspace's enabled queue functions are, correctly, "its own" here). What it must
/// not do is reach a *broker* outside the function's own workspace: <see cref="ConsumeOnceAsync"/>
/// re-checks that the attached <c>ManagedService</c> both is RabbitMQ and belongs to the function's
/// own <c>WorkspaceId</c> before ever opening a connection — defensively, even though
/// <c>FunctionAppService.Validate</c> already refuses to save a cross-workspace attachment, because a
/// background reader trusting that every row was written through that one door is exactly how this
/// class of bug has recurred before.
/// </para>
///
/// <para>
/// <b>Honest about throughput.</b> One worker per function, one message in flight per worker
/// (prefetch 1) — this is one panel-side consumer, not a scaled pool, and the editor says so.
/// </para>
/// </summary>
public sealed class QueueFunctionConsumerHost(
    IServiceScopeFactory scopeFactory,
    IQueueBrokerConnectionFactory brokerFactory,
    ISecretProtector protector,
    ISystemClock clock,
    ILogger<QueueFunctionConsumerHost> logger) : BackgroundService
{
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(15);

    private readonly Dictionary<Guid, (CancellationTokenSource Cts, Task Task)> _workers = new();

    /// <summary>Which functions currently have a live (or reconnecting) worker. Test-only window into
    /// otherwise-private state — the same reason <c>FakeDockerEngine.Calls</c> exists.</summary>
    internal IReadOnlyCollection<Guid> RunningFunctionIds => _workers.Keys.ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(WarmUp, stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(ReconcileInterval);
        do
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Queue-function reconciliation failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (cts, task) in _workers.Values.ToList())
        {
            cts.Cancel();
            try { await task; } catch { /* shutting down; nothing left to report to */ }
        }
        _workers.Clear();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// One pass: starts a worker for every enabled queue-triggered function this table names that does
    /// not already have one, and stops every worker whose function is no longer wanted — disabled,
    /// deleted, its trigger changed, or its queue/broker cleared. Public because it is this service's
    /// own behaviour, the same reason <c>FunctionCronScheduler.TickAsync</c> is.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var wantedIds = (await db.FunctionDefinitions.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Trigger == FunctionTrigger.Queue && f.IsEnabled
                        && f.QueueServiceId != null && f.QueueName != null && f.QueueName != "")
            .Select(f => f.Id)
            .ToListAsync(stoppingToken))
            .ToHashSet();

        foreach (var goneId in _workers.Keys.Where(id => !wantedIds.Contains(id)).ToList())
        {
            var (cts, task) = _workers[goneId];
            cts.Cancel();
            try { await task; } catch { /* the worker loop itself never throws past its own catch */ }
            _workers.Remove(goneId);
        }

        foreach (var functionId in wantedIds)
        {
            if (_workers.ContainsKey(functionId)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _workers[functionId] = (cts, Task.Run(() => RunWorkerAsync(functionId, cts.Token), cts.Token));
        }
    }

    /// <summary>
    /// Owns one function's connection for as long as it is wanted: connect, consume until the
    /// connection is lost or cancelled, record why, wait, try again. Never tested directly against a
    /// real timer — <see cref="ConsumeOnceAsync"/> and <see cref="HandleAsync"/> carry the behaviour
    /// this class's tests actually assert on.
    /// </summary>
    private async Task RunWorkerAsync(Guid functionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumeOnceAsync(functionId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await RecordBrokerErrorAsync(functionId, Summarise(ex), ct);
                logger.LogWarning(ex,
                    "Queue consumer for function {FunctionId} lost its connection; retrying.", functionId);
            }

            // Applies whichever way the attempt above ended — thrown, or returned because there was
            // nothing valid to consume right now (disabled mid-flight, a broker that failed the
            // tenancy check). Retrying immediately in either case would just spin.
            try { await Task.Delay(ReconnectDelay, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One connect-and-consume attempt. Returns (rather than throws) when there is nothing
    /// valid to consume right now; the caller's own loop decides what happens next.</summary>
    internal async Task ConsumeOnceAsync(Guid functionId, CancellationToken ct)
    {
        FunctionDefinition? fn;
        ManagedService? svc;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            fn = await db.FunctionDefinitions.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == functionId, ct);
            if (fn is null || !fn.IsEnabled || fn.Trigger != FunctionTrigger.Queue
                || fn.QueueServiceId is null || string.IsNullOrWhiteSpace(fn.QueueName))
                return; // no longer wanted; the next reconciliation tick stops this worker

            svc = await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == fn.QueueServiceId, ct);

            // Tenancy, defensively — see this class's own doc. FunctionAppService.Validate already
            // refuses to save a cross-workspace or non-RabbitMQ attachment; this is the belt to that
            // suspenders for a row that reached the table some other way.
            if (svc is null || svc.Type != ManagedServiceType.RabbitMq || svc.WorkspaceId != fn.WorkspaceId)
            {
                await RecordBrokerErrorAsync(functionId,
                    "The attached broker no longer exists, is not RabbitMQ, or belongs to another workspace.", ct);
                return;
            }
        }

        var password = SafeUnprotect(svc.EncryptedPassword);
        var address = new QueueBrokerAddress(
            svc.ContainerName, ServiceCatalog.All[svc.Type].Port, svc.Username, password ?? "");

        await using var connection = await brokerFactory.ConnectAsync(address, ct);
        await ClearBrokerErrorAsync(functionId, ct);

        await connection.ConsumeAsync(
            fn.QueueName!, (delivery, handleCt) => HandleAsync(functionId, fn.QueueName!, delivery, handleCt), ct);
    }

    /// <summary>
    /// One delivery's verdict. Never throws — a broker message must always get an outcome, or it sits
    /// unacked until the connection eventually drops.
    /// </summary>
    internal async Task<QueueAckOutcome> HandleAsync(
        Guid functionId, string queueName, QueueDelivery delivery, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IFunctionInvoker>();

        FunctionInvocation? invocation;
        try
        {
            invocation = await invoker.InvokeNowAsync(functionId, FunctionTrigger.Queue, evt: null, delivery.Body, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Calling function {FunctionId} for a queue delivery threw.", functionId);
            invocation = null;
        }

        if (invocation is null)
        {
            // Disabled, unpublished, or lost its entitlement between the reconcile tick that started
            // this worker and this message arriving (or the call itself threw) — not a delivery
            // failure, so it must not spend the message's one retry. Put it back for whoever is
            // consuming once that is true again; the next reconciliation tick stops this worker if it
            // stays untrue.
            return QueueAckOutcome.NackRequeue;
        }

        if (invocation.Succeeded) return QueueAckOutcome.Ack;

        if (!delivery.Redelivered) return QueueAckOutcome.NackRequeue;

        // Second failure: parked, not dropped. A queue consumer that drops a failed message and moves
        // on is the defect class this codebase has spent weeks removing.
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        db.FunctionQueueDeadLetters.Add(new FunctionQueueDeadLetter
        {
            FunctionId = functionId,
            AppId = invocation.AppId,
            WorkspaceId = invocation.WorkspaceId,
            QueueName = queueName,
            Body = delivery.Body,
            Reason = invocation.Error is { Length: > 0 } e ? e : "The function failed twice."
        });
        await db.SaveChangesAsync(ct);
        return QueueAckOutcome.NackDrop;
    }

    private async Task RecordBrokerErrorAsync(Guid functionId, string error, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var fn = await db.FunctionDefinitions.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == functionId, ct);
        if (fn is null) return;

        fn.QueueLastError = error.Length > 900 ? error[..900] : error;
        fn.QueueLastAttemptAt = clock.UtcNow;
        fn.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task ClearBrokerErrorAsync(Guid functionId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var fn = await db.FunctionDefinitions.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == functionId, ct);
        if (fn is null || fn.QueueLastError is null) return; // avoid a write when there is nothing to clear

        fn.QueueLastError = null;
        fn.QueueLastAttemptAt = clock.UtcNow;
        fn.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private string? SafeUnprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return protector.Unprotect(ciphertext); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A queue function's attached broker password could not be decrypted.");
            return null;
        }
    }

    private static string Summarise(Exception ex) => ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
}
