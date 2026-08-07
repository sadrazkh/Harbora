using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The SFTP destination fields, structurally.
///
/// They used to render inside the Schedules <c>@@foreach</c>, once per schedule, rather than inside
/// the destination form that actually submits them (<c>CreateDestination</c>) — so
/// <c>form.querySelector('[data-when-sftp]')</c> found nothing on page load, threw, and took the
/// Local/S3 toggle down with it too. An SFTP destination could not be created at all, though the
/// option was offered and the backend already supported it.
///
/// Checked structurally — by slicing the destination form out of the source and asserting the SFTP
/// fields are inside that slice — rather than with a plain "does this string appear in the file"
/// search, because that search would have passed for the entire time the block was in the wrong
/// place.
/// </summary>
public class BackupDestinationFormTests
{
    private static string Markup =>
        File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Views", "Backups", "Index.cshtml"));

    [Fact]
    public void The_sftp_fields_are_inside_the_destination_form()
    {
        var markup = Markup;

        var formStart = markup.IndexOf("data-dest-form", StringComparison.Ordinal);
        formStart.Should().BeGreaterThan(-1, "the destination form must exist");

        var formEnd = markup.IndexOf("</form>", formStart, StringComparison.Ordinal);
        formEnd.Should().BeGreaterThan(formStart, "the destination form must close");

        var destinationForm = markup[formStart..formEnd];

        destinationForm.Should().Contain("data-when-sftp",
            "the SFTP fields must render inside the destination form the Sftp option submits to, " +
            "or the toggle script's querySelector finds nothing and throws on page load");
        destinationForm.Should().Contain("name=\"sftpHost\"");
        destinationForm.Should().Contain("name=\"sftpHostKey\"");
    }

    [Fact]
    public void The_schedules_loop_no_longer_carries_the_sftp_fields()
    {
        // The regression this defect actually was: the block lived here, re-rendered once per
        // schedule, instead of once in the destination form above.
        var markup = Markup;

        var loopStart = markup.IndexOf("@foreach (var s in Model.Schedules)", StringComparison.Ordinal);
        loopStart.Should().BeGreaterThan(-1, "the schedules loop must exist");

        var loopEnd = markup.IndexOf("<details>", loopStart, StringComparison.Ordinal);
        loopEnd.Should().BeGreaterThan(loopStart, "the loop must be followed by the add-schedule disclosure");

        var loopBody = markup[loopStart..loopEnd];

        loopBody.Should().NotContain("data-when-sftp",
            "the SFTP block does not belong inside the per-schedule loop");
    }
}
