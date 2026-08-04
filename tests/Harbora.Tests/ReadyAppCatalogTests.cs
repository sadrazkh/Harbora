using FluentAssertions;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The shipped catalogue of ready-made apps.
///
/// These assertions are about the data itself, because a catalogue entry is a promise: somebody
/// clicks it and expects working software. An unpinned image, a missing logo file or two versions
/// both claiming to be recommended are all things that look fine in review and go wrong in use.
/// </summary>
public class ReadyAppCatalogTests
{
    private static string LogoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull();
        return Path.Combine(dir!.FullName, "src", "Harbora.Web", "wwwroot");
    }

    [Fact]
    public void The_requested_apps_are_all_present()
    {
        var keys = ReadyAppCatalog.All().Select(a => a.Template.Key).ToList();

        keys.Should().Contain(["n8n", "docker-workspace", "sentry", "gitea", "rocketchat",
                               "minio", "grafana", "metabase"]);
    }

    [Fact]
    public void Every_version_is_pinned_to_a_digest()
    {
        // The reason versions exist at all. A tag moves; two people who both installed "16" a month
        // apart would be running different software with nothing recording the difference.
        foreach (var app in ReadyAppCatalog.All())
        foreach (var version in app.Versions)
        {
            version.ImageDigest.Should().StartWith("sha256:",
                $"{app.Template.Key} {version.Version} must be pinned");
            version.ImageRepository.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void No_version_relies_on_the_latest_tag()
    {
        foreach (var app in ReadyAppCatalog.All())
        foreach (var version in app.Versions)
            version.ImageTag.Should().NotBe("latest",
                $"{app.Template.Key} would install something different every deploy");
    }

    [Fact]
    public void Each_app_recommends_exactly_one_version()
    {
        // Two recommendations is an interface with two default selections and no way to choose
        // between them; none is a deploy form with nothing preselected.
        foreach (var app in ReadyAppCatalog.All())
        {
            app.Versions.Count(v => v.Lifecycle == VersionLifecycle.Recommended)
                .Should().Be(1, $"{app.Template.Key} must recommend exactly one version");
        }
    }

    [Fact]
    public void Every_app_ships_a_logo_file_that_actually_exists()
    {
        // The catalogue records a path; this checks the file is really in the repository, which is
        // the difference between a logo and a broken image icon.
        foreach (var app in ReadyAppCatalog.All())
        {
            var relative = app.Asset.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(LogoRoot(), relative);

            File.Exists(full).Should().BeTrue($"{app.Template.Key} claims a logo at {app.Asset.Path}");
            new FileInfo(full).Length.Should().BeGreaterThan(80, "an empty file is not a logo");
        }
    }

    [Fact]
    public void Every_logo_records_where_it_came_from_and_under_what_terms()
    {
        // "Where did this logo come from" is a question that arrives long after whoever added it.
        foreach (var app in ReadyAppCatalog.All())
        {
            app.Asset.License.Should().NotBe(AssetLicense.Unknown, $"{app.Template.Key}");
            app.Asset.SourceUrl.Should().NotBeNullOrWhiteSpace($"{app.Template.Key}");
            app.Asset.LicenseNote.Should().NotBeNullOrWhiteSpace($"{app.Template.Key}");
        }
    }

    [Fact]
    public void No_logo_carries_a_background_plate()
    {
        // A white rectangle behind a mark looks like a sticker on the card in light mode and a hole
        // in it in dark mode.
        foreach (var app in ReadyAppCatalog.All())
        {
            var relative = app.Asset.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var svg = File.ReadAllText(Path.Combine(LogoRoot(), relative));

            svg.Should().NotContain("fill=\"#fff\"", $"{app.Template.Key} draws a white plate");
            svg.Should().NotContain("fill=\"white\"", $"{app.Template.Key} draws a white plate");
        }
    }

    [Fact]
    public void Every_logo_keeps_its_aspect_ratio()
    {
        // A viewBox with equal sides is what stops the card stretching the mark when the slot is
        // not square.
        foreach (var app in ReadyAppCatalog.All())
        {
            var relative = app.Asset.Path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var svg = File.ReadAllText(Path.Combine(LogoRoot(), relative));

            svg.Should().Contain("viewBox=\"0 0 24 24\"", $"{app.Template.Key}");
        }
    }

    [Fact]
    public void Docker_workspace_is_not_offerable_until_the_secure_runtime_exists()
    {
        // The one entry that must not be deployable yet. A workspace template that mounts the host
        // Docker socket hands the node to whoever deploys it, so this ships as a draft and the
        // secure runtime lands before it is published.
        var workspace = ReadyAppCatalog.All().Single(a => a.Template.Key == "docker-workspace");

        workspace.Versions.Should().OnlyContain(v => v.Publication == VersionPublication.Draft);
        VersionSelection.Offerable(workspace.Versions).Should().BeEmpty();
    }

    [Fact]
    public void No_template_manifest_mounts_the_host_docker_socket()
    {
        // The single most dangerous thing a template can do.
        foreach (var app in ReadyAppCatalog.All())
        {
            app.Template.ManifestJson.Should().NotContain("/var/run/docker.sock", $"{app.Template.Key}");
            foreach (var version in app.Versions)
                version.ManifestJson.Should().NotContain("/var/run/docker.sock", $"{app.Template.Key}");
        }
    }

    [Fact]
    public void Every_app_carries_documentation_a_person_can_open()
    {
        foreach (var app in ReadyAppCatalog.All())
            app.Template.ManifestJson.Should().Contain("\"documentation\"", $"{app.Template.Key}");
    }

    [Fact]
    public void Secrets_in_a_manifest_are_marked_as_secrets()
    {
        // A password recorded as an ordinary variable is rendered in plain text on the app page and
        // copied into a preview environment.
        foreach (var app in ReadyAppCatalog.All())
        {
            var manifest = app.Template.ManifestJson;
            foreach (var word in new[] { "PASSWORD", "SECRET_KEY", "ENCRYPTION_KEY" })
            {
                var at = manifest.IndexOf(word, StringComparison.Ordinal);
                if (at < 0) continue;

                var entry = manifest[at..Math.Min(manifest.Length, at + 160)];
                entry.Should().Contain("\"secret\":true", $"{app.Template.Key} leaves {word} unmarked");
            }
        }
    }
}
