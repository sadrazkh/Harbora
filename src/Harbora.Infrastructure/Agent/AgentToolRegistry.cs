using System.Security.Claims;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// "May this person exercise this capability" — asked of the same authorization policies the
/// controllers' <c>[Authorize(Policy = Capabilities.X)]</c> attributes use.
///
/// <para>
/// An interface rather than <c>IAuthorizationService</c> itself because this project has no ASP.NET
/// Core reference; the web host implements it over <c>IAuthorizationService.AuthorizeAsync(user,
/// capability)</c> (<c>AuthorizationAgentCapabilityCheck</c>). A second evaluator written here over
/// the role matrix would be a second source of truth, and two of those is how a tool ends up offered
/// to somebody the panel's own button refuses.
/// </para>
/// </summary>
public interface IAgentCapabilityCheck
{
    Task<bool> AllowsAsync(ClaimsPrincipal user, string capability, CancellationToken ct);
}

/// <summary>
/// Every <see cref="IAgentTool"/> registered in DI, and which of them one person may use.
///
/// <para>
/// The model is only ever offered the tools whose <see cref="IAgentTool.Capability"/> the user holds;
/// the same check runs again before any tool is validated or executed, because a model can name a
/// tool it was never offered.
/// </para>
/// </summary>
public sealed class AgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _byName;
    private readonly IAgentCapabilityCheck _capabilities;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools, IAgentCapabilityCheck capabilities)
    {
        _capabilities = capabilities;
        _byName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            // Two tools under one name would make a stored plan or a remembered approval ambiguous
            // about which one it meant. That is a registration mistake, so it fails loudly at the
            // first request rather than letting whichever registered last quietly win.
            if (!_byName.TryAdd(tool.Name, tool))
                throw new InvalidOperationException(
                    $"Two agent tools are registered under the name '{tool.Name}' ({_byName[tool.Name].GetType().Name} and {tool.GetType().Name}).");
        }
    }

    /// <summary>Every registered tool, in name order.</summary>
    public IReadOnlyList<IAgentTool> All => _byName.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    /// <summary>The tool by its exact snake_case name, or null.</summary>
    public IAgentTool? Find(string? name) =>
        name is not null && _byName.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>Whether this person may use this tool at all — a null capability means any member.</summary>
    public async Task<bool> AllowsAsync(ClaimsPrincipal user, IAgentTool tool, CancellationToken ct) =>
        tool.Capability is null || await _capabilities.AllowsAsync(user, tool.Capability, ct);

    /// <summary>The tools this person may use, in name order.</summary>
    public async Task<IReadOnlyList<IAgentTool>> AllowedForAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var allowed = new List<IAgentTool>();
        foreach (var tool in All)
            if (await AllowsAsync(user, tool, ct)) allowed.Add(tool);
        return allowed;
    }

    /// <summary>The function the chat-completions <c>tools</c> array carries for this tool.</summary>
    public static AgentToolSpec SpecOf(IAgentTool tool) => new(tool.Name, tool.Description, tool.ParametersSchema);
}
