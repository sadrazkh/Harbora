using System.Text.Json;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// The base every tool that changes something derives from. It adds nothing to the contract except one
/// way of turning a phase-0 service's <see cref="ServiceRefusal"/>s into what the agent reads: two tools
/// refusing the same thing in different words is how the model learns to retry the wrong way, and a
/// Persian user seeing English for one tool and Persian for the next is the same defect in the pane.
/// </summary>
public abstract class AgentWriteTool : IAgentTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string ParametersSchema { get; }
    public abstract AgentToolKind Kind { get; }
    public abstract string? Capability { get; }

    public abstract Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct);

    public abstract Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct);

    /// <summary>A validation refused for these reasons — each on its own line, English and Persian kept apart.</summary>
    protected static AgentValidation Refused(IReadOnlyList<ServiceRefusal> refusals) =>
        AgentValidation.Refused(English(refusals), Persian(refusals));

    /// <summary>An execution refused for these reasons. Nothing was changed: that is what a refusal from a
    /// phase-0 service means.</summary>
    protected static AgentToolResult Failed(IReadOnlyList<ServiceRefusal> refusals) =>
        AgentToolResult.Failed(English(refusals), Persian(refusals));

    private static string English(IReadOnlyList<ServiceRefusal> refusals) =>
        refusals.Count == 0 ? "Refused." : string.Join("\n", refusals.Select(r => r.Message));

    private static string Persian(IReadOnlyList<ServiceRefusal> refusals) =>
        refusals.Count == 0 ? "پذیرفته نشد." : string.Join("\n", refusals.Select(r => r.MessageFa));
}
