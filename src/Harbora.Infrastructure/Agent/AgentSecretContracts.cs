using System.Text.RegularExpressions;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// A secret the person gave the panel through the pane's lock button, held so the model never sees it.
///
/// <para>
/// The model is only ever shown the handle — <c>{{secret:&lt;id&gt;}}</c> — inside the person's message,
/// and passes it on unchanged as a tool argument; the tool resolves it on the server and applies the
/// value. So the value never reaches the AI provider, the conversation history or a stored plan.
/// Handles live in memory only, encrypted, for <see cref="AgentSecretHandles.Lifetime"/>, and only the
/// person who made one, in the workspace they made it in, can resolve it. A restart forgets them: a step
/// whose handle is gone is refused ("enter the secret again"), never applied with something else.
/// </para>
/// </summary>
public interface IAgentSecretVault
{
    /// <summary>Holds <paramref name="value"/> and returns its handle. <paramref name="name"/> is the
    /// label the person gave it (for example the env var it is meant for); it is not secret.</summary>
    AgentSecretView Put(Guid workspaceId, Guid userId, string name, string value);

    /// <summary>The value behind <paramref name="handle"/> for this person in this workspace, or null when
    /// it is unknown, expired, or someone else's.</summary>
    string? Resolve(Guid workspaceId, Guid userId, string handle);
}

/// <summary>Body of <c>POST /assistant/secrets</c>.</summary>
public sealed record AgentSecretRequest(string Name, string Value);

/// <summary>What the pane gets back: the handle it inserts into the message, never the value.</summary>
public sealed record AgentSecretView(string Handle, string Name, DateTimeOffset ExpiresAt);

/// <summary>The handle format, in one place, so the vault, the tools and the pane agree.</summary>
public static class AgentSecretHandles
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary><c>{{secret:</c> + 22 URL-safe base64 characters + <c>}}</c>.</summary>
    public static readonly Regex Pattern = new(@"\{\{secret:[A-Za-z0-9_\-]{22}\}\}", RegexOptions.Compiled);

    public static string Format(string id) => "{{secret:" + id + "}}";

    /// <summary>True when the whole value is exactly one handle (surrounding whitespace allowed).</summary>
    public static bool IsHandle(string? value) =>
        value is not null && Pattern.Match(value.Trim()) is { Success: true } m && m.Length == value.Trim().Length;

    /// <summary>What a card or a summary shows in place of a secret.</summary>
    public const string Masked = "••••••";
}
