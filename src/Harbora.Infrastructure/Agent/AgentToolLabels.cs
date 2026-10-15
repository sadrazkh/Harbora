namespace Harbora.Infrastructure.Agent;

/// <summary>A tool's name for people: what the pane shows instead of its identifier, and its icon's group.</summary>
/// <param name="En">An activity, in the present — "Reading the deployment's logs" — so the same words
/// serve the running line ("…") and the finished one.</param>
/// <param name="Category">One of <see cref="AgentToolLabels.Categories"/>.</param>
public sealed record AgentToolLabel(string En, string Fa, string Category);

/// <summary>
/// The names people read for the agent's tools. <c>get_deployment</c> is the model's word; "Reading the
/// deployment's logs" / «خواندن لاگ‌های استقرار» is the person's. Every registered tool has an entry — a
/// test holds the registry to it — and a tool without one falls back to its identifier under
/// <see cref="Categories.Other"/> rather than to nothing.
/// </summary>
public static class AgentToolLabels
{
    /// <summary>The icon groups. The pane maps each to one icon; a value it does not know gets the generic one.</summary>
    public static class Categories
    {
        public const string App = "app";
        public const string Deployment = "deployment";
        public const string Database = "database";
        public const string Project = "project";
        public const string Domain = "domain";
        public const string Volume = "volume";
        public const string Env = "env";
        public const string Catalog = "catalog";
        public const string Usage = "usage";
        public const string Resources = "resources";
        public const string Other = "other";
    }

    /// <summary>Tool name → label. Keyed by <see cref="IAgentTool.Name"/>, ordinal.</summary>
    public static IReadOnlyDictionary<string, AgentToolLabel> All { get; } =
        new Dictionary<string, AgentToolLabel>(StringComparer.Ordinal);

    public static AgentToolLabel For(string toolName) =>
        All.TryGetValue(toolName, out var label) ? label : new AgentToolLabel(toolName, toolName, Categories.Other);
}
