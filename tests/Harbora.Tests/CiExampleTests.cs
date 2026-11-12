using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Cli;
using Spectre.Console.Cli;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Harbora.Tests;

/// <summary>
/// The CI files a person copies into their repository (<c>examples/ci/</c>), held to the CLI they call
/// and to the two guides that show them, for the reason <see cref="LaunchChapterTests"/> gives: a page
/// that reads well is not thereby still true.
///
/// <para>
/// Nothing here runs a workflow. Only GitHub or GitLab can prove one, so what can be checked from a
/// test is checked: that each file parses, that every command and flag in it is one the CLI takes,
/// that both guides show the same lines, and that the place they send a person for a token is on the
/// screen. Each failure names the file to edit, because whoever breaks one will be editing code.
/// </para>
/// </summary>
public sealed class CiExampleTests
{
    public static TheoryData<string> Examples => new() { "github-actions.yml", "gitlab-ci.yml" };

    private static readonly string[] Guides = ["docs/cli-deploy.md", "docs/tutorial/12-cli-deploy.md"];

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine([TestPaths.RepoRoot, .. relative.Split('/')]));

    private static string Example(string name) => Read($"examples/ci/{name}");

    /// <summary>What a CI service runs: every line but blank ones and comments.</summary>
    private static List<string> CodeLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))
            .Select(line => line.TrimEnd())
            .ToList();

    [Theory, MemberData(nameof(Examples))]
    public void Each_example_is_yaml_its_ci_service_can_read_with_a_deploy_in_it(string name)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(Example(name)));
        var root = (YamlMappingNode)yaml.Documents.Single().RootNode;

        IEnumerable<string> commands;
        if (name.StartsWith("github", StringComparison.Ordinal))
        {
            root.Children.Keys.Select(k => k.ToString()).Should().Contain(["on", "jobs"]);
            var steps = (YamlSequenceNode)((YamlMappingNode)((YamlMappingNode)root["jobs"])["deploy"])["steps"];
            commands = steps.Children.Cast<YamlMappingNode>()
                .Where(step => step.Children.ContainsKey("run"))
                .Select(step => step["run"].ToString());
        }
        else
        {
            var job = (YamlMappingNode)root["deploy"];
            job.Children.Keys.Select(k => k.ToString()).Should().Contain(["image", "script"]);
            commands = ((YamlSequenceNode)job["script"]).Children.Select(c => c.ToString());
        }

        commands.Should().Contain(c => c.StartsWith("harbora deploy", StringComparison.Ordinal),
            $"examples/ci/{name} exists to run harbora deploy");
    }

    /// <summary>
    /// The commands are registered inside a lambda in a top-level program, so their names are read
    /// from its source, the way <see cref="DocumentationDriftTests"/> reads them; the flags each one
    /// takes are read off its settings type, which is the thing Spectre parses.
    /// </summary>
    [Theory, MemberData(nameof(Examples))]
    public void Every_harbora_command_and_flag_in_an_example_is_one_the_cli_takes(string name)
    {
        var program = Read("src/Harbora.Cli/Program.cs");
        var invocations = CodeLines(Example(name))
            .Select(line => Regex.Match(line, @"(?<![\w./-])harbora ([a-z][a-z-]*)(.*)$"))
            .Where(m => m.Success)
            .ToList();

        invocations.Should().NotBeEmpty($"examples/ci/{name} runs the CLI");

        foreach (var invocation in invocations)
        {
            var command = invocation.Groups[1].Value;
            var registered = Regex.Match(program, $@"AddCommand<(\w+)>\(""{command}""\)");
            registered.Success.Should().BeTrue(
                $"examples/ci/{name} runs `harbora {command}`, which src/Harbora.Cli/Program.cs does not register");

            var declared = OptionsOf(registered.Groups[1].Value);
            var flags = Regex.Matches(invocation.Groups[2].Value, @"(?<=\s)(--[a-z][a-z-]*|-[a-zA-Z])(?=[\s=]|$)")
                .Select(m => m.Value)
                .ToList();

            flags.Should().OnlyContain(flag => declared.Contains(flag),
                $"examples/ci/{name} passes these to `harbora {command}`, and its settings must declare each one");
        }
    }

    private static HashSet<string> OptionsOf(string commandType)
    {
        var type = typeof(DeployCommand).Assembly.GetType($"Harbora.Cli.{commandType}", throwOnError: true)!;
        var settings = type;
        while (settings is not null && !(settings.IsGenericType && settings.GetGenericArguments() is [{ } arg] && typeof(CommandSettings).IsAssignableFrom(arg)))
            settings = settings.BaseType;

        settings.Should().NotBeNull($"{commandType} should be a command with settings");
        var options = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in settings!.GetGenericArguments()[0].GetProperties())
        {
            if (property.GetCustomAttribute<CommandOptionAttribute>() is not { } option) continue;
            foreach (var name in option.LongNames) options.Add("--" + name);
            foreach (var name in option.ShortNames) options.Add("-" + name);
        }
        return options;
    }

    [Theory, MemberData(nameof(Examples))]
    public void Both_guides_show_each_example_as_it_is_apart_from_its_comments(string name)
    {
        var expected = CodeLines(Example(name));

        foreach (var guide in Guides)
        {
            var blocks = Regex.Matches(Read(guide), @"```yaml\r?\n(.*?)```", RegexOptions.Singleline)
                .Select(m => CodeLines(m.Groups[1].Value));

            blocks.Should().Contain(block => block.SequenceEqual(expected),
                $"{guide} shows examples/ci/{name} for a person to copy. Copy the file's lines into it again, comments aside");
        }
    }

    [Theory, MemberData(nameof(Examples))]
    public void The_two_secrets_an_example_reads_are_the_two_it_and_the_guides_tell_a_person_to_set(string name)
    {
        var text = Example(name);
        var read = Regex.Matches(string.Join("\n", CodeLines(text)), @"\$(HARBORA_[A-Z_]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        read.Should().Equal("HARBORA_SERVER", "HARBORA_TOKEN");

        var comments = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Where(l => l.TrimStart().StartsWith('#')));
        foreach (var secret in read)
        {
            comments.Should().Contain(secret, $"examples/ci/{name} must say which secrets to set");
            foreach (var guide in Guides)
                Read(guide).Should().Contain($"`{secret}`", $"{guide} must tell a person to set {secret}");
        }
    }

    /// <summary>
    /// A person is sent to a menu, a section and a button by name. A label reworded in a view fails
    /// nothing else, and leaves the guides describing a button nobody can find.
    /// </summary>
    [Fact]
    public void Where_the_guides_send_a_person_for_a_token_is_where_the_panel_makes_one()
    {
        var topbar = Read("src/Harbora.Web/Views/Shared/Design/_Topbar.cshtml");
        topbar.Should().Contain("<a href=\"/settings\">").And.Contain("(isFa ? \"تنظیمات\" : \"Settings\")");

        var settings = Read("src/Harbora.Web/Views/Settings/Index.cshtml");
        settings.Should().Contain("@T[\"API Tokens\"]").And.Contain("asp-action=\"CreateToken\"")
            .And.Contain("(isFa ? \"ساخت\" : \"Create\")");
        Read("src/Harbora.Web/Resources/SharedResource.fa.resx")
            .Should().Contain("<data name=\"API Tokens\"><value>توکن‌های API</value></data>");

        Read("docs/cli-deploy.md").Should().Contain("account menu → **Settings** → **API Tokens**").And.Contain("**Create**");
        Read("docs/tutorial/12-cli-deploy.md").Should().Contain("«تنظیمات»").And.Contain("«توکن‌های API»").And.Contain("«ساخت»");
        foreach (var name in new[] { "github-actions.yml", "gitlab-ci.yml" })
            Example(name).Should().Contain("account menu → Settings → API Tokens → Create");

        var details = Read("src/Harbora.Web/Views/Apps/Details.cshtml");
        details.Should().Contain("(isFa ? \"استقرار خودکار (Git)\" : \"Auto-deploy (Git)\")",
            "both guides point an app with a repository at that block instead of CI");
        Read("docs/cli-deploy.md").Should().Contain("**Auto-deploy (Git)**");
        Read("docs/tutorial/12-cli-deploy.md").Should().Contain("«استقرار خودکار (Git)»");
    }

    [Fact]
    public void The_installer_the_examples_fetch_is_this_repositorys_and_installs_a_glibc_binary()
    {
        const string installer = "https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/install-cli.sh";

        File.Exists(Path.Combine(TestPaths.RepoRoot, "deploy", "install-cli.sh")).Should().BeTrue();
        Read("README.md").Should().Contain(installer, "the examples install the CLI the way the README does");
        foreach (var name in new[] { "github-actions.yml", "gitlab-ci.yml" })
            Example(name).Should().Contain(installer);

        // The GitLab job says why its image is Debian: the release is built for linux-x64, not musl.
        var release = Read(".github/workflows/release-cli.yml");
        release.Should().Contain("rid: linux-x64").And.NotContain("musl");
        Read("deploy/install-cli.sh").Should().Contain("asset=\"harbora-${os}-${arch}\"");
    }
}
