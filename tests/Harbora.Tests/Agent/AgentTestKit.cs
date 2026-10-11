using System.Security.Claims;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Assistant;
using Harbora.Infrastructure.Features;
using Harbora.Infrastructure.Security;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbora.Tests.Agent;

/// <summary>
/// A scripted model. Each <see cref="IAgentChatClient.CompleteAsync"/> returns the next queued answer
/// and records exactly what it was sent, so a test can assert on what reached "the model" — including
/// that something did not. Never touches a network.
/// </summary>
public sealed class ScriptedAgentChatClient : IAgentChatClient
{
    private readonly Queue<AgentChatResponse> _script = new();
    private readonly List<AgentChatRequest> _requests = [];
    private int _ids;

    public AssistantUnavailable? Unavailable { get; set; }
    public string? Model { get; set; } = "test/agent-model:free";

    public IReadOnlyList<AgentChatRequest> Requests { get { lock (_requests) return _requests.ToList(); } }

    public Task<AssistantUnavailable?> GetAvailabilityAsync(CancellationToken ct) => Task.FromResult(Unavailable);

    public Task<string?> GetModelAsync(CancellationToken ct) => Task.FromResult(Model);

    public Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, CancellationToken ct)
    {
        lock (_requests)
        {
            _requests.Add(request with { Messages = request.Messages.ToList(), Tools = request.Tools.ToList() });
            return Task.FromResult(_script.Count > 0
                ? _script.Dequeue()
                : AgentChatResponse.Failed("The test script ran out of answers.", "اسکریپت آزمون تمام شد."));
        }
    }

    public ScriptedAgentChatClient Then(AgentChatResponse response)
    {
        lock (_requests) _script.Enqueue(response);
        return this;
    }

    public ScriptedAgentChatClient ThenText(string text) =>
        Then(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, text), Model,
            new AgentChatUsage(100, 20), "stop", null, null));

    public ScriptedAgentChatClient ThenCalls(params AgentToolCall[] calls) =>
        Then(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, null, calls), Model,
            new AgentChatUsage(100, 10), "tool_calls", null, null));

    public ScriptedAgentChatClient ThenCall(string name, string argumentsJson = "{}") => ThenCalls(Call(name, argumentsJson));

    public AgentToolCall Call(string name, string argumentsJson = "{}") =>
        new($"call_{Interlocked.Increment(ref _ids)}", name, argumentsJson);

    /// <summary>Everything every request carried, as one string — for "this never reached the model".</summary>
    public string EverythingSent() => string.Join("\n", Requests.Select(r => JsonSerializer.Serialize(r)));

    public void Clear()
    {
        lock (_requests)
        {
            _script.Clear();
            _requests.Clear();
        }
    }
}

/// <summary>
/// A tool of any kind whose verdicts and results a test decides. Counts how often it was validated and
/// executed, which is what "nothing ran" is asserted against.
/// </summary>
public sealed class FakeTool(string name, AgentToolKind kind, string? capability = null) : IAgentTool
{
    private int _validations;
    private int _executions;
    private readonly List<string> _executedArgs = [];

    public string Name => name;
    public string Description => $"Test tool {name}.";
    public string ParametersSchema => """{ "type": "object", "properties": { "name": { "type": "string" } } }""";
    public AgentToolKind Kind { get; set; } = kind;
    public string? Capability { get; set; } = capability;

    public Func<JsonElement, AgentToolContext, AgentPlanScope, AgentValidation>? OnValidate { get; set; }
    public Func<JsonElement, AgentToolContext, AgentToolResult>? OnExecute { get; set; }

    public int Validations => _validations;
    public int Executions => _executions;
    public IReadOnlyList<string> ExecutedArgs { get { lock (_executedArgs) return _executedArgs.ToList(); } }

    public Task<AgentValidation> ValidateAsync(JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        Interlocked.Increment(ref _validations);
        return Task.FromResult(OnValidate?.Invoke(args, context, scope) ?? new AgentValidation(true,
            TitleEn: $"Run {name}", TitleFa: $"اجرای {name}",
            ConfirmationPhrase: Kind == AgentToolKind.Destructive ? "shop-db" : null));
    }

