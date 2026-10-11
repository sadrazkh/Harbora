using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Features;
using Harbora.Infrastructure.Agent;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Harbora.Web.Controllers;

/// <summary>
/// The in-panel agent's endpoints (round six, 2026-10-11 assistant-agent design) — the API the chat
/// pane reads. JSON shapes are exactly <c>AgentViews.cs</c>; the pane mirrors them in TypeScript.
///
/// <para>
/// A message is answered as <c>text/event-stream</c> from inside this request, so the workspace
/// filter, <c>ICurrentUser</c> and project access apply to every tool exactly as they do to a click.
/// The stream starts lazily, on the orchestrator's first event: a refusal that needs no stream (an
/// empty message, an unknown conversation) is still an ordinary status code, and once the stream has
/// begun a failure is an <c>error</c> event followed by <c>done</c>, never a 500 halfway through a
/// response the browser is already reading.
/// </para>
///
/// <para>
/// POSTs carry the antiforgery token in the <c>X-CSRF-TOKEN</c> header (Program.cs), the way the
/// route designer's island sends it.
/// </para>
/// </summary>
[Authorize]
[Route("assistant")]
[RequireFeature(PlatformFeatures.Assistant)]
public sealed class AssistantController(
    AgentConversationService conversations,
    AgentPlanService plans,
    ICurrentUser currentUser,
    ILogger<AssistantController> logger) : Controller
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private AgentActor Actor => new(
        currentUser.WorkspaceId ?? Guid.Empty, currentUser.UserId ?? Guid.Empty, User, ClientIp);

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct) =>
        Json(await conversations.StatusAsync(Actor, ct));

    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations(CancellationToken ct) =>
        Json(await conversations.ListAsync(Actor, ct));

    [HttpGet("conversations/{id:guid}")]
    public async Task<IActionResult> Conversation(Guid id, CancellationToken ct) =>
        await conversations.GetAsync(Actor, id, IsFa, ct) is { } view ? Json(view) : NotFound();

    /// <summary><c>id</c> is a conversation id, or <c>new</c> to start one.</summary>
    [HttpPost("conversations/{id}/messages")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(
        string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AgentSendMessageRequest? request,
        CancellationToken ct)
    {
        Guid? conversationId = null;
        if (!string.Equals(id, "new", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(id, out var parsed)) return NotFound();
            conversationId = parsed;
        }

        var started = false;
        Guid? streamedConversation = null;

        async Task Emit(AgentStreamEvent e, CancellationToken token)
        {
            if (!started)
            {
                started = true;
                Response.StatusCode = StatusCodes.Status200OK;
                Response.ContentType = "text/event-stream; charset=utf-8";
                Response.Headers.CacheControl = "no-cache, no-transform";
                // Traefik and nginx both honour this; without it a proxy may hold the events until the
                // whole turn is over, and the pane would show nothing for a minute and then everything.
                Response.Headers["X-Accel-Buffering"] = "no";
                HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            }

            if (e.Data is AgentStartedData s) streamedConversation = s.ConversationId;

            var data = JsonSerializer.Serialize(e.Data, e.Data.GetType(), StreamJson);
            await Response.WriteAsync($"event: {e.Name}\ndata: {data}\n\n", token);
            await Response.Body.FlushAsync(token);
        }

        try
        {
            var outcome = await conversations.SendAsync(Actor, conversationId, request, IsFa, Emit, ct);
            if (started) return new EmptyResult();

            var body = new
            {
                error = outcome.Status switch
                {
                    AgentSendStatus.NotEntitled => "feature_not_enabled",
                    AgentSendStatus.NotFound => "not_found",
                    _ => "invalid"
                },
                messageEn = outcome.MessageEn,
                messageFa = outcome.MessageFa
            };
            return outcome.Status switch
            {
                AgentSendStatus.NotEntitled => new ObjectResult(body) { StatusCode = StatusCodes.Status403Forbidden },
                AgentSendStatus.NotFound => NotFound(body),
                _ => BadRequest(body)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The browser went away. There is nobody left to tell.
            return new EmptyResult();
        }
        catch (Exception ex) when (started)
        {
            // The orchestrator already turns its own failures into an error event; this is the backstop
            // for one that escaped it, so the stream still ends the way the pane expects.
            logger.LogError(ex, "The assistant stream failed after it had started.");
            try
            {
                await Emit(new AgentStreamEvent(AgentStreamEvents.Error, new AgentErrorData(
                    "Something went wrong on the server while answering.", "هنگام پاسخ‌دادن خطایی در سرور رخ داد.")), ct);
                if (streamedConversation is { } conversation)
                    await Emit(new AgentStreamEvent(AgentStreamEvents.Done, new AgentDoneData(conversation, null)), ct);
            }
            catch (Exception)
            {
                // The connection itself is what failed; the log line above is all that is left to do.
            }
            return new EmptyResult();
        }
    }

    [HttpPost("plans/{id:guid}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AgentApproveRequest? request,
        CancellationToken ct)
    {
        var outcome = await plans.ApproveAsync(Actor, id, request, ct);
        return outcome.Ok ? Json(outcome.Result) : Refusal(outcome.Refusal!);
    }

    [HttpPost("plans/{id:guid}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct) =>
        await plans.RejectAsync(Actor, id, ct) is { } refusal ? Refusal(refusal) : NoContent();

    [HttpGet("approvals")]
    public async Task<IActionResult> Approvals(CancellationToken ct) =>
        Json(await plans.ApprovalsAsync(Actor, ct));

    [HttpPost("approvals/{tool}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgetApproval(string tool, CancellationToken ct)
    {
        await plans.RevokeApprovalAsync(Actor, tool, ct);
        return NoContent();
    }

    /// <summary>
    /// A refused decision as <c>{ error, messageEn, messageFa, status, stepIndex }</c>: 404 for a plan
    /// that is not this person's, 400 for a typed name that did not match (the plan is still pending and
    /// can be approved again), 409 for a plan that is no longer pending or has expired.
    /// </summary>
    private ObjectResult Refusal(AgentPlanRefusal refusal) => new(new
    {
        error = refusal.Code,
        messageEn = refusal.MessageEn,
        messageFa = refusal.MessageFa,
        status = refusal.Status,
        stepIndex = refusal.StepIndex
    })
    {
        StatusCode = refusal.Code switch
        {
            AgentPlanRefusal.NotFoundCode => StatusCodes.Status404NotFound,
            AgentPlanRefusal.ConfirmationMismatchCode => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict
        }
    };
}
