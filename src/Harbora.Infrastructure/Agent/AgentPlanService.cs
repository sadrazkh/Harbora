using System.Text.Json;
using System.Text.Json.Nodes;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The generic plan engine: stores the write steps a turn proposed, and approves, rejects, expires and
/// executes them — once.
///
/// <para>
/// <b>The design is in <see cref="TryMoveAsync"/>.</b> Approval moves a plan <c>pending →
/// executing</c> with one conditional <c>UPDATE</c> whose <c>WHERE</c> clause is the whole check
/// (this person, this workspace, still pending, not yet expired), exactly the shape of
/// <c>SignInTokenService.TryClaimAsync</c>: two approvals racing each other are two concurrent
/// statements against one row, Postgres serialises them, and the loser matches zero rows. Only the
/// caller whose update matched goes on to run anything. The same InMemory caveat applies and is
/// documented there: EF InMemory has no <c>ExecuteUpdateAsync</c>, so on that provider the move is a
/// plain read-then-write that is not atomic — the two-approvals race is proven only in the Postgres
/// lane (<c>AgentPlanApprovalRaceTests</c>).
/// </para>
///
/// <para>
/// Execution never trusts the plan it was handed. Every step is validated again with a fresh
/// <see cref="AgentPlanScope"/> before anything runs — the world may have changed in the minutes the
/// card was open — and if any step is refused now, nothing runs at all. Then the steps run in order
/// and the first failure stops the rest. What happened is written three times: into the plan, as tool
/// messages in the conversation (so the next turn knows), and as one <c>assistant.step_executed</c>
/// audit entry per step that ran.
/// </para>
/// </summary>
public sealed class AgentPlanService(
    HarboraDbContext db,
    AgentToolRegistry registry,
    IAuditLogger audit,
    ISystemClock clock,
    ILogger<AgentPlanService> logger,
    IAgentSecretValues secretValues)
{
    public const string StepExecutedAction = "assistant.step_executed";
    public const string PlanRejectedAction = "assistant.plan_rejected";

    // ---- creating ------------------------------------------------------------------------------

    /// <summary>Stores a turn's validated steps as a plan waiting for consent, expiring in
    /// <see cref="AgentPlan.Lifetime"/>.</summary>
    public async Task<AgentPlan> CreatePendingAsync(
        AgentActor actor, Guid conversationId, IReadOnlyList<AgentPlanStep> steps, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var plan = new AgentPlan
        {
            WorkspaceId = actor.WorkspaceId,
            UserId = actor.UserId,
            ConversationId = conversationId,
            Status = AgentPlanStatus.Pending,
            StepsJson = AgentJson.Serialize(steps),
            ExpiresAt = now + AgentPlan.Lifetime,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AgentPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        return plan;
    }

    // ---- deciding ------------------------------------------------------------------------------

    /// <summary>
    /// Approves and runs a plan. Refused, with nothing run and nothing changed, when the plan is not
    /// this person's in this workspace, is no longer pending, has expired, or a destructive step's typed
    /// name does not match its confirmation phrase. Also refused — "revalidation_refused", with nothing
    /// run — when the approval was accepted but a step no longer passes its checks; the plan is then
    /// <see cref="AgentPlanStatus.Failed"/> with the refused step's reason in its results.
    /// </summary>
    public async Task<AgentApproveOutcome> ApproveAsync(
        AgentActor actor, Guid planId, AgentApproveRequest? request, CancellationToken ct)
    {
        var plan = await LoadAsync(actor, planId, ct);
        if (plan is null) return new(null, AgentPlanRefusal.NotFound());

        var now = clock.UtcNow;
        if (await RefuseUndecidableAsync(actor, plan, now, ct) is { } refusal) return new(null, refusal);

        var steps = StepsOf(plan);

        // Typed confirmation, before the claim: a mismatch leaves the plan pending so the person can
        // correct what they typed, instead of burning the plan on a typo.
        foreach (var step in steps.Where(s => AgentConsentPolicy.NeedsTypedConfirmation(s.Kind)))
        {
            string? typed = null;
            request?.Confirmations?.TryGetValue(step.Index, out typed);
            if (!AgentConsentPolicy.ConfirmationMatches(step.ConfirmationPhrase, typed))
                return new(null, new AgentPlanRefusal(
                    AgentPlanRefusal.ConfirmationMismatchCode,
                    $"Step {step.Index + 1}: type \"{step.ConfirmationPhrase}\" exactly to confirm.",
                    $"مرحلهٔ {step.Index + 1}: برای تأیید، «{step.ConfirmationPhrase}» را دقیقاً تایپ کنید.",
                    StatusName(plan.Status), step.Index));
        }

        if (await TryMoveAsync(actor, plan.Id, AgentPlanStatus.Executing, now, requireUnexpired: true, ct) == 0)
            return new(null, await DiagnoseLostClaimAsync(actor, plan.Id, now, ct));

        // Past this line this call is provably the only one that could have reached here for this plan
        // — the conditional update is what proves it. From here on the work is not abandoned because the
        // browser went away (RunClaimedAsync takes no token): a plan cancelled halfway through would be
        // the worst of both outcomes.
        var run = await RunClaimedAsync(actor, plan, steps, request?.RememberTools);
        return run.Refusal is { } refused ? new(null, refused) : new(run.Result, null);
    }

    /// <summary>
    /// Runs a plan that needs no card — every step a remembered safe tool — through the same claim and
    /// the same re-validation as an approval, so "auto-approved" never means "unchecked".
    /// </summary>
    public async Task<AgentPlanResultView> ExecuteAutoApprovedAsync(AgentActor actor, AgentPlan plan, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (await TryMoveAsync(actor, plan.Id, AgentPlanStatus.Executing, now, requireUnexpired: true, ct) == 0)
        {
            var refusal = await DiagnoseLostClaimAsync(actor, plan.Id, now, ct);
            throw new InvalidOperationException($"A plan created a moment ago could not be claimed: {refusal.MessageEn}");
        }

        // A refusal at re-validation is reported in the result itself here — the refused step failed,
        // the rest skipped — because there is no response status to carry it; the executed event shows it.
        return (await RunClaimedAsync(actor, plan, StepsOf(plan), rememberTools: null)).Result;
    }

    /// <summary>
    /// Rejects a pending plan. Null on success — including a second rejection of the same plan, which
    /// changes nothing — otherwise why it could not be rejected.
    /// </summary>
    public async Task<AgentPlanRefusal?> RejectAsync(AgentActor actor, Guid planId, CancellationToken ct)
    {
        var plan = await LoadAsync(actor, planId, ct);
        if (plan is null) return AgentPlanRefusal.NotFound();
        if (plan.Status == AgentPlanStatus.Rejected) return null;

        var now = clock.UtcNow;
        if (await RefuseUndecidableAsync(actor, plan, now, ct) is { } refusal) return refusal;

        if (await TryMoveAsync(actor, plan.Id, AgentPlanStatus.Rejected, now, requireUnexpired: true, ct) == 0)
        {
            var current = await db.AgentPlans.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == plan.Id, ct);
            return current.Status == AgentPlanStatus.Rejected ? null : await DiagnoseLostClaimAsync(actor, plan.Id, now, ct);
        }

        var steps = StepsOf(plan);
        await RecordOutcomeAsync(plan, steps.Select(s => Outcome(s, "rejected",
            "Rejected by the user — it did not run.", "کاربر رد کرد — اجرا نشد.")).ToList(), now, ct);

        await audit.LogAsync(PlanRejectedAction, targetType: "agent_plan", targetId: plan.Id.ToString(),
            ipAddress: actor.IpAddress,
            metadataJson: JsonSerializer.Serialize(new
            {
                conversationId = plan.ConversationId,
                planId = plan.Id,
                tools = steps.Select(s => s.Tool).ToList()
            }),
            workspaceId: actor.WorkspaceId, ct: ct);

        return null;
    }

    /// <summary>
    /// Expires every plan still pending in this conversation — called when the person sends a new
    /// message instead of deciding, so an old card cannot be approved after the conversation has moved
    /// on, and the next turn is told it did not run.
    /// </summary>
    public async Task SupersedePendingAsync(AgentActor actor, Guid conversationId, CancellationToken ct)
    {
        var pending = await db.AgentPlans.IgnoreQueryFilters()
            .Where(p => p.ConversationId == conversationId && p.WorkspaceId == actor.WorkspaceId
                        && p.UserId == actor.UserId && p.Status == AgentPlanStatus.Pending)
            .ToListAsync(ct);

        var now = clock.UtcNow;
        foreach (var plan in pending)
            await ExpireAsync(actor, plan, now, ct);
    }

    /// <summary>
    /// Moves a pending plan to expired and, if this call is the one that moved it, tells the
    /// conversation it did not run — whether the plan was superseded by a new message or found past its
    /// time at approval, so the next turn never still believes a step is waiting.
    /// </summary>
    private async Task ExpireAsync(AgentActor actor, AgentPlan plan, DateTimeOffset now, CancellationToken ct)
    {
        if (await TryMoveAsync(actor, plan.Id, AgentPlanStatus.Expired, now, requireUnexpired: false, ct) == 0)
            return;

        await RecordOutcomeAsync(plan, StepsOf(plan).Select(s => Outcome(s, "expired",
            "Not approved — the plan expired and nothing ran.", "تأیید نشد — برنامه منقضی شد و هیچ‌چیز اجرا نشد.")).ToList(),
            now, ct);
    }

    // ---- remembered tools ----------------------------------------------------------------------

    /// <summary>The tools this person said "don't ask again" for, in this workspace.</summary>
    public async Task<IReadOnlySet<string>> RememberedToolsAsync(AgentActor actor, CancellationToken ct) =>
        (await db.AgentToolApprovals.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.UserId == actor.UserId && a.WorkspaceId == actor.WorkspaceId)
            .Select(a => a.ToolName)
            .ToListAsync(ct))
        .ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<AgentApprovalView>> ApprovalsAsync(AgentActor actor, CancellationToken ct) =>
        await db.AgentToolApprovals.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.UserId == actor.UserId && a.WorkspaceId == actor.WorkspaceId)
            .OrderBy(a => a.ToolName)
            .Select(a => new AgentApprovalView(a.ToolName, a.CreatedAt))
            .ToListAsync(ct);

    /// <summary>Forgets one "don't ask again". Idempotent: revoking what is not remembered changes nothing.</summary>
    public async Task RevokeApprovalAsync(AgentActor actor, string tool, CancellationToken ct)
    {
        var rows = await db.AgentToolApprovals.IgnoreQueryFilters()
            .Where(a => a.UserId == actor.UserId && a.WorkspaceId == actor.WorkspaceId && a.ToolName == tool)
            .ToListAsync(ct);
        if (rows.Count == 0) return;
        db.AgentToolApprovals.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }

    // ---- reading -------------------------------------------------------------------------------

    /// <summary>The plan's status as a person should see it: a pending plan past its expiry is expired,
    /// whether or not anything has written that down yet.</summary>
    public AgentPlanStatus EffectiveStatus(AgentPlan plan) =>
        plan.Status == AgentPlanStatus.Pending && clock.UtcNow >= plan.ExpiresAt ? AgentPlanStatus.Expired : plan.Status;

    /// <summary>The conversation's plan still waiting for a decision, if any. <see cref="PendingAmong"/> is
    /// the same rule over plans already in hand.</summary>
    public async Task<AgentPlan?> PendingPlanAsync(AgentActor actor, Guid conversationId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await db.AgentPlans.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.WorkspaceId == actor.WorkspaceId
                        && p.UserId == actor.UserId && p.Status == AgentPlanStatus.Pending && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The plans of this person's conversation that the conversation view may need, oldest first — pending,
    /// decided and expired alike: those made at or after <paramref name="since"/>, and the plan still
    /// waiting for a decision whatever its age. The view is re-read every few seconds while an answer is
    /// awaited, and a long conversation has far more plans than the window of messages it shows; each row
    /// carries its steps and results as JSON, so the older ones are not read at all. A null
    /// <paramref name="since"/> leaves only the pending plan.
    /// </summary>
    public async Task<IReadOnlyList<AgentPlan>> PlansOfAsync(
        AgentActor actor, Guid conversationId, DateTimeOffset? since, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await db.AgentPlans.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.WorkspaceId == actor.WorkspaceId && p.UserId == actor.UserId
                        && ((since != null && p.CreatedAt >= since)
                            || (p.Status == AgentPlanStatus.Pending && p.ExpiresAt > now)))
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .ToListAsync(ct);
    }

    /// <summary>The newest of these plans still waiting for a decision — <see cref="EffectiveStatus"/>
    /// pending, so one past its expiry is not — or null. What <see cref="PendingPlanAsync"/> asks the database.</summary>
    public AgentPlan? PendingAmong(IEnumerable<AgentPlan> candidates) =>
        candidates.Where(p => EffectiveStatus(p) == AgentPlanStatus.Pending).MaxBy(p => p.CreatedAt);

    public AgentPlanView ViewOf(AgentPlan plan) =>
        new(plan.Id, StatusName(EffectiveStatus(plan)), plan.ExpiresAt, StepsOf(plan).Select(StepView).ToList());

    /// <summary>
    /// What a stored plan did, in the shape the approve response carries (<see cref="ResultView"/> over the
    /// steps <see cref="FinishAsync"/> wrote into <c>ResultsJson</c> — scrubbed of held values already). Null
    /// when nothing ran: the plan is still waiting or executing, was rejected or expired, or re-validation
    /// refused a step at approval and left the rest unrun (<c>ExecutedAt</c> stays empty then, and the
    /// approval was answered with a refusal, not a result).
    /// </summary>
    public static AgentPlanResultView? ResultOf(AgentPlan plan)
    {
        if (plan.ResultsJson is null || plan.ExecutedAt is null) return null;
        return AgentJson.Deserialize<List<AgentPlanStepResultView>>(plan.ResultsJson) is { } steps
            ? ResultView(plan, plan.Status, steps)
            : null;
    }

    /// <summary>The one place a plan's result is put together — the approve response and the stored
    /// conversation both end here, so they cannot name the plan or its status differently.</summary>
    private static AgentPlanResultView ResultView(AgentPlan plan, AgentPlanStatus status, IReadOnlyList<AgentPlanStepResultView> steps) =>
        new(plan.Id, StatusName(status), steps);

    public static AgentPlanStepView StepView(AgentPlanStep step) => new(
        step.Index, step.Tool, AgentConsentPolicy.KindLabel(step.Kind), step.TitleEn, step.TitleFa,
        step.DetailEn, step.DetailFa, step.WarningsEn, step.WarningsFa, step.ConfirmationPhrase,
        step.AutoApproved, AgentConsentPolicy.IsRememberable(step.Kind));

    public static IReadOnlyList<AgentPlanStep> StepsOf(AgentPlan plan) =>
        AgentJson.Deserialize<List<AgentPlanStep>>(plan.StepsJson) ?? [];

    /// <summary>The wire name of a status — what <see cref="AgentPlanView.Status"/> carries.</summary>
    public static string StatusName(AgentPlanStatus status) => status switch
    {
        AgentPlanStatus.Pending => "pending",
        AgentPlanStatus.Executing => "executing",
        AgentPlanStatus.Executed => "executed",
        AgentPlanStatus.Failed => "failed",
        AgentPlanStatus.Rejected => "rejected",
        AgentPlanStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    // ---- execution -----------------------------------------------------------------------------

    /// <summary>What a claimed run produced; <see cref="Refusal"/> is set when re-validation refused a
    /// step and nothing ran.</summary>
    private sealed record ClaimedRun(AgentPlanResultView Result, AgentPlanRefusal? Refusal = null);

    /// <summary>
    /// What has been decided about each step so far, by position — kept outside the work itself so
    /// that whatever escapes it, the plan can still be finished with what really happened.
    /// </summary>
    private sealed class RunLedger(int steps)
    {
        public AgentPlanStepResultView?[] Results { get; } = new AgentPlanStepResultView?[steps];
        public StepOutcome?[] Outcomes { get; } = new StepOutcome?[steps];

        /// <summary>The position of the step whose tool is running right now.</summary>
        public int? Running { get; set; }

        /// <summary>Whether the running step's audit entry has been written.</summary>
        public bool RunningAudited { get; set; }

        /// <summary>Whether any step's tool was entered at all.</summary>
        public bool AnyRan { get; set; }

        /// <summary>The person's held values when the run began — a handle a step resolves may expire or be
        /// evicted before the run ends, and its value must still be scrubbed from what that step said.</summary>
        public IReadOnlyList<string> KnownAtStart { get; init; } = [];

        /// <summary>Set when re-validation refused a step: the approval's answer, whatever happens after.</summary>
        public AgentPlanRefusal? Refusal { get; set; }
    }

    /// <summary>
    /// Runs a claimed plan to an end — <see cref="AgentPlanStatus.Executed"/> or
    /// <see cref="AgentPlanStatus.Failed"/>, with results in the plan, the conversation and the audit
    /// log — whatever a tool does. A claimed plan left at <c>executing</c> would be a plan whose earlier
    /// steps ran and were audited while the plan, the person and the next turn were all told nothing.
    ///
    /// <para>
    /// It takes no cancellation token: claimed work runs to its end even if the browser goes away, so
    /// an <see cref="OperationCanceledException"/> seen here is never the caller's — it is a tool's own
    /// timeout (an <c>HttpClient.Timeout</c> throws one), and it fails that step like any other error.
    /// </para>
    /// </summary>
    private async Task<ClaimedRun> RunClaimedAsync(
        AgentActor actor, AgentPlan plan, IReadOnlyList<AgentPlanStep> steps, IReadOnlyList<string>? rememberTools)
    {
        var ledger = new RunLedger(steps.Count) { KnownAtStart = KnownSecrets(plan) };
        try
        {
            return await RunClaimedCoreAsync(actor, plan, steps, rememberTools, ledger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Plan {Plan} broke after it was claimed; it is marked failed with what had run.", plan.Id);
            return await FinishBrokenRunAsync(actor, plan, steps, ledger);
        }
    }

    private async Task<ClaimedRun> RunClaimedCoreAsync(
        AgentActor actor, AgentPlan plan, IReadOnlyList<AgentPlanStep> steps, IReadOnlyList<string>? rememberTools,
        RunLedger ledger)
    {
        var ct = CancellationToken.None;
        var context = new AgentToolContext(actor.WorkspaceId, actor.UserId, actor.User, plan.ConversationId, Page: null);

        // 1. Every step is validated again, in order, with a fresh scope, before any of them runs.
        var scope = new AgentPlanScope();
        var ready = new List<(int Position, AgentPlanStep Step, IAgentTool Tool, JsonElement Args)>();
        (int Position, AgentPlanStep Step, string Error, string ErrorFa)? refused = null;

        for (var position = 0; position < steps.Count; position++)
        {
            var check = await RevalidateAsync(steps[position], actor, context, scope, ct);
            if (check.Error is not null)
            {
                refused = (position, steps[position], check.Error, check.ErrorFa!);
                break;
            }
            ready.Add((position, steps[position], check.Tool!, check.Args));
        }

        if (refused is { } r)
        {
            // Nothing runs. The refused step says why; every other step — before it too — was skipped.
            for (var position = 0; position < steps.Count; position++)
            {
                var step = steps[position];
                if (position == r.Position)
                {
                    ledger.Results[position] = new(step.Index, "failed", null, null, r.Error, r.ErrorFa, []);
                    ledger.Outcomes[position] = Outcome(step, "refused", r.Error, r.ErrorFa,
                        error: $"Refused when re-checked at approval; nothing in the plan ran. {r.Error}");
                }
                else
                {
                    ledger.Results[position] = Skipped(step.Index, NotRunEn, NotRunFa);
                    ledger.Outcomes[position] = Outcome(step, "skipped", NotRunEn, NotRunFa);
                }
            }

            // Decided before anything is saved, so the answer is still the refusal even if the save below
            // breaks and the plan is finished by FinishBrokenRunAsync instead.
            ledger.Refusal = new AgentPlanRefusal(AgentPlanRefusal.RevalidationRefusedCode,
                $"Nothing ran: step {r.Step.Index + 1} was refused when the plan was checked again. {r.Error}",
                $"هیچ‌چیز اجرا نشد: مرحلهٔ {r.Step.Index + 1} هنگام بررسی دوباره رد شد. {r.ErrorFa}",
                StatusName(AgentPlanStatus.Failed), r.Step.Index);

            var results = await FinishAsync(plan, AgentPlanStatus.Failed, ledger, executedAt: null, ct);

            // A refused approval remembers nothing: like every other refusal it changes nothing but the
            // plan's own state, and "don't ask again" was consent to an approval that did not go ahead.
            return new ClaimedRun(ResultView(plan, plan.Status, results), ledger.Refusal);
        }

        // The approval is going ahead, so its "don't ask again" ticks are recorded now.
        await RememberAsync(actor, steps, rememberTools, clock.UtcNow, ct);

        // 2. Run in order; the first failure stops the rest.
        var stopped = false;
        foreach (var (position, step, tool, args) in ready)
        {
            if (stopped)
            {
                ledger.Results[position] = Skipped(step.Index, "Skipped: an earlier step failed.", "رد شد: مرحلهٔ قبلی ناموفق بود.");
                ledger.Outcomes[position] = Outcome(step, "skipped", "Skipped: an earlier step failed, so this did not run.",
                    "رد شد: مرحلهٔ قبلی ناموفق بود و این مرحله اجرا نشد.");
                continue;
            }

            // What was already pending before this step — an audit row an earlier swallowed save left
            // Added, say — is not this step's to drop if it fails.
            var before = AgentTracker.ForeignChanges(db);
            ledger.Running = position;
            ledger.RunningAudited = false;
            ledger.AnyRan = true;

            AgentToolResult result;
            try
            {
                result = await tool.ExecuteAsync(args, context, ct);
            }
            catch (Exception ex)
            {
                // Every exception, cancellation included — see RunClaimedAsync for why none is the caller's.
                logger.LogError(ex, "Agent tool {Tool} threw while executing step {Step} of plan {Plan}.",
                    step.Tool, step.Index + 1, plan.Id);
                result = AgentToolResult.Failed(StepBrokeEn, StepBrokeFa);
            }

            if (!result.Ok)
            {
                // A failed step may have left half-made rows in the shared context — an entity still Added
                // after its save threw on a unique-slug race. The audit entry's save below would write
                // them again and fail the same way, so what this step left is dropped first.
                var dropped = AgentTracker.DetachForeignChanges(db, before);
                if (dropped.Count > 0)
                    logger.LogWarning("Step {Step} of plan {Plan} ({Tool}) failed leaving unsaved changes; dropped: {Types}.",
                        step.Index + 1, plan.Id, step.Tool, string.Join(", ", dropped));
            }

            await AuditStepAsync(actor, plan, step, result.Ok);
            ledger.RunningAudited = true;

            if (result.Ok)
            {
                ledger.Results[position] = new(step.Index, "done", result.SummaryEn, result.SummaryFa, null, null, result.Links ?? []);
                ledger.Outcomes[position] = Outcome(step, "done", result.SummaryEn ?? "Done.", result.SummaryFa ?? "انجام شد.",
                    resultJson: result.ResultJson);
            }
            else
            {
                var error = result.Error ?? "The step failed.";
                var errorFa = result.ErrorFa ?? "این مرحله ناموفق بود.";
                ledger.Results[position] = new(step.Index, "failed", null, null, error, errorFa, result.Links ?? []);
                ledger.Outcomes[position] = Outcome(step, "failed", error, errorFa, error: error, resultJson: result.ResultJson);
                stopped = true;
            }

            ledger.Running = null;
        }

        var finished = await FinishAsync(plan, stopped ? AgentPlanStatus.Failed : AgentPlanStatus.Executed, ledger,
            executedAt: clock.UtcNow, ct);
        return new ClaimedRun(ResultView(plan, plan.Status, finished));
    }

    /// <summary>One <c>assistant.step_executed</c> entry: conversation, plan, step, tool and whether it worked.</summary>
    private Task AuditStepAsync(AgentActor actor, AgentPlan plan, AgentPlanStep step, bool ok) =>
        audit.LogAsync(StepExecutedAction, targetType: "agent_plan", targetId: plan.Id.ToString(),
            ipAddress: actor.IpAddress,
            metadataJson: JsonSerializer.Serialize(new
            {
                conversationId = plan.ConversationId,
                planId = plan.Id,
                step = step.Index + 1, // the ordinal a person reads on the card
                stepIndex = step.Index,
                tool = step.Tool,
                ok
            }),
            workspaceId: actor.WorkspaceId, ct: CancellationToken.None);

    /// <summary>
    /// The way out for a run that something escaped — a save that threw, a tracker a tool left
    /// inconsistent, a failure in the record itself. The tracker is cleared outright
    /// (<c>ChangeTracker.Clear</c>, which, unlike enumerating its entries, cannot itself throw on an
    /// inconsistent graph), so nothing half-written is retried along with the record. The step that was
    /// running fails — and is audited, if it had not been — every step without a result is skipped, and
    /// the plan is finished. If even that cannot be written, it is logged and the plan stays executing —
    /// the one state that never claims anything happened.
    /// </summary>
    private async Task<ClaimedRun> FinishBrokenRunAsync(
        AgentActor actor, AgentPlan plan, IReadOnlyList<AgentPlanStep> steps, RunLedger ledger)
    {
        db.ChangeTracker.Clear();

        for (var position = 0; position < steps.Count; position++)
        {
            if (ledger.Results[position] is not null) continue;
            var step = steps[position];
            if (position == ledger.Running)
            {
                ledger.Results[position] = new(step.Index, "failed", null, null, StepBrokeEn, StepBrokeFa, []);
                ledger.Outcomes[position] = Outcome(step, "failed", StepBrokeEn, StepBrokeFa, error: StepBrokeEn);
            }
            else
            {
                ledger.Results[position] = Skipped(step.Index, StoppedEn, StoppedFa);
                ledger.Outcomes[position] = Outcome(step, "skipped", StoppedEn, StoppedFa);
            }
        }

        // Every step done means only the record failed the first time; the plan itself did not.
        var status = ledger.Results.All(r => r!.State == "done") ? AgentPlanStatus.Executed : AgentPlanStatus.Failed;
        try
        {
            // The plan is finished first and the broken step audited after. AuditLogger swallows a failed
            // save and leaves its row Added, so auditing first would make FinishAsync's save retry that row
            // — and an audit table that fails every time would then keep the plan executing for good.
            var results = await FinishAsync(plan, status, ledger,
                executedAt: ledger.AnyRan ? clock.UtcNow : null, CancellationToken.None);

            if (ledger.Running is { } running && !ledger.RunningAudited)
                await AuditStepAsync(actor, plan, steps[running], ok: false);

            return new ClaimedRun(ResultView(plan, status, results), ledger.Refusal);
        }
        catch (Exception again)
        {
            logger.LogError(again, "Plan {Plan} could not be recorded either; it stays executing.", plan.Id);
            db.ChangeTracker.Clear();
            return new ClaimedRun(ResultView(plan, status,
                ledger.Results.Select(x => Scrub(x!, KnownSecrets(plan, ledger))).ToList()), ledger.Refusal);
        }
    }


    private const string NotRunEn = "Not run: another step was refused when the plan was re-checked.";
    private const string NotRunFa = "اجرا نشد: مرحلهٔ دیگری هنگام بررسی دوباره رد شد.";
    private const string StepBrokeEn = "The step failed unexpectedly; see the server log.";
    private const string StepBrokeFa = "این مرحله به‌طور غیرمنتظره ناموفق بود؛ لاگ سرور را ببینید.";
    private const string StoppedEn = "Not run: the plan stopped because of an error on the server.";
    private const string StoppedFa = "اجرا نشد: برنامه به‌خاطر خطایی در سرور متوقف شد.";

    private sealed record Revalidation(IAgentTool? Tool, JsonElement Args, string? Error, string? ErrorFa);

    /// <summary>
    /// One step, checked again as if it had just been proposed: the tool still exists, still has the
    /// consent level the person approved, the person still holds its capability, and the tool's own
    /// validation still accepts it — without changing anything. A destructive step must also still name
    /// the same resource — the phrase the person typed was checked against the old one.
    /// </summary>
    private async Task<Revalidation> RevalidateAsync(
        AgentPlanStep step, AgentActor actor, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var tool = registry.Find(step.Tool);
        if (tool is null)
            return new(null, default, $"The tool '{step.Tool}' no longer exists.", $"ابزار «{step.Tool}» دیگر وجود ندارد.");

        // A tool whose consent level changed since the card was drawn was approved under the wrong
        // rules — a step that became destructive was never confirmed by name.
        if (tool.Kind != step.Kind)
            return new(null, default, $"The tool '{step.Tool}' changed since this plan was made; ask again.",
                $"ابزار «{step.Tool}» پس از ساخت این برنامه تغییر کرده است؛ دوباره درخواست کنید.");

        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(step.ArgumentsJson);
            args = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new(null, default, "The stored arguments of this step are unreadable.", "آرگومان‌های ذخیره‌شدهٔ این مرحله خوانا نیستند.");
        }

        var before = AgentTracker.ForeignChanges(db);
        AgentValidation validation;
        try
        {
            if (!await registry.AllowsAsync(actor.User, tool, ct))
                return new(null, default, "Your role no longer allows this step.", "نقش شما دیگر اجازهٔ این مرحله را نمی‌دهد.");

            validation = await tool.ValidateAsync(args, context, scope, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent tool {Tool} threw while re-validating.", step.Tool);
            AgentTracker.DetachForeignChanges(db, before);
            return new(null, default, "This step could not be checked; see the server log.", "این مرحله بررسی نشد؛ لاگ سرور را ببینید.");
        }

        if (ValidationLeftChanges(step.Tool, before) is { } leaked)
            return new(null, default, leaked.En, leaked.Fa);

        if (!validation.Ok)
            return new(null, default, validation.Error ?? "Refused.", validation.ErrorFa ?? "رد شد.");

        if (AgentConsentPolicy.NeedsTypedConfirmation(step.Kind)
            && !AgentConsentPolicy.ConfirmationMatches(step.ConfirmationPhrase, validation.ConfirmationPhrase))
            return new(null, default, "The resource this step deletes changed since the plan was made; ask again.",
                "منبعی که این مرحله حذف می‌کند پس از ساخت برنامه تغییر کرده است؛ دوباره درخواست کنید.");

        if (validation.Creates is not null) scope.Add(validation.Creates);
        return new(tool, args, null, null);
    }

    /// <summary>
    /// Validation must not change anything. A tool whose validation left unsaved changes in the shared
    /// context would have them written by the next save the agent makes — before anybody consented — so
    /// they are dropped, logged, and the step refused. Shared with the loop; wave-B tools rely on it.
    /// </summary>
    internal (string En, string Fa)? ValidationLeftChanges(string toolName, IReadOnlySet<object> before)
    {
        var dropped = AgentTracker.DetachForeignChanges(db, before);
        if (dropped.Count == 0) return null;

        logger.LogWarning("Agent tool {Tool} left unsaved changes while validating ({Types}); they were dropped and the step refused.",
            toolName, string.Join(", ", dropped));
        return ("This step was refused: checking it tried to change data, which a check must never do.",
            "این مرحله رد شد: بررسی آن کوشید داده‌ای را تغییر دهد، که یک بررسی هرگز نباید بکند.");
    }

    /// <summary>
    /// Ends a claimed plan: its status, its results and the conversation's record of them, in one save —
    /// so the plan never says it finished while the conversation still believes the steps are waiting.
    /// Every step must have a result in <paramref name="ledger"/> by now.
    /// </summary>
    private async Task<List<AgentPlanStepResultView>> FinishAsync(
        AgentPlan plan, AgentPlanStatus status, RunLedger ledger, DateTimeOffset? executedAt, CancellationToken ct)
    {
        var known = KnownSecrets(plan, ledger);
        var results = ledger.Results.Select(r => Scrub(r!, known)).ToList();

        // Plain tracked write: this call owns the plan since it won the claim, so there is no race left.
        var tracked = await db.AgentPlans.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == plan.Id, ct);
        if (tracked is null)
        {
            // Gone with its conversation while it ran. Deleting a conversation is refused while one of its
            // plans is executing (AgentConversationDeletion), so this is the residue of a race that lock
            // closes on Postgres — or InMemory, where nothing is atomic. There is nowhere left to write the
            // outcome, and nothing is thrown at a person who only approved: every step that ran has its
            // audit entry already.
            logger.LogWarning("Plan {Plan} finished as {Status}, but it and its conversation were deleted while it ran; " +
                              "its outcome is in the audit log only.", plan.Id, StatusName(status));
            plan.Status = status;
            plan.ExecutedAt = executedAt;
            return results;
        }

        tracked.Status = status;
        tracked.ResultsJson = AgentJson.Serialize(results);
        tracked.ExecutedAt = executedAt;
        await AddOutcomeMessagesAsync(plan, ledger.Outcomes.Select(o => o!).ToList(), known, clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);

        plan.Status = status;
        plan.ResultsJson = tracked.ResultsJson;
        plan.ExecutedAt = executedAt;
        return results;
    }

    private async Task RememberAsync(
        AgentActor actor, IReadOnlyList<AgentPlanStep> steps, IReadOnlyList<string>? requested, DateTimeOffset now,
        CancellationToken ct)
    {
        if (requested is null || requested.Count == 0) return;

        var existing = await RememberedToolsAsync(actor, ct);
        var added = new List<AgentToolApproval>();

        foreach (var name in requested.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
        {
            // Only ever for a safe tool — judged by the tool as registered now, not by the request and
            // not by the stored step — and only for a tool this plan actually used.
            var tool = registry.Find(name);
            if (tool is null || !AgentConsentPolicy.IsRememberable(tool.Kind)) continue;
            if (!steps.Any(s => s.Tool == name && s.Kind == AgentToolKind.SafeWrite)) continue;
            if (existing.Contains(name)) continue;

            var approval = new AgentToolApproval
            {
                WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, ToolName = name, CreatedAt = now, UpdatedAt = now
            };
            db.AgentToolApprovals.Add(approval);
            added.Add(approval);
        }

        if (added.Count == 0) return;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same tool remembered by a concurrent approval: the unique index kept one row, which
            // is the outcome both callers wanted.
            foreach (var approval in added) db.Entry(approval).State = EntityState.Detached;
        }
    }

    // ---- the claim -----------------------------------------------------------------------------

    /// <summary>
    /// Moves a plan out of <see cref="AgentPlanStatus.Pending"/> to <paramref name="to"/>, but only if
    /// it is still pending (and, with <paramref name="requireUnexpired"/>, not yet expired) — the check
    /// folded into the <c>UPDATE</c>'s own <c>WHERE</c> clause, so the database decides who wins rather
    /// than a read this method reasons about. Returns the number of rows moved: 1 for the one winner,
    /// 0 for everybody else. See the class doc and <c>SignInTokenService.TryClaimAsync</c> for the
    /// InMemory fallback, which is deliberately not atomic.
    /// </summary>
    private async Task<int> TryMoveAsync(
        AgentActor actor, Guid planId, AgentPlanStatus to, DateTimeOffset now, bool requireUnexpired, CancellationToken ct)
    {
        var movable = db.AgentPlans.IgnoreQueryFilters()
            .Where(p => p.Id == planId && p.WorkspaceId == actor.WorkspaceId && p.UserId == actor.UserId
                        && p.Status == AgentPlanStatus.Pending && (!requireUnexpired || p.ExpiresAt > now));

        // An expiry is not a decision anybody made, so it leaves DecidedAt empty.
        DateTimeOffset? decidedAt = to == AgentPlanStatus.Expired ? null : now;

        if (db.Database.IsRelational())
        {
            var moved = await movable.ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, to)
                .SetProperty(p => p.DecidedAt, decidedAt)
                .SetProperty(p => p.UpdatedAt, now), ct);
            // Once the move has committed it must not be undone in memory by a cancelled refresh: the
            // caller goes on to run the plan, and a stale tracked copy would say it is still pending.
            await RefreshTrackedAsync(planId, moved > 0 ? CancellationToken.None : ct);
            return moved;
        }

        var row = await movable.FirstOrDefaultAsync(ct);
        if (row is null) return 0;
        row.Status = to;
        row.DecidedAt = decidedAt;
        await db.SaveChangesAsync(ct);
        return 1;
    }

    /// <summary>
    /// Brings a tracked copy of the plan back in line after <c>ExecuteUpdateAsync</c>, which writes
    /// straight to the database and never touches the change tracker — the reason is in
    /// <c>SignInTokenService.RefreshTrackedAsync</c>'s own doc.
    /// </summary>
    private async Task RefreshTrackedAsync(Guid planId, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<AgentPlan>().FirstOrDefault(e => e.Entity.Id == planId);
        if (tracked is not null) await tracked.ReloadAsync(ct);
    }

    /// <summary>
    /// Why a move matched nothing, read after it rather than from the copy read before it — the loser
    /// of a race may hold a copy that predates the winner's commit (SignInTokenService's own lesson).
    /// </summary>
    private async Task<AgentPlanRefusal> DiagnoseLostClaimAsync(AgentActor actor, Guid planId, DateTimeOffset now, CancellationToken ct)
    {
        var current = await db.AgentPlans.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId && p.WorkspaceId == actor.WorkspaceId && p.UserId == actor.UserId, ct);
        if (current is null) return AgentPlanRefusal.NotFound();
        if (current.Status == AgentPlanStatus.Pending && now >= current.ExpiresAt) return Expired();
        return NotPending(current.Status);
    }

    /// <summary>The refusal for a plan that can no longer be decided — expired (written down on the
    /// way out, and told to the conversation) or already decided — or null when it is still pending and
    /// in time.</summary>
    private async Task<AgentPlanRefusal?> RefuseUndecidableAsync(AgentActor actor, AgentPlan plan, DateTimeOffset now, CancellationToken ct)
    {
        if (plan.Status == AgentPlanStatus.Pending && now >= plan.ExpiresAt)
        {
            await ExpireAsync(actor, plan, now, ct);
            return Expired();
        }

        return plan.Status == AgentPlanStatus.Pending ? null : NotPending(plan.Status);
    }

    private static AgentPlanRefusal Expired() => new(AgentPlanRefusal.ExpiredCode,
        "This plan expired before it was approved; nothing ran. Ask again for a fresh plan.",
        "این برنامه پیش از تأیید منقضی شد و هیچ‌چیز اجرا نشد. دوباره درخواست کنید.",
        StatusName(AgentPlanStatus.Expired));

    private static AgentPlanRefusal NotPending(AgentPlanStatus status) => new(AgentPlanRefusal.NotPendingCode,
        $"This plan is already {StatusName(status)}; it can only be decided once.",
        $"این برنامه قبلاً «{StatusName(status)}» شده است و فقط یک بار می‌توان درباره‌اش تصمیم گرفت.",
        StatusName(status));

    private async Task<AgentPlan?> LoadAsync(AgentActor actor, Guid planId, CancellationToken ct) =>
        await db.AgentPlans.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == planId && p.WorkspaceId == actor.WorkspaceId && p.UserId == actor.UserId, ct);

    // ---- what the conversation is told -----------------------------------------------------------

    private sealed record StepOutcome(
        AgentPlanStep Step, string State, string SummaryEn, string SummaryFa, string? Error, string? ResultJson)
    {
        /// <summary>What the stored line says about the step: it worked, it did not (a step refused when checked
        /// again counts — the plan's result calls it failed too), or nothing ran for it — skipped, rejected or
        /// expired — which is neither.</summary>
        public bool? Succeeded => State switch { "done" => true, "failed" or "refused" => false, _ => null };
    }

    private static StepOutcome Outcome(
        AgentPlanStep step, string state, string summaryEn, string summaryFa, string? error = null, string? resultJson = null) =>
        new(step, state, summaryEn, summaryFa, error, resultJson);

    private static AgentPlanStepResultView Skipped(int index, string en, string fa) => new(index, "skipped", en, fa, null, null, []);

    /// <summary>
    /// Writes what happened to a plan into its conversation, so the model's next turn knows. It is
    /// written as one assistant message calling each step's tool, followed by one tool message per step
    /// with its outcome — the chat-completions shape every tool-capable model already understands —
    /// rather than as extra answers to the original calls, which were already answered ("added as step
    /// N") and may only be answered once.
    /// </summary>
    private async Task RecordOutcomeAsync(AgentPlan plan, IReadOnlyList<StepOutcome> outcomes, DateTimeOffset now, CancellationToken ct)
    {
        if (outcomes.Count == 0) return;
        await AddOutcomeMessagesAsync(plan, outcomes, KnownSecrets(plan), now, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary><see cref="RecordOutcomeAsync"/>'s messages, added to the context but not saved — so
    /// <see cref="FinishAsync"/> can write them in the same save as the plan's own result.</summary>
    private async Task AddOutcomeMessagesAsync(
        AgentPlan plan, IReadOnlyList<StepOutcome> outcomes, IReadOnlyList<string> known, DateTimeOffset now, CancellationToken ct)
    {
        if (outcomes.Count == 0) return;

        var sequence = await AgentConversationLog.NextSequenceAsync(db, plan.ConversationId, ct);
        var calls = outcomes.Select(o => new AgentToolCall(CallId(plan, o.Step), o.Step.Tool, o.Step.ArgumentsJson)).ToList();

        db.AgentMessages.Add(new AgentMessage
        {
            WorkspaceId = plan.WorkspaceId,
            ConversationId = plan.ConversationId,
            Sequence = sequence++,
            Role = AgentMessageRole.Assistant,
            ToolCallsJson = AgentJson.Serialize(calls),
            PlanId = plan.Id,
            CreatedAt = now,
            UpdatedAt = now
        });

        foreach (var outcome in outcomes)
        {
            var content = new JsonObject
            {
                ["plan"] = plan.Id.ToString(),
                ["step"] = outcome.Step.Index + 1,
                ["tool"] = outcome.Step.Tool,
                ["state"] = outcome.State,
                ["summary"] = outcome.SummaryEn
            };
            if (outcome.Error is not null) content["error"] = outcome.Error;
            if (outcome.ResultJson is not null) content["result"] = ParseOrText(outcome.ResultJson);

            db.AgentMessages.Add(new AgentMessage
            {
                WorkspaceId = plan.WorkspaceId,
                ConversationId = plan.ConversationId,
                Sequence = sequence++,
                Role = AgentMessageRole.Tool,
                ToolCallId = CallId(plan, outcome.Step),
                ToolName = outcome.Step.Tool,
                Text = AgentJson.ForModel(content.ToJsonString(AgentJson.Options), known),
                SummaryEn = AgentConversationLog.Bound(Scrub(outcome.SummaryEn, known)),
                SummaryFa = AgentConversationLog.Bound(Scrub(outcome.SummaryFa, known)),
                ToolSucceeded = outcome.Succeeded,
                PlanId = plan.Id,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await AgentConversationLog.TouchAsync(db, plan.ConversationId, now, ct);
    }

    /// <summary>
    /// The values the plan's person holds in the lock-button vault. A step resolves a handle at execution
    /// and applies the value; if its result or error then echoes it, it is scrubbed here by value before
    /// it reaches the plan's results, the conversation or the model.
    /// </summary>
    private IReadOnlyList<string> KnownSecrets(AgentPlan plan) => secretValues.LiveValues(plan.WorkspaceId, plan.UserId);

    /// <summary>For a claimed run: the values held when it began, together with those held now — so a
    /// handle that expired or was evicted while its step ran is still scrubbed from what the step said.</summary>
    private IReadOnlyList<string> KnownSecrets(AgentPlan plan, RunLedger ledger) =>
        ledger.KnownAtStart.Union(KnownSecrets(plan), StringComparer.Ordinal).ToList();

    /// <summary>By value only: these lines are shown to a person, and a shape rule would turn a handle in
    /// them into something that is neither the handle nor its mask.</summary>
    private static string? Scrub(string? text, IReadOnlyList<string> known) => AgentSecretScrub.ByValue(text, known);

    private static AgentPlanStepResultView Scrub(AgentPlanStepResultView result, IReadOnlyList<string> known) =>
        known.Count == 0 ? result : result with
        {
            SummaryEn = Scrub(result.SummaryEn, known),
            SummaryFa = Scrub(result.SummaryFa, known),
            ErrorEn = Scrub(result.ErrorEn, known),
            ErrorFa = Scrub(result.ErrorFa, known)
        };

    private static string CallId(AgentPlan plan, AgentPlanStep step) => $"plan_{plan.Id:N}_{step.Index}";

    private static JsonNode? ParseOrText(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return JsonValue.Create(json); }
    }
}

/// <summary>Small shared writes on a conversation's message log.</summary>
internal static class AgentConversationLog
{
    /// <summary>The next free <see cref="AgentMessage.Sequence"/> in a conversation.</summary>
    public static async Task<int> NextSequenceAsync(HarboraDbContext db, Guid conversationId, CancellationToken ct)
    {
        var stored = await db.AgentMessages.IgnoreQueryFilters()
            .Where(m => m.ConversationId == conversationId)
            .MaxAsync(m => (int?)m.Sequence, ct) ?? 0;

        // Messages added to this context but not saved yet count too.
        var pending = db.ChangeTracker.Entries<AgentMessage>()
            .Where(e => e.State == EntityState.Added && e.Entity.ConversationId == conversationId)
            .Select(e => e.Entity.Sequence)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Max(stored, pending) + 1;
    }

    /// <summary>Moves the conversation to the top of the list.</summary>
    public static async Task TouchAsync(HarboraDbContext db, Guid conversationId, DateTimeOffset now, CancellationToken ct)
    {
        var conversation = await db.AgentConversations.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is not null) conversation.UpdatedAt = now;
    }

    /// <summary>A summary cut to the column's length.</summary>
    public static string? Bound(string? text) =>
        text is null || text.Length <= AgentMessage.MaxSummaryLength ? text : text[..(AgentMessage.MaxSummaryLength - 1)] + "…";
}
