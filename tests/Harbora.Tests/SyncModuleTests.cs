using FluentAssertions;
using Harbora.Modules.Sync.Contracts;
using Harbora.Modules.Sync.Domain;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sync validation.
///
/// <para>
/// The rules worth having are the ones that catch a configuration which looks complete and either
/// moves nothing, or — far worse — sends readable files to a device that exists precisely so it
/// cannot read them.
/// </para>
/// </summary>
public class SyncValidationTests
{
    private const string ValidId =
        "P56IOI7-MZJNU2Y-IQGDRE6-I2JQOTP-ZLQGRQD-D5JQNSY-JYQMQVL-QAKZQAP";

    [Fact]
    public void Accepts_a_device_id_in_the_form_syncthing_prints()
    {
        SyncValidation.IsValidDeviceId(ValidId).Should().BeTrue();
    }

    [Fact]
    public void Accepts_the_same_id_without_its_separators()
    {
        SyncValidation.IsValidDeviceId(ValidId.Replace("-", "")).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("P56IOI7-MZJNU2Y-IQGDRE6-I2JQOTP-ZLQGRQD-D5JQNSY-JYQMQVL")]     // 7 groups
    [InlineData("P56IOI7-MZJNU2Y-IQGDRE6-I2JQOTP-ZLQGRQD-D5JQNSY-JYQMQVL-QAKZQA!")]
    public void Rejects_anything_that_is_not_a_device_id(string value)
    {
        SyncValidation.IsValidDeviceId(value).Should().BeFalse();
    }

    [Fact]
    public void Normalises_an_id_to_the_grouped_form()
    {
        var normalised = SyncValidation.NormaliseDeviceId(ValidId.Replace("-", "").ToLowerInvariant());

        normalised.Should().Be(ValidId);
        normalised.Split('-').Should().HaveCount(8).And.OnlyContain(g => g.Length == 7);
    }

    // --- membership ---------------------------------------------------------------------------

    private static SyncDevice Device(bool untrusted = false, SyncDeviceStatus status = SyncDeviceStatus.Connected)
        => new() { Name = "Storage node", EngineDeviceId = ValidId, IsUntrusted = untrusted, Status = status };

    /// <summary>
    /// The check that matters most in this module. An untrusted device exists so it cannot read what
    /// it stores; any mode but the encrypted one silently sends it plaintext.
    /// </summary>
    [Theory]
    [InlineData(SyncMode.SendAndReceive)]
    [InlineData(SyncMode.SendOnly)]
    [InlineData(SyncMode.ReceiveOnly)]
    public void An_untrusted_device_cannot_be_joined_in_a_plaintext_mode(SyncMode mode)
    {
        var error = SyncValidation.ValidateMembership(Device(untrusted: true), mode, "a-long-password");

        error.Should().NotBeNull();
        error!.Message.Should().Contain("readable");
    }

    [Fact]
    public void A_trusted_device_cannot_be_given_an_encrypted_only_share()
    {
        // Otherwise "which devices can read this folder" stops being answerable from the device list.
        var error = SyncValidation.ValidateMembership(
            Device(untrusted: false), SyncMode.EncryptedReceiveOnly, "a-long-password");

        error.Should().NotBeNull();
        error!.Message.Should().Contain("Mark it untrusted");
    }

    [Fact]
    public void An_encrypted_share_without_a_password_is_refused()
    {
        var error = SyncValidation.ValidateMembership(
            Device(untrusted: true), SyncMode.EncryptedReceiveOnly, null);

        error.Should().NotBeNull();
        error!.Message.Should().Contain("readable files");
    }

    [Fact]
    public void An_encrypted_share_with_a_short_password_is_refused()
    {
        var error = SyncValidation.ValidateMembership(
            Device(untrusted: true), SyncMode.EncryptedReceiveOnly, "short");

        error.Should().NotBeNull();
        error!.Message.Should().Contain("12 characters");
    }

    [Fact]
    public void An_encrypted_share_to_an_untrusted_device_is_allowed()
    {
        SyncValidation.ValidateMembership(
            Device(untrusted: true), SyncMode.EncryptedReceiveOnly, "a-sufficiently-long-password")
            .Should().BeNull();
    }

