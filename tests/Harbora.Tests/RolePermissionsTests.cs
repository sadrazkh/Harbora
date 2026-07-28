using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Full access-control matrix (RBAC / doc 10 §2.12). Every role × capability pairing is asserted so
/// the least-privilege model can't silently regress. Deny-by-default is the contract.
/// </summary>
public class RolePermissionsTests
{
    [Fact]
    public void Owner_and_Admin_have_every_capability()
    {
        foreach (var cap in Capabilities.All)
        {
            RolePermissions.Allows(SystemRole.Owner, cap).Should().BeTrue($"Owner needs {cap}");
            RolePermissions.Allows(SystemRole.Admin, cap).Should().BeTrue($"Admin needs {cap}");
        }
    }

    [Fact]
    public void Viewer_has_no_write_capabilities()
    {
        foreach (var cap in Capabilities.All)
            RolePermissions.Allows(SystemRole.Viewer, cap).Should().BeFalse($"Viewer must not have {cap}");
    }

    [Fact]
    public void Member_is_a_developer_scoped_to_app_resources()
    {
        var expected = new[]
        {
            Capabilities.AppsCreate, Capabilities.AppsDeploy, Capabilities.AppsOperate,
            Capabilities.AppsDelete, Capabilities.AppsEnv,
            Capabilities.DatabasesManage, Capabilities.RoutesManage, Capabilities.GitManage
        };
        RolePermissions.CapabilitiesFor(SystemRole.Member).Should().BeEquivalentTo(expected);

        // Explicitly denied platform/tenant/server/backup administration.
        RolePermissions.Allows(SystemRole.Member, Capabilities.ServersManage).Should().BeFalse();
        RolePermissions.Allows(SystemRole.Member, Capabilities.PlatformManage).Should().BeFalse();
        RolePermissions.Allows(SystemRole.Member, Capabilities.TenantsManage).Should().BeFalse();
        RolePermissions.Allows(SystemRole.Member, Capabilities.BackupsRestore).Should().BeFalse();
        RolePermissions.Allows(SystemRole.Member, Capabilities.BackupsRun).Should().BeFalse();
    }

    [Fact]
    public void Operator_is_day2_operations_only()
    {
        RolePermissions.CapabilitiesFor(SystemRole.Operator).Should().BeEquivalentTo(new[]
        {
            Capabilities.AppsOperate, Capabilities.BackupsRun
        });

        // The important denials: no deploy, no create/delete, no restore, no platform admin.
        RolePermissions.Allows(SystemRole.Operator, Capabilities.AppsDeploy).Should().BeFalse();
        RolePermissions.Allows(SystemRole.Operator, Capabilities.AppsCreate).Should().BeFalse();
        RolePermissions.Allows(SystemRole.Operator, Capabilities.AppsDelete).Should().BeFalse();
        RolePermissions.Allows(SystemRole.Operator, Capabilities.BackupsRestore).Should().BeFalse();
        RolePermissions.Allows(SystemRole.Operator, Capabilities.PlatformManage).Should().BeFalse();
    }

    [Fact]
    public void Every_capability_name_is_unique_and_listed()
    {
        Capabilities.All.Should().OnlyHaveUniqueItems();
        Capabilities.All.Should().HaveCount(16);
    }
}
