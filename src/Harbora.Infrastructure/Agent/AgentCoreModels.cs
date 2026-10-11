using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbora.Infrastructure.Assistant;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// Who the agent is working for in this request: the signed-in person, their workspace and their
/// claims. Every tool context, plan and stored row is keyed by these — never by anything the model said.
/// </summary>
/// <param name="IpAddress">The request's address, for the audit rows an executed step writes.</param>
public sealed record AgentActor(Guid WorkspaceId, Guid UserId, ClaimsPrincipal User, string? IpAddress = null);

/// <summary>
/// One server-sent event: <paramref name="Name"/> is one of <see cref="AgentStreamEvents"/>, and
/// <paramref name="Data"/> is serialized camelCase as the event's single JSON object.
/// </summary>
public sealed record AgentStreamEvent(string Name, object Data);

/// <summary>Data of <see cref="AgentStreamEvents.Started"/>.</summary>
public sealed record AgentStartedData(Guid ConversationId);

/// <summary>Data of <see cref="AgentStreamEvents.Tool"/>. <paramref name="State"/> is "running",
/// "done" or "failed"; <paramref name="Kind"/> is <see cref="AgentConsentPolicy.KindLabel"/>.</summary>
public sealed record AgentToolEventData(string Name, string Kind, string State, string? SummaryEn, string? SummaryFa);

/// <summary>Data of <see cref="AgentStreamEvents.Error"/>.</summary>
public sealed record AgentErrorData(string MessageEn, string MessageFa);

/// <summary>Data of <see cref="AgentStreamEvents.Done"/>. <paramref name="RemainingToday"/> is null when
/// the workspace has no daily cap.</summary>
public sealed record AgentDoneData(Guid ConversationId, int? RemainingToday);

/// <summary>One row of <c>GET /assistant/approvals</c>: a tool this person said "don't ask again" for.</summary>
public sealed record AgentApprovalView(string Tool, DateTimeOffset CreatedAt);

/// <summary>Why <see cref="AgentConversationService.SendAsync"/> did not open a stream.</summary>
public enum AgentSendStatus
{
    /// <summary>The stream was opened (<c>started</c> was emitted) and closed with <c>done</c>.</summary>
    Streamed,

    /// <summary>The request itself was unusable — an empty or overlong message.</summary>
    Invalid,

    /// <summary>The workspace is not entitled to the assistant.</summary>
    NotEntitled,

    /// <summary>No such conversation for this person in this workspace.</summary>
    NotFound
}

/// <summary>The outcome of a send. Nothing was emitted unless <see cref="Status"/> is
/// <see cref="AgentSendStatus.Streamed"/>, so the caller is still free to answer with a status code.</summary>
public sealed record AgentSendOutcome(AgentSendStatus Status, string? MessageEn = null, string? MessageFa = null)
{
    public static readonly AgentSendOutcome Streamed = new(AgentSendStatus.Streamed);
}

/// <summary>
/// Why a plan decision was refused. Nothing ran. The HTTP body carries <see cref="MessageEn"/> and
/// <see cref="MessageFa"/> — the same shape as the stream's <c>error</c> event — beside the code.
/// </summary>
/// <param name="Code">"not_found", "expired", "not_pending", "confirmation_mismatch" or
/// "revalidation_refused" (the approval was accepted, but a step was refused when checked again, so
/// the plan failed with nothing run).</param>
/// <param name="Status">The plan's status as it stands, when there is a plan.</param>
/// <param name="StepIndex">The 0-based <see cref="AgentPlanStep.Index"/> of the step the refusal is
/// about, for "confirmation_mismatch" and "revalidation_refused".</param>
public sealed record AgentPlanRefusal(
    string Code, string MessageEn, string MessageFa, string? Status = null, int? StepIndex = null)
{
    public const string NotFoundCode = "not_found";
    public const string ExpiredCode = "expired";
    public const string NotPendingCode = "not_pending";
    public const string ConfirmationMismatchCode = "confirmation_mismatch";
    public const string RevalidationRefusedCode = "revalidation_refused";

    public static AgentPlanRefusal NotFound() =>
        new(NotFoundCode, "There is no such plan.", "چنین برنامه‌ای وجود ندارد.");
}

/// <summary>An approval's outcome: the result of running the plan, or why it was refused.</summary>
public sealed record AgentApproveOutcome(AgentPlanResultView? Result, AgentPlanRefusal? Refusal)
{
    public bool Ok => Result is not null;
}

/// <summary>
/// One validated step of a plan, as stored in <c>AgentPlan.StepsJson</c>: what to run (tool and
/// arguments, exactly as validated) and what the card says about it.
/// </summary>
/// <param name="Index">The step's 0-based position in the plan, in execution order — the key of
/// <see cref="AgentApproveRequest.Confirmations"/> and of every <see cref="AgentPlanStepResultView"/>.
/// Text a person or the model reads calls it "step Index + 1".</param>
/// <param name="ToolCallId">The model's call this step came from; null for steps not proposed by a call.</param>
/// <param name="ConfirmationPhrase">Destructive steps only: what the person must type.</param>
/// <param name="AutoApproved">A remembered safe tool when the plan was made.</param>
public sealed record AgentPlanStep(
    int Index,
    string Tool,
    AgentToolKind Kind,
    string ArgumentsJson,
    string? ToolCallId,
    string TitleEn,
    string TitleFa,
    string? DetailEn,
    string? DetailFa,
    IReadOnlyList<string> WarningsEn,
    IReadOnlyList<string> WarningsFa,
    string? ConfirmationPhrase,
    bool AutoApproved);

/// <summary>
/// The JSON conventions of the agent: camelCase like the rest of the panel's JSON, enums by name, and
/// Persian left readable rather than <c>\u</c>-escaped — a tool result is read by a model, never
/// embedded in HTML, and escaping triples the tokens a Persian name costs.
/// </summary>
public static class AgentJson
{
    /// <summary>The most of one tool result that is ever sent to the model.</summary>
    public const int MaxToolResultLength = 4000;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// What may leave the server as a tool result: redacted first (by value for
    /// <paramref name="knownSecrets"/>, then by shape — <see cref="AssistantRedaction"/>), then cut to
    /// <see cref="MaxToolResultLength"/>. In that order on purpose: truncating first can cut a secret in
    /// half, and half a secret is no longer recognisable by value.
    /// </summary>
    public static string ForModel(string? text, IEnumerable<string>? knownSecrets = null)
    {
        var redacted = AssistantRedaction.Redact(text, knownSecrets).Text;
        if (redacted.Length <= MaxToolResultLength) return redacted;

        var cut = redacted.Length - MaxToolResultLength;
        return redacted[..MaxToolResultLength] + $"… [truncated: {cut} more characters]";
    }
}
