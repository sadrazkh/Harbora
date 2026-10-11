using System.Security.Claims;
using System.Text.Json;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// How much consent a tool needs. This is the whole confirmation policy, decided by the tool's author,
/// never by the model.
/// </summary>
public enum AgentToolKind
{
    /// <summary>Reads only. Runs as soon as the model asks.</summary>
    Read,

    /// <summary>
    /// Changes something, additively or reversibly (create a database, set an env var, deploy). Shown
    /// in a plan the first time; the user may tick "don't ask again" for this tool, after which it runs
    /// without a card — still validated, still audited, still reported.
    /// </summary>
    SafeWrite,

    /// <summary>Can break a running app or cost money (rollback, detach, resize, stop). Always confirmed.</summary>
    DangerousWrite,

    /// <summary>Deletes something. Always confirmed by typing the resource's name.</summary>
    Destructive
}

/// <summary>Where the user was when they asked — so "restart this app" has a referent.</summary>
public sealed record AgentPageContext(
    string? Path,
    Guid? AppId,
    Guid? ServiceId,
    Guid? ProjectId,
    Guid? EnvironmentId);

/// <summary>
/// Who a tool acts for. Tools run inside the user's own HTTP request, so the workspace query filter,
/// <c>ICurrentUser</c> and project access all apply exactly as they do for a click in the panel.
/// </summary>
public sealed record AgentToolContext(
    Guid WorkspaceId,
    Guid UserId,
    ClaimsPrincipal User,
    Guid ConversationId,
    AgentPageContext? Page);

/// <summary>A resource an earlier step of the same plan will create.</summary>
/// <param name="Kind">One of <see cref="AgentResourceKinds"/>.</param>
/// <param name="Name">The slug the step will create it under.</param>
public sealed record AgentPlannedResource(string Kind, string Name, string? Project = null, string? Environment = null);

public static class AgentResourceKinds
{
    public const string Project = "project";
    public const string Environment = "environment";
    public const string App = "app";
    public const string Database = "database";
}

/// <summary>
/// What the earlier steps of the plan being assembled will create. A later step may name one of these
/// although it does not exist yet ("create shop-db, then attach shop-db to shop"); its validation
/// checks the name against this list instead of the database, and the real check happens when the
/// step executes.
/// </summary>
public sealed class AgentPlanScope
{
    private readonly List<AgentPlannedResource> _planned = [];

    public IReadOnlyList<AgentPlannedResource> Planned => _planned;

    public void Add(AgentPlannedResource resource) => _planned.Add(resource);

    public bool Plans(string kind, string name) =>
        _planned.Any(p => p.Kind == kind && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The verdict on one proposed step, with no side effects taken. On success it carries the words the
/// plan card shows; on failure, the reason goes back to the model so it can correct the call.
/// </summary>
/// <param name="ConfirmationPhrase">For <see cref="AgentToolKind.Destructive"/> tools: the exact text
/// (the resource's name) the user must type before the step can run.</param>
/// <param name="Creates">What this step will create, added to the <see cref="AgentPlanScope"/> for the
/// steps after it.</param>
public sealed record AgentValidation(
    bool Ok,
    string? Error = null,
    string? ErrorFa = null,
    string? TitleEn = null,
    string? TitleFa = null,
    string? DetailEn = null,
    string? DetailFa = null,
    IReadOnlyList<string>? WarningsEn = null,
    IReadOnlyList<string>? WarningsFa = null,
    string? ConfirmationPhrase = null,
    AgentPlannedResource? Creates = null)
{
    public static AgentValidation Refused(string error, string errorFa) => new(false, error, errorFa);
}

/// <summary>A link the panel shows next to a result — "open the database".</summary>
public sealed record AgentResourceLink(string Kind, Guid Id, string Label, string Url);

/// <summary>
/// What a tool did. <paramref name="ResultJson"/> goes back to the model and must never contain a
/// secret value; the summaries and links go to the person.
/// </summary>
public sealed record AgentToolResult(
    bool Ok,
    string ResultJson,
    string? SummaryEn = null,
    string? SummaryFa = null,
    string? Error = null,
    string? ErrorFa = null,
    IReadOnlyList<AgentResourceLink>? Links = null)
{
    public static AgentToolResult Failed(string error, string errorFa) =>
        new(false, JsonSerializer.Serialize(new { error }), null, null, error, errorFa);
}

/// <summary>
/// One function the agent can call. Implementations are registered in DI as <c>IAgentTool</c>; the
/// registry turns each into an <see cref="AgentToolSpec"/>.
///
/// <para>
/// Read tools are only ever executed. Write tools are validated when the model proposes them and
/// executed only after the plan is approved (or auto-approved, for a remembered
/// <see cref="AgentToolKind.SafeWrite"/>); <see cref="ExecuteAsync"/> must re-check everything
/// <see cref="ValidateAsync"/> checked, because the world may have changed in between.
/// </para>
/// </summary>
public interface IAgentTool
{
    /// <summary>snake_case, stable: it is stored in plans and in "don't ask again" approvals.</summary>
    string Name { get; }

    /// <summary>English, written for the model: what it does, when to use it, what it returns.</summary>
    string Description { get; }

    /// <summary>A JSON Schema object describing the arguments, as text.</summary>
    string ParametersSchema { get; }

    AgentToolKind Kind { get; }

    /// <summary>The <c>Capabilities</c> value the user must hold; checked by the agent before the tool
    /// is offered, validated or executed. Null for tools any member may use.</summary>
    string? Capability { get; }

    Task<AgentValidation> ValidateAsync(JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct);

    Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct);
}
