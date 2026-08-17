using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// One booted panel, shared by every test in the HTTP collection.
///
/// <para>
/// A host per test class would cost a second or two each and buy nothing: the pipeline is stateless
/// between requests, and the state that is not — the database and the rate limiters — is separated
/// by giving each test its own rows and its own client address rather than its own server.
/// </para>
/// </summary>
public sealed class HarboraHttpFixture : IDisposable
{
    public HarboraWebFactory Panel { get; } = new();

    /// <summary>The workspace nearly every test hangs its fixture off.</summary>
    public Guid WorkspaceId { get; } = Guid.CreateVersion7();

    /// <summary>
    /// The environment nearly every test that seeds an <c>App</c> or <c>ManagedService</c> places it
    /// in when it does not care which one. EnvironmentId is required now (P2, 2026-08-17
    /// app-environment-management design), and this fixture is shared by every test class in the
    /// collection, so it is seeded exactly once here rather than once per file — the alternative most
    /// files would otherwise reinvent, with its own risk of colliding on a fixed default slug.
    /// </summary>
    public Guid DefaultEnvironmentId { get; } = Guid.CreateVersion7();

    public HarboraHttpFixture()
    {
        // Past first run, like a panel anyone would be making requests to. Without this the setup
        // guard would answer every request in this collection with a redirect to /setup — which is
        // itself asserted, by SetupGuardHttpTests, on a panel of its own.
        Panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();

            db.Workspaces.Add(new Workspace
            {
                Id = WorkspaceId,
                Name = "Harbora",
                Slug = "harbora-http",
                IsDefault = true,
                PlanId = planId == Guid.Empty ? null : planId
            });

            db.Settings.Add(new Setting { Key = SettingKeys.SetupCompleted, Value = "true" });

            var projectId = Guid.CreateVersion7();
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = WorkspaceId, Name = "Shop", Slug = "shop"
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = DefaultEnvironmentId, WorkspaceId = WorkspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
        });
    }

    public void Dispose() => Panel.Dispose();
}

/// <summary>
/// Everything that speaks HTTP runs here. xunit runs a collection's classes one after another, which
/// is what keeps the shared panel's rate-limit windows and the setup guard's process-wide flag from
/// being read by one test while another is writing them.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HarboraHttpCollection : ICollectionFixture<HarboraHttpFixture>
{
    public const string Name = "harbora-http";
}
