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
    public void No_digest_is_a_hand_written_placeholder()
    {
        // Every digest in this catalogue was once invented — `0f8b1c2d3e4f5a6b...`, a walking hex
        // pattern typed by hand. They are the right length, the right alphabet and the right prefix,
        // so every test here passed, the catalogue rendered, the deploy form offered them and the
        // pull failed with "manifest unknown" on a page that had already promised the app.
        //
        // A real digest is a hash: consecutive bytes do not count upwards. This catches the shape of
        // a fabrication rather than the fabrication itself, which is the only part a test can know.
        foreach (var app in ReadyAppCatalog.All())
        foreach (var version in app.Versions)
        {
            var hex = version.ImageDigest!["sha256:".Length..]
                .Select(c => Convert.ToInt32(c.ToString(), 16)).ToArray();

            // Several lags, because the placeholders that were here interleaved two counters —
            // 8b9c0d1e2f… climbs by one every *second* character, so a check on neighbours alone
            // sees nothing wrong. A hash has no such relationship at any small lag; for each lag the
            // commonest difference should turn up around one time in sixteen.
            for (var lag = 1; lag <= 4; lag++)
            {
                var counts = new int[16];
                for (var i = 0; i + lag < hex.Length; i++)
                    counts[(hex[i + lag] - hex[i] + 16) % 16]++;

                var pairs = hex.Length - lag;
                counts.Max().Should().BeLessThan(pairs * 6 / 10,
                    $"{app.Template.Key} {version.Version} looks counted rather than hashed " +
                    $"(lag {lag}): {version.ImageDigest}");
            }
        }
    }

    [Fact]
    public void No_two_versions_share_a_digest()
    {
        // Two versions with the same digest are the same image under two names — which means at
        // least one of them was copied rather than resolved.
        var digests = ReadyAppCatalog.All().SelectMany(a => a.Versions).Select(v => v.ImageDigest).ToList();

        digests.Should().OnlyHaveUniqueItems();
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
    public void Every_manifest_actually_parses()
    {
        // The catalogue page silently drops any template whose manifest does not parse, and the
        // deploy service refuses one. Both failures look identical to "the feature was never built":
        // the entry is seeded, sits in the database, and is nowhere on screen.
        foreach (var app in ReadyAppCatalog.All())
        {
            TemplateManifest.TryParse(app.Template.ManifestJson, out _, out var errors)
                .Should().BeTrue($"{app.Template.Key}: {string.Join(" ", errors)}");

            foreach (var version in app.Versions)
                TemplateManifest.TryParse(version.ManifestJson, out _, out var versionErrors)
                    .Should().BeTrue($"{app.Template.Key} {version.Version}: {string.Join(" ", versionErrors)}");
        }
    }

    [Fact]
    public void Every_manifest_names_an_image_with_a_tag()
    {
        // A manifest with no image at all does not parse. One whose image carries no tag resolves
        // through :latest, which is the thing the digest pinning exists to prevent — and it would
        // only show up as "this deployed a different version than the page said".
        foreach (var app in ReadyAppCatalog.All())
        {
            TemplateManifest.TryParse(app.Template.ManifestJson, out var manifest, out _);

            manifest!.Image.Should().NotBeNullOrWhiteSpace($"{app.Template.Key}");
            manifest.Image.Should().Contain(":", $"{app.Template.Key} names an untagged image");
            manifest.Image.Should().NotEndWith(":latest", $"{app.Template.Key}");
        }
    }

    [Fact]
    public void The_manifest_image_matches_the_version_it_stands_for()
    {
        // The manifest's image is what a person reads on the catalogue page; the digest is what gets
        // deployed. If they name different versions the page is lying, quietly.
        foreach (var app in ReadyAppCatalog.All())
        {
            var offered = VersionSelection.Default(app.Versions)
                          ?? app.Versions.OrderBy(v => v.Lifecycle).First();

            TemplateManifest.TryParse(app.Template.ManifestJson, out var manifest, out _);
            manifest!.Image.Should().Be($"{offered.ImageRepository}:{offered.ImageTag}",
                $"{app.Template.Key} shows one version and deploys another");
        }
    }

    [Fact]
    public void Each_version_manifest_names_its_own_image()
    {
        // Not the recommended one's. The manifest is per-version precisely because ports, variables
        // and images change between releases; copying the recommended one into all of them makes
        // every version describe the same software.
        foreach (var app in ReadyAppCatalog.All())
        foreach (var version in app.Versions)
        {
            TemplateManifest.TryParse(version.ManifestJson, out var manifest, out _);
            manifest!.Image.Should().Be($"{version.ImageRepository}:{version.ImageTag}",
                $"{app.Template.Key} {version.Version}");
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
