namespace Harbora.Infrastructure.Agent;

/// <summary>
/// Platform settings the in-panel agent adds on top of the deployment explainer's
/// <c>assistant.*</c> keys (provider, base URL, encrypted API key and the enabled flag are shared).
/// </summary>
public static class AgentSettingKeys
{
    /// <summary>The model the agent asks for. Blank falls back to <c>assistant.model</c>.</summary>
    public const string Model = "assistant.agent_model";

    /// <summary>
    /// Comma-separated model ids tried in order when the first one fails — sent as OpenRouter's
    /// <c>models</c> array, so the provider does the falling back.
    /// </summary>
    public const string FallbackModels = "assistant.agent_fallback_models";

    /// <summary>
    /// How many model calls one workspace may make per UTC day. Blank means
    /// <see cref="DefaultDailyRequestLimit"/>; 0 means unlimited.
    /// </summary>
    public const string DailyRequestLimit = "assistant.agent_daily_request_limit";

    public const int DefaultDailyRequestLimit = 300;

    /// <summary>
    /// Conversations not used for this many days are deleted, once a day, with their messages and plans.
    /// Blank or 0 keeps them for ever (the default). A conversation still answering, or with a plan still
    /// executing, is never deleted. The audit trail of what the assistant did is not touched.
    /// </summary>
    public const string ConversationRetentionDays = "assistant.conversation_retention_days";
}
