namespace Harbora.Infrastructure.Assistant;

/// <summary>How the assistant is configured. Empty is the normal state: off, and calling nothing.</summary>
/// <param name="Enabled">The flag. Off by default, and off is a complete answer.</param>
/// <param name="Provider">Which API shape to speak — see <see cref="AssistantProviders"/>.</param>
/// <param name="Model">The model name, as that provider spells it.</param>
/// <param name="ApiKey">Decrypted at the point of use, never rendered.</param>
/// <param name="BaseUrl">Overridden for a gateway or a self-hosted endpoint; blank means the default.</param>
public sealed record AssistantConfig(
    bool Enabled,
    string? Provider,
    string? Model,
    string? ApiKey,
    string? BaseUrl);

/// <summary>The provider names the panel understands.</summary>
public static class AssistantProviders
{
    /// <summary>Anthropic's Messages API.</summary>
    public const string Anthropic = "anthropic";

    /// <summary>Anything speaking OpenAI's chat-completions shape — OpenAI, a gateway, Ollama.</summary>
    public const string OpenAiCompatible = "openai";

    public static readonly IReadOnlyList<string> All = [Anthropic, OpenAiCompatible];

    public static bool IsKnown(string? provider) =>
        provider is not null && All.Contains(provider.Trim().ToLowerInvariant());
}

/// <summary>Setting keys for the assistant. Only the key is stored encrypted.</summary>
public static class AssistantSettingKeys
{
    public const string Enabled = "assistant.enabled";
    public const string Provider = "assistant.provider";
    public const string Model = "assistant.model";
    public const string ApiKey = "assistant.api_key";
    public const string BaseUrl = "assistant.base_url";
}

/// <summary>Why the assistant is not available, in words a person can act on.</summary>
public sealed record AssistantUnavailable(string Reason);

/// <summary>
/// Whether the assistant may be offered at all.
///
/// Separated from the calling code because "is it on" is asked in three places — the button, the
/// endpoint behind it, and the settings screen — and three copies of a condition is how a feature
/// ends up disabled in the UI and reachable by POST. A missing piece is never assumed: an assistant
/// with no key configured must refuse, not try and fail with somebody else's error message.
/// </summary>
public static class AssistantAvailability
{
    /// <summary>Null when it can be used; otherwise the reason it cannot.</summary>
    public static AssistantUnavailable? Check(AssistantConfig config)
    {
        if (!config.Enabled)
            return new AssistantUnavailable("The assistant is turned off.");

        if (!AssistantProviders.IsKnown(config.Provider))
            return new AssistantUnavailable("No AI provider is configured.");

        if (string.IsNullOrWhiteSpace(config.Model))
            return new AssistantUnavailable("No model is configured.");

        // A self-hosted endpoint on this machine needs no key; anything reached over the internet
        // does, and sending an unauthenticated request there only produces a confusing 401.
        if (string.IsNullOrWhiteSpace(config.ApiKey) && !IsLocal(config.BaseUrl))
            return new AssistantUnavailable("No API key is configured for the AI provider.");

        return null;
    }

    public static bool IsAvailable(AssistantConfig config) => Check(config) is null;

    /// <summary>
    /// Whether the endpoint is on this machine. Only then is a missing key acceptable — and this is
    /// deliberately narrow: "localhost-ish" hostnames elsewhere on the internet are a real thing.
    /// </summary>
    private static bool IsLocal(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)) return false;

        return uri.IsLoopback
            || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }
}
