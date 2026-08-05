using Harbora.Application.Abstractions;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Tenancy;

namespace Harbora.Web.ViewModels;

public sealed class PlansPageViewModel
{
    public WorkspaceUsage Usage { get; set; } = null!;
    public bool IsProvider { get; set; }
    public List<Plan> Plans { get; set; } = new();
    public List<InstanceSize> Sizes { get; set; } = new();

    /// <summary>
    /// Tenants already past a limit. Carried on the model rather than in ViewBag: it is a typed
    /// list the page renders per resource, and an untyped one is how a new resource gets added to
    /// the rule and quietly never appears on the page.
    /// </summary>
    public List<TenantOverPlanViewModel> Overages { get; set; } = new();
}

public sealed record TenantOverPlanViewModel(string Tenant, string PlanName, PlanBreach Breach);
