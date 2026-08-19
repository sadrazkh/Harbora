using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The Backups page's markup, checked structurally by slicing the actual section out of source — a
/// plain "does this string appear in the file" search would have passed for the entire time
/// HARBORA-0008's SFTP block sat in the wrong container, because the string was genuinely in the file.
///
/// <para>
/// The 690-line <c>Index.cshtml</c> this class originally read has since been split into one partial
/// per concern (destinations, schedules, quick actions, history) — the file paths below point at
/// wherever each fact actually lives now. <see cref="BackupDestinationHttpTests"/> covers the same
/// SFTP-placement question the harder way, by rendering the composed page through a real request and
/// parsing the DOM; it does not care which file a section's markup lives in, which is why it kept
/// passing untouched through the split. This class is kept for the parts still worth checking as raw
/// source (confirmation words, encrypted-secret handling) and updated to the new file boundaries.
/// </para>
/// </summary>
public class BackupDestinationFormTests
{
    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Views", "Backups", fileName));

    private static string IndexMarkup => Read("Index.cshtml");
    private static string DestinationsMarkup => Read("_Destinations.cshtml");
    private static string SchedulesMarkup => Read("_Schedules.cshtml");
    private static string QuickActionsMarkup => Read("_QuickActions.cshtml");
    private static string HistoryMarkup => Read("_History.cshtml");

    [Fact]
    public void The_sftp_fields_are_inside_the_destination_form()
    {
        var markup = DestinationsMarkup;

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
        // schedule, instead of once in the destination form (now a sibling file, _Destinations.cshtml).
        var markup = SchedulesMarkup;

        var loopStart = markup.IndexOf("@foreach (var s in Model.Page.Schedules)", StringComparison.Ordinal);
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

        // And the file that actually renders the destination form has no per-schedule loop of its own
        // — it only counts schedules per destination, it does not render a row per one.
        DestinationsMarkup.Should().NotContain("@foreach (var s in Model.Page.Schedules)",
            "the destinations partial must not carry its own copy of the schedules loop");
    }

    // ---- the controls the page grew ------------------------------------------------------------

    [Fact]
    public void Deleting_a_backup_asks_for_a_word_the_way_restoring_does()
    {
        // Deleting the last copy of something is the one action here that taking another backup cannot
        // undo, so it gets at least the friction a restore gets. Both go through one handler keyed on
        // data-confirm-word: a second copy of the prompt logic would be a second chance for one of them
        // to quietly stop asking.
        var markup = HistoryMarkup;

        markup.Should().Contain(@"data-confirm-word=""DELETE""",
            "the delete form must require the word to be typed");
        markup.Should().Contain(@"data-confirm-word=""RESTORE""",
            "and the restore form must still require its own");

        // The one handler lives in Index.cshtml's own @section Scripts — Razor sections can only be
        // declared by the top-level view, so it could not travel into a partial with the forms it
        // serves.
        IndexMarkup.Should().Contain("[data-confirm-word]",
            "one handler serves every form that wants a typed word");
    }

    [Fact]
    public void Every_destructive_action_on_the_page_types_a_word_rather_than_asking_a_native_confirm()
    {
        // Do-not-change list item 19: extend the destructive-confirmation pattern, never downgrade to
        // a native confirm() dialog. Before this redesign, deleting a destination or a schedule did
        // exactly that, and deleting a delivery channel asked nothing at all. All three now go through
        // the same data-confirm-word handler the backup Delete and Restore actions already used.
        foreach (var (fileName, markup) in new[]
                 {
                     ("_Destinations.cshtml", DestinationsMarkup),
                     ("_Schedules.cshtml", SchedulesMarkup),
                     ("_History.cshtml", HistoryMarkup),
                 })
        {
            markup.Should().NotContain("return confirm(",
                $"{fileName} must not fall back to a native confirm() dialog");
        }

        DestinationsMarkup.Should().Contain(@"asp-action=""DeleteDestination""")
            .And.Contain(@"data-confirm-word=""DELETE""",
                "removing a destination must be typed-confirmed like every other delete on this page");
        SchedulesMarkup.Should().Contain(@"asp-action=""DeleteSchedule""")
            .And.Contain(@"data-confirm-word=""DELETE""");
    }

    [Fact]
    public void Pausing_a_schedule_posts_a_value_rather_than_an_absence()
    {
        // An unchecked checkbox posts nothing at all, so the hidden false is what makes "paused" a
        // value somebody sent. It has to come FIRST: last value wins, so a hidden false after the
        // checkbox would overwrite every tick and no schedule could ever be enabled.
        var markup = SchedulesMarkup;

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
        var markup = QuickActionsMarkup;

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
        var markup = DestinationsMarkup;

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
        var markup = DestinationsMarkup;

        markup.Should().NotContain("value=\"@d.EncryptedSecretKey",
            "an encrypted secret must not be echoed into the form");
        markup.Should().NotContain("value=\"@d.EncryptedSftpPassword",
            "nor the SFTP one");
        markup.Should().Contain("name=\"secretKey\" type=\"password\"",
            "the box exists to change the secret, not to display it");
    }

    [Fact]
    public void The_sftp_credentials_are_folded_behind_advanced_not_hidden()
    {
        // Do-not-change list item 23, PanelMode fold-never-remove, applied to the one genuinely
        // specialist material this page has: the fields stay in the form (submittable whether the
        // disclosure is open or closed) and the fold's open/closed state is computed, not hardcoded.
        var markup = DestinationsMarkup;

        var sftpBlockStart = markup.IndexOf("data-when-sftp", StringComparison.Ordinal);
        sftpBlockStart.Should().BeGreaterThan(-1);

        markup.IndexOf("Design/_AdvancedStart", sftpBlockStart, StringComparison.Ordinal)
            .Should().BeGreaterThan(sftpBlockStart, "the SFTP fields must be wrapped in the shared fold");
        markup.Should().Contain("Model.SftpAdvancedOpen",
            "whether the fold starts open must be computed (Advanced mode, or a rejected submission) " +
            "rather than always folded or always open");
    }
}