    [Fact]
    public void A_revoked_device_cannot_join_anything()
    {
        var error = SyncValidation.ValidateMembership(
            Device(status: SyncDeviceStatus.Revoked), SyncMode.SendAndReceive, null);

        error.Should().NotBeNull();
        error!.Message.Should().Contain("revoked");
    }

    // --- configurations that quietly do nothing -------------------------------------------------

    [Fact]
    public void Two_receive_only_ends_would_never_sync()
    {
        SyncValidation.WouldNeverSync([SyncMode.ReceiveOnly, SyncMode.EncryptedReceiveOnly])
            .Should().BeTrue("nothing in that space ever publishes a change");
    }

    [Fact]
    public void Two_send_only_ends_would_never_sync()
    {
        SyncValidation.WouldNeverSync([SyncMode.SendOnly, SyncMode.SendOnly])
            .Should().BeTrue("nothing in that space ever accepts a change");
    }

    [Fact]
    public void A_send_only_laptop_and_a_receiving_node_is_a_valid_arrangement()
    {
        SyncValidation.WouldNeverSync([SyncMode.SendOnly, SyncMode.EncryptedReceiveOnly])
            .Should().BeFalse();
    }

    [Fact]
    public void A_space_with_one_device_is_not_reported_as_broken()
    {
        // A space you have not shared yet is unfinished, not misconfigured.
        SyncValidation.WouldNeverSync([SyncMode.SendAndReceive]).Should().BeFalse();
    }

    // --- space validation ------------------------------------------------------------------------

    [Fact]
    public void A_versioning_mode_that_keeps_nothing_is_refused()
    {
        var space = new SyncSpace
        {
            Name = "Documents",
            LocalPath = "/srv/sync/documents",
            VersioningMode = SyncVersioningMode.Simple,
            VersioningParameter = 0
        };

        SyncValidation.ValidateSpace(space)
            .Should().Contain(e => e.Field == nameof(SyncSpace.VersioningParameter));
    }

    [Fact]
    public void Accepts_a_well_formed_space()
    {
        var space = new SyncSpace { Name = "Documents", LocalPath = "/srv/sync/documents" };

        SyncValidation.ValidateSpace(space).Should().BeEmpty();
    }
}

/// <summary>
/// Reading the engine's conflict filenames, so a conflict can be shown as "your copy of report.docx"
/// rather than as a file with an alarming name that people delete without reading.
/// </summary>
public class SyncConflictNameTests
{
    [Fact]
    public void Recovers_the_original_path_keeping_its_extension()
    {
        var parsed = SyncConflictName.Parse("notes/report.sync-conflict-20260805-101500-P56IOI7.docx");

        parsed.Should().NotBeNull();
        parsed!.Value.OriginalPath.Should().Be("notes/report.docx");
    }

    [Fact]
    public void Recovers_the_device_and_the_time()
    {
        var parsed = SyncConflictName.Parse("report.sync-conflict-20260805-101500-P56IOI7.docx");

        parsed!.Value.Device.Should().Be("P56IOI7");
        parsed.Value.At.Should().NotBeNull();
        parsed.Value.At!.Value.Year.Should().Be(2026);
        parsed.Value.At.Value.Month.Should().Be(8);
    }

    [Fact]
    public void Handles_a_file_with_no_extension()
    {
        var parsed = SyncConflictName.Parse("Makefile.sync-conflict-20260805-101500-P56IOI7");

        parsed.Should().NotBeNull();
        parsed!.Value.OriginalPath.Should().Be("Makefile");
    }

    [Fact]
    public void Returns_nothing_for_an_ordinary_file()
    {
        SyncConflictName.Parse("notes/report.docx").Should().BeNull();
        SyncConflictName.IsConflictFile("notes/report.docx").Should().BeFalse();
    }

    /// <summary>
    /// A guess about who changed a file is worse than saying nothing, because it gets acted on.
    /// </summary>
    [Fact]
    public void Reports_no_device_when_the_name_does_not_carry_one()
    {
        var parsed = SyncConflictName.Parse("report.sync-conflict-20260805-101500.docx");

        parsed.Should().NotBeNull();
        parsed!.Value.Device.Should().BeNull();
    }

    [Fact]
    public void Recognises_a_conflict_file_by_its_marker()
    {
        SyncConflictName.IsConflictFile("a.sync-conflict-20260805-101500-ABCDEFG.txt").Should().BeTrue();
    }
}
