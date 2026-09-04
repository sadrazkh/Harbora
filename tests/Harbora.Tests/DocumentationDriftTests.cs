using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Infrastructure.Services;
using Harbora.NodeAgent.Contracts;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Claims the documentation makes that are really facts about the code, asserted against the code.
///
/// <para>
/// This exists because of what an audit found: three node documents said the command allowlist had
/// twenty-one verbs. Each was correct on the day it was written — v1.0.0 of the contract shipped
/// exactly twenty-one — and three additive versions later the sentences were still there. Nobody
/// had lied; the number had simply stopped being true, and no build ever noticed. Documentation
/// drift is not a class of defect that reviews catch, because reviewing a document tells you it
/// reads well, not that it is still true.
/// </para>
///
/// <para>
/// So: every claim in a document that can be expressed as a fact about the code is expressed here
/// instead of being trusted. Each failure message names the document to edit rather than only the
/// number that was wrong, because the person who breaks one of these will be editing code and will
/// have no idea a document said anything about it.
/// </para>
/// </summary>
public class DocumentationDriftTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the tests read repository files and must be able to find the root");
        return dir!.FullName;
    }

    private static string At(params string[] parts) => Path.Combine([RepoRoot(), .. parts]);

    private static string Read(params string[] parts)
    {
        var path = At(parts);
        File.Exists(path).Should().BeTrue($"{path} is read by these tests and must exist");
        return File.ReadAllText(path);
    }

    /// <summary>Repository-relative, for a failure message somebody can act on without a path.</summary>
    private static string Relative(string absolute) =>
        Path.GetRelativePath(RepoRoot(), absolute).Replace('\\', '/');

    // ---- the node command allowlist ----

    /// <summary>
    /// Written out because English does not have digits. A document that says "twenty-four verbs"
    /// is making the same assertion as one that says "24", and the one that says it in words is the
    /// one that survived being wrong for three releases.
    /// </summary>
    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["twenty-one"] = 21, ["twenty-two"] = 22, ["twenty-three"] = 23,
        ["twenty-four"] = 24, ["twenty-five"] = 25, ["twenty-six"] = 26, ["twenty-seven"] = 27,
        ["twenty-eight"] = 28, ["twenty-nine"] = 29, ["thirty"] = 30,
    };

    /// <summary>
    /// The same table for the Persian README, which is not a translation of the English one but a
    /// document in its own right and states its own counts. A guard that only reads English would
    /// hold half the product's documentation to no standard at all — and the Persian README is the
    /// default language of this panel.
    /// </summary>
    private static readonly Dictionary<string, int> PersianNumberWords = new(StringComparer.Ordinal)
    {
        ["یک"] = 1, ["دو"] = 2, ["سه"] = 3, ["چهار"] = 4, ["پنج"] = 5,
        ["شش"] = 6, ["هفت"] = 7, ["هشت"] = 8, ["نه"] = 9, ["ده"] = 10,
    };

    /// <summary>
    /// A number immediately in front of the word "verbs" — "24 verbs", "21 named verbs",
    /// "twenty-four verbs". Anything else in front of it ("the verbs", "these verbs", "no verbs")
    /// resolves to nothing and is passed over, because it is prose rather than a count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The leading <c>\b</c> earns its place: without it, "the v1 verbs" matches as the number
    /// one, because <c>\d+</c> is happy to start halfway through a word.
    /// </para>
    /// <para>
    /// <b>Two forms it does not catch,</b> stated rather than chased. An unhyphenated spelling
    /// ("twenty one verbs") resolves only its second word, which is not in the table, so the claim
    /// is skipped; and an attributive form ("a 24-verb allowlist") is not matched at all, because
    /// the noun is singular and hyphen-joined. Both are silent passes rather than false failures,
    /// which is the right way round for a guard: it never blocks a build over a sentence it
    /// misread. Widening the pattern to cover them means either a full number-word grammar or a
    /// regex loose enough to start matching prose, and neither is worth it for two spellings this
    /// repository does not currently use. If one appears, add it to <see cref="NumberWords"/> or to
    /// the pattern — the test will not tell you it is there.
    /// </para>
    /// </remarks>
    private static readonly Regex VerbCountClaim =
        new(@"\b([A-Za-z]+(?:-[A-Za-z]+)?|\d+)\s+(?:named\s+|runtime\s+)?verbs\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Every document that describes the allowlist as it is today.
    ///
    /// <para>
    /// Changelogs are the one exclusion, and they are excluded as a kind rather than by name. A
    /// changelog says what a named version or commit contained — "v1.0.0: the allowlist itself, 21
    /// verbs", "the sixteen runtime verbs" — and those sentences are true about the past and must
    /// stay wrong about the present. A history that had to be edited whenever the present changed
    /// would not be a history.
    /// </para>
    /// </summary>
    public static TheoryData<string> DocumentsStatingTheVerbCount()
    {
        var files = new List<string>
        {
            At("README.md"),
            At("README.fa.md"),
            At("docs", "product-audit", "01-current-system-map.md"),
        };
        files.AddRange(Directory.EnumerateFiles(At("docs", "node-agent"), "*.md"));
        files.AddRange(Directory.EnumerateFiles(At("contracts", "node-agent", "v1"), "*.md"));

        var data = new TheoryData<string>();
        foreach (var file in files.Where(f =>
                     !Path.GetFileNameWithoutExtension(f)
                          .Equals("changelog", StringComparison.OrdinalIgnoreCase)))
            data.Add(file);

        return data;
    }

    [Theory]
    [MemberData(nameof(DocumentsStatingTheVerbCount))]
    public void No_document_states_a_verb_count_the_catalogue_does_not_have(string file)
    {
        var expected = NodeCommandCatalog.All.Count;

        foreach (Match match in VerbCountClaim.Matches(File.ReadAllText(file)))
        {
            var token = match.Groups[1].Value;

            var claimed =
                int.TryParse(token, out var digits) ? digits
                : NumberWords.TryGetValue(token, out var word) ? word
                : (int?)null;

            if (claimed is null) continue;   // prose, not a count

            claimed.Should().Be(expected,
                $"{Relative(file)} says \"{match.Value.Trim()}\", but NodeCommandCatalog has " +
                $"{expected} verbs. Adding a verb means updating that sentence — and the entry in " +
                "contracts/node-agent/v1/CHANGELOG.md.");
        }
    }

    [Fact]
    public void Every_verb_in_the_catalogue_is_described_in_the_contract_changelog()
    {
        // The contract is what the two codebases are written against, and its changelog is the only
        // place that says why a verb exists and what an older peer does when it arrives. A verb in
        // the schema with no entry here is a capability that reached a customer's server without
        // anybody writing down what it lets the control plane do.
        var changelog = Read("contracts", "node-agent", "v1", "CHANGELOG.md");

        var undocumented = NodeCommandCatalog.All
            .Where(verb => !changelog.Contains(verb, StringComparison.Ordinal))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        undocumented.Should().BeEmpty(
            "contracts/node-agent/v1/CHANGELOG.md must name every verb in the allowlist. Missing: " +
            $"{string.Join(", ", undocumented)}. Add the entry to the version that introduced it.");
    }

    // ---- what the panel can actually provision ----

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.fa.md")]
    public void Both_READMEs_name_every_managed_service_the_panel_can_provision(string readme)
    {
        // The English README listed five and the panel had offered seven since RabbitMQ and NATS
        // landed. A customer choosing a platform reads this list; a broker missing from it is a
        // feature that was built and then not sold.
        var text = Read(readme);

        var unmentioned = ServiceCatalog.All.Values
            .Select(s => s.DisplayName)
            .Where(name => !text.Contains(name, StringComparison.Ordinal))
            .ToList();

        unmentioned.Should().BeEmpty(
            $"{readme} must name every service in ServiceCatalog. Missing: " +
            $"{string.Join(", ", unmentioned)}.");
    }

    // ---- what an operator has to configure ----

    [Fact]
    public void Every_environment_variable_the_compose_stack_reads_is_documented_in_the_runbook()
    {
        // Compose substitutes an empty string for a variable that is not set. It does not warn, and
        // the stack starts. So a setting the runbook forgets is not a missing paragraph — it is an
        // install that comes up and then behaves strangely: MinIO signing pre-signed URLs for the
        // empty host name, a node handed an empty control-plane URL that it reports success about
        // and never dials. Every one of these has to be written down somewhere an operator reads.
        var compose = Read("deploy", "docker-compose.yml");
        var runbook = Read("deploy", "RUNBOOK.md");

        var referenced = Regex.Matches(compose, @"\$\{([A-Za-z_][A-Za-z0-9_]*)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        referenced.Should().NotBeEmpty("the compose file is templated and must reference variables");

        var undocumented = referenced
            .Where(name => !runbook.Contains(name, StringComparison.Ordinal))
            .ToList();

        undocumented.Should().BeEmpty(
            "deploy/RUNBOOK.md must document every variable deploy/docker-compose.yml reads. " +
            $"Missing: {string.Join(", ", undocumented)}.");
    }

    [Fact]
    public void Every_server_command_the_disaster_recovery_runbook_gives_is_one_the_script_dispatches()
    {
        // A recovery document is read once, under pressure, by somebody who will type exactly what
        // it says. The audit found the node quickstart telling operators to run `harbora node-ca`
        // months before that verb existed. A command that does not exist costs the reader the one
        // thing they have none of.
        var doc = Read("docs", "disaster-recovery.md");
        var script = Read("deploy", "harbora");

        // The dispatch table at the bottom of the script: "  doctor)  shift; …" and the shared
        // "  info|users|reset-password|…)" arm. Read from there rather than from a list kept here,
        // so renaming a verb in the script is what this test notices.
        var dispatched = Regex.Matches(script, @"(?m)^\s{2}([a-z][a-z0-9|-]*)\)\s")
            .SelectMany(m => m.Groups[1].Value.Split('|'))
            .ToHashSet(StringComparer.Ordinal);

        dispatched.Should().Contain("doctor", "the dispatch table was not parsed as expected");

        // Only what the document presents *as a command* — a fenced block or an inline code span.
        // Matching bare prose would mean an ordinary sentence like "run harbora again once DNS has
        // moved" failing the build with a message claiming the document invented a verb called
        // `again`, which is a guard that punishes writing rather than drift.
        var invoked = CommandsPresentedAsCode(doc)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        invoked.Should().NotBeEmpty("the recovery runbook is made of commands");

        var unknown = invoked.Where(verb => !dispatched.Contains(verb)).ToList();

        unknown.Should().BeEmpty(
            "docs/disaster-recovery.md tells an operator to run a command deploy/harbora does not " +
            $"dispatch: {string.Join(", ", unknown)}. Either the verb was renamed or the document " +
            "invented it.");
    }

    [Fact]
    public void Every_command_the_deploy_CLI_registers_is_documented_in_the_README()
    {
        // `harbora cancel` shipped with the queue-transparency work and never reached the README,
        // so the one verb somebody reaches for while a bad build is running was the one they could
        // not find. A command nobody is told about is a command nobody uses, and the effort that
        // built it is spent either way.
        //
        // Read out of Program.cs's own registrations rather than from a list kept here, because a
        // list kept here would be a second thing to forget. The CLI's commands are configured
        // inside a lambda in a top-level program, so there is no public surface to reflect over —
        // this is the same source-reading idiom the localization and navigation conventions use.
        var program = Read("src", "Harbora.Cli", "Program.cs");
        var readme = Read("README.md");

        var registered = RegisteredCommandNames(program);

        registered.Should().Contain("deploy", "the registrations were not parsed as expected");

        var undocumented = registered
            .Where(command => !readme.Contains("harbora " + command, StringComparison.Ordinal))
            .ToList();

        undocumented.Should().BeEmpty(
            "README.md must show every command the CLI accepts. Missing: " +
            $"{string.Join(", ", undocumented)}. Add it to the 'Deploy directly' block.");
    }

    /// <summary>
    /// Every command name Program.cs registers, as somebody would actually type it — including a
    /// multi-word one added through <c>AddBranch</c> (4.1, 2026-09-04 local-dev-parity plan: <c>env
    /// pull</c> is the CLI's first subcommand group). A bare <c>AddCommand&lt;EnvPullCommand&gt;("pull")</c>
    /// inside that branch is not itself a runnable verb — <c>harbora pull</c> does not exist, only
    /// <c>harbora env pull</c> does — so a branch's nested registrations are prefixed with its own
    /// name before anything checks the README for them.
    /// </summary>
    private static List<string> RegisteredCommandNames(string program)
    {
        var names = new List<string>();

        foreach (Match branch in Regex.Matches(
                     program, @"AddBranch\(""([a-z][a-z0-9-]*)"",[^;]*?=>\s*\{(?<body>.*?)\}\s*\);",
                     RegexOptions.Singleline))
        {
            var branchName = branch.Groups[1].Value;
            foreach (Match cmd in Regex.Matches(
                         branch.Groups["body"].Value, @"AddCommand<\w+>\(""([a-z][a-z0-9-]*)""\)"))
                names.Add($"{branchName} {cmd.Groups[1].Value}");
        }

        // Everything outside a branch body is a flat, top-level command — remove the branch bodies
        // first so their nested AddCommand calls are not also counted as bare, unprefixed verbs.
        var withoutBranches = Regex.Replace(
            program, @"AddBranch\(""[a-z][a-z0-9-]*"",[^;]*?=>\s*\{.*?\}\s*\);", "", RegexOptions.Singleline);
        names.AddRange(Regex.Matches(withoutBranches, @"AddCommand<\w+>\(""([a-z][a-z0-9-]*)""\)")
            .Select(m => m.Groups[1].Value));

        return names.OrderBy(v => v, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void No_document_states_a_CLI_command_count_the_CLI_does_not_have()
    {
        // The first version of this scoped to README.md, corrected the two missing commands there,
        // and left "8 commands" standing in docs/product-audit/01-current-system-map.md — the same
        // drift, in a second file, surviving the test that was written for it. A guard whose file
        // set is one file guards one file.
        //
        // So the count is asserted wherever it is claimed, the same way the verb count is.
        var registered = Regex.Matches(Read("src", "Harbora.Cli", "Program.cs"),
                                       @"AddCommand<\w+>\(""[a-z][a-z0-9-]*""\)").Count;
        registered.Should().BeGreaterThan(0, "the registrations were not parsed as expected");

        string[] files =
        [
            At("README.md"),
            At("README.fa.md"),
            At("docs", "product-audit", "01-current-system-map.md"),
            At("docs", "cli-deploy.md"),
        ];

        foreach (var file in files.Where(File.Exists))
        {
            // Only a count attributed to the CLI: "10 commands" on a line that also names the CLI,
            // rather than every "N commands" in the repository. The node agent has commands too,
            // and they are a different number.
            foreach (Match line in Regex.Matches(File.ReadAllText(file), @"(?m)^.*\bcommands\b.*$"))
            {
                if (!line.Value.Contains("Harbora.Cli", StringComparison.Ordinal)
                    && !line.Value.Contains("deploy CLI", StringComparison.Ordinal)) continue;

                var claim = Regex.Match(line.Value, @"\b(\d+|[A-Za-z]+(?:-[A-Za-z]+)?)\s+commands\b");
                if (!claim.Success) continue;

                var token = claim.Groups[1].Value;
                var claimed = int.TryParse(token, out var digits) ? digits
                    : NumberWords.TryGetValue(token, out var word) ? word
                    : (int?)null;

                if (claimed is null) continue;

                claimed.Should().Be(registered,
                    $"{Relative(file)} says \"{claim.Value}\" of the deploy CLI, but " +
                    $"src/Harbora.Cli/Program.cs registers {registered}");
            }
        }
    }

    /// <summary>
    /// Every <c>harbora &lt;verb&gt;</c> the document presents as something to type: inside a fenced
    /// code block, or inside an inline code span. Prose is deliberately not read.
    /// </summary>
    private static IEnumerable<string> CommandsPresentedAsCode(string markdown)
    {
        var fenced = Regex.Matches(markdown, @"```[a-z]*\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value);
        var inline = Regex.Matches(markdown, @"`([^`\r\n]+)`").Select(m => m.Groups[1].Value);

        foreach (var span in fenced.Concat(inline))
            foreach (Match command in Regex.Matches(span, @"(?<![\w./-])harbora ([a-z][a-z0-9-]*)"))
                yield return command.Groups[1].Value;
    }

    // ---- counts that are facts about the code ----

    [Theory]
    [InlineData("README.md", "databases", "message brokers")]
    [InlineData("README.fa.md", "دیتابیس", "صف پیام")]
    public void Both_READMEs_state_the_managed_service_counts_the_catalogue_has(
        string readme, string databaseNoun, string brokerNoun)
    {
        // The names are asserted above; this is the sentence around them. "Five databases and two
        // message brokers" is a claim with two numbers in it, and an eighth service would leave both
        // wrong while every name in the list stayed correct — which is precisely how the original
        // "five managed databases" survived RabbitMQ and NATS arriving.
        //
        // The split comes from ServiceTypeKey.IsBroker rather than from a list here, because that
        // predicate is what the product itself asks when it decides whether a thing has data worth
        // backing up.
        var databases = ServiceCatalog.All.Keys.Count(t => !ServiceTypeKey.IsBroker(t));
        var brokers = ServiceCatalog.All.Keys.Count(ServiceTypeKey.IsBroker);

        // Anchored to the bullet that lists the services rather than searched for across the whole
        // README. The first attempt scanned the document, and on the Persian side matched "یک
        // دیتابیس" — "a database" — in an unrelated sentence, reported a claim of one, and would
        // have had me "correcting" a number that was never wrong. A count is only a claim in the
        // sentence that makes it.
        //
        // The bullet finds itself: it is the one that names every service in the catalogue, which
        // is the same fact the test above asserts.
        var names = ServiceCatalog.All.Values.Select(s => s.DisplayName).ToList();
        var bullets = Regex.Split(Read(readme), @"(?m)^- ")
            .Select(b => Regex.Replace(b, @"\s*\r?\n\s*", " "))
            .Where(b => names.All(n => b.Contains(n, StringComparison.Ordinal)))
            .ToList();

        bullets.Should().ContainSingle(
            $"{readme} must have exactly one bullet that lists every managed service, and it is the " +
            "one these counts are read from");

        var claim = bullets[0];
        var words = readme.Contains(".fa.", StringComparison.Ordinal) ? PersianNumberWords : NumberWords;

        int? Claimed(string noun) => words
            .Where(w => Regex.IsMatch(claim, $@"(?<![\p{{L}}]){Regex.Escape(w.Key)}\s+{Regex.Escape(noun)}",
                                      RegexOptions.IgnoreCase))
            .Select(w => (int?)w.Value)
            .FirstOrDefault();

        Claimed(databaseNoun).Should().Be(databases,
            $"{readme} must say, in words, how many databases the panel provisions, beside the list " +
            $"of them. ServiceCatalog has {databases} that ServiceTypeKey.IsBroker calls stores.");

        Claimed(brokerNoun).Should().Be(brokers,
            $"{readme} must say, in words, how many message brokers the panel provisions. " +
            $"ServiceTypeKey.IsBroker counts {brokers}.");
    }

    // ---- claims about what the code does not contain ----

    [Fact]
    public void Nothing_in_the_product_opens_a_connection_to_Redis()
    {
        // Both READMEs say the job queue is PostgreSQL and that the Redis package and container are
        // leftovers nothing uses. That is a claim about an absence, and an absence is exactly the
        // kind of thing that stops being true without anybody noticing: the package reference is
        // still in Harbora.Infrastructure.csproj, so the day somebody types `new
        // ConnectionMultiplexer(...)` it will compile. Then the READMEs are wrong and nothing says
        // so.
        //
        // If this fails, the honest fix is usually to correct the READMEs rather than to delete the
        // code — but it must be one or the other, on purpose.
        var offenders = Directory
            .EnumerateFiles(At("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"StackExchange\.Redis|ConnectionMultiplexer"))
            .Select(Relative)
            .ToList();

        offenders.Should().BeEmpty(
            "README.md and README.fa.md both state that nothing in Harbora connects to Redis and " +
            "that the package reference is dead. Something now uses it: " +
            $"{string.Join(", ", offenders)}. Correct the READMEs, or remove the usage.");
    }

    [Fact]
    public void No_test_project_references_the_legacy_agent()
    {
        // The deprecation notices in the README, the Servers page and merge-notes all give "it has
        // no automated tests" as the reason it is frozen rather than developed. If somebody ever
        // writes tests for it, that reason evaporates and three documents are quietly wrong about
        // why a supported component is not being worked on.
        var referencing = Directory
            .EnumerateFiles(At("tests"), "*.csproj", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("Harbora.Agent", StringComparison.Ordinal))
            .Select(Relative)
            .ToList();

        referencing.Should().BeEmpty(
            "the legacy agent's deprecation notices — README.md, README.fa.md, " +
            "docs/node-agent/merge-notes.md and Views/Servers/Index.cshtml — all say it ships with " +
            $"no tests, which is why it is frozen. These now test it: {string.Join(", ", referencing)}. " +
            "Update those notices, or drop the reference.");
    }

    // ---- the legacy agent ----

    [Fact]
    public void While_the_legacy_agent_still_ships_the_servers_page_says_it_is_deprecated()
    {
        // It is not being removed — it works, and fleets run it. But it is the page's own form that
        // invites somebody to add a new server through it, and a deprecation nobody meets at the
        // moment of the decision is a deprecation that changes nothing. This test is tied to the
        // project's existence rather than to a date: delete src/Harbora.Agent and the requirement
        // goes away with it.
        File.Exists(At("src", "Harbora.Agent", "Program.cs")).Should().BeTrue(
            "if the legacy agent has been removed, delete this test with it");

        var page = Read("src", "Harbora.Web", "Views", "Servers", "Index.cshtml");

        page.Should().Contain("Deprecated",
            "src/Harbora.Web/Views/Servers/Index.cshtml must mark the legacy HTTP agent deprecated " +
            "while src/Harbora.Agent still ships — the panel is where somebody decides to use it");
        page.Should().Contain("منسوخ",
            "the deprecation notice on the Servers page must be bilingual, like every other notice " +
            "in this panel");
    }

    [Fact]
    public void No_runbook_teaches_the_legacy_agent_as_the_way_to_add_a_node()
    {
        // The RUNBOOK's "add a helper node" section taught the legacy agent exclusively, which made
        // the deprecation in the README a contradiction rather than a policy: the two documents an
        // operator reads said different things, and the one with the copy-paste commands wins.
        var runbook = Read("deploy", "RUNBOOK.md");

        runbook.Should().Contain("deploy/node-agent/install.sh",
            "deploy/RUNBOOK.md must teach Node Agent v1's installer as the way to add a node");
        runbook.Should().NotContain("agent.compose.yml",
            "deploy/RUNBOOK.md must not give the legacy HTTP agent's install commands: it is " +
            "deprecated, and the README documents it for the fleets that already run it");
    }
}
