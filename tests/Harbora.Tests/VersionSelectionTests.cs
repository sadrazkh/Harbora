using FluentAssertions;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Choosing which version of a ready-made app to run.
///
/// Each rule here exists because its absence fails somewhere far away: an unpinned image installs
/// different software on different days, and a wrong-architecture image dies inside the container
/// runtime with a message about exec formats that mentions no architecture at all.
/// </summary>
public class VersionSelectionTests
{
    private static AppTemplateVersion V(
        string version,
        VersionLifecycle lifecycle = VersionLifecycle.Stable,
        VersionPublication publication = VersionPublication.Published,
        string digest = "sha256:aaaa",
        string arch = "amd64,arm64",
        int releasedDaysAgo = 0) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Version = version,
            ImageRepository = "postgres",
            ImageTag = version,
            ImageDigest = digest,
            Lifecycle = lifecycle,
            Publication = publication,
            SupportedArchitectures = arch,
            ReleasedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-releasedDaysAgo)
        };

    [Fact]
    public void The_recommended_version_is_offered_first()
    {
        var versions = new[] { V("15"), V("16", VersionLifecycle.Recommended), V("14", VersionLifecycle.Legacy) };

        VersionSelection.Default(versions)!.Version.Should().Be("16");
    }

    [Fact]
    public void A_draft_version_is_never_offered_to_a_tenant()
    {
        // A draft is an operator's note to themselves, not an offer.
        var versions = new[] { V("17", publication: VersionPublication.Draft), V("16") };

        VersionSelection.Offerable(versions).Should().OnlyContain(v => v.Version == "16");
    }

    [Fact]
    public void An_unsupported_version_is_never_offered()
    {
        var versions = new[] { V("9", VersionLifecycle.Unsupported), V("16") };

        VersionSelection.Offerable(versions).Should().OnlyContain(v => v.Version == "16");
    }

    [Fact]
    public void A_deprecated_version_is_still_offered_because_people_are_running_it()
    {
        // Hiding it would leave somebody mid-migration unable to pick the version they already have.
        var versions = new[] { V("14", VersionLifecycle.Deprecated) };

        VersionSelection.Offerable(versions).Should().HaveCount(1);
    }

    [Fact]
    public void A_version_for_another_architecture_is_not_offered()
    {
        var versions = new[] { V("16", arch: "amd64"), V("15", arch: "amd64,arm64") };

        VersionSelection.Offerable(versions, "arm64").Should().OnlyContain(v => v.Version == "15");
    }

    [Fact]
    public void Architecture_matching_ignores_case_and_spacing()
    {
        VersionSelection.RunsOn(V("16", arch: "amd64, ARM64"), "arm64").Should().BeTrue();
    }

    [Fact]
    public void Deploying_an_unpinned_version_is_refused()
    {
        // The whole reason versions exist here. Without a digest the deployment resolves whatever
        // the tag points at today, which is not the version anybody chose.
        var refusal = VersionSelection.Refuse(V("16", digest: ""), "amd64");

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Contain("digest");
    }

    [Fact]
    public void Deploying_a_draft_is_refused_even_if_someone_asks_for_it_directly()
    {
        // The list the page drew is not a permission. A version can be withdrawn between the form
        // rendering and the person submitting it.
        VersionSelection.Refuse(V("17", publication: VersionPublication.Draft), "amd64")!
            .Reason.Should().Contain("published");
    }

    [Fact]
    public void Deploying_an_unsupported_version_is_refused_not_merely_hidden()
    {
        // Hiding it from the list is presentation. Somebody with an old link, or a scripted call,
        // asks for the id directly — and an end-of-life image is exactly the one that must not
        // start again.
        var refusal = VersionSelection.Refuse(V("9", VersionLifecycle.Unsupported), "amd64");

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Contain("no longer supported");
    }

    [Fact]
    public void Deploying_onto_the_wrong_architecture_is_refused_by_name()
    {
        var refusal = VersionSelection.Refuse(V("16", arch: "amd64"), "arm64");

        refusal!.Reason.Should().Contain("arm64").And.Contain("amd64");
    }

    [Fact]
    public void A_good_version_is_not_refused()
    {
        // The guard on all of the above: a rule that refuses everything is safe and useless.
        VersionSelection.Refuse(V("16"), "amd64").Should().BeNull();
    }

    [Fact]
    public void The_deployed_image_is_the_digest_not_the_tag()
    {
        var image = VersionSelection.PinnedImage(V("16"));

        image.Should().Be("postgres@sha256:aaaa");
        image.Should().NotContain(":16", "a tag in the reference invites someone to edit it later");
    }

    [Fact]
    public void An_unpinned_version_has_no_deployable_image()
    {
        VersionSelection.PinnedImage(V("16", digest: "")).Should().BeNull();
    }

    [Fact]
    public void With_nothing_published_there_is_no_default_rather_than_a_bad_one()
    {
        var versions = new[] { V("17", publication: VersionPublication.Draft) };

        VersionSelection.Default(versions).Should().BeNull();
    }
}

