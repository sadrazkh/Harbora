using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The backup destination form, rendered through a real request (backlog HARBORA-0008: "SFTP fields
/// out of the schedules loop").
///
/// <para>
/// The defect as filed: <c>data-when-sftp</c> rendered inside the Schedules <c>@@foreach</c> instead of
/// the destination-creation form that submits it, so <c>form.querySelector('[data-when-sftp]')</c> found
/// nothing on page load and threw — taking the whole Local/S3/SFTP toggle down with it, and leaving no
/// way to create an SFTP destination from the UI even though the backend already supported one. No
/// assertion over the Razor source alone can catch this: the <c>data-when-sftp</c> markup genuinely is
/// in the file, only its container is wrong. That is why, like <see cref="FunctionEditorHttpTests"/>,
/// this class renders the page through a real HTTP request and parses the DOM with AngleSharp rather
/// than grepping source.
/// </para>
///
/// <para>
/// <b>Already fixed — verified, not assumed.</b> <c>git log --follow</c> on
/// <c>Views/Backups/Index.cshtml</c> shows the block was moved into the destination form by commit
/// <c>995ebe7</c> (2026-08-07, "Fix the nine defects a form, a bell and a bundle were quietly
/// shipping"), and a source-slicing regression test already guards the shape
/// (<see cref="BackupDestinationFormTests"/>). Neither of the two commits that touched this view since
/// (<c>181a49b</c>, <c>64662d6</c>) reintroduced it. This class adds the stronger, DOM-rendered version
/// the brief asked for — proof over the actual parsed page, plus an end-to-end POST that proves a
/// customer can really create an SFTP destination — rather than a second fix. Its RED side was verified
/// by hand: temporarily moving <c>data-when-sftp</c> back into the Schedules loop made every fact below
/// stop holding (the toggle's own lookup found nothing, the block appeared twice — once per seeded
/// schedule — nested in schedule rows instead of the destination form, and the create-SFTP POST test
/// still passed on its own, because the controller-side support was never what was broken), then the
/// file was restored.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class BackupDestinationHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    /// <summary>Walks up from a form to the card <c>&lt;section&gt;</c> that renders it — the same
    /// grouping a person reading the page sees, since neither destinations nor schedules give their
    /// rows an id to select by.</summary>
    private static IElement? SectionAncestor(IElement start)
    {
        var current = start.ParentElement;
        while (current is not null)
        {
            if (string.Equals(current.TagName, "section", StringComparison.OrdinalIgnoreCase)) return current;
            current = current.ParentElement;
        }
        return null;
    }

    [Fact]
    public async Task The_toggle_scripts_three_lookups_all_find_their_field_block_inside_the_destination_form()
    {
        Panel.GivenUser(fixture.WorkspaceId, "dest-toggle@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", "dest-toggle@example.com");

        var response = await client.GetAsync("/backups");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await ParseAsync(await response.Content.ReadAsStringAsync());

        var form = document.QuerySelector("[data-dest-form]");
        form.Should().NotBeNull("the add-destination form must render");

        // Exactly what the page's toggle script does on load: three lookups, scoped to this one form.
        // The historical bug was the third of these returning null and throwing — which also took the
        // first two down with it, since one unhandled exception stops the rest of the handler.
        form!.QuerySelector("[data-when-local]").Should().NotBeNull();
        form.QuerySelector("[data-when-s3]").Should().NotBeNull();
        form.QuerySelector("[data-when-sftp]").Should().NotBeNull(
            "the SFTP block must live where the toggle script looks for it, or the script throws on " +
            "load and takes the Local/S3 toggle down with it (HARBORA-0008)");

        form.QuerySelector("input[name='sftpHost']").Should().NotBeNull(
            "the SFTP host field must actually be submittable from this form");
        form.QuerySelector("[name='sftpHostKey']").Should().NotBeNull(
            "the host key field must be here too — without it no SFTP destination can pass validation");
    }

    [Fact]
    public async Task The_sftp_fields_do_not_belong_to_any_schedule_row()
    {
        var destinationId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.BackupDestinations.Add(new BackupDestination
            {
                Id = destinationId, WorkspaceId = fixture.WorkspaceId, Name = "nightly-store",
                Type = BackupDestinationType.Local, LocalPath = "/var/lib/harbora/backups"
            });
            // Two rows, not one: if the SFTP block were still inside this loop it would be duplicated
            // once per row, which a single seeded schedule could not show.
            db.BackupSchedules.AddRange(
                new BackupSchedule
                {
                    WorkspaceId = fixture.WorkspaceId, DestinationId = destinationId,
                    Type = BackupType.FullPlatform, TargetRef = "platform",
                    IntervalHours = 24, RetentionCount = 7, IsEnabled = true
                },
                new BackupSchedule
                {
                    WorkspaceId = fixture.WorkspaceId, DestinationId = destinationId,
                    Type = BackupType.FullPlatform, TargetRef = "platform",
                    IntervalHours = 12, RetentionCount = 3, IsEnabled = true
                });
        });

        Panel.GivenUser(fixture.WorkspaceId, "dest-rows@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", "dest-rows@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync("/backups")).Content.ReadAsStringAsync());

        var sftpBlocks = document.QuerySelectorAll("[data-when-sftp]");
        sftpBlocks.Length.Should().Be(1,
            "the SFTP field block must render exactly once, in the destination form — not once per " +
            "schedule row");

        document.QuerySelector("[data-dest-form]")!.Contains(sftpBlocks[0]).Should().BeTrue(
            "the one SFTP block on the page must belong to the destination-creation form, not a schedule");

        // Every schedule row's own card, identified the way a browser would find it: by the form that
        // saves that specific row (UpdateSchedule and DeleteSchedule both route under
        // /backups/schedules/{id}, unlike the add-schedule form which posts to /backups/schedules).
        var scheduleRowForms = document.QuerySelectorAll("form[action^='/backups/schedules/']");
        scheduleRowForms.Length.Should().BeGreaterThan(0, "the two seeded schedules must render as rows");

        var scheduleSections = scheduleRowForms
            .Select(SectionAncestor)
            .Where(section => section is not null)
            .Distinct()
            .ToList();

        foreach (var section in scheduleSections)
            section!.QuerySelector("[data-when-sftp]").Should().BeNull(
                "a schedule row must not carry the destination form's SFTP fields (HARBORA-0008)");
    }

    [Fact]
    public async Task An_sftp_destination_is_creatable_end_to_end_through_the_rendered_form()
    {
        Panel.GivenUser(fixture.WorkspaceId, "dest-create@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.162", "dest-create@example.com");

        var page = await client.GetAsync("/backups");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await ParseAsync(await page.Content.ReadAsStringAsync());

        var form = document.QuerySelector("[data-dest-form]");
        form.Should().NotBeNull();
        form!.QuerySelector("[data-when-sftp]").Should().NotBeNull(
            "the fields being posted below must actually be the ones this form renders");

        // The URL the rendered form itself submits to, not a hardcoded guess at the route.
        var action = form.GetAttribute("action");
        action.Should().NotBeNullOrEmpty();

        var token = document.QuerySelector("input[name='__RequestVerificationToken']")?.GetAttribute("value");
        token.Should().NotBeNullOrEmpty("the destination form must carry its own antiforgery token");

        var response = await client.PostFormAsync(action!, token!,
            ("name", "offsite-sftp"), ("type", "Sftp"),
            ("sftpHost", "backup.example.com"), ("sftpPort", "2222"),
            ("sftpUsername", "harbora"), ("sftpPassword", "s3cret"),
            ("sftpDirectory", "/srv/harbora-backups"),
            ("sftpHostKey", "backup.example.com ssh-ed25519 AAAAexample"));

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "a fully filled-in SFTP destination must be accepted, not just parsed");
        response.RedirectPath().Should().Be("/backups");

        var stored = Panel.Read(db => db.BackupDestinations.AsNoTracking()
            .SingleOrDefault(d => d.WorkspaceId == fixture.WorkspaceId && d.Name == "offsite-sftp"));

        stored.Should().NotBeNull("the destination the customer configured through the UI must be saved");
        stored!.Type.Should().Be(BackupDestinationType.Sftp);
        stored.SftpHost.Should().Be("backup.example.com");
        stored.SftpPort.Should().Be(2222);
        stored.SftpUsername.Should().Be("harbora");
        stored.SftpDirectory.Should().Be("/srv/harbora-backups");
        stored.SftpHostKey.Should().Be("backup.example.com ssh-ed25519 AAAAexample");
        stored.EncryptedSftpPassword.Should().NotBeNullOrEmpty()
            .And.NotBe("s3cret", "the password must be stored encrypted, not verbatim");
    }
}
