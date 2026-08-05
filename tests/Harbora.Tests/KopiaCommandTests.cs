using FluentAssertions;
using Harbora.Modules.Backup.Infrastructure;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The command builder is where injection is either prevented or introduced, so it is asserted
/// directly rather than through a process that would have to exist to run.
///
/// <para>
/// The property under test throughout: a hostile value stays exactly ONE element of the argument
/// list. Because <see cref="EngineProcessRunner"/> passes that list to
/// <c>ProcessStartInfo.ArgumentList</c> and never spawns a shell, an argument containing <c>;</c>
/// is a string the engine receives, not a second command (THREAT_MODEL T1).
/// </para>
/// </summary>
public class KopiaCommandTests
{
    private static readonly Guid RepositoryId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static KopiaOptions Options() => new()
    {
        BinaryPath = "kopia",
        ConfigDirectory = Path.Combine(Path.GetTempPath(), "kopia-config"),
        CacheDirectory = Path.Combine(Path.GetTempPath(), "kopia-cache")
    };

    private static string Absolute(string relative) =>
        Path.Combine(Path.GetTempPath(), relative);

    [Fact]
    public void The_password_never_appears_in_the_argument_list()
    {
        var arguments = KopiaCommands.CreateFilesystemRepository(Options(), RepositoryId, Absolute("repo"));

        arguments.Should().NotContain(a => a.Contains("password", StringComparison.OrdinalIgnoreCase));
        KopiaCommands.PasswordVariable.Should().Be("KOPIA_PASSWORD",
            "the password is supplied through the environment, where the process table cannot expose it");
    }

    [Fact]
    public void A_source_path_containing_shell_syntax_stays_one_argument()
    {
        // A directory really can be named this. It must reach the engine as a name.
        var hostile = Absolute("data; rm -rf ~");

        var arguments = KopiaCommands.CreateSnapshot(Options(), RepositoryId, hostile);

        arguments.Should().ContainSingle(a => a.Contains("; rm -rf ~", StringComparison.Ordinal));
        arguments.Should().NotContain(a => a == "rm");
    }

    [Fact]
    public void Positional_paths_are_separated_by_a_double_dash()
    {
        var arguments = KopiaCommands.CreateSnapshot(Options(), RepositoryId, Absolute("data"));

        var separator = arguments.IndexOf("--");
        separator.Should().BeGreaterThan(0, "so a path beginning with '-' cannot be read as a flag");
        arguments.Should().HaveCount(separator + 2, "the source path is the only positional argument");
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    public void Refuses_a_source_path_that_is_not_absolute(string path)
    {
        var act = () => KopiaCommands.CreateSnapshot(Options(), RepositoryId, path);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Refuses_a_path_containing_a_null_byte()
    {
        var act = () => KopiaCommands.CreateSnapshot(Options(), RepositoryId, Absolute("data\0evil"));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("$(whoami)")]
    [InlineData("abc; cat /etc/shadow")]
    [InlineData("--delete-all")]
    public void Refuses_a_hostile_snapshot_id(string snapshotId)
    {
        var act = () => KopiaCommands.Restore(Options(), RepositoryId, snapshotId, Absolute("restore"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Refuses_a_browse_path_that_climbs_out_of_the_snapshot()
    {
        var act = () => KopiaCommands.BrowseSnapshot(Options(), RepositoryId, "k9f3a1", "../../etc");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Refuses_a_tag_carrying_syntax()
    {
        var tags = new Dictionary<string, string> { ["target"] = "app; rm -rf /" };

        var act = () => KopiaCommands.CreateSnapshot(Options(), RepositoryId, Absolute("data"), tags);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Every_command_pins_its_own_config_file()
    {
        // Without this, two operations on different repositories share one config file and
        // "which repository am I connected to" becomes a race.
        var first = KopiaCommands.RepositoryStatus(Options(), Guid.CreateVersion7());
        var second = KopiaCommands.RepositoryStatus(Options(), Guid.CreateVersion7());

        var firstConfig = first.Single(a => a.StartsWith("--config-file=", StringComparison.Ordinal));
        var secondConfig = second.Single(a => a.StartsWith("--config-file=", StringComparison.Ordinal));

        firstConfig.Should().NotBe(secondConfig);
    }
}

/// <summary>
/// Redaction runs on everything an engine prints before it reaches a log, a column or a screen.
/// </summary>
public class EngineOutputRedactorTests
{
    [Fact]
    public void Masks_a_registered_secret_wherever_it_appears()
    {
        var redactor = new EngineOutputRedactor();
        redactor.Register("s3cret-password-value");

        var output = redactor.Redact("failed to open repository with s3cret-password-value at /repo");

        output.Should().NotContain("s3cret-password-value");
        output.Should().Contain(EngineOutputRedactor.Mask);
        output.Should().Contain("/repo", "non-secret context stays readable");
    }

    /// <summary>
    /// Longest first, or a password that contains a shorter registered value is masked in the middle
    /// and leaves its remainder visible.
    /// </summary>
    [Fact]
    public void Masks_the_longest_registered_value_first()
    {
        var redactor = new EngineOutputRedactor();
        redactor.Register("secret");
        redactor.Register("secret-and-more");

        var output = redactor.Redact("value=secret-and-more");

        output.Should().Be("value=" + EngineOutputRedactor.Mask);
    }

    [Fact]
    public void Ignores_values_too_short_to_be_secrets()
    {
        var redactor = new EngineOutputRedactor();
        redactor.Register("ab");

        // Masking a two-character value would blank ordinary words and hide nothing.
        redactor.Redact("a stable backup").Should().Be("a stable backup");
    }

    [Fact]
    public void Strips_control_characters()
    {
        var redactor = new EngineOutputRedactor();

        var output = redactor.Redact("line\0withcontrol");

        output.Should().Be("linewithcontrol");
    }

    [Fact]
    public void Handles_empty_output()
    {
        new EngineOutputRedactor().Redact(null).Should().BeEmpty();
    }
}
