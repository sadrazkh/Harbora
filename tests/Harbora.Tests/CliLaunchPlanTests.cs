using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Harbora.Cli;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Verification;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What <c>harbora launch</c> decides without a terminal or a network. The JSON here is the contracts
/// themselves, serialised the way <c>LaunchApiController</c> says it serialises them, so a contract that
/// moves breaks these rather than a hand-written string that goes on agreeing with itself.
/// <c>CliLaunchEndToEndTests</c> covers the same thing through the real controller.
/// </summary>
public class CliLaunchPlanTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, value.GetType(), Wire);

    private static readonly JsonElement Proposal = Json(LaunchScript.Proposal(Guid.CreateVersion7()));

    private static readonly IReadOnlySet<string> NothingDropped = new HashSet<string>();

    private static Dictionary<string, string> NoAnswers => new();

    // ---- flags ------------------------------------------------------------------------------------

    [Fact]
    public void Flags_that_contradict_each_other_are_said_before_anything_is_sent()
    {
        LaunchPlan.OptionProblem(null, null, null, null).Should().BeNull();
        LaunchPlan.OptionProblem("./app", null, null, null).Should().BeNull();
        LaunchPlan.OptionProblem(null, "https://x/y", "main", "tok").Should().BeNull();

        LaunchPlan.OptionProblem("./app", "https://x/y", null, null).Should().Contain("not both");
        LaunchPlan.OptionProblem(null, null, "main", null).Should().Contain("--git");
        LaunchPlan.OptionProblem(null, null, null, "tok").Should().Contain("--git");
    }

    [Fact]
    public void Set_splits_at_the_first_equals_and_the_last_one_wins()
    {
        var (answers, problems) = LaunchPlan.ParseSet(["A=1", "B=x=y=z", "C=", "A=2", " D =spaced "]);

        problems.Should().BeEmpty();
        answers.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["A"] = "2", ["B"] = "x=y=z", ["C"] = "", ["D"] = "spaced "
        });
    }

    [Fact]
    public void A_set_with_no_key_is_reported_by_position_and_never_quoted()
    {
        // Somebody who left out the KEY= has typed the value on its own, and it is probably a secret.
        var (answers, problems) = LaunchPlan.ParseSet(["A=1", "sk_live_only_the_value", "=sk_live_no_key", "  =sk_live_blank_key"]);

        answers.Keys.Should().Equal("A");
        problems.Should().HaveCount(3);
        problems[0].Should().Contain("number 2");
        string.Join(" ", problems).Should().NotContain("sk_live");
    }

    [Fact]
    public void No_set_at_all_is_nothing_to_answer()
    {
        var (answers, problems) = LaunchPlan.ParseSet(null);

        answers.Should().BeEmpty();
        problems.Should().BeEmpty();
    }

    // ---- which folder -----------------------------------------------------------------------------

    [Fact]
    public void A_folder_is_needed_only_while_there_are_several_and_none_is_chosen()
    {
        var id = Guid.CreateVersion7();

        LaunchPlan.NeedsFolder(Proposal).Should().BeFalse();
        LaunchPlan.NeedsFolder(Json(LaunchScript.Proposal(id, LaunchScript.Inspection(roots: ["a", "b"])))).Should().BeTrue();
        LaunchPlan.NeedsFolder(Json(LaunchScript.Proposal(id, LaunchScript.Inspection(roots: ["a", "b"], root: "a")))).Should().BeFalse();
        LaunchPlan.Candidates(Json(LaunchScript.Proposal(id, LaunchScript.Inspection(roots: ["a", "b"])))).Should().Equal("a", "b");
    }

    [Fact]
    public void A_folder_flag_only_has_to_agree_when_there_was_nothing_to_choose_between()
    {
        var atRoot = Proposal;
        var inFolder = Json(LaunchScript.Proposal(Guid.CreateVersion7(), LaunchScript.Inspection(root: "services/api")));

        LaunchPlan.FolderDisagrees(atRoot, null).Should().BeFalse();
        LaunchPlan.FolderDisagrees(atRoot, "services/api").Should().BeTrue("that folder was never read");
        LaunchPlan.FolderDisagrees(inFolder, "services/api").Should().BeFalse();
        LaunchPlan.FolderDisagrees(inFolder, @"services\api\").Should().BeFalse();
        LaunchPlan.FolderDisagrees(inFolder, "services/web").Should().BeTrue();
    }

    // ---- no-service --------------------------------------------------------------------------------

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("postgresql")]
    [InlineData("postgres")]
    [InlineData("POSTGRES")]
    public void A_service_is_turned_down_by_any_clear_start_of_its_type(string name)
    {
        var (dropped, unknown) = LaunchPlan.ResolveNoService(Proposal, [name]);

        dropped.Should().Equal("PostgreSql");
        unknown.Should().BeEmpty();
    }

    [Theory]
    [InlineData("postgress")]
    [InlineData("")]
    [InlineData("oracle")]
    public void A_service_name_that_means_nothing_is_reported_because_they_think_it_saved_them_money(string name)
    {
        var (dropped, unknown) = LaunchPlan.ResolveNoService(Proposal, [name]);

        dropped.Should().BeEmpty();
        unknown.Should().Equal(name);
    }

    [Fact]
    public void A_name_that_could_be_two_services_is_not_guessed_at()
    {
        var proposal = Json(new
        {
            serviceOptions = new[] { new { type = "MySql" }, new { type = "MongoDb" } },
            defaults = new { services = Array.Empty<object>() }
        });

        var (dropped, unknown) = LaunchPlan.ResolveNoService(proposal, ["m"]);

        dropped.Should().BeEmpty();
        unknown.Should().Equal("m");
    }

    [Fact]
    public void A_proposed_service_the_catalogue_no_longer_offers_can_still_be_turned_down()
    {
        var proposal = Json(new
        {
            serviceOptions = new[] { new { type = "Redis" } },
            defaults = new { services = new[] { new { type = "Nats" } } }
        });

        LaunchPlan.ResolveNoService(proposal, ["nats"]).Dropped.Should().Equal("Nats");
    }

    // ---- the choices -------------------------------------------------------------------------------

    [Fact]
    public void With_nothing_said_the_choices_are_the_proposals_own_defaults_exactly()
    {
        var choices = LaunchPlan.ChoicesFrom(Proposal, null, NoAnswers, NothingDropped);

        JsonSerializer.SerializeToElement(choices).GetRawText()
            .Should().Be(Proposal.GetProperty("defaults").GetRawText());
    }

    [Fact]
    public void What_this_cli_does_not_ask_about_goes_back_as_it_came()
    {
        var choices = LaunchPlan.ChoicesFrom(Proposal, "renamed", new Dictionary<string, string> { ["LOG_LEVEL"] = "debug" }, NothingDropped);

        choices["name"]!.GetValue<string>().Should().Be("renamed");
        choices["kind"]!.GetValue<string>().Should().Be("Web");
        choices["containerPort"]!.GetValue<int>().Should().Be(3000);
        choices["releaseCommand"]!.GetValue<string>().Should().Be("npx prisma migrate deploy");
        choices["instanceSizeKey"]!.GetValue<string>().Should().Be("small");
    }

    [Fact]
    public void A_blank_name_is_not_a_name()
    {
        LaunchPlan.ChoicesFrom(Proposal, "   ", NoAnswers, NothingDropped)["name"]!.GetValue<string>().Should().Be("shop");
    }

    [Fact]
    public void An_answer_lands_on_its_variable_and_keeps_what_the_server_says_about_it()
    {
        var choices = LaunchPlan.ChoicesFrom(Proposal, null, new Dictionary<string, string>
        {
            ["STRIPE_SECRET_KEY"] = "sk_live_real",
            ["LOG_LEVEL"] = "debug"
        }, NothingDropped);

        var variables = choices["envVars"]!.AsArray();
        var stripe = variables.Single(v => v!["key"]!.GetValue<string>() == "STRIPE_SECRET_KEY")!;
        stripe["value"]!.GetValue<string>().Should().Be("sk_live_real");
        stripe["isSecret"]!.GetValue<bool>().Should().BeTrue();

        variables.Single(v => v!["key"]!.GetValue<string>() == "LOG_LEVEL")!["value"]!.GetValue<string>().Should().Be("debug");
        variables.Single(v => v!["key"]!.GetValue<string>() == "JWT_SECRET")!["value"].Should().BeNull("the server generates that one");
    }

    [Fact]
    public void A_blank_answer_means_unset_not_an_empty_string()
    {
        var choices = LaunchPlan.ChoicesFrom(Proposal, null, new Dictionary<string, string>
        {
            ["SUPPORT_EMAIL"] = "",
            ["LOG_LEVEL"] = ""
        }, NothingDropped);

        var variables = choices["envVars"]!.AsArray();
        variables.Single(v => v!["key"]!.GetValue<string>() == "SUPPORT_EMAIL")!["value"].Should().BeNull();
        variables.Single(v => v!["key"]!.GetValue<string>() == "LOG_LEVEL")!["value"].Should().BeNull("blanking a suggestion drops it");
    }

    [Fact]
    public void An_answer_to_something_nobody_asked_is_sent_and_kept_as_a_secret()
    {
        var answers = new Dictionary<string, string> { ["SENTRY_DSN"] = "https://x@sentry.example.com/1" };

        var added = LaunchPlan.ChoicesFrom(Proposal, null, answers, NothingDropped)["envVars"]!.AsArray()
            .Single(v => v!["key"]!.GetValue<string>() == "SENTRY_DSN")!;

        added["value"]!.GetValue<string>().Should().Be("https://x@sentry.example.com/1");
        added["isSecret"]!.GetValue<bool>().Should().BeTrue("the safer wrong guess is to mask it");
        LaunchPlan.UnaskedKeys(Proposal, answers).Should().Equal("SENTRY_DSN");
    }

    [Fact]
    public void An_answer_to_an_alias_is_something_the_proposal_knows_about()
    {
        var answers = new Dictionary<string, string> { ["DB_HOST"] = "db.example.com" };

        LaunchPlan.UnaskedKeys(Proposal, answers).Should().BeEmpty();
        LaunchPlan.ChoicesFrom(Proposal, null, answers, NothingDropped)["envVars"]!.AsArray()
            .Single(v => v!["key"]!.GetValue<string>() == "DB_HOST")!["isSecret"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void A_service_turned_down_goes_with_its_aliases_and_is_named_as_leaving_them_unset()
    {
        var dropped = new HashSet<string> { "PostgreSql" };

        LaunchPlan.ChoicesFrom(Proposal, null, NoAnswers, dropped)["services"]!.AsArray().Should().BeEmpty();

        var description = LaunchPlan.Describe(Proposal, null, NoAnswers, dropped);
        description.Unattached.Should().Equal("DB_HOST");
        description.Creates.Should().ContainSingle().Which.Should().Be("The app shop");

        LaunchPlan.Describe(Proposal, null, new Dictionary<string, string> { ["DB_HOST"] = "x" }, dropped)
            .Unattached.Should().BeEmpty("they answered it themselves");
    }

    // ---- questions ---------------------------------------------------------------------------------

    [Fact]
    public void Only_what_needs_an_answer_is_asked_and_only_until_it_has_one()
    {
        LaunchPlan.Questions(Proposal, NoAnswers).Should().Equal(
            new LaunchQuestion("STRIPE_SECRET_KEY", true),
            new LaunchQuestion("SUPPORT_EMAIL", false));

        LaunchPlan.Questions(Proposal, new Dictionary<string, string> { ["STRIPE_SECRET_KEY"] = "" })
            .Should().Equal(new LaunchQuestion("SUPPORT_EMAIL", false));
    }

    // ---- the proposal as sentences ------------------------------------------------------------------

    [Fact]
    public void The_description_says_what_it_found_what_it_will_create_and_why()
    {
        var description = LaunchPlan.Describe(Proposal, null, NoAnswers, NothingDropped);

        description.Found.Should().Be("This looks like a Node.js app, listening on port 3000.");
        description.Details.Should().Equal("Before traffic moves it will run: npx prisma migrate deploy");
        description.Notes.Should().Equal("A real .env was left out.");
        description.Creates.Should().Equal("The app shop", "PostgreSQL, because of \"pg\" in dependencies (package.json)");
        description.Guesses.Should().ContainSingle().Which.Should().Contain("Redis").And.Contain("REDIS_URL named").And.Contain("will not be created");
        description.Generated.Should().Equal("JWT_SECRET");
        description.Kept.Should().Equal("LOG_LEVEL");
    }

    [Fact]
    public void A_name_from_the_flag_is_the_name_it_says_it_will_create()
    {
        LaunchPlan.Describe(Proposal, "renamed", NoAnswers, NothingDropped).Creates[0].Should().Be("The app renamed");
    }

    [Fact]
    public void A_secret_somebody_supplied_is_not_described_as_generated()
    {
        LaunchPlan.Describe(Proposal, null, new Dictionary<string, string> { ["JWT_SECRET"] = "mine" }, NothingDropped)
            .Generated.Should().BeEmpty();
    }

    [Fact]
    public void The_description_holds_no_value_of_any_kind()
    {
        var description = LaunchPlan.Describe(Proposal, null, new Dictionary<string, string> { ["STRIPE_SECRET_KEY"] = "sk_live_typed" }, NothingDropped);

        JsonSerializer.Serialize(description).Should().NotContain("sk_live").And.NotContain("example-alias-host").And.NotContain("\"info\"");
    }

    [Fact]
    public void A_worker_is_not_described_as_listening_on_anything()
    {
        var worker = LaunchScript.Inspection() with { Kind = Harbora.Shared.Inspection.DetectedAppKind.Worker, RootDirectory = "bot" };

        LaunchPlan.Describe(Json(LaunchScript.Proposal(Guid.CreateVersion7(), worker)), null, NoAnswers, NothingDropped)
            .Found.Should().Be("This looks like a Node.js app in the folder bot, running as a worker with no address of its own.");
    }

    [Fact]
    public void A_stack_it_could_not_recognise_is_said_plainly()
    {
        var unknown = LaunchScript.Inspection() with { Stack = Harbora.Shared.Inspection.DetectedStack.Unknown };

        LaunchPlan.Describe(Json(LaunchScript.Proposal(Guid.CreateVersion7(), unknown)), null, NoAnswers, NothingDropped)
            .Found.Should().Contain("could not tell");
    }

    // ---- what it costs ------------------------------------------------------------------------------

    private static JsonElement Quote(long firstHour, long? hourly, long? balance = null, bool exempt = false, string? refusal = null) => Json(new
    {
        lines = new[] { new { name = "App", nameFa = "اپ", hourlyMinor = hourly } },
        firstHourMinor = firstHour,
        balanceMinor = balance,
        currency = "USD",
        exempt,
        refusal = refusal is null ? null : new { message = refusal, messageFa = refusal, field = (string?)null },
        firstHour = "12.00 USD",
        perHour = "3.00 USD",
        balance = balance is null ? null : "300.00 USD"
    });

    [Fact]
    public void The_cost_is_one_sentence_whichever_it_is()
    {
        LaunchPlan.CostSentence(Quote(1200, 300, 30000))
            .Should().Be("12.00 USD comes off your balance now (one hour of running), then 3.00 USD an hour. Balance: 300.00 USD.");
        LaunchPlan.CostSentence(Quote(1200, 300))
            .Should().Be("12.00 USD comes off your balance now (one hour of running), then 3.00 USD an hour.");
        LaunchPlan.CostSentence(Quote(0, 0)).Should().Contain("free");
        LaunchPlan.CostSentence(Quote(1200, 300, exempt: true)).Should().Contain("Nothing is charged");
    }

    [Fact]
    public void A_quote_refusal_is_read_and_a_quote_without_one_is_not_refused()
    {
        LaunchPlan.QuoteRefusal(Quote(1200, 300, refusal: "No credit.")).Should().Be("No credit.");
        LaunchPlan.QuoteRefusal(Quote(1200, 300)).Should().BeNull();
    }

    // ---- refusals -----------------------------------------------------------------------------------

    [Fact]
    public void A_refusal_is_printed_with_its_field_when_it_has_one()
    {
        LaunchPlan.RefusalLines("""{ "errors": [ { "field": "InstanceSizeKey", "message": "Choose a size.", "messageFa": "x" }, { "field": null, "message": "Plan full.", "messageFa": "y" } ] }""")
            .Should().Equal("InstanceSizeKey: Choose a size.", "Plan full.");
    }

    [Theory]
    [InlineData("""{ "error": "Launch not found." }""", "Launch not found.")]
    public void The_older_single_sentence_is_read_too(string body, string expected)
    {
        LaunchPlan.RefusalLines(body).Should().Equal(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>a proxy said no</html>")]
    [InlineData("[]")]
    public void A_body_with_nothing_to_say_is_nothing_to_say(string body)
    {
        LaunchPlan.RefusalLines(body).Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "harbora login")]
    [InlineData(HttpStatusCode.Forbidden, "may not create apps")]
    [InlineData(HttpStatusCode.NotFound, "older than this CLI")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "512 MB")]
    public void A_status_with_no_body_still_gets_a_sentence(HttpStatusCode status, string expected)
    {
        LaunchPlan.WhyRefused(new ApiRefusedException(status, "", $"{(int)status} Nope: ")).Should().ContainSingle()
            .Which.Should().Contain(expected);
    }

    // ---- what an ending is --------------------------------------------------------------------------

    [Theory]
    [InlineData(LaunchPlan.Succeeded, 0)]
    [InlineData(LaunchPlan.SucceededWithProblems, 2)]
    [InlineData(LaunchPlan.Failed, 1)]
    [InlineData(LaunchPlan.InspectFailed, 1)]
    [InlineData(LaunchPlan.Abandoned, 1)]
    [InlineData("Deploying", 0)]
    public void Each_ending_has_its_exit_code(string status, int expected)
    {
        LaunchPlan.ExitCode(status).Should().Be(expected);
    }

    [Fact]
    public void The_status_names_this_cli_compares_against_are_the_panels()
    {
        // This project cannot reference the enum, so a renamed status would just stop matching, and a
        // launch that failed would read as still going for ever.
        new[]
        {
            LaunchPlan.Inspecting, LaunchPlan.InspectFailed, LaunchPlan.AwaitingConfirmation,
            LaunchPlan.Succeeded, LaunchPlan.SucceededWithProblems, LaunchPlan.Failed, LaunchPlan.Abandoned
        }.Should().BeSubsetOf(Enum.GetNames<LaunchStatus>());
    }

    [Fact]
    public void Every_step_state_and_check_status_the_panel_has_is_one_this_cli_knows()
    {
        foreach (var state in Enum.GetValues<LaunchStepState>())
        {
            var line = LaunchPlan.StepLine(Json(new LaunchStepView("deploy", "Deploying", "x", state)), launchFailed: false);
            if (state == LaunchStepState.Waiting) line.Should().BeNull("a step that has not begun is not news");
            else line.Should().NotBeNull($"{state} would otherwise never be printed");
        }

        foreach (var status in Enum.GetValues<AppCheckStatus>())
            LaunchPlan.CheckLine(Json(new AppCheck("url", status, "Does it work?", "x"))).Icon.Should().NotBe("?", $"{status} should be a state this knows");
    }

    [Fact]
    public void A_step_prints_with_its_detail_except_a_failed_launchs_failure_which_is_said_once_at_the_end()
    {
        var failed = Json(new LaunchStepView("deploy", "Deploying", "x", LaunchStepState.Failed, "The build failed."));

        LaunchPlan.StepLine(failed, launchFailed: false)!.Text.Should().Be("Deploying: The build failed.");
        LaunchPlan.StepLine(failed, launchFailed: true)!.Text.Should().Be("Deploying");
    }

    [Fact]
    public void A_skipped_step_says_so()
    {
        LaunchPlan.StepLine(Json(new LaunchStepView("services", "Databases", "x", LaunchStepState.Skipped, "This app needs none.")), false)!
            .Text.Should().Be("Databases: skipped: This app needs none.");
    }

    [Theory]
    [InlineData(AppCheckStatus.Skipped, "not checked")]
    [InlineData(AppCheckStatus.Pending, "not settled yet")]
    public void A_check_that_was_not_answered_gets_no_tick_and_says_so(AppCheckStatus status, string words)
    {
        var line = LaunchPlan.CheckLine(Json(new AppCheck("url", status, "Does the address work?", "x", "A v1 node cannot run a one-off.")));

        line.Icon.Should().NotBe("✓");
        line.Color.Should().NotBe("green");
        line.Text.Should().Contain(words).And.Contain("A v1 node cannot run a one-off.");
    }

    [Fact]
    public void Only_a_check_that_passed_gets_a_tick()
    {
        foreach (var status in Enum.GetValues<AppCheckStatus>())
        {
            var line = LaunchPlan.CheckLine(Json(new AppCheck("url", status, "t", "x")));
            (line.Icon == "✓").Should().Be(status == AppCheckStatus.Passed, $"{status}");
        }
    }

    [Fact]
    public void What_went_wrong_comes_first()
    {
        var view = Json(new
        {
            checks = new[]
            {
                new AppCheck("running", AppCheckStatus.Passed, "a", "x"),
                new AppCheck("url", AppCheckStatus.Skipped, "b", "x"),
                new AppCheck("logs", AppCheckStatus.Warning, "c", "x"),
                new AppCheck("service:1", AppCheckStatus.Failed, "d", "x"),
                new AppCheck("restarts", AppCheckStatus.Passed, "e", "x")
            }
        });

        LaunchPlan.ChecksWorstFirst(view).Select(c => c.GetProperty("title").GetString()).Should().Equal("d", "c", "a", "b", "e");
    }

    [Fact]
    public void What_exists_says_only_what_the_view_can_vouch_for_on_the_panels_real_pages()
    {
        var appId = Guid.CreateVersion7();
        var deploymentId = Guid.CreateVersion7();
        var view = Json(LaunchScript.View(LaunchStatus.Failed, Guid.CreateVersion7(), appId, "shop", deploymentId: deploymentId,
            steps: LaunchScript.Steps(services: LaunchStepState.Done, app: LaunchStepState.Done, deploy: LaunchStepState.Failed)));

        LaunchPlan.WhatExists(view, "https://panel.example.com").Should().Equal(
            $"The app shop: https://panel.example.com/apps/details/{appId}",
            "Its databases, on the app's page",
            $"Its deployment: https://panel.example.com/deployments/details/{deploymentId}");

        LaunchPlan.WhatExists(Json(LaunchScript.View(LaunchStatus.Failed, Guid.CreateVersion7())), "https://panel.example.com")
            .Should().BeEmpty();
    }
}
