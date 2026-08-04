using Harbora.Domain.Apps;
using Harbora.Domain.Services;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Web.ViewModels;

/// <summary>One row on the projects list: enough to answer "is anything wrong here?" without opening it.</summary>
public sealed class ProjectSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public int Environments { get; set; }
    public int Services { get; set; }
    public int Databases { get; set; }

    /// <summary>Services that are crashed or whose last deploy failed — the only count worth a colour.</summary>
    public int Unhealthy { get; set; }
}

public sealed class ProjectDetailsViewModel
{
    public Project Project { get; set; } = null!;
    public List<Environment> Environments { get; set; } = [];

    /// <summary>The environment being viewed; null only if a project somehow has none.</summary>
    public Environment? Selected { get; set; }

    public List<App> Services { get; set; } = [];
    public List<ManagedService> Databases { get; set; } = [];

    /// <summary>
    /// Which database container names each service connects to, by app id. Computed rather than read
    /// off the page because the variables that hold a connection string are encrypted — matching the
    /// stored value against a hostname compares ciphertext and always answers "no connection".
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> Connections { get; set; } =
        new Dictionary<Guid, IReadOnlyList<string>>();
}

/// <summary>
/// The private network of one environment: what is attached, and the address each thing answers on.
/// </summary>
public class NetworksViewModel
{
    public List<Environment> Environments { get; set; } = [];
    public Environment? Selected { get; set; }

    /// <summary>The Docker network name, derived from the same rule the deploy engine uses.</summary>
    public string? NetworkName { get; set; }

    public List<App> Services { get; set; } = [];
    public List<ManagedService> Databases { get; set; } = [];

    /// <summary>Which databases each service holds a connection string for.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> Connections { get; set; } =
        new Dictionary<Guid, IReadOnlyList<string>>();

    /// <summary>The same information as a diagram.</summary>
    public Harbora.Infrastructure.Networking.ArchitecturePicture? Picture { get; set; }
}

/// <summary>What moving a service to another environment would cost.</summary>
public class MoveServiceViewModel
{
    public App Service { get; set; } = null!;
    public Environment? Current { get; set; }
    public Environment Target { get; set; } = null!;
    public Harbora.Infrastructure.Networking.WiringVerdict Verdict { get; set; } = null!;
}

/// <summary>What a customer sees about their AI service.</summary>
public class AiOverviewViewModel
{
    public Harbora.Domain.Ai.AiSubscription? Subscription { get; set; }
    public Harbora.Domain.Ai.AiPlan? Plan { get; set; }

    public IReadOnlyList<Harbora.Domain.Ai.AiModel> Models { get; set; } = [];
    public List<Harbora.Domain.Ai.AiUserApiKey> Keys { get; set; } = [];

    /// <summary>Metadata only — never prompts. See AiUsageRecord.</summary>
    public List<Harbora.Domain.Ai.AiUsageRecord> Recent { get; set; } = [];

    public int RequestsThisPeriod { get; set; }
    public string EndpointUrl { get; set; } = "/v1";
}

/// <summary>Everything the AI administration page shows.</summary>
public class AiAdminViewModel
{
    public List<Harbora.Domain.Ai.AiProvider> Providers { get; set; } = [];
    public List<Harbora.Domain.Ai.AiModel> Models { get; set; } = [];
    public List<Harbora.Domain.Ai.AiPlan> Plans { get; set; } = [];

    /// <summary>Recent failures, so routing trouble is visible without reading logs.</summary>
    public List<Harbora.Domain.Ai.AiUsageRecord> RecentFailures { get; set; } = [];

    public decimal SpendLast30Days { get; set; }
    public decimal ChargedLast30Days { get; set; }
    public int RequestsLast30Days { get; set; }
}
