namespace Harbora.Infrastructure.Agent;

/// <summary>Who wrote a message in a conversation with the model — OpenAI chat-completions roles.</summary>
public enum AgentRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>
/// One function call the model asked for. <paramref name="ArgumentsJson"/> is the model's raw JSON
/// text, unparsed: a weak model can send something that is not valid JSON, and that has to reach the
/// tool layer as a refusal the model can correct, not as an exception in the client.
/// </summary>
public sealed record AgentToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// One message in the conversation sent to the model. An assistant message may carry
/// <paramref name="ToolCalls"/> and no text; a tool message answers one call by its
/// <paramref name="ToolCallId"/>.
/// </summary>
public sealed record AgentChatMessage(
    AgentRole Role,
    string? Content,
    IReadOnlyList<AgentToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>
/// A function the model may call, in the shape the chat-completions <c>tools</c> array wants.
/// <paramref name="ParametersJsonSchema"/> is a JSON Schema object, as text.
/// </summary>
public sealed record AgentToolSpec(string Name, string Description, string ParametersJsonSchema);

public sealed record AgentChatRequest(
    IReadOnlyList<AgentChatMessage> Messages,
    IReadOnlyList<AgentToolSpec> Tools,
    int MaxOutputTokens = 2048);

public sealed record AgentChatUsage(long InputTokens, long OutputTokens);

/// <summary>
/// The model's answer, or why there is none. A failure is a value, never an exception: the caller
/// renders <paramref name="Error"/>/<paramref name="ErrorFa"/> to a person.
/// </summary>
/// <param name="Model">The model that actually answered — with fallbacks configured it can differ
/// from the one asked for.</param>
/// <param name="FinishReason">The provider's finish_reason, e.g. "stop", "tool_calls", "length".</param>
public sealed record AgentChatResponse(
    bool Ok,
    AgentChatMessage? Message,
    string? Model,
    AgentChatUsage? Usage,
    string? FinishReason,
    string? Error,
    string? ErrorFa)
{
    public static AgentChatResponse Failed(string error, string errorFa) =>
        new(false, null, null, null, null, error, errorFa);
}

/// <summary>
/// A multi-turn, tool-capable chat client for the in-panel agent, speaking OpenAI's chat-completions
/// shape (OpenRouter, OpenAI, or any compatible gateway). Configuration comes from the same platform
/// settings as the deployment explainer (<c>assistant.*</c>), plus <see cref="AgentSettingKeys"/>.
/// </summary>
public interface IAgentChatClient
{
    /// <summary>Null when the agent can be used; otherwise why not, in both languages.</summary>
    Task<Assistant.AssistantUnavailable?> GetAvailabilityAsync(CancellationToken ct);

    /// <summary>The model the next call will ask for first, for display. Null when unconfigured.</summary>
    Task<string?> GetModelAsync(CancellationToken ct);

    /// <summary>One chat-completions round trip. Never throws for provider or network failures.</summary>
    Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, CancellationToken ct);
}
