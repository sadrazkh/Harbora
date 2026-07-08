using Harbora.Domain.Tenancy;

namespace Harbora.Web.ViewModels;

public sealed class TenantsPageViewModel
{
    public List<TenantRow> Tenants { get; set; } = new();
    public List<Plan> Plans { get; set; } = new();
}

public sealed record TenantRow(
    Guid WorkspaceId,
    string Name,
    string Slug,
    bool IsDefault,
    Guid? PlanId,
    string PlanName,
    int Members,
    int Apps,
    int Services,
    bool Suspended);

public sealed class TenantDetailsViewModel
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool Suspended { get; set; }
    public Application.Abstractions.WorkspaceUsage Usage { get; set; } = null!;
    public List<TenantMember> Members { get; set; } = new();
}

public sealed record TenantMember(Guid UserId, string Email, string DisplayName, string WorkspaceRole, bool Active);

