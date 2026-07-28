using System.Security.Claims;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Microsoft.AspNetCore.Authorization;

namespace Harbora.Web.Infrastructure;

/// <summary>Authorization requirement carrying the capability a policy demands (doc 10 §2.12).</summary>
public sealed class CapabilityRequirement(string capability) : IAuthorizationRequirement
{
    public string Capability { get; } = capability;
}

/// <summary>
/// Evaluates a <see cref="CapabilityRequirement"/> against the caller's role claim (set identically
/// for cookie and bearer-token auth), using the pure <see cref="RolePermissions"/> matrix. Applies
/// to both the MVC UI and the API, so a Viewer/Operator/Member cannot perform a privileged action
/// via a direct POST or API call — deny-by-default.
/// </summary>
public sealed class CapabilityAuthorizationHandler : AuthorizationHandler<CapabilityRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, CapabilityRequirement requirement)
    {
        var roleValue = context.User.FindFirstValue(ClaimTypes.Role);
        if (Enum.TryParse<SystemRole>(roleValue, ignoreCase: true, out var role) &&
            RolePermissions.Allows(role, requirement.Capability))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

public static class CapabilityAuthorizationExtensions
{
    /// <summary>Registers one authorization policy per capability plus the evaluating handler.</summary>
    public static IServiceCollection AddCapabilityAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, CapabilityAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            foreach (var capability in Capabilities.All)
                options.AddPolicy(capability, policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new CapabilityRequirement(capability));
                });
        });
        return services;
    }
}
