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

        // Anchored on the add-schedule form rather than on a <details> element. This looked for the
        // disclosure that used to follow the loop, and the page no longer has one — the forms are open
        // cards now — so the delimiter became something a restyle will not remove. What is being
        // checked is unchanged.
        var loopEnd = markup.IndexOf("asp-action=\"CreateSchedule\"", loopStart, StringComparison.Ordinal);
        loopEnd.Should().BeGreaterThan(loopStart, "the loop must be followed by the add-schedule form");

        var loopBody = markup[loopStart..loopEnd];

        loopBody.Should().NotContain("data-when-sftp",
            "the SFTP block does not belong inside the per-schedule loop");
    }

    // ---- the controls the page grew ------------------------------------------------------------

    [Fact]
    public void Deleting_a_backup_asks_for_a_word_the_way_restoring_does()
    {
        // Deleting the last copy of something is the one action here that taking another backup cannot
        // undo, so it gets at least the friction a restore gets. Both go through one handler keyed on
        // data-confirm-word: a second copy of the prompt logic would be a second chance for one of them
        // to quietly stop asking.
        var markup = Markup;

        markup.Should().Contain(@"data-confirm-word=""DELETE""",
            "the delete form must require the word to be typed");
        markup.Should().Contain(@"data-confirm-word=""RESTORE""",
            "and the restore form must still require its own");
        markup.Should().Contain("[data-confirm-word]",
            "one handler serves every form that wants a typed word");
    }

    [Fact]
    public void Pausing_a_schedule_posts_a_value_rather_than_an_absence()
    {
        // An unchecked checkbox posts nothing at all, so the hidden false is what makes "paused" a
        // value somebody sent. It has to come FIRST: last value wins, so a hidden false after the
        // checkbox would overwrite every tick and no schedule could ever be enabled.
        var markup = Markup;

        var start = markup.IndexOf("asp-action=\"UpdateSchedule\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the schedule edit form must exist");
        var form = markup[start..markup.IndexOf("</form>", start, StringComparison.Ordinal)];

        var hiddenAt = form.IndexOf(@"type=""hidden"" name=""enabled"" value=""false""", StringComparison.Ordinal);
        var checkboxAt = form.IndexOf(@"type=""checkbox"" name=""enabled""", StringComparison.Ordinal);

        hiddenAt.Should().BeGreaterThan(-1, "an unchecked box posts nothing, so a false has to be sent");
        checkboxAt.Should().BeGreaterThan(hiddenAt, "the hidden false must come before the checkbox");
    }

    [Fact]
    public void The_upload_form_posts_a_file_a_target_and_a_destination()
    {
        // All three, because each one missing is its own kind of wrong: no file is nothing stored, no
        // target is an archive a restore cannot be pointed at, and no destination is bytes with nowhere
        // to live. The encoding matters too — without it the file arrives as a file name.
        var markup = Markup;

        var start = markup.IndexOf("asp-action=\"Upload\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the upload form must exist");
        var form = markup[start..markup.IndexOf("</form>", start, StringComparison.Ordinal)];

        form.Should().Contain(@"enctype=""multipart/form-data""");
        form.Should().Contain(@"name=""file""").And.Contain(@"type=""file""");
        form.Should().Contain(@"name=""target""");
        form.Should().Contain(@"name=""destinationId""");
    }

    [Fact]
    public void A_destination_can_be_corrected_tested_and_removed()
    {
        // Destinations could only be created, so a rotated key meant adding a second one and leaving
        // the first on the page failing every night.
        var markup = Markup;

        markup.Should().Contain("asp-action=\"UpdateDestination\"", "a destination must be correctable");
        markup.Should().Contain("asp-action=\"TestDestination\"", "and provable before a backup needs it");
        markup.Should().Contain("asp-action=\"DeleteDestination\"", "and removable once nothing points at it");
    }

    [Fact]
    public void A_stored_secret_is_never_rendered_back_into_the_form()
    {
        // The edit form shows every other column and must not show these two: they are encrypted at
        // rest, and a value attribute is exactly where a decrypted secret would end up in a page. A
        // blank box means "leave it alone", which is what the placeholder says instead.
        var markup = Markup;

        markup.Should().NotContain("value=\"@d.EncryptedSecretKey",
            "an encrypted secret must not be echoed into the form");
        markup.Should().NotContain("value=\"@d.EncryptedSftpPassword",
            "nor the SFTP one");
        markup.Should().Contain("name=\"secretKey\" type=\"password\"",
            "the box exists to change the secret, not to display it");
    }
}
