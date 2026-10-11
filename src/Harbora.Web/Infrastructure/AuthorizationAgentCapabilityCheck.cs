using System.Security.Claims;
using Harbora.Infrastructure.Agent;
using Microsoft.AspNetCore.Authorization;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// The agent's capability question asked of the very policies the controllers' <c>[Authorize(Policy =
/// Capabilities.X)]</c> attributes evaluate (<see cref="CapabilityAuthorizationExtensions"/>) — so a
/// tool is offered to exactly the people the matching button in the panel would let through.
/// </summary>
public sealed class AuthorizationAgentCapabilityCheck(IAuthorizationService authorization) : IAgentCapabilityCheck
{
    public async Task<bool> AllowsAsync(ClaimsPrincipal user, string capability, CancellationToken ct) =>
        (await authorization.AuthorizeAsync(user, capability)).Succeeded;
}
