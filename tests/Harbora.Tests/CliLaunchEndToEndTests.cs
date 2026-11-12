using System.ComponentModel;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Cli;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Verification;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Run on their own, after everything that runs in parallel. These capture <c>AnsiConsole.Console</c>,
/// which <see cref="CliConsoleCollection"/> keeps to itself, and they boot a panel, whose setup guard
/// holds a process-wide flag that <see cref="HarboraHttpCollection"/> keeps to itself; a class can only
/// be in one collection, and running apart from both is the one thing that satisfies the two.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliLaunchEndToEndCollection
{
    public const string Name = "cli-launch-end-to-end";
}

/// <summary>
/// The real <see cref="LaunchCommand"/> over the real <c>LaunchApiController</c>, through the real
/// pipeline with a real token, over a scripted <see cref="ILaunchService"/>. So every byte between the
/// two is what would really cross: the upload, the enums by name, the refusals' shape, the secrets.
/// </summary>
[Collection(CliLaunchEndToEndCollection.Name)]
public sealed class CliLaunchEndToEndTests : IClassFixture<LaunchPanelFixture>, IDisposable
{
    private const string Server = "https://localhost";

    private readonly LaunchPanelFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harbora-launch-cli-" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;
    private readonly string _token;

    private static int _nextUser;

