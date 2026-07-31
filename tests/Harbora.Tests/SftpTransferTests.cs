using FluentAssertions;
using Harbora.Infrastructure.Backups;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sending a backup to an SFTP server.
///
/// The host key is the part that matters. Accepting whatever answers on the address — which is what
/// most quick implementations do — means the backup, and the credentials used to send it, can be
/// handed to anyone who can reach that address first.
/// </summary>
public class SftpTransferTests
{
    private const string Password = "s3cretpassword";

    [Fact]
    public void A_destination_with_no_host_key_is_refused_and_told_how_to_fix_it()
    {
        // The whole security property. Trusting on first use here would mean trusting on every use,
        // since the panel is the only thing that ever connects.
        var reason = SftpTransfer.WhyUnusable("backup.example.com", "harbora", hostKey: null);

        reason.Should().NotBeNull();
        reason.Should().Contain("host key").And.Contain("ssh-keyscan");
    }

    [Fact]
    public void A_complete_destination_is_accepted()
    {
        SftpTransfer.WhyUnusable("backup.example.com", "harbora", "backup.example.com ssh-ed25519 AAAA")
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null, "harbora")]
    [InlineData("", "harbora")]
    [InlineData("host", null)]
    [InlineData("host", "")]
    public void Missing_basics_are_named_rather_than_failing_at_transfer_time(string? host, string? user)
    {
        SftpTransfer.WhyUnusable(host, user, "key").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_password_travels_in_the_environment_and_never_in_the_process_list()
    {
        var upload = SftpTransfer.Upload("h", 22, "u", Password, "/backups", "db.sql.gz");

        string.Join(" ", upload.Command).Should().NotContain(Password);
        upload.Env["SSHPASS"].Should().Be(Password);
    }

    [Fact]
    public void The_transfer_refuses_an_unknown_host_key()
    {
        // Not a preference: without this the connection succeeds against an impostor.
        var command = string.Join(" ", SftpTransfer.Upload("h", 22, "u", Password, null, "x.gz").Command);

        command.Should().Contain("StrictHostKeyChecking=yes");
        command.Should().Contain("known_hosts");
    }

    [Fact]
    public void An_upload_creates_the_remote_directory_first()
    {
        // It is almost never already there, and sftp's own error for that is obscure.
        var command = string.Join(" ", SftpTransfer.Upload("h", 22, "u", Password, "/srv/backups", "db.sql.gz").Command);

        // Specifically the remote mkdir: the script also does "mkdir -p ~/.ssh" for the host key,
        // so a looser assertion here passed even with the remote one removed.
        command.Should().Contain("-mkdir \"/srv/backups\"");
        command.Should().Contain("cd \"/srv/backups\"");
        command.Should().Contain("put \"/backup/db.sql.gz\"");
    }

    [Fact]
    public void With_no_directory_the_file_goes_where_the_account_lands()
    {
        var command = string.Join(" ", SftpTransfer.Upload("h", 22, "u", Password, null, "db.sql.gz").Command);

        command.Should().NotContain("cd ");
        command.Should().Contain("put \"/backup/db.sql.gz\"");
    }

    [Fact]
    public void A_download_writes_into_staging_where_the_panel_can_read_it()
    {
        var command = string.Join(" ", SftpTransfer.Download("h", 22, "u", Password, "/srv", "db.sql.gz").Command);

        command.Should().Contain("get \"/srv/db.sql.gz\" \"/backup/db.sql.gz\"");
    }

    [Fact]
    public void A_trailing_slash_on_the_directory_does_not_produce_a_doubled_path()
    {
        var command = string.Join(" ", SftpTransfer.Download("h", 22, "u", Password, "/srv/", "db.sql.gz").Command);

        command.Should().Contain("\"/srv/db.sql.gz\"").And.NotContain("//db.sql.gz");
    }

    [Fact]
    public void A_delete_removes_exactly_the_one_artifact()
    {
        // Retention runs this unattended, so anything broader than one file is a data-loss bug.
        var command = string.Join(" ", SftpTransfer.Delete("h", 22, "u", Password, "/srv", "db.sql.gz").Command);

        command.Should().Contain("rm \"/srv/db.sql.gz\"");
        command.Should().NotContain("*");
    }

    [Fact]
    public void A_port_other_than_the_default_is_honoured()
    {
        string.Join(" ", SftpTransfer.Upload("h", 2222, "u", Password, null, "x.gz").Command)
            .Should().Contain("-P 2222");
    }

    [Fact]
    public void A_quote_in_a_name_cannot_end_the_command()
    {
        var command = string.Join(" ", SftpTransfer.Upload("h", 22, "u'; rm -rf /", Password, null, "x.gz").Command);

        command.Should().Contain(@"'u'\''; rm -rf /'");
    }
}
