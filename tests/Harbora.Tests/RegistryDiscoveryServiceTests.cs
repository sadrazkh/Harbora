using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The discovery run itself: whether it asks, what it stores, and what it refuses to store.
///
/// The rules are covered in <see cref="RegistryDiscoveryTests"/>. What is proved here is that the
/// job honours the setting, survives a registry that will not answer, and never writes a version
/// that cannot be deployed — each of which, wrong, produces a job that logs a clean run and leaves
/// either nothing or rubbish behind.
/// </summary>
public class RegistryDiscoveryServiceTests
{
    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>A registry that answers from a script and counts what it was asked.</summary>
    private sealed class ScriptedRegistry(
        IReadOnlyList<string> tags, IReadOnlyDictionary<string, string>? digests = null) : IContainerRegistry
    {
        public int TagListsRequested { get; private set; }
        public List<string> DigestsRequested { get; } = [];

        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct)
        {
            TagListsRequested++;
            return Task.FromResult(tags);
        }

        public Task<string?> ResolveDigestAsync(string repository, string tag, CancellationToken ct)
        {
            DigestsRequested.Add(tag);
            var known = digests ?? new Dictionary<string, string>();
            return Task.FromResult(known.TryGetValue(tag, out var digest) ? digest : null);
        }
    }

    private static string Digest(char c) => "sha256:" + new string(c, 64);

    private sealed record Fixture(HarboraDbContext Db, RegistryDiscoveryService Service, ScriptedRegistry Registry, Guid TemplateId);

    private static Fixture Build(
        bool enabled, IReadOnlyList<string> tags, IReadOnlyDictionary<string, string>? digests = null,
        bool withVersions = true)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("registry-" + Guid.NewGuid()).Options);

        var template = new AppTemplate
        {
            Id = Guid.CreateVersion7(), Key = "demo", Name = "Demo", Category = "apps",
            IsBuiltIn = true, IsEnabled = true, Status = TemplateStatus.Approved,
            ManifestJson = """{"image":"demo/app:16.4","port":80}"""
        };
        db.Add(template);

        if (withVersions)
        {
            db.Add(new AppTemplateVersion
            {
                Id = Guid.CreateVersion7(), AppTemplateId = template.Id, Version = "16.4",
                ImageRepository = "demo/app", ImageTag = "16.4", ImageDigest = Digest('a'),
                Lifecycle = VersionLifecycle.Recommended, Publication = VersionPublication.Published,
                SupportedArchitectures = "amd64",
                ManifestJson = """{"image":"demo/app:16.4","port":80}"""
            });
        }

        db.Add(new Setting { Id = Guid.CreateVersion7(), Key = SettingKeys.RegistryDiscoveryEnabled, Value = enabled ? "true" : "false" });
        db.SaveChanges();

        var registry = new ScriptedRegistry(tags, digests);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<IContainerRegistry>(registry);
        services.AddSingleton<ISystemClock>(new Clock());

        var service = new RegistryDiscoveryService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RegistryDiscoveryService>.Instance);

        return new Fixture(db, service, registry, template.Id);
    }

    [Fact]
    public async Task Nothing_is_requested_while_the_feature_is_off()
    {
        // Off is the default, and it must mean no outbound request at all — not a request whose
        // result is discarded. This talks to somebody else's infrastructure on their rate limit.
        var f = Build(enabled: false, tags: ["16.5"]);

        var added = await f.Service.DiscoverAsync(CancellationToken.None);

        added.Should().Be(0);
        f.Registry.TagListsRequested.Should().Be(0);
    }

    [Fact]
    public async Task A_newer_tag_becomes_a_draft()
    {
        var f = Build(enabled: true, tags: ["16.5"], digests: new Dictionary<string, string> { ["16.5"] = Digest('b') });

        var added = await f.Service.DiscoverAsync(CancellationToken.None);

        added.Should().Be(1);
        var stored = await f.Db.AppTemplateVersions.SingleAsync(v => v.Version == "16.5");
        stored.Publication.Should().Be(VersionPublication.Draft);
        stored.ImageDigest.Should().Be(Digest('b'));
        stored.DiscoveredAt.Should().Be(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_tag_whose_digest_cannot_be_resolved_is_not_stored()
    {
        // A version without a digest is refused at deploy time, so storing one produces a row that
        // looks like an option and fails every time it is chosen.
        var f = Build(enabled: true, tags: ["16.5"]);

        var added = await f.Service.DiscoverAsync(CancellationToken.None);

        added.Should().Be(0);
        f.Registry.DigestsRequested.Should().Equal("16.5");
        (await f.Db.AppTemplateVersions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Nothing_published_by_the_registry_is_ever_published_here()
    {
        var f = Build(enabled: true, tags: ["16.5", "16.6"], digests: new Dictionary<string, string>
        {
            ["16.5"] = Digest('b'), ["16.6"] = Digest('c')
        });

        await f.Service.DiscoverAsync(CancellationToken.None);

        var discovered = await f.Db.AppTemplateVersions.Where(v => v.DiscoveredAt != null).ToListAsync();
        discovered.Should().HaveCount(2);
        discovered.Should().OnlyContain(v => v.Publication == VersionPublication.Draft);
    }

    [Fact]
    public async Task A_second_run_does_not_add_the_same_version_twice()
    {
        // The job runs daily against a repository that changes rarely. Duplicating on every pass
        // would bury the drafts that actually need a decision.
        var f = Build(enabled: true, tags: ["16.5"], digests: new Dictionary<string, string> { ["16.5"] = Digest('b') });

        await f.Service.DiscoverAsync(CancellationToken.None);
        var second = await f.Service.DiscoverAsync(CancellationToken.None);

        second.Should().Be(0);
        (await f.Db.AppTemplateVersions.CountAsync(v => v.Version == "16.5")).Should().Be(1);
    }

    [Fact]
    public async Task A_template_with_no_versions_is_left_alone()
    {
        // It deploys from its own manifest and has no shape to follow.
        var f = Build(enabled: true, tags: ["1.0", "2.0"], withVersions: false);

        (await f.Service.DiscoverAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task An_unreadable_setting_is_treated_as_off()
    {
        // Anything other than an explicit "true" means nobody turned this on.
        var f = Build(enabled: false, tags: ["16.5"]);
        var setting = await f.Db.Settings.SingleAsync();
        setting.Value = "yes, please";
        await f.Db.SaveChangesAsync();

        await f.Service.DiscoverAsync(CancellationToken.None);

        f.Registry.TagListsRequested.Should().Be(0);
    }

    [Fact]
    public void The_job_can_be_resolved_by_its_own_type()
    {
        // AddHostedService<T> registers only IHostedService. Used alone, the admin page's "check
        // now" button fails to resolve the job — at runtime, on a page that compiles, renders and
        // looks finished.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harbora:MasterKey"] = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData("tests"u8.ToArray()))
            })
            .Build();

        var services = new ServiceCollection();
        Harbora.Infrastructure.DependencyInjection.AddHarboraInfrastructure(services, config);

        services.Should().Contain(d => d.ServiceType == typeof(RegistryDiscoveryService),
            "the admin page injects it directly");
        services.Should().Contain(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService),
            "it must still run on its timer");
    }

    [Fact]
    public async Task A_registry_that_lists_nothing_is_not_an_error()
    {
        // Unreachable registries are an ordinary Tuesday. A run that throws on the first one never
        // reaches the rest of the catalogue.
        var f = Build(enabled: true, tags: []);

        var added = await f.Service.DiscoverAsync(CancellationToken.None);

        added.Should().Be(0);
        f.Registry.TagListsRequested.Should().Be(1);
    }
}