    public CliLaunchEndToEndTests(LaunchPanelFixture fixture)
    {
        _fixture = fixture;
        fixture.Launches.Reset();

        _dir = Path.Combine(_root, "my-shop");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "package.json"), """{ "name": "shop" }""");

        _token = fixture.GivenToken(fixture.WorkspaceId, $"launch-cli-{Interlocked.Increment(ref _nextUser)}@example.com");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp folder */ }
        catch (UnauthorizedAccessException) { /* a temp folder */ }
    }

    private FakeLaunchService Launches => _fixture.Launches;
    private Guid Id => Launches.NextStartId;

    private ApiClient Api(string token, Func<HttpRequestMessage, bool>? breaksOn) => breaksOn is null
        ? new(Server, token, _fixture.Panel.Server.CreateHandler())
        : new(Server, token, new Unreliable(_fixture.Panel.Server.CreateHandler(), breaksOn));

    /// <summary>The real thing, with a connection that drops whenever the test says it does.</summary>
    private sealed class Unreliable(HttpMessageHandler inner, Func<HttpRequestMessage, bool> breaksOn) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            breaksOn(request)
                ? throw new HttpRequestException("The SSL connection could not be established.")
                : base.SendAsync(request, ct);
    }

    /// <summary>A person, and everything they were asked.</summary>
    private sealed class Person : ILaunchPrompts
    {
        public string Folder { get; init; } = "";
        public Dictionary<string, string> Answers { get; init; } = [];
        public bool SaysYes { get; init; } = true;

        public List<IReadOnlyList<string>> FolderQuestions { get; } = [];
        public List<(string Key, bool Secret)> Questions { get; } = [];
        public List<string> Confirmations { get; } = [];

        public string ChooseFolder(IReadOnlyList<string> folders)
        {
            FolderQuestions.Add(folders);
            return Folder;
        }

        public string Ask(string key, bool secret)
        {
            Questions.Add((key, secret));
            return Answers.GetValueOrDefault(key, "");
        }

        public bool Confirm(string question)
        {
            Confirmations.Add(question);
            return SaysYes;
        }
    }

    private LaunchRun Run(
        IEnumerable<string>? set = null, IEnumerable<string>? noService = null, bool yes = true, bool noFollow = false,
        string? folder = null, string? name = null, string? git = null, string? gitRef = null, string? gitToken = null, string? dir = null) => new()
    {
        Dir = dir ?? _dir,
        GitUrl = git,
        GitRef = gitRef,
        GitToken = gitToken,
        Folder = folder,
        Name = name,
        Set = (set ?? []).ToList(),
        NoService = (noService ?? []).ToList(),
        Yes = yes,
        NoFollow = noFollow,
        ReadEvery = TimeSpan.FromMilliseconds(1),
        FollowEvery = TimeSpan.FromMilliseconds(1)
    };

    private async Task<(int Exit, string Output)> LaunchAsync(
        LaunchRun run, ILaunchPrompts? person = null, CancellationToken ct = default,
        Func<HttpRequestMessage, bool>? breaksOn = null, string? token = null)
    {
        // A launch that never ends is followed for ever, which is right for a person and would hang the
        // whole suite: a regression that goes on past the point it should have stopped has to fail here.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));

        return await CapturedAsync(() => LaunchCommand.RunAsync(Api(token ?? _token, breaksOn), run, person, limit.Token));
    }

    /// <summary>What a person would have seen. Spectre folds to a width, so only the words are compared.</summary>
    private static async Task<(int Exit, string Output)> CapturedAsync(Func<Task<int>> command)
    {
        var previous = AnsiConsole.Console;
        var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer)
        });

        try { return (await command(), Regex.Replace(writer.ToString(), @"\s+", " ").Trim()); }
        finally { AnsiConsole.Console = previous; }
    }

    // ---- scripts -------------------------------------------------------------------------------------

    private Guid GivenAnApp(string slug)
    {
        var app = new App { WorkspaceId = _fixture.WorkspaceId, Name = "shop", Slug = slug, SourceType = AppSourceType.Upload, Status = AppStatus.Created };
        _fixture.Seed(db => db.Apps.Add(app));
        return app.Id;
    }

    /// <summary>A launch that reads, waits at the proposal, and once confirmed walks to <paramref name="ending"/>.</summary>
    private void GivenALaunch(LaunchProposal? proposal = null, LaunchQuote? quote = null, params LaunchView[] ending)
    {
        var id = Id;
        Launches.Add(_fixture.WorkspaceId, LaunchScript.View(LaunchStatus.Inspecting, id, steps: LaunchScript.Steps(source: LaunchStepState.Running)),
            proposal ?? LaunchScript.Proposal(id), quote ?? LaunchScript.Charged);
        Launches.Then(id,
            LaunchScript.View(LaunchStatus.Inspecting, id, steps: LaunchScript.Steps(source: LaunchStepState.Running)),
            LaunchScript.View(LaunchStatus.AwaitingConfirmation, id));
        Launches.ThenAfterConfirm(id, ending);
    }

    private LaunchView Deploying(Guid deploymentId, Guid? appId = null) => LaunchScript.View(LaunchStatus.Deploying, Id,
        appId, "shop", deploymentId: deploymentId,
        steps: LaunchScript.Steps(services: LaunchStepState.Done, app: LaunchStepState.Done, deploy: LaunchStepState.Running));

    private LaunchView Succeeded(Guid appId, Guid deploymentId, params AppCheck[] checks) => LaunchScript.View(LaunchStatus.Succeeded, Id,
        appId, "shop", "https://shop.apps.example.com", deploymentId,
        steps: LaunchScript.Steps(services: LaunchStepState.Done, app: LaunchStepState.Done, deploy: LaunchStepState.Done, verify: LaunchStepState.Done),
        checks: checks);

    private static readonly AppCheck Running = new("running", AppCheckStatus.Passed, "Is it still running?", "x");

    // ---- the happy path ---------------------------------------------------------------------------------

    [Fact]
    public async Task With_yes_a_folder_goes_all_the_way_to_its_address_and_every_check()
    {
        var appId = GivenAnApp("shop-happy");
        var deploymentId = Guid.CreateVersion7();
        GivenALaunch(ending:
        [
            Deploying(deploymentId, appId),
            Succeeded(appId, deploymentId, Running, new AppCheck("url", AppCheckStatus.Passed, "Does the address work?", "x", "It answered 200."))
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(0, output);

        // What it said it found, before anything existed.
        output.Should().Contain("This looks like a Node.js app, listening on port 3000.");
        output.Should().Contain("A real .env was left out.");
        output.Should().Contain("PostgreSQL, because of \"pg\" in dependencies (package.json)");
        output.Should().Contain("Redis is only a guess");
        output.Should().Contain("It will generate these secrets: JWT_SECRET");
        output.Should().Contain("12.00 USD comes off your balance now (one hour of running), then 12.00 USD an hour. Balance: 300.00 USD.");

        // The unanswered ones are named and it goes on, because --yes said to.
        output.Should().Contain("Not answered, so not set: STRIPE_SECRET_KEY, SUPPORT_EMAIL");

        // The walk.
        output.Should().Contain("→ Deploying");
        output.Should().Contain($"{Server}/deployments/details/{deploymentId}");
        output.Should().Contain($"harbora logs {deploymentId}");
        output.Should().Contain("✓ Deploying");

        // The end.
        output.Should().Contain("https://shop.apps.example.com");
        output.Should().Contain("✓ Is it still running?");
        output.Should().Contain("✓ Does the address work?: It answered 200.");
        output.Should().EndWith("harbora deploy", "the last line is what to do from now on");

        Launches.Quoted.Should().ContainSingle();
        Launches.Validated.Should().ContainSingle();
        var confirmed = Launches.Confirmed.Should().ContainSingle().Subject.Choices;
        confirmed.Name.Should().Be("shop");
        confirmed.Kind.Should().Be(ServiceKind.Web);
        confirmed.Services.Should().ContainSingle().Which.Type.Should().Be(ManagedServiceType.PostgreSql);
        confirmed.Services[0].Aliases.Should().Equal(new AttachEnvAlias("DB_HOST", "PGHOST"));
        confirmed.EnvVars.Single(v => v.Key == "LOG_LEVEL").Value.Should().Be("info");
        confirmed.EnvVars.Single(v => v.Key == "JWT_SECRET").Value.Should().BeNull("the server generates it");
        confirmed.EnvVars.Single(v => v.Key == "STRIPE_SECRET_KEY").Value.Should().BeNull();
        Launches.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_step_is_printed_once_however_many_times_it_is_looked_at()
    {
        var deploymentId = Guid.CreateVersion7();
        GivenALaunch(ending:
        [
            Deploying(deploymentId), Deploying(deploymentId), Deploying(deploymentId),
            Succeeded(GivenAnApp("shop-once"), deploymentId, Running)
        ]);

        var (_, output) = await LaunchAsync(Run());

        Regex.Matches(output, "→ Deploying").Should().HaveCount(1);
        Regex.Matches(output, Regex.Escape($"/deployments/details/{deploymentId}")).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_step_whose_detail_changes_is_printed_again()
    {
        var deploymentId = Guid.CreateVersion7();
        var waiting = LaunchScript.View(LaunchStatus.Deploying, Id, deploymentId: deploymentId,
            steps: LaunchScript.Steps(deploy: LaunchStepState.Running, deployDetail: "This environment is protected: it is waiting for somebody to approve the deployment."));
        GivenALaunch(ending: [Deploying(deploymentId), waiting, Succeeded(GivenAnApp("shop-detail"), deploymentId, Running)]);

        var (_, output) = await LaunchAsync(Run());

        output.Should().Contain("→ Deploying: This environment is protected");
    }

    [Fact]
    public async Task A_panel_address_too_long_for_the_console_still_goes_out_whole()
    {
        // 80 columns with no terminal behind it, and Spectre would fold this mid-URL.
        var longUrl = "https://" + new string('a', 90) + ".apps.example.com";
        var appId = GivenAnApp("shop-long-url");
        var deploymentId = Guid.CreateVersion7();
        GivenALaunch(ending:
        [
            LaunchScript.View(LaunchStatus.Succeeded, Id, appId, "shop", longUrl, deploymentId, checks: [Running])
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(0);
        output.Should().Contain(longUrl);
        output.Should().Contain($"{Server}/deployments/details/{deploymentId}");
    }

    [Fact]
    public async Task A_worker_is_not_told_it_has_lost_its_address()
    {
        var appId = GivenAnApp("shop-worker");
        GivenALaunch(ending: [LaunchScript.View(LaunchStatus.Succeeded, Id, appId, "bot", checks: [Running])]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(0);
        output.Should().Contain("It has no address of its own.");
    }

    // ---- what leaves the machine -------------------------------------------------------------------------

    [Fact]
    public async Task The_upload_is_the_folder_without_its_env_files_and_is_named_after_it()
    {
        File.WriteAllText(Path.Combine(_dir, ".env"), "REAL_SECRET=hunter2-from-dotenv");
        File.WriteAllText(Path.Combine(_dir, ".env.local"), "REAL_SECRET=hunter2-from-dotenv-local");
        File.WriteAllText(Path.Combine(_dir, ".env.production"), "REAL_SECRET=hunter2-from-production");
        File.WriteAllText(Path.Combine(_dir, ".env.example"), "REAL_SECRET=");
        Directory.CreateDirectory(Path.Combine(_dir, "services", "api"));
        File.WriteAllText(Path.Combine(_dir, "services", "api", ".env"), "REAL_SECRET=hunter2-from-nested");
        File.WriteAllText(Path.Combine(_dir, "services", "api", "index.js"), "// nested source");
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-upload"), Guid.CreateVersion7(), Running)]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(0, output);

        var path = Launches.Started.Should().ContainSingle().Subject.ArchivePath!;
        LaunchSource.NameFromArchive(path).Should().Be("my-shop");

        var entries = await ReadTarAsync(path);
        entries.Keys.Should().BeEquivalentTo("package.json", ".env.example", "services/api/index.js");
        entries.Values.Should().NotContain(content => content.Contains("hunter2"));

        output.Should().Contain("built-in rule '.env'", "what went missing, and why, is said out loud");
        output.Should().Contain("Kept on this machine: .env, .env.local, .env.production, services/api/.env",
            "each env file is named, because a build may have been reading one");
        output.Should().Contain("\"!.env.production\"");
    }

    private static async Task<Dictionary<string, string>> ReadTarAsync(string path)
    {
        var entries = new Dictionary<string, string>();
        await using var file = File.OpenRead(path);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        await using var tar = new TarReader(gz);
        while (await tar.GetNextEntryAsync(copyData: true) is { } entry)
        {
            using var reader = new StreamReader(entry.DataStream!, Encoding.UTF8);
            entries[entry.Name] = await reader.ReadToEndAsync();
        }
        return entries;
    }

    [Fact]
    public async Task A_folder_with_nothing_in_it_says_so_and_sends_nothing()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        var (exit, output) = await LaunchAsync(Run(dir: empty));

        exit.Should().Be(1);
        output.Should().Contain("nothing to launch");
        Launches.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_holding_only_what_is_never_uploaded_says_so_too()
    {
        var onlySecrets = Path.Combine(_root, "only-secrets");
        Directory.CreateDirectory(onlySecrets);
        File.WriteAllText(Path.Combine(onlySecrets, ".env"), "A=b");

        var (exit, output) = await LaunchAsync(Run(dir: onlySecrets));

        exit.Should().Be(1);
        output.Should().Contain("nothing to launch").And.Contain("built-in rule '.env'");
        Launches.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_that_is_not_there_says_so()
    {
        var (exit, output) = await LaunchAsync(Run(dir: Path.Combine(_root, "no-such-folder")));

        exit.Should().Be(1);
        output.Should().Contain("Folder not found");
    }

    [Fact]
    public async Task A_repository_is_named_rather_than_uploaded_and_its_token_is_never_printed()
    {
        const string gitToken = "ghp_the_token_that_must_not_be_printed";
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-git"), Guid.CreateVersion7(), Running)]);

        var (exit, output) = await LaunchAsync(Run(git: "https://github.com/you/shop", gitRef: "develop", gitToken: gitToken));

        exit.Should().Be(0, output);
        var started = Launches.Started.Should().ContainSingle().Subject;
        started.Source.Should().Be(LaunchSourceKind.Git);
        started.CloneUrl.Should().Be("https://github.com/you/shop");
        started.GitRef.Should().Be("develop");
        started.GitToken.Should().Be(gitToken);
        started.ArchivePath.Should().BeNull();
        output.Should().NotContain(gitToken);

        File.Exists(Path.Combine(_dir, "harbora.yml")).Should().BeFalse("this folder had nothing to do with it");
    }

    [Fact]
    public void A_run_cannot_print_its_token_or_answers_by_accident()
    {
        var run = Run(gitToken: "ghp_in_a_tostring", set: ["A=value_in_a_tostring"]);

        run.ToString().Should().NotContain("ghp_in_a_tostring").And.NotContain("value_in_a_tostring");
    }

    [Fact]
    public async Task A_refused_repository_is_the_servers_sentence_and_nothing_was_created()
    {
        Launches.StartRefusal = new ServiceRefusal("An SSH address cannot be read here.", "x", "CloneUrl");

        var (exit, output) = await LaunchAsync(Run(git: "git@github.com:you/shop.git"));

        exit.Should().Be(1);
        output.Should().Contain("CloneUrl: An SSH address cannot be read here.");
        output.Should().Contain("Nothing was created.");
    }

    [Fact]
    public async Task A_token_that_may_not_create_apps_gets_a_sentence()
    {
        var viewer = _fixture.GivenToken(_fixture.WorkspaceId, $"launch-cli-viewer-{Interlocked.Increment(ref _nextUser)}@example.com", SystemRole.Viewer);

        var (exit, output) = await LaunchAsync(Run(), token: viewer);

        exit.Should().Be(1);
        output.Should().Contain("may not create apps");
        output.Should().Contain("Nothing was created.");
        Launches.Started.Should().BeEmpty();
    }

    // ---- reading fails -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_source_that_cannot_be_read_is_the_servers_message_and_says_nothing_was_created()
    {
        var id = Id;
        Launches.Add(_fixture.WorkspaceId, LaunchScript.View(LaunchStatus.Inspecting, id));
        Launches.Then(id, LaunchScript.View(LaunchStatus.InspectFailed, id, failure: "Could not read this repository. The URL may be wrong."));

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(1);
        output.Should().Contain("Could not read this repository. The URL may be wrong.");
        output.Should().Contain("Nothing was created.");
        Launches.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_servers_message_that_already_says_nothing_was_created_is_not_said_twice()
    {
        var id = Id;
        Launches.Add(_fixture.WorkspaceId, LaunchScript.View(LaunchStatus.Inspecting, id));
        Launches.Then(id, LaunchScript.View(LaunchStatus.InspectFailed, id, failure: "Could not read this file. Nothing was created."));

        var (_, output) = await LaunchAsync(Run());

        Regex.Matches(output, "Nothing was created").Should().HaveCount(1);
    }

    // ---- which folder --------------------------------------------------------------------------------------

    private LaunchProposal SeveralApps() => LaunchScript.Proposal(Id, LaunchScript.Inspection(roots: ["services/api", "services/web"]));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Several_folders_with_no_flag_and_nobody_to_ask_names_them_and_creates_nothing(bool yes)
    {
        GivenALaunch(SeveralApps());

        var (exit, output) = await LaunchAsync(Run(yes: yes));

        exit.Should().Be(1);
        output.Should().Contain("services/api, services/web").And.Contain("--folder");
        output.Should().Contain("Nothing was created.");
        Launches.ChosenRoots.Should().BeEmpty();
        Launches.Quoted.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
        Launches.Abandoned.Should().ContainSingle();
    }

    [Fact]
    public async Task Yes_means_no_questions_even_with_a_person_there()
    {
        GivenALaunch(SeveralApps());
        var person = new Person { Folder = "services/api" };

        var (exit, output) = await LaunchAsync(Run(yes: true), person);

        exit.Should().Be(1);
        output.Should().Contain("--folder");
        person.FolderQuestions.Should().BeEmpty();
    }

    [Fact]
    public async Task The_folder_flag_settles_it_and_what_follows_is_that_folders_proposal()
    {
        GivenALaunch(SeveralApps(), ending: [Succeeded(GivenAnApp("shop-folder"), Guid.CreateVersion7(), Running)]);

        var (exit, output) = await LaunchAsync(Run(folder: "services/api"));

        exit.Should().Be(0, output);
        Launches.ChosenRoots.Should().ContainSingle().Which.Candidate.Should().Be("services/api");
        output.Should().Contain("in the folder services/api");
    }

    [Fact]
    public async Task A_person_chooses_from_the_list()
    {
        GivenALaunch(SeveralApps(), ending: [Succeeded(GivenAnApp("shop-chooses"), Guid.CreateVersion7(), Running)]);
        var person = new Person { Folder = "services/web" };

        var (exit, _) = await LaunchAsync(Run(yes: false), person);

        exit.Should().Be(0);
        person.FolderQuestions.Should().ContainSingle().Which.Should().Equal("services/api", "services/web");
        Launches.ChosenRoots.Should().ContainSingle().Which.Candidate.Should().Be("services/web");
    }

    [Fact]
    public async Task A_folder_the_list_does_not_have_is_the_servers_refusal()
    {
        GivenALaunch(SeveralApps());

        var (exit, output) = await LaunchAsync(Run(folder: "services/nope"));

        exit.Should().Be(1);
        output.Should().Contain("That folder isn't in this repository.");
        Launches.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_flag_on_something_read_as_one_app_is_refused_rather_than_launched_on_the_wrong_reading()
    {
        GivenALaunch();

        var (exit, output) = await LaunchAsync(Run(folder: "services/api"));

        exit.Should().Be(1);
        output.Should().Contain("--folder chooses between several apps");
        Launches.ChosenRoots.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
    }

    // ---- questions and answers -----------------------------------------------------------------------------

    [Fact]
    public async Task A_question_answered_by_set_is_not_asked_is_sent_and_is_never_printed()
    {
        const string secret = "sk_live_the_value_that_must_not_be_printed";
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-set"), Guid.CreateVersion7(), Running)]);

        var (exit, output) = await LaunchAsync(Run(set: [$"STRIPE_SECRET_KEY={secret}", "SUPPORT_EMAIL=help@example.com"]));

        exit.Should().Be(0, output);
        output.Should().NotContain(secret);
        output.Should().NotContain("help@example.com");
        output.Should().NotContain("Not answered");

        var variables = Launches.Confirmed.Single().Choices.EnvVars;
        variables.Single(v => v.Key == "STRIPE_SECRET_KEY").Should().BeEquivalentTo(new { Value = secret, IsSecret = true });
        variables.Single(v => v.Key == "SUPPORT_EMAIL").Should().BeEquivalentTo(new { Value = "help@example.com", IsSecret = false });

        Launches.Quoted.Single().Choices.EnvVars.Single(v => v.Key == "STRIPE_SECRET_KEY").Value
            .Should().Be(secret, "the same choices the cost was worked out for");
    }

    [Fact]
    public async Task A_malformed_set_stops_it_before_anything_leaves_and_does_not_quote_it()
    {
        const string secret = "sk_live_typed_with_no_key";

        var (exit, output) = await LaunchAsync(Run(set: [secret]));

        exit.Should().Be(1);
        output.Should().NotContain(secret);
        output.Should().Contain("KEY=VALUE");
        Launches.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_nothing_asked_for_is_warned_about_by_name_and_set_anyway_as_a_secret()
    {
        const string secret = "value_of_something_nobody_asked_for";
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-unasked"), Guid.CreateVersion7(), Running)]);

        var (exit, output) = await LaunchAsync(Run(set: [$"STRIPE_SECRET_KY={secret}"]));

        exit.Should().Be(0, output);
        output.Should().Contain("--set STRIPE_SECRET_KY: nothing here asked for this");
        output.Should().NotContain(secret);
        Launches.Confirmed.Single().Choices.EnvVars.Single(v => v.Key == "STRIPE_SECRET_KY").IsSecret.Should().BeTrue();
    }

    [Fact]
    public async Task A_person_is_asked_only_what_needs_answering_and_a_secret_is_asked_as_one()
    {
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-person"), Guid.CreateVersion7(), Running)]);
        var person = new Person { Answers = { ["STRIPE_SECRET_KEY"] = "sk_live_typed" } };

        var (exit, output) = await LaunchAsync(Run(yes: false), person);

        exit.Should().Be(0, output);
        person.Questions.Should().Equal(("STRIPE_SECRET_KEY", true), ("SUPPORT_EMAIL", false));
        person.Confirmations.Should().ContainSingle();
        output.Should().NotContain("sk_live_typed");
        output.Should().NotContain("Not answered", "a blank answer is an answer");

        var variables = Launches.Confirmed.Single().Choices.EnvVars;
        variables.Single(v => v.Key == "STRIPE_SECRET_KEY").Value.Should().Be("sk_live_typed");
        variables.Single(v => v.Key == "SUPPORT_EMAIL").Value.Should().BeNull("left blank, and blank is allowed");
    }

    [Fact]
    public async Task What_set_already_answered_is_not_asked_again()
    {
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-both"), Guid.CreateVersion7(), Running)]);
        var person = new Person();

        await LaunchAsync(Run(set: ["STRIPE_SECRET_KEY=x"], yes: false), person);

        person.Questions.Should().Equal(("SUPPORT_EMAIL", false));
    }

    [Fact]
    public async Task A_question_with_nobody_to_answer_it_and_no_yes_creates_nothing()
    {
        GivenALaunch();

        var (exit, output) = await LaunchAsync(Run(yes: false));

        exit.Should().Be(1);
        output.Should().Contain("Not answered, so not set: STRIPE_SECRET_KEY, SUPPORT_EMAIL");
        output.Should().Contain("--yes");
        output.Should().Contain("nothing was created");
        Launches.Validated.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
        Launches.Abandoned.Should().ContainSingle();
    }

    [Fact]
    public async Task Somebody_who_says_no_creates_nothing_and_lets_go_of_the_upload()
    {
        GivenALaunch();
        var person = new Person { SaysYes = false };

        var (exit, output) = await LaunchAsync(Run(yes: false), person);

        exit.Should().Be(1);
        person.Confirmations.Should().ContainSingle();
        output.Should().Contain("Nothing was created.");
        Launches.Confirmed.Should().BeEmpty();
        Launches.Abandoned.Should().ContainSingle();
    }

    // ---- services -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_service_turned_down_is_not_quoted_or_created_and_what_it_would_have_set_is_named()
    {
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-no-service"), Guid.CreateVersion7(), Running)]);

        var (exit, output) = await LaunchAsync(Run(noService: ["postgres"]));

        exit.Should().Be(0, output);
        Launches.Quoted.Single().Choices.Services.Should().BeEmpty();
        Launches.Confirmed.Single().Choices.Services.Should().BeEmpty();
        output.Should().NotContain("PostgreSQL, because of");
        output.Should().Contain("nothing will set DB_HOST, DATABASE_URL");
        Launches.Confirmed.Single().Choices.EnvVars.Should().Contain(v => v.Key == "DATABASE_URL" && v.Value == null,
            "so the timeline names it afterwards");
    }

    [Fact]
    public async Task A_database_of_their_own_is_set_as_a_secret_and_not_called_a_mistake_and_what_is_left_is_named()
    {
        const string url = "postgres://me:neon-password@neon.example/db";
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-own-db"), Guid.CreateVersion7(), Running)]);

        var (exit, output) = await LaunchAsync(Run(noService: ["postgres"], set: [$"DATABASE_URL={url}"]));

        exit.Should().Be(0, output);
        output.Should().NotContain("nothing here asked for this");
        output.Should().NotContain("neon-password");
        output.Should().Contain("nothing will set DB_HOST");
        output.Should().NotContain("nothing will set DB_HOST, DATABASE_URL");

        var own = Launches.Confirmed.Single().Choices.EnvVars.Single(v => v.Key == "DATABASE_URL");
        own.Value.Should().Be(url);
        own.IsSecret.Should().BeTrue();
    }

    [Fact]
    public async Task A_database_of_their_own_with_no_example_file_to_name_it_is_not_called_a_mistake_either()
    {
        var id = Id;
        GivenALaunch(LaunchScript.Proposal(id, fields: []), ending: [Succeeded(GivenAnApp("shop-own-db-bare"), Guid.CreateVersion7(), Running)]);

        var (exit, output) = await LaunchAsync(Run(noService: ["postgres"], set: ["DATABASE_URL=postgres://me:pw@neon.example/db"]));

        exit.Should().Be(0, output);
        output.Should().NotContain("nothing here asked for this");
        Launches.Confirmed.Single().Choices.EnvVars.Single(v => v.Key == "DATABASE_URL").IsSecret.Should().BeTrue();
    }

    [Fact]
    public async Task A_mistyped_service_stops_it_rather_than_quietly_creating_the_database()
    {
        GivenALaunch();

        var (exit, output) = await LaunchAsync(Run(noService: ["postgress"]));

        exit.Should().Be(1);
        output.Should().Contain("--no-service does not know postgress");
        output.Should().Contain("PostgreSql, Redis, MongoDb");
        Launches.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task The_name_flag_is_what_gets_created()
    {
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-name"), Guid.CreateVersion7(), Running)]);

        var (_, output) = await LaunchAsync(Run(name: "Corner Shop"));

        output.Should().Contain("The app Corner Shop");
        Launches.Confirmed.Single().Choices.Name.Should().Be("Corner Shop");
    }

    // ---- refusals before anything exists ------------------------------------------------------------------

    [Fact]
    public async Task A_quote_refusal_creates_nothing_and_names_billing()
    {
        GivenALaunch(quote: LaunchScript.Charged with
        {
            BalanceMinor = 0,
            Refusal = new ServiceRefusal("Your balance cannot pay for the first hour.", "موجودی کافی نیست."),
            RefusalIsAboutMoney = true
        });
        var person = new Person();

        var (exit, output) = await LaunchAsync(Run(yes: false), person);

        exit.Should().Be(1);
        output.Should().Contain("Your balance cannot pay for the first hour.");
        output.Should().Contain("Nothing was created.");
        output.Should().Contain($"{Server}/billing");
        person.Questions.Should().BeEmpty("there is no point asking anything it cannot go on to do");
        person.Confirmations.Should().BeEmpty();
        Launches.Validated.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_more_credit_would_not_fix_sends_nobody_to_billing()
    {
        GivenALaunch(quote: LaunchScript.Charged with
        {
            Refusal = new ServiceRefusal("This size has no price yet.", "این اندازه هنوز قیمت ندارد.")
        });

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(1);
        output.Should().Contain("This size has no price yet.");
        output.Should().Contain("Nothing was created.");
        output.Should().NotContain("Add credit").And.NotContain("/billing");
        Launches.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task Validation_refusals_are_printed_with_their_fields_and_the_panel_page_that_can_settle_them()
    {
        Launches.ValidateRefusals =
        [
            new ServiceRefusal("Choose a size.", "یک اندازه انتخاب کنید.", "InstanceSizeKey"),
            new ServiceRefusal("Plan quota exceeded.", "سقف پلن پر شده است.")
        ];
        GivenALaunch();

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(1);
        output.Should().Contain("InstanceSizeKey: Choose a size.");
        output.Should().Contain("Plan quota exceeded.");
        output.Should().Contain("Nothing was created.");
        output.Should().Contain($"{Server}/launch/{Id}");
        Launches.Confirmed.Should().BeEmpty();
        Launches.Abandoned.Should().BeEmpty("the panel's own screen can still settle this one");
    }

    // ---- following, and stopping ---------------------------------------------------------------------------

    [Fact]
    public async Task No_follow_returns_as_soon_as_it_is_confirmed_and_says_where_to_look()
    {
        GivenALaunch(ending: [Deploying(Guid.CreateVersion7())]);

        var (exit, output) = await LaunchAsync(Run(noFollow: true));

        exit.Should().Be(0);
        Launches.Confirmed.Should().ContainSingle();
        output.Should().Contain("It carries on without you.");
        output.Should().Contain($"{Server}/launch/{Id}");
        output.Should().NotContain("→ Deploying");
    }

    [Fact]
    public async Task Stopping_watches_no_more_and_says_the_launch_carries_on_and_how_to_look()
    {
        var deploymentId = Guid.CreateVersion7();
        var going = Deploying(deploymentId);
        GivenALaunch(ending: Enumerable.Repeat(going, 500).ToArray());

        using var stop = new CancellationTokenSource();
        var running = LaunchAsync(Run(), null, stop.Token);
        while (Launches.Confirmed.Count == 0 && !running.IsCompleted) await Task.Delay(1);

        // A look after the confirm's own one: the answer has come back and it is following.
        var seen = Launches.Reads;
        while (Launches.Reads == seen && !running.IsCompleted) await Task.Delay(1);
        await stop.CancelAsync();

        var (exit, output) = await running;

        exit.Should().Be(1, output);
        output.Should().Contain("Stopped watching. The launch carries on without you.");
        output.Should().Contain($"{Server}/launch/{Id}");
        Launches.Abandoned.Should().BeEmpty("stopping watching is not stopping the launch");
        Launches.Retried.Should().BeEmpty();
    }

    [Fact]
    public void Ctrl_C_really_is_wired_to_that()
    {
        // The token a command is handed comes from RunAsync(args), which nothing cancels: Spectre does
        // not turn the key into it. Without this handler the code above is only ever reached by a test.
        CliSource("LaunchCommand.cs").Should().Contain("Console.CancelKeyPress += pressed");
    }

    [Fact]
    public async Task Stopping_while_it_is_still_being_read_lets_go_of_it()
    {
        var id = Id;
        Launches.Add(_fixture.WorkspaceId, LaunchScript.View(LaunchStatus.Inspecting, id));
        using var stop = new CancellationTokenSource();

        var running = LaunchAsync(Run(), null, stop.Token);
        while (Launches.Reads == 0 && !running.IsCompleted) await Task.Delay(1);
        await stop.CancelAsync();

        var (exit, output) = await running;

        exit.Should().Be(1, output);
        output.Should().Contain("Stopped. Nothing was created.");
        Launches.Abandoned.Should().ContainSingle();
    }

    // ---- a connection that drops --------------------------------------------------------------------------

    [Fact]
    public async Task An_upload_cut_off_by_the_connection_says_to_try_again_and_blames_nothing_it_does_not_know_about()
    {
        var (exit, output) = await LaunchAsync(Run(), breaksOn: r => r.RequestUri!.AbsolutePath.EndsWith("/launch/archive"));

        exit.Should().Be(1);
        output.Should().Contain("Try again").And.Contain("Nothing was created.").And.Contain("SSL connection");
        output.Should().NotContain("app name", "a launch has none: the sentence the other upload route uses would send somebody the wrong way");
        Launches.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task A_moment_of_lost_connection_while_following_is_not_news()
    {
        var deploymentId = Guid.CreateVersion7();
        GivenALaunch(ending: [Deploying(deploymentId), Succeeded(GivenAnApp("shop-blip"), deploymentId, Running)]);

        var looks = 0;
        var (exit, output) = await LaunchAsync(Run(), breaksOn: r =>
        {
            if (r.Method != HttpMethod.Get || !Launches.Confirmed.Any() || !r.RequestUri!.AbsolutePath.Contains("/launch/")) return false;
            return ++looks is >= 2 and <= 4;
        });

        exit.Should().Be(0, output);
        output.Should().NotContain("SSL connection");
        output.Should().Contain("https://shop.apps.example.com");
    }

    [Fact]
    public async Task A_panel_that_stops_answering_while_it_is_followed_is_said_out_loud_and_is_not_a_success()
    {
        var deploymentId = Guid.CreateVersion7();
        GivenALaunch(ending: [Deploying(deploymentId)]);

        var (exit, output) = await LaunchAsync(Run(), breaksOn: r =>
            r.Method == HttpMethod.Get && Launches.Confirmed.Any() && r.RequestUri!.AbsolutePath.Contains("/launch/"));

        exit.Should().Be(1, output);
        output.Should().Contain("what happened next is not known");
        output.Should().Contain("The launch carries on without you.");
        output.Should().Contain($"{Server}/launch/{Id}");
        Launches.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_lost_before_the_confirm_says_how_far_it_got_is_not_known_rather_than_guessing()
    {
        GivenALaunch();

        var (exit, output) = await LaunchAsync(Run(), breaksOn: r => r.RequestUri!.AbsolutePath.EndsWith("/confirm"));

        exit.Should().Be(1, output);
        output.Should().Contain("how far this got is not known");
        output.Should().Contain($"{Server}/launch/{Id}");
    }

    // ---- the endings ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Succeeded_with_problems_is_exit_two_with_the_failed_checks_first_and_their_fixes()
    {
        var appId = GivenAnApp("shop-problems");
        var deploymentId = Guid.CreateVersion7();
        GivenALaunch(ending:
        [
            LaunchScript.View(LaunchStatus.SucceededWithProblems, Id, appId, "shop", "https://shop.apps.example.com", deploymentId,
                steps: LaunchScript.Steps(services: LaunchStepState.Done, app: LaunchStepState.Done, deploy: LaunchStepState.Done, verify: LaunchStepState.Failed),
                checks:
                [
                    Running,
                    new AppCheck("url", AppCheckStatus.Skipped, "Does the address work?", "x", "This is a worker."),
                    new AppCheck("service:1", AppCheckStatus.Failed, "Does the database answer?", "x", "Refused from the app's network.",
                        Fix: "Connect it to the environment's network.", LinkPath: "/databases/abc")
                ])
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(2, output);
        output.Should().Contain("https://shop.apps.example.com");
        output.Should().Contain("✗ Does the database answer?: Refused from the app's network.");
        output.Should().Contain("What to do: Connect it to the environment's network.");
        output.Should().Contain($"{Server}/databases/abc");

        output.IndexOf("Does the database answer?", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("Is it still running?", StringComparison.Ordinal), "what went wrong comes first");

        output.Should().Contain("- Does the address work?: not checked: This is a worker.");
        output.Should().NotContain("✓ Does the address work?", "a skipped check is never printed as a pass");

        File.Exists(Path.Combine(_dir, "harbora.yml")).Should().BeTrue("the app exists, and deploying again is how this gets fixed");
    }

    [Fact]
    public async Task Nothing_found_wrong_and_something_unchecked_is_not_called_wrong_and_names_what_could_not_be_checked()
    {
        var appId = GivenAnApp("shop-unsure");
        GivenALaunch(ending:
        [
            LaunchScript.View(LaunchStatus.SucceededWithProblems, Id, appId, "shop", "https://shop.apps.example.com", Guid.CreateVersion7(),
                checks:
                [
                    new AppCheck("running", AppCheckStatus.Skipped, "Is it still running?", "x", "The server did not answer."),
                    new AppCheck("logs", AppCheckStatus.Passed, "Is its log quiet?", "x")
                ])
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(2, output);
        output.Should().Contain("It came up, but could not be confirmed as working");
        output.Should().NotContain("something is wrong");
        output.Should().Contain("Could not be checked: Is it still running?");
    }

    [Fact]
    public async Task A_warning_is_still_something_wrong()
    {
        var appId = GivenAnApp("shop-warned");
        GivenALaunch(ending:
        [
            LaunchScript.View(LaunchStatus.SucceededWithProblems, Id, appId, "shop", "https://shop.apps.example.com", Guid.CreateVersion7(),
                checks: [Running, new AppCheck("logs", AppCheckStatus.Warning, "Is its log quiet?", "x", "ECONNREFUSED")])
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(2, output);
        output.Should().Contain("It is running, but something is wrong");
        output.Should().NotContain("could not be confirmed");
    }

    [Fact]
    public async Task A_launch_whose_app_was_deleted_says_so_is_not_a_success_and_remembers_nothing()
    {
        GivenALaunch(ending:
        [
            LaunchScript.View(LaunchStatus.Succeeded, Id, appName: "shop", publicUrl: "https://shop.apps.example.com", appGone: true,
                created: [new LaunchCreatedThing("database", "shop-postgresql", "/databases/one", Ready: true)])
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(1, output);
        output.Should().Contain("has since been deleted");
        output.Should().NotContain("Your app is running").And.NotContain("https://shop.apps.example.com");
        output.Should().Contain($"The database shop-postgresql: {Server}/databases/one");
        File.Exists(Path.Combine(_dir, "harbora.yml")).Should().BeFalse("there is no app to deploy to");
    }

    [Fact]
    public async Task A_failed_launch_whose_app_was_deleted_says_why_and_does_not_offer_to_try_again()
    {
        GivenALaunch(ending:
        [
            LaunchScript.View(LaunchStatus.Failed, Id, failure: "The build failed.", canRetry: true, appGone: true, created: [],
                steps: LaunchScript.Steps(deploy: LaunchStepState.Failed))
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(1, output);
        output.Should().Contain("The build failed.").And.Contain("has since been deleted");
        output.Should().NotContain("Trying again");
    }

    [Fact]
    public async Task A_skipped_check_on_a_success_is_still_not_a_pass()
    {
        GivenALaunch(ending:
        [
            Succeeded(GivenAnApp("shop-skipped"), Guid.CreateVersion7(), Running,
                new AppCheck("service:1", AppCheckStatus.Skipped, "Does the broker answer?", "x", "A broker has no shell client."))
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(0);
        output.Should().Contain("- Does the broker answer?: not checked: A broker has no shell client.");
        output.Should().NotContain("✓ Does the broker answer?");
    }

    [Fact]
    public async Task Failed_is_exit_one_with_the_step_the_message_what_exists_and_where_to_try_again()
    {
        var appId = GivenAnApp("shop-failed");
        var deploymentId = Guid.CreateVersion7();
        GivenALaunch(ending:
        [
            LaunchScript.View(LaunchStatus.Failed, Id, appId, "shop", deploymentId: deploymentId,
                failure: "The build failed: npm ci exited 1.",
                steps: LaunchScript.Steps(services: LaunchStepState.Done, app: LaunchStepState.Done, deploy: LaunchStepState.Failed),
                canRetry: true)
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(1, output);
        output.Should().Contain("It stopped at: Deploying");
        output.Should().Contain("The build failed: npm ci exited 1.");
        output.Should().Contain("What exists so far:");
        output.Should().Contain($"The app shop: {Server}/apps/details/{appId}");
        output.Should().Contain($"Its deployment: {Server}/deployments/details/{deploymentId}");
        output.Should().Contain($"Trying again could help. Do it on the panel: {Server}/launch/{Id}");
        File.Exists(Path.Combine(_dir, "harbora.yml")).Should().BeFalse("it did not work");
    }

    [Fact]
    public async Task A_failure_with_nothing_to_retry_does_not_offer_it()
    {
        GivenALaunch(ending:
        [
            LaunchScript.View(LaunchStatus.Failed, Id, failure: "No.", steps: LaunchScript.Steps(services: LaunchStepState.Failed), canRetry: false)
        ]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(1);
        output.Should().NotContain("Trying again");
        output.Should().NotContain("What exists so far");
    }

    [Fact]
    public async Task A_launch_abandoned_while_it_was_being_watched_says_so_and_is_not_a_success()
    {
        GivenALaunch(ending: [LaunchScript.View(LaunchStatus.Abandoned, Id)]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(1);
        output.Should().Contain("ended as Abandoned");
    }

    // ---- next time -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Success_from_a_folder_writes_the_file_that_makes_deploy_work_and_the_last_line_says_so()
    {
        GivenALaunch(ending: [Succeeded(GivenAnApp("corner-shop"), Guid.CreateVersion7(), Running)]);

        var (_, output) = await LaunchAsync(Run());

        var config = ProjectConfig.Load(_dir);
        config.App.Should().Be("corner-shop", "the slug, read from the server, not the name it was given");
        config.Server.Should().Be(Server);
        output.Should().EndWith("Wrote harbora.yml. From now on, in this folder, just run harbora deploy");
    }

    [Fact]
    public async Task An_existing_config_is_never_overwritten_and_the_last_line_says_that_instead()
    {
        const string mine = "app: something-else\n# mine\n";
        File.WriteAllText(Path.Combine(_dir, "harbora.yml"), mine);
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-existing"), Guid.CreateVersion7(), Running)]);

        var (exit, output) = await LaunchAsync(Run());

        exit.Should().Be(0, output);
        File.ReadAllText(Path.Combine(_dir, "harbora.yml")).Should().Be(mine);
        output.Should().EndWith("harbora.yml was already here and was left as it is. To deploy this app from here, run harbora deploy shop-existing");
    }

    [Fact]
    public async Task An_ignore_list_in_the_config_applies_to_the_launch_as_it_does_to_a_deploy()
    {
        File.WriteAllText(Path.Combine(_dir, "harbora.yml"), "ignore:\n  - private-notes\n");
        Directory.CreateDirectory(Path.Combine(_dir, "private-notes"));
        File.WriteAllText(Path.Combine(_dir, "private-notes", "a.txt"), "not for the server");
        GivenALaunch(ending: [Succeeded(GivenAnApp("shop-ignore"), Guid.CreateVersion7(), Running)]);

        await LaunchAsync(Run());

        (await ReadTarAsync(Launches.Started.Single().ArchivePath!)).Keys.Should().NotContain(k => k.StartsWith("private-notes"));
    }

    // ---- the command line ------------------------------------------------------------------------------------

    private sealed class StopHere : Exception;

    private sealed class Seen(Action<LaunchCommand.Settings> seen) : ICommandInterceptor
    {
        public void Intercept(CommandContext context, CommandSettings settings)
        {
            seen((LaunchCommand.Settings)settings);
            throw new StopHere();
        }
    }

    [Fact]
    public async Task The_flags_parse_the_way_the_brief_writes_them()
    {
        LaunchCommand.Settings? parsed = null;
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.AddCommand<LaunchCommand>("launch");
            config.SetInterceptor(new Seen(s => parsed = s));
            config.PropagateExceptions();
        });

        var run = () => app.RunAsync(
        [
            "launch", "./app",
            "--folder", "services/api", "--name", "Shop",
            "--set", "A=1", "--set", "B=x=y",
            "--no-service", "redis", "--no-service", "postgres",
            "-y", "--no-follow",
            "--server", "https://panel.example.com", "--token", "tok", "--account", "me@example.com"
        ]);
        await run.Should().ThrowAsync<StopHere>();

        parsed!.Path.Should().Be("./app");
        parsed.Folder.Should().Be("services/api");
        parsed.Name.Should().Be("Shop");
        parsed.Set.Should().Equal("A=1", "B=x=y");
        parsed.NoService.Should().Equal("redis", "postgres");
        parsed.Yes.Should().BeTrue();
        parsed.NoFollow.Should().BeTrue();
        parsed.Server.Should().Be("https://panel.example.com");
        parsed.Token.Should().Be("tok");
        parsed.Account.Should().Be("me@example.com");
    }

    [Fact]
    public async Task The_git_flags_parse_too()
    {
        LaunchCommand.Settings? parsed = null;
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.AddCommand<LaunchCommand>("launch");
            config.SetInterceptor(new Seen(s => parsed = s));
            config.PropagateExceptions();
        });

        var run = () => app.RunAsync(["launch", "--git", "https://github.com/you/shop", "--ref", "develop", "--git-token", "ghp_x"]);
        await run.Should().ThrowAsync<StopHere>();

        parsed!.Git.Should().Be("https://github.com/you/shop");
        parsed.Ref.Should().Be("develop");
        parsed.GitToken.Should().Be("ghp_x");
        parsed.Path.Should().BeNull();
    }

    [Theory]
    [InlineData("not both", "launch", "./app", "--git", "https://github.com/you/shop")]
    [InlineData("only mean something with --git", "launch", "--ref", "develop")]
    [InlineData("only mean something with --git", "launch", "--git-token", "ghp_x")]
    public async Task Flags_that_contradict_each_other_stop_the_real_command_before_it_needs_a_server(string expected, params string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.AddCommand<LaunchCommand>("launch");
            config.PropagateExceptions();
        });

        var (exit, output) = await CapturedAsync(() => app.RunAsync(args));

        exit.Should().Be(1);
        output.Should().Contain(expected);
        output.Should().NotContain("ghp_x");
    }

    [Fact]
    public void The_cli_registers_the_command_and_the_git_token_can_come_from_the_environment()
    {
        CliSource("Program.cs").Should().Contain("AddCommand<LaunchCommand>(\"launch\")");
        CliSource("LaunchCommand.cs").Should().Contain("HARBORA_GIT_TOKEN");
        typeof(LaunchCommand.Settings).GetProperty(nameof(LaunchCommand.Settings.GitToken))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description.Should().Contain("HARBORA_GIT_TOKEN");
    }

    [Fact]
    public void The_cli_keeps_the_panels_reading_to_itself()
    {
        // Two readings is how they come to disagree: the server's is the proposal.
        foreach (var file in new[] { "LaunchCommand.cs", "LaunchPlan.cs" })
            CliSource(file).Should().NotContain("SourceInspector").And.NotContain("Harbora.Shared.Inspection");
    }

    private static string CliSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Harbora.Cli", file));
    }
}
