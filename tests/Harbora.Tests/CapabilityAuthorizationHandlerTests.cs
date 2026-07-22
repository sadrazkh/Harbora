using System.Security.Claims;
using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Verifies the ASP.NET authorization adapter reads the role claim and enforces the RBAC matrix
/// (deny-by-default), so the same rules apply to the MVC UI and the token-authenticated API.
/// </summary>
public class CapabilityAuthorizationHandlerTests
{
    private static async Task<bool> Evaluate(string? roleClaim, string capability)
    {
        var handler = new CapabilityAuthorizationHandler();
        var requirement = new CapabilityRequirement(capability);
        var claims = roleClaim is null ? [] : new[] { new Claim(ClaimTypes.Role, roleClaim) };
        // A non-null authenticationType marks the identity authenticated.
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, roleClaim is null ? null : "test"));
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task Owner_is_allowed_a_privileged_capability()
        => (await Evaluate(nameof(SystemRole.Owner), Capabilities.ServersManage)).Should().BeTrue();

    [Fact]
    public async Task Viewer_is_denied_every_write_capability()
    {
        (await Evaluate(nameof(SystemRole.Viewer), Capabilities.AppsDeploy)).Should().BeFalse();
        (await Evaluate(nameof(SystemRole.Viewer), Capabilities.AppsCreate)).Should().BeFalse();
    }

    [Fact]
    public async Task Operator_can_operate_but_not_deploy()
    {
        (await Evaluate(nameof(SystemRole.Operator), Capabilities.AppsOperate)).Should().BeTrue();
        (await Evaluate(nameof(SystemRole.Operator), Capabilities.AppsDeploy)).Should().BeFalse();
    }

    [Fact]
    public async Task Member_can_deploy_but_not_manage_servers()
    {
        (await Evaluate(nameof(SystemRole.Member), Capabilities.AppsDeploy)).Should().BeTrue();
        (await Evaluate(nameof(SystemRole.Member), Capabilities.ServersManage)).Should().BeFalse();
    }

    [Fact]
    public async Task Missing_or_unknown_role_claim_is_denied()
    {
        (await Evaluate(null, Capabilities.AppsOperate)).Should().BeFalse();
        (await Evaluate("Bogus", Capabilities.AppsOperate)).Should().BeFalse();
    }
}
