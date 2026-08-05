using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Servers;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Templates;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which image a ready-made app actually deploys.
///
/// The versioning model existed for a while with nothing consuming it: versions were seeded, shown
/// nowhere, and the deploy path used the manifest's floating tag. Everything was green. These tests
/// are the ones that would have failed.
/// </summary>
public class TemplateVersionDeploymentTests
{
    private sealed class AllowAll : IQuotaService
    {
        public Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? size, Guid? exclude, CancellationToken ct) =>
            Task.FromResult(QuotaCheck.Ok);

        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) =>
            Task.FromResult(QuotaCheck.Ok);
    }

    private sealed class RecordingDeployments : IDeploymentEngine
    {
        public List<Guid> Queued { get; } = [];

        public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct)
        {
            Queued.Add(request.AppId);
            return Task.FromResult(Guid.CreateVersion7());
        }

        public Task CancelAsync(Guid deploymentId, CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private const string Digest16 = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Digest15 = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private sealed record Fixture(
        HarboraDbContext Db, TemplateDeploymentService Service, Guid WorkspaceId, AppTemplate Template);

    private static Fixture Build(string? serverArchitecture = "amd64", bool withVersions = true)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("template-version-" + Guid.NewGuid()).Options);

        var workspaceId = Guid.CreateVersion7();
        db.Add(new Server
        {
            Id = Guid.CreateVersion7(), Name = "local", IsLocal = true, Architecture = serverArchitecture
        });

        var template = new AppTemplate
        {
            Id = Guid.CreateVersion7(),
            Key = "demo", Name = "Demo", Category = "apps",
            IsBuiltIn = true, IsEnabled = true, Status = TemplateStatus.Approved,
            ManifestJson = """{"image":"demo/app:16","port":80}"""
        };
        db.Add(template);

        if (withVersions)
        {
            db.AddRange(
                new AppTemplateVersion
                {
                    Id = Guid.CreateVersion7(), AppTemplateId = template.Id, Version = "16",
                    ImageRepository = "demo/app", ImageTag = "16", ImageDigest = Digest16,
                    Lifecycle = VersionLifecycle.Recommended, Publication = VersionPublication.Published,
                    SupportedArchitectures = "amd64,arm64"
                },
                new AppTemplateVersion
                {
                    Id = Guid.CreateVersion7(), AppTemplateId = template.Id, Version = "15",
                    ImageRepository = "demo/app", ImageTag = "15", ImageDigest = Digest15,
                    Lifecycle = VersionLifecycle.PreviousStable, Publication = VersionPublication.Published,
                    SupportedArchitectures = "amd64"
                });
        }

        db.SaveChanges();

        var service = new TemplateDeploymentService(
            db,
            new ProjectService(db, new FixedClock(Now)),
            new AllowAll(),
            new PassthroughProtector(),
            new FakeManagedServiceEngine(),
            new RecordingDeployments());

        return new Fixture(db, service, workspaceId, template);
    }

    private static TemplateDeployRequest Request(Fixture f, Guid? versionId = null) =>
        new(f.WorkspaceId, Guid.CreateVersion7(), f.Template.Id, "Demo", "Demo",
            null, null, new Dictionary<string, string>(), DeployNow: false, VersionId: versionId);

    private static Guid VersionId(Fixture f, string version) =>
        f.Db.AppTemplateVersions.Single(v => v.AppTemplateId == f.Template.Id && v.Version == version).Id;

    [Fact]
    public async Task The_recommended_version_is_deployed_when_nobody_picks_one()
    {
        var f = Build();

        var result = await f.Service.DeployAsync(Request(f), CancellationToken.None);

        var app = await f.Db.Apps.SingleAsync(a => a.Id == result.AppId);
        app.PrebuiltImage.Should().Be($"demo/app@{Digest16}");
    }

    [Fact]
    public async Task The_deployed_image_is_pinned_by_digest_and_carries_no_tag()
    {
        // The tag is deliberately absent. "demo/app:16@sha256:…" is legal and reads well, and it
        // invites someone to edit the tag later, quietly changing what runs.
        var f = Build();

        var result = await f.Service.DeployAsync(Request(f), CancellationToken.None);
        var app = await f.Db.Apps.SingleAsync(a => a.Id == result.AppId);

        app.PrebuiltImage.Should().Contain("@sha256:");
        app.PrebuiltImage.Should().NotContain(":16@");
    }

    [Fact]
    public async Task A_chosen_version_is_the_one_that_deploys()
    {
        var f = Build();

        var result = await f.Service.DeployAsync(Request(f, VersionId(f, "15")), CancellationToken.None);

        var app = await f.Db.Apps.SingleAsync(a => a.Id == result.AppId);
        app.PrebuiltImage.Should().Be($"demo/app@{Digest15}");
    }

    [Fact]
    public async Task What_was_deployed_is_recorded_on_the_app()
    {
        // Without this column, "who is running the version we are about to deprecate" cannot be
        // answered: a digest says what is running but not which of our versions that is.
        var f = Build();
        var chosen = VersionId(f, "15");

        var result = await f.Service.DeployAsync(Request(f, chosen), CancellationToken.None);

        var app = await f.Db.Apps.SingleAsync(a => a.Id == result.AppId);
        app.TemplateVersionId.Should().Be(chosen);
        app.TemplateId.Should().Be(f.Template.Id);
    }

    [Fact]
    public async Task A_draft_version_cannot_be_deployed_even_when_asked_for_directly()
    {
        // The list the page drew is not a permission. A version can be withdrawn between render and
        // submit, and a scripted caller never saw the list at all.
        var f = Build();
        var draft = await f.Db.AppTemplateVersions.SingleAsync(v => v.Version == "15");
        draft.Publication = VersionPublication.Draft;
        await f.Db.SaveChangesAsync();

        var act = () => f.Service.DeployAsync(Request(f, draft.Id), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not been published*");
    }

    [Fact]
    public async Task An_unsupported_version_is_refused()
    {
        var f = Build();
        var old = await f.Db.AppTemplateVersions.SingleAsync(v => v.Version == "15");
        old.Lifecycle = VersionLifecycle.Unsupported;
        await f.Db.SaveChangesAsync();

        var act = () => f.Service.DeployAsync(Request(f, old.Id), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no longer supported*");
    }

    [Fact]
    public async Task A_version_without_a_digest_is_refused_rather_than_resolved_through_its_tag()
    {
        // Falling back to the tag here would defeat the entire model while looking like it worked.
        var f = Build();
        var unpinned = await f.Db.AppTemplateVersions.SingleAsync(v => v.Version == "15");
        unpinned.ImageDigest = null;
        await f.Db.SaveChangesAsync();

        var act = () => f.Service.DeployAsync(Request(f, unpinned.Id), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no pinned image digest*");
    }

    [Fact]
    public async Task A_version_built_for_another_architecture_is_refused()
    {
        // Left through, this fails deep in the container runtime with a message about exec formats
        // that says nothing about architectures.
        var f = Build(serverArchitecture: "arm64");

        var act = () => f.Service.DeployAsync(Request(f, VersionId(f, "15")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not support arm64*");
    }

    [Fact]
    public async Task An_unknown_server_architecture_filters_nothing()
    {
        // Unknown is not amd64. Guessing refuses versions that would have run perfectly well.
        var f = Build(serverArchitecture: null);

        var result = await f.Service.DeployAsync(Request(f, VersionId(f, "15")), CancellationToken.None);

        var app = await f.Db.Apps.SingleAsync(a => a.Id == result.AppId);
        app.PrebuiltImage.Should().Be($"demo/app@{Digest15}");
    }

    [Fact]
    public async Task A_version_belonging_to_another_template_is_refused()
    {
        var f = Build();
        var other = new AppTemplateVersion
        {
            Id = Guid.CreateVersion7(), AppTemplateId = Guid.CreateVersion7(), Version = "99",
            ImageRepository = "other/app", ImageTag = "99", ImageDigest = Digest16,
            Publication = VersionPublication.Published
        };
        f.Db.Add(other);
        await f.Db.SaveChangesAsync();

        var act = () => f.Service.DeployAsync(Request(f, other.Id), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not belong*");
    }

    [Fact]
    public async Task A_template_with_versions_but_none_offerable_is_refused_rather_than_falling_back()
    {
        // The operator published versions precisely so the manifest's floating tag would stop being
        // what customers get. Falling back to it here would undo that at the worst moment.
        var f = Build();
        foreach (var version in await f.Db.AppTemplateVersions.ToListAsync())
            version.Publication = VersionPublication.Draft;
        await f.Db.SaveChangesAsync();

        var act = () => f.Service.DeployAsync(Request(f), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No version*");
    }

    [Fact]
    public async Task A_template_with_no_versions_still_deploys_from_its_manifest()
    {
        // Every template that existed before versions did. Breaking these to add a feature would be
        // the worst possible trade.
        var f = Build(withVersions: false);

        var result = await f.Service.DeployAsync(Request(f), CancellationToken.None);

        var app = await f.Db.Apps.SingleAsync(a => a.Id == result.AppId);
        app.PrebuiltImage.Should().Be("demo/app:16");
        app.TemplateVersionId.Should().BeNull();
    }

    [Fact]
    public async Task Asking_for_a_version_of_a_template_that_has_none_is_refused()
    {
        // A stale link or a typo. Quietly deploying something else is how somebody ends up running
        // a version they never chose.
        var f = Build(withVersions: false);

        var act = () => f.Service.DeployAsync(Request(f, Guid.CreateVersion7()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not belong*");
    }
}
