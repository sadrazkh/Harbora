using Harbora.Application.Abstractions;
using Harbora.Domain.Tenancy;

namespace Harbora.Web.ViewModels;

public sealed class PlansPageViewModel
{
    public WorkspaceUsage Usage { get; set; } = null!;
    public bool IsProvider { get; set; }
    public List<Plan> Plans { get; set; } = new();
    public List<InstanceSize> Sizes { get; set; } = new();
}
