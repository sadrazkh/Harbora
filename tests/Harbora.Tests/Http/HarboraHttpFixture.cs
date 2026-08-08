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