    public Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _executions);
        lock (_executedArgs) _executedArgs.Add(args.GetRawText());
        return Task.FromResult(OnExecute?.Invoke(args, context)
                               ?? new AgentToolResult(true, $$"""{"ran":"{{name}}"}""", $"{name} done", $"{name} انجام شد"));
    }
}

public sealed class MutableClock(DateTimeOffset now) : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

public sealed class RecordingAuditLogger : IAuditLogger
{
    public sealed record Entry(string Action, string? TargetId, string? MetadataJson, Guid? WorkspaceId);

    private readonly List<Entry> _entries = [];
    public IReadOnlyList<Entry> Entries { get { lock (_entries) return _entries.ToList(); } }

    public Task LogAsync(string action, string? targetType = null, string? targetId = null, string? ipAddress = null,
        string? actorEmailOverride = null, Guid? userIdOverride = null, string? metadataJson = null,
        Guid? workspaceId = null, CancellationToken ct = default)
    {
        lock (_entries) _entries.Add(new Entry(action, targetId, metadataJson, workspaceId));
        return Task.CompletedTask;
    }
}

/// <summary>Stores secrets with a visible prefix, so a test can tell ciphertext from plaintext.</summary>
public sealed class PrefixProtector : ISecretProtector
{
    public string Protect(string plaintext) => "enc:" + plaintext;

    public string Unprotect(string ciphertext) =>
        ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : throw new FormatException("not ours");

    public byte[] DeriveKey(string purpose) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test:" + purpose));
}

