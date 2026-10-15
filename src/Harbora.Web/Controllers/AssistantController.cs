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
/// POSTs and DELETEs carry the antiforgery token in the <c>X-CSRF-TOKEN</c> header (Program.cs), the
/// way the route designer's island sends it.
/// </para>
/// </summary>
[Authorize]
[Route("assistant")]
[RequireFeature(PlatformFeatures.Assistant)]
public sealed class AssistantController(
    AgentConversationService conversations,
    AgentPlanService plans,
    AgentSecretService secrets,
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

    /// <summary>
    /// One page of the list: <c>q</c> searches the titles, <c>skip</c> pages, 30 rows a page — a shorter
    /// page is the last. 400 for a search over 100 characters or a skip that is not a whole number of at
    /// least zero.
    /// </summary>
    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations([FromQuery] string? q, [FromQuery] int skip, CancellationToken ct)
    {
        // "?skip=abc" binds as 0 with an error in the model state; it is refused rather than read as page one.
        if (ModelState.TryGetValue(nameof(skip), out var entry) && entry.Errors.Count > 0)
            return BadRequest(new
            {
                error = "invalid", messageEn = "skip must be a whole number, 0 or more.", messageFa = "skip باید عددی صحیح، ۰ یا بیشتر باشد."
            });

        var page = await conversations.ListAsync(Actor, q, skip, ct);
        return page.Ok
            ? Json(page.Value)
            : BadRequest(new { error = "invalid", messageEn = page.Refusals[0].Message, messageFa = page.Refusals[0].MessageFa });
    }

    /// <summary>
    /// Deletes one conversation with its messages and plans: 204; 404 when it is not this person's here;
    /// 409 <c>busy</c> while a turn is being answered in it or one of its plans is executing.
    /// </summary>
    [HttpDelete("conversations/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConversation(Guid id, CancellationToken ct)
    {
        var outcome = await conversations.DeleteAsync(Actor, id, ct);
        return outcome.Status switch
        {
            AgentDeleteStatus.Deleted => NoContent(),
            AgentDeleteStatus.NotFound => NoSuchConversation(),
            _ => Conflict(new { error = "busy", messageEn = outcome.MessageEn, messageFa = outcome.MessageFa })
        };
    }

    /// <summary>Deletes every conversation of this person here except the busy ones, which are counted.</summary>
    [HttpDelete("conversations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAllConversations(CancellationToken ct) =>
        Json(await conversations.DeleteAllAsync(Actor, ct));

    /// <summary>
    /// Stop: the turn being answered in this conversation ends at its next step, and its stream carries
    /// <c>stopped</c> before <c>done</c>. <c>{ stopped: false }</c> when nothing was running; 404 when the
    /// conversation is not this person's here.
    /// </summary>
    [HttpPost("conversations/{id:guid}/stop")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct) =>
        await conversations.StopAsync(Actor, id, ct) is { } view ? Json(view) : NoSuchConversation();

    [HttpGet("conversations/{id:guid}")]
    public async Task<IActionResult> Conversation(Guid id, CancellationToken ct) =>
        await conversations.GetAsync(Actor, id, IsFa, ct) is { } view ? Json(view) : NoSuchConversation();

    /// <summary>
    /// A new, empty conversation, before its first message — so the pane can remember the id at once and
    /// a reload in the middle of the first answer still knows the conversation. Sending to <c>new</c>
    /// keeps working for a client that does not do this.
    /// </summary>
    [HttpPost("conversations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NewConversation(CancellationToken ct) =>
        await conversations.CreateAsync(Actor, ct) is { } created
            ? Json(created)
            : new ObjectResult(new
            {
                error = "feature_not_enabled",
                messageEn = "The assistant is not enabled for this workspace.",
                messageFa = "دستیار برای این فضای کاری فعال نیست."
            }) { StatusCode = StatusCodes.Status403Forbidden };

    /// <summary>
    /// The lock button: holds a secret in the vault and answers with its handle — never the value. The
    /// pane inserts the handle into the message, the model passes it on unchanged, and a tool resolves it
    /// on the server. 400 for a name or value out of bounds, 403 without the entitlement.
    /// </summary>
    [HttpPost("secrets")]
    [ValidateAntiForgeryToken]
    // A name of at most 100 characters and a value of at most 8,000, in JSON: 32 KB is generous for that
    // and refuses a body nobody's lock button would send before it is read into memory.
    [RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> HoldSecret(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AssistantSecretBody? body, CancellationToken ct)
    {
        var outcome = await secrets.HoldAsync(Actor, body?.ToRequest(), ct);
        if (outcome.Ok) return Json(outcome.View);

        var refusal = new { error = outcome.Code, messageEn = outcome.MessageEn, messageFa = outcome.MessageFa };
        return outcome.Code == "feature_not_enabled"
            ? new ObjectResult(refusal) { StatusCode = StatusCodes.Status403Forbidden }
            : BadRequest(refusal);
    }

    private NotFoundObjectResult NoSuchConversation() => NotFound(new
    {
        error = "not_found",
        messageEn = "There is no such conversation.",
        messageFa = "چنین گفت‌وگویی وجود ندارد."
    });

    /// <summary>
    /// <c>id</c> is a conversation id, or <c>new</c> to start one. 409 <c>turn_in_progress</c> when the
    /// conversation is still answering the previous message. Once the stream has begun, the turn no
    /// longer depends on this request: a reload or a closed tab stops the writing, not the answer.
    /// </summary>
    [HttpPost("conversations/{id}/messages")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(
        string id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AgentSendMessageRequest? request,
        CancellationToken ct)
    {
        Guid? conversationId = null;
        if (!string.Equals(id, "new", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(id, out var parsed)) return NoSuchConversation();
            conversationId = parsed;
        }

        var started = false;
        var listening = true;
        Guid? streamedConversation = null;
        var aborted = HttpContext.RequestAborted;

        // Writes go to the person's connection, on its own token. A reload or a closed tab ends that
        // connection but not the turn: the first write that fails marks the listener gone, nothing more
        // is written, and the orchestrator carries on and saves the answer for when the person comes back.
        async Task Emit(AgentStreamEvent e, CancellationToken _)
        {
            if (!listening) return;
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
            try
            {
                aborted.ThrowIfCancellationRequested();
                await Response.WriteAsync($"event: {e.Name}\ndata: {data}\n\n", aborted);
                await Response.Body.FlushAsync(aborted);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                listening = false;
            }
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
                    AgentSendStatus.Busy => "turn_in_progress",
                    _ => "invalid"
                },
                messageEn = outcome.MessageEn,
                messageFa = outcome.MessageFa
            };
            return outcome.Status switch
            {
                AgentSendStatus.NotEntitled => new ObjectResult(body) { StatusCode = StatusCodes.Status403Forbidden },
                AgentSendStatus.NotFound => NotFound(body),
                AgentSendStatus.Busy => Conflict(body),
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
    /// A refused decision as <c>{ error, messageEn, messageFa, status, stepIndex }</c> — messageEn and
    /// messageFa being the same pair the stream's <c>error</c> event carries: 404 for a plan that is not
    /// this person's, 400 for a typed name that did not match (the plan is still pending and can be
    /// approved again), 409 for a plan that is no longer pending, has expired, or whose step was refused
    /// when checked again (<c>revalidation_refused</c>: nothing ran, and the plan has failed).
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

/// <summary>
/// The body of <c>POST /assistant/secrets</c> as the controller binds it — deliberately not the contract
/// record <see cref="AgentSecretRequest"/>, whose compiler-generated <c>ToString</c> prints every property.
/// A bound model can end up in a log line, a debugger watch or an exception message; this one says the
/// value is there without saying what it is.
/// </summary>
public sealed class AssistantSecretBody
{
    public string? Name { get; set; }
    public string? Value { get; set; }

    public AgentSecretRequest ToRequest() => new(Name!, Value!);

    public override string ToString() => $"AssistantSecretBody {{ Name = {Name}, Value = {AgentSecretHandles.Masked} }}";
}
