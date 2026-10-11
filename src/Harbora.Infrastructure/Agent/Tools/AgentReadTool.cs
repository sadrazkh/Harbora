using System.Text.Json;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// Reading a tool call's arguments without trusting them: a weak model sends a number as a string, a
/// string as a number, or nothing at all, and none of that may become an exception.
/// </summary>
public static class AgentArgs
{
    /// <summary>A string property, trimmed; null when absent, blank or not a string/number.</summary>
    public static string? String(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>An integer property, accepting "3" as well as 3; null when absent or not a whole number.</summary>
    public static int? Int(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    /// <summary>A boolean property, accepting "true"/"false" too; null when absent.</summary>
    public static bool? Bool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}

/// <summary>
/// The shape every read tool shares: kind <see cref="AgentToolKind.Read"/>, usable by any member (what
/// a person may see is decided per resource by <see cref="AgentResourceResolver"/>, the way the list
/// pages decide it), and never part of a plan — so there is nothing to validate.
/// </summary>
public abstract class AgentReadTool : IAgentTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string ParametersSchema { get; }

    public AgentToolKind Kind => AgentToolKind.Read;

    public virtual string? Capability => null;

    /// <summary>Reads are executed, never planned; the orchestrator does not call this for them.</summary>
    public Task<AgentValidation> ValidateAsync(JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct) =>
        Task.FromResult(new AgentValidation(true, TitleEn: Name, TitleFa: Name));

    public abstract Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct);

    /// <summary>A successful read: the payload as the model's JSON, and a line for the person.</summary>
    protected static AgentToolResult Found(object payload, string summaryEn, string summaryFa) =>
        new(true, AgentJson.Serialize(payload), summaryEn, summaryFa);

    /// <summary>A read that found nothing — or nothing this person may see, which reads the same.</summary>
    protected static AgentToolResult NotFound(string error, string errorFa) => AgentToolResult.Failed(error, errorFa);

    /// <summary>Text cut to <paramref name="max"/> characters, for a free-text field that could be long.</summary>
    protected static string? Clip(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + "…";
}
