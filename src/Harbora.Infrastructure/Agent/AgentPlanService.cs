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
    ILogger<AgentPlanService> logger)
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
        // browser went away: a plan cancelled halfway through would be the worst of both outcomes.
        await RememberAsync(actor, steps, request?.RememberTools, now, CancellationToken.None);
        var run = await RunClaimedAsync(actor, plan, steps, CancellationToken.None);
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
        return (await RunClaimedAsync(actor, plan, StepsOf(plan), CancellationToken.None)).Result;
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
        {
            if (await TryMoveAsync(actor, plan.Id, AgentPlanStatus.Expired, now, requireUnexpired: false, ct) == 0)
                continue;

            await RecordOutcomeAsync(plan, StepsOf(plan).Select(s => Outcome(s, "expired",
                "Not approved — the plan expired and nothing ran.", "تأیید نشد — برنامه منقضی شد و هیچ‌چیز اجرا نشد.")).ToList(),
                now, ct);
        }
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

    /// <summary>The conversation's plan still waiting for a decision, if any.</summary>
    public async Task<AgentPlan?> PendingPlanAsync(AgentActor actor, Guid conversationId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await db.AgentPlans.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ConversationId == conversationId && p.WorkspaceId == actor.WorkspaceId
                        && p.UserId == actor.UserId && p.Status == AgentPlanStatus.Pending && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public AgentPlanView ViewOf(AgentPlan plan) =>
        new(plan.Id, StatusName(EffectiveStatus(plan)), plan.ExpiresAt, StepsOf(plan).Select(StepView).ToList());

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

    private async Task<ClaimedRun> RunClaimedAsync(
        AgentActor actor, AgentPlan plan, IReadOnlyList<AgentPlanStep> steps, CancellationToken ct)
    {
        var context = new AgentToolContext(actor.WorkspaceId, actor.UserId, actor.User, plan.ConversationId, Page: null);
        var results = new List<AgentPlanStepResultView>();
        var outcomes = new List<StepOutcome>();

        // 1. Every step is validated again, in order, with a fresh scope, before any of them runs.
        var scope = new AgentPlanScope();
        var ready = new List<(AgentPlanStep Step, IAgentTool Tool, JsonElement Args)>();
        (AgentPlanStep Step, string Error, string ErrorFa)? refused = null;

        foreach (var step in steps)
        {
            var check = await RevalidateAsync(step, actor, context, scope, ct);
            if (check.Error is not null)
            {
                refused = (step, check.Error, check.ErrorFa!);
                break;
            }
            ready.Add((step, check.Tool!, check.Args));
        }

        var now = clock.UtcNow;
        if (refused is { } r)
        {
            // Nothing runs. The refused step says why; every other step — before it too — was skipped.
            foreach (var step in steps)
            {
                if (step.Index == r.Step.Index)
                {
                    results.Add(new(step.Index, "failed", null, null, r.Error, r.ErrorFa, []));
                    outcomes.Add(Outcome(step, "refused", r.Error, r.ErrorFa,
                        error: $"Refused when re-checked at approval; nothing in the plan ran. {r.Error}"));
                }
                else
                {
                    results.Add(Skipped(step.Index, "Not run: another step was refused when the plan was re-checked.",
                        "اجرا نشد: مرحلهٔ دیگری هنگام بررسی دوباره رد شد."));
                    outcomes.Add(Outcome(step, "skipped", "Not run: another step was refused when the plan was re-checked.",
                        "اجرا نشد: مرحلهٔ دیگری هنگام بررسی دوباره رد شد."));
                }
            }

            await FinishAsync(plan, AgentPlanStatus.Failed, results, executedAt: null, ct);
            await RecordOutcomeAsync(plan, outcomes, now, ct);
            return new ClaimedRun(
                new AgentPlanResultView(plan.Id, StatusName(plan.Status), results),
                new AgentPlanRefusal(AgentPlanRefusal.RevalidationRefusedCode,
                    $"Nothing ran: step {r.Step.Index + 1} was refused when the plan was checked again. {r.Error}",
                    $"هیچ‌چیز اجرا نشد: مرحلهٔ {r.Step.Index + 1} هنگام بررسی دوباره رد شد. {r.ErrorFa}",
                    StatusName(plan.Status), r.Step.Index));
        }

        // 2. Run in order; the first failure stops the rest.
        var stopped = false;
        foreach (var (step, tool, args) in ready)
        {
            if (stopped)
            {
                results.Add(Skipped(step.Index, "Skipped: an earlier step failed.", "رد شد: مرحلهٔ قبلی ناموفق بود."));
                outcomes.Add(Outcome(step, "skipped", "Skipped: an earlier step failed, so this did not run.",
                    "رد شد: مرحلهٔ قبلی ناموفق بود و این مرحله اجرا نشد."));
                continue;
            }

            AgentToolResult result;
            try
            {
                result = await tool.ExecuteAsync(args, context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Agent tool {Tool} threw while executing step {Step} of plan {Plan}.",
                    step.Tool, step.Index, plan.Id);
                result = AgentToolResult.Failed("The step failed unexpectedly; see the server log.",
                    "این مرحله به‌طور غیرمنتظره ناموفق بود؛ لاگ سرور را ببینید.");
            }

            await audit.LogAsync(StepExecutedAction, targetType: "agent_plan", targetId: plan.Id.ToString(),
                ipAddress: actor.IpAddress,
                metadataJson: JsonSerializer.Serialize(new
                {
                    conversationId = plan.ConversationId,
                    planId = plan.Id,
                    step = step.Index + 1, // the ordinal a person reads on the card
                    stepIndex = step.Index,
                    tool = step.Tool,
                    ok = result.Ok
                }),
                workspaceId: actor.WorkspaceId, ct: ct);

            if (result.Ok)
            {
                results.Add(new(step.Index, "done", result.SummaryEn, result.SummaryFa, null, null, result.Links ?? []));
                outcomes.Add(Outcome(step, "done", result.SummaryEn ?? "Done.", result.SummaryFa ?? "انجام شد.",
                    resultJson: result.ResultJson));
            }
            else
            {
                var error = result.Error ?? "The step failed.";
                var errorFa = result.ErrorFa ?? "این مرحله ناموفق بود.";
                results.Add(new(step.Index, "failed", null, null, error, errorFa, result.Links ?? []));
                outcomes.Add(Outcome(step, "failed", error, errorFa, error: error, resultJson: result.ResultJson));
                stopped = true;
            }
        }

        now = clock.UtcNow;
        await FinishAsync(plan, stopped ? AgentPlanStatus.Failed : AgentPlanStatus.Executed, results, executedAt: now, ct);
        await RecordOutcomeAsync(plan, outcomes, now, ct);
        return new ClaimedRun(new AgentPlanResultView(plan.Id, StatusName(plan.Status), results));
    }

    private sealed record Revalidation(IAgentTool? Tool, JsonElement Args, string? Error, string? ErrorFa);

    /// <summary>
    /// One step, checked again as if it had just been proposed: the tool still exists, still has the
    /// consent level the person approved, the person still holds its capability, and the tool's own
    /// validation still accepts it. A destructive step must also still name the same resource — the
    /// phrase the person typed was checked against the old one.
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

        if (!await registry.AllowsAsync(actor.User, tool, ct))
            return new(null, default, "Your role no longer allows this step.", "نقش شما دیگر اجازهٔ این مرحله را نمی‌دهد.");

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

        AgentValidation validation;
        try
        {
            validation = await tool.ValidateAsync(args, context, scope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent tool {Tool} threw while re-validating.", step.Tool);
            return new(null, default, "This step could not be checked; see the server log.", "این مرحله بررسی نشد؛ لاگ سرور را ببینید.");
        }

        if (!validation.Ok)
            return new(null, default, validation.Error ?? "Refused.", validation.ErrorFa ?? "رد شد.");

        if (AgentConsentPolicy.NeedsTypedConfirmation(step.Kind)
            && !AgentConsentPolicy.ConfirmationMatches(step.ConfirmationPhrase, validation.ConfirmationPhrase))
            return new(null, default, "The resource this step deletes changed since the plan was made; ask again.",
                "منبعی که این مرحله حذف می‌کند پس از ساخت برنامه تغییر کرده است؛ دوباره درخواست کنید.");

        if (validation.Creates is not null) scope.Add(validation.Creates);
        return new(tool, args, null, null);
    }

    private async Task FinishAsync(
        AgentPlan plan, AgentPlanStatus status, List<AgentPlanStepResultView> results, DateTimeOffset? executedAt,
        CancellationToken ct)
    {
        // Plain tracked write: this call owns the plan since it won the claim, so there is no race left.
        var tracked = await db.AgentPlans.IgnoreQueryFilters().FirstAsync(p => p.Id == plan.Id, ct);
        tracked.Status = status;
        tracked.ResultsJson = AgentJson.Serialize(results);
        tracked.ExecutedAt = executedAt;
        await db.SaveChangesAsync(ct);

        plan.Status = status;
        plan.ResultsJson = tracked.ResultsJson;
        plan.ExecutedAt = executedAt;
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
            await RefreshTrackedAsync(planId, ct);
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
    /// way out) or already decided — or null when it is still pending and in time.</summary>
    private async Task<AgentPlanRefusal?> RefuseUndecidableAsync(AgentActor actor, AgentPlan plan, DateTimeOffset now, CancellationToken ct)
    {
        if (plan.Status == AgentPlanStatus.Pending && now >= plan.ExpiresAt)
        {
            await TryMoveAsync(actor, plan.Id, AgentPlanStatus.Expired, now, requireUnexpired: false, ct);
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
        AgentPlanStep Step, string State, string SummaryEn, string SummaryFa, string? Error, string? ResultJson);

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
                Text = AgentJson.ForModel(content.ToJsonString(AgentJson.Options)),
                SummaryEn = AgentConversationLog.Bound(outcome.SummaryEn),
                SummaryFa = AgentConversationLog.Bound(outcome.SummaryFa),
                PlanId = plan.Id,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await AgentConversationLog.TouchAsync(db, plan.ConversationId, now, ct);
        await db.SaveChangesAsync(ct);
    }

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