/// <summary>
/// The agent core assembled over EF InMemory, one workspace and one person — the real registry,
/// consent policy, meter, snapshot, plan engine and loop, with the capability question asked of the
/// panel's real authorization policies. Only the model is scripted.
/// </summary>
public sealed class AgentHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static readonly IAuthorizationService Authorization = BuildAuthorization();

    public string DatabaseName { get; }
    public Guid WorkspaceId { get; }
    public Guid UserId { get; } = Guid.CreateVersion7();
    public Guid ProjectId { get; } = Guid.CreateVersion7();
    public Guid EnvironmentId { get; } = Guid.CreateVersion7();

    public HarboraDbContext Db { get; }
    public MutableClock Clock { get; }
    public ScriptedAgentChatClient Chat { get; } = new();
    public RecordingAuditLogger Audit { get; } = new();
    public PrefixProtector Protector { get; } = new();
    public List<IAgentTool> Tools { get; }
    public SystemRole Role { get; }
    public ClaimsPrincipal Principal { get; }
    public AgentActor Actor { get; }

    public AgentHarness(
        IEnumerable<IAgentTool>? tools = null, SystemRole role = SystemRole.Owner, bool entitled = true,
        string? databaseName = null, Guid? workspaceId = null, MutableClock? clock = null, bool scopedToProjects = false)
    {
        DatabaseName = databaseName ?? "agent-" + Guid.NewGuid();
        WorkspaceId = workspaceId ?? Guid.CreateVersion7();
        Role = role;
        Clock = clock ?? new MutableClock(Start);

        Db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(DatabaseName).Options,
            new FixedWorkspaceScope(WorkspaceId));

        var workspaceRole = role switch
        {
            SystemRole.Owner or SystemRole.Admin => WorkspaceRole.Admin,
            SystemRole.Operator => WorkspaceRole.Operator,
            SystemRole.Viewer => WorkspaceRole.Viewer,
            _ => WorkspaceRole.Member
        };

        if (!Db.Workspaces.IgnoreQueryFilters().Any(w => w.Id == WorkspaceId))
            Db.Workspaces.Add(new Workspace { Id = WorkspaceId, Name = "Acme", Slug = "acme-" + WorkspaceId.ToString("N")[..8] });
        Db.Users.Add(new User { Id = UserId, Email = $"{UserId:N}@example.com", DisplayName = "Person", Role = role });
        Db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = WorkspaceId, UserId = UserId, Role = workspaceRole, ScopedToProjects = scopedToProjects
        });
        Db.Projects.Add(new Harbora.Domain.Projects.Project { Id = ProjectId, WorkspaceId = WorkspaceId, Name = "Shop", Slug = "shop-" + ProjectId.ToString("N")[..6] });
        Db.Environments.Add(new Harbora.Domain.Projects.Environment
        {
            Id = EnvironmentId, WorkspaceId = WorkspaceId, ProjectId = ProjectId, Name = "Production", Slug = "production", IsDefault = true
        });
        if (entitled && !Db.FeatureGrants.Any(g => g.TargetId == WorkspaceId && g.FeatureKey == PlatformFeatures.Assistant))
            Db.FeatureGrants.Add(new FeatureGrant
            {
                Scope = FeatureScope.Workspace, TargetId = WorkspaceId, FeatureKey = PlatformFeatures.Assistant, State = FeatureState.Enabled
            });
        Db.SaveChanges();

        Principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
            new Claim(ClaimTypes.Email, $"{UserId:N}@example.com"),
            new Claim(ClaimTypes.Role, role.ToString()),
            new Claim(HarboraClaims.Workspace, WorkspaceId.ToString()),
            new Claim(HarboraClaims.WorkspaceRole, workspaceRole.ToString())
        ], authenticationType: "test"));
        Actor = new AgentActor(WorkspaceId, UserId, Principal, "203.0.113.9");

        Tools = tools?.ToList() ?? [];
    }

    public string ProjectSlug => "shop-" + ProjectId.ToString("N")[..6];

    private sealed class Caller(Guid userId, Guid workspaceId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public string? Email => "person@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId => workspaceId;
    }

    public ProjectAccessService Access => new(Db, new Caller(UserId, WorkspaceId));
    public AgentResourceResolver Resolver => new(Db, Access);
    public AgentUsageMeter Meter => new(Db, Clock);
    public IAgentCapabilityCheck Capabilities { get; } = new AuthorizationAgentCapabilityCheck(Authorization);
    public AgentToolRegistry Registry => new(Tools, Capabilities);
    public AgentPlanService Plans => new(Db, Registry, Audit, Clock, NullLogger<AgentPlanService>.Instance);

    public AgentConversationService Service => new(
        Db, Chat, Registry, Plans, Meter, new AgentWorkspaceSnapshot(Db, Resolver, Access),
        new FeatureGate(Db), Capabilities, Clock, NullLogger<AgentConversationService>.Instance);

    /// <summary>Sends one message and collects every event, in order.</summary>
    public async Task<(AgentSendOutcome Outcome, List<AgentStreamEvent> Events)> SendAsync(
        string text, Guid? conversationId = null, AgentPageContext? page = null, bool persian = false)
    {
        var events = new List<AgentStreamEvent>();
        var outcome = await Service.SendAsync(Actor, conversationId, new AgentSendMessageRequest(text, page), persian,
            (e, _) => { events.Add(e); return Task.CompletedTask; }, CancellationToken.None);
        return (outcome, events);
    }

    /// <summary>A second person, or the same database seen from another workspace, sharing this store.</summary>
    public AgentHarness Another(SystemRole role = SystemRole.Owner, bool sameWorkspace = true) =>
        new(Tools, role, entitled: true, databaseName: DatabaseName, workspaceId: sameWorkspace ? WorkspaceId : null, clock: Clock);

    public void SetDailyLimit(string value)
    {
        Db.Settings.Add(new Harbora.Domain.Settings.Setting { Key = AgentSettingKeys.DailyRequestLimit, Value = value });
        Db.SaveChanges();
    }

    public void Remember(string tool)
    {
        Db.AgentToolApprovals.Add(new Harbora.Domain.Agent.AgentToolApproval { WorkspaceId = WorkspaceId, UserId = UserId, ToolName = tool });
        Db.SaveChanges();
    }

    private static IAuthorizationService BuildAuthorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCapabilityAuthorization();
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    public void Dispose() => Db.Dispose();
}

public static class AgentEvents
{
    public static IEnumerable<string> Names(this IEnumerable<AgentStreamEvent> events) => events.Select(e => e.Name);

    public static T Single<T>(this IEnumerable<AgentStreamEvent> events, string name) =>
        (T)events.Single(e => e.Name == name).Data;
}