/// <summary>
/// Moving a running service between versions.
///
/// The rule that matters is about skipped versions: upgrading 14 → 17 crosses 15 and 16, and their
/// migration notes apply just as much as the destination's.
/// </summary>
public class VersionChangeTests
{
    private static AppTemplateVersion V(string version, int order, bool allowsDowngrade = false,
        string? notes = null, string? warnings = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Version = version,
            ImageRepository = "postgres",
            ImageDigest = "sha256:aaaa",
            Publication = VersionPublication.Published,
            SupportedArchitectures = "amd64",
            AllowsDowngrade = allowsDowngrade,
            UpgradeNotes = notes,
            MigrationWarnings = warnings,
            ReleasedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(order)
        };

    [Fact]
    public void An_upgrade_carries_the_notes_of_every_version_it_crosses()
    {
        // Reading only the destination's notes is reading the least important third of what is
        // about to happen to the data.
        var v14 = V("14", 0);
        var v15 = V("15", 1, notes: "Reindex required.");
        var v16 = V("16", 2, warnings: "On-disk format changes.");
        var v17 = V("17", 3, notes: "Config key renamed.");

        var plan = VersionChange.Plan(v14, v17, [v14, v15, v16, v17], "amd64");

        plan.Allowed.Should().BeTrue();
        plan.Notes.Should().Contain(n => n.Contains("Reindex"));
        plan.Notes.Should().Contain(n => n.Contains("Config key"));
        plan.Warnings.Should().Contain(w => w.Contains("on-disk") || w.Contains("On-disk"));
    }

    [Fact]
    public void A_downgrade_is_refused_unless_the_target_says_it_is_safe()
    {
        var v15 = V("15", 0);
        var v16 = V("16", 1);

        var plan = VersionChange.Plan(v16, v15, [v15, v16], "amd64");

        plan.Allowed.Should().BeFalse();
        plan.Reason.Should().Contain("backup");
    }

    [Fact]
    public void A_downgrade_is_allowed_when_the_target_supports_it()
    {
        var v15 = V("15", 0, allowsDowngrade: true);
        var v16 = V("16", 1);

        var plan = VersionChange.Plan(v16, v15, [v15, v16], "amd64");

        plan.Allowed.Should().BeTrue();
        plan.IsDowngrade.Should().BeTrue();
    }

    [Fact]
    public void Moving_to_the_version_it_is_already_on_is_refused()
    {
        var v16 = V("16", 0);

        VersionChange.Plan(v16, v16, [v16], "amd64").Allowed.Should().BeFalse();
    }

    [Fact]
    public void An_unpinned_destination_is_refused_here_too()
    {
        // The same gate as a fresh deploy: an upgrade is a deploy of a different image.
        var v16 = V("16", 0);
        var v17 = V("17", 1);
        v17.ImageDigest = null;

        VersionChange.Plan(v16, v17, [v16, v17], "amd64").Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_clean_upgrade_with_nothing_to_say_says_nothing()
    {
        // The guard on the notes: inventing reassurance is as bad as hiding a warning.
        var v16 = V("16", 0);
        var v17 = V("17", 1);

        var plan = VersionChange.Plan(v16, v17, [v16, v17], "amd64");

        plan.Allowed.Should().BeTrue();
        plan.Notes.Should().BeEmpty();
        plan.Warnings.Should().BeEmpty();
    }
}
