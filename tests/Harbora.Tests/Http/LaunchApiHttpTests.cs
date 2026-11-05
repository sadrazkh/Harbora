using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Verification;
using Harbora.Web.Controllers.Api;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>/api/v1/launch</c> as requests: the real pipeline (bearer authentication, the capability policies,
/// the controller) over a scripted <see cref="ILaunchService"/>. What these prove is the API's half of
/// the bargain. That the real service survives being driven by a token is
/// <see cref="LaunchApiTokenHttpTests"/>'s.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class LaunchApiHttpTests : IClassFixture<LaunchPanelFixture>
{
    private readonly LaunchPanelFixture _fixture;
    private static int _nextUser;

    public LaunchApiHttpTests(LaunchPanelFixture fixture)
    {
        _fixture = fixture;
        fixture.Launches.Reset();
    }

    private FakeLaunchService Launches => _fixture.Launches;

    private HttpClient Client(SystemRole role = SystemRole.Owner, Guid? workspace = null) =>
        _fixture.BearerClient(_fixture.GivenToken(
            workspace ?? _fixture.WorkspaceId,
            $"launch-api-{Interlocked.Increment(ref _nextUser)}@example.com",
            role));

    private Guid Given(LaunchStatus status, LaunchProposal? proposal = null, LaunchQuote? quote = null, Guid? workspace = null, LaunchView? view = null)
    {
        var id = view?.Id ?? Guid.CreateVersion7();
        Launches.Add(workspace ?? _fixture.WorkspaceId, view ?? LaunchScript.View(status, id), proposal, quote);
        return id;
    }

    private Guid GivenAwaiting(LaunchQuote? quote = null, Guid? workspace = null)
    {
        var id = Guid.CreateVersion7();
        Launches.Add(workspace ?? _fixture.WorkspaceId, LaunchScript.View(LaunchStatus.AwaitingConfirmation, id), LaunchScript.Proposal(id), quote);
        return id;
    }

    private static StringContent ChoicesJson(string json = """{ "name": "shop" }""") => new(json, Encoding.UTF8, "application/json");

    private static ByteArrayContent Archive(byte[]? bytes = null)
    {
        var body = new ByteArrayContent(bytes ?? [0x1F, 0x8B, 1, 2, 3]);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        return body;
    }

    private IEnumerable<string> Uploads()
    {
        var directory = Path.Combine(_fixture.WorkDir, "_uploads");
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory) : [];
    }

    // ---- starting from a repository ----------------------------------------------------------------

    [Fact]
    public async Task A_repository_starts_a_launch_as_the_caller_and_answers_its_id()
    {
        var response = await Client().PostAsync("/api/v1/launch", HttpConversation.Json(new
        {
            cloneUrl = "https://github.com/you/shop", gitRef = "develop", gitToken = "ghp_in_the_body"
        }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("id").GetGuid().Should().Be(Launches.NextStartId);

        var started = Launches.Started.Should().ContainSingle().Subject;
        started.Source.Should().Be(LaunchSourceKind.Git);
        started.CloneUrl.Should().Be("https://github.com/you/shop");
        started.GitRef.Should().Be("develop");
        started.GitToken.Should().Be("ghp_in_the_body");
        started.ArchivePath.Should().BeNull();
        started.WorkspaceId.Should().Be(_fixture.WorkspaceId, "the workspace comes from the token, never from the body");
        started.UserId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_refused_start_is_a_400_in_the_refusal_shape_with_both_languages()
    {
        Launches.StartRefusal = new ServiceRefusal("Only http and https.", "فقط http و https.", "CloneUrl");

        var response = await Client().PostAsync("/api/v1/launch", HttpConversation.Json(new { cloneUrl = "git@github.com:you/shop.git" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await response.JsonAsync()).GetProperty("errors").EnumerateArray().Should().ContainSingle().Subject;
        error.GetProperty("field").GetString().Should().Be("CloneUrl");
        error.GetProperty("message").GetString().Should().Be("Only http and https.");
        error.GetProperty("messageFa").GetString().Should().Be("فقط http و https.");
    }

    // ---- starting from a pushed folder ---------------------------------------------------------------

    [Fact]
    public async Task An_archive_is_written_where_a_launch_can_name_itself_from_it()
    {
        var bytes = "\u001f\u008bnot really a tar, but the bytes are what is being compared"u8.ToArray();

        var response = await Client().PostAsync("/api/v1/launch/archive?name=my-shop", Archive(bytes));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("id").GetGuid().Should().Be(Launches.NextStartId);

        var started = Launches.Started.Should().ContainSingle().Subject;
        started.Source.Should().Be(LaunchSourceKind.Upload);
        started.CloneUrl.Should().BeNull();

        var path = started.ArchivePath!;
        Path.GetDirectoryName(path).Should().Be(Path.Combine(_fixture.WorkDir, "_uploads"));
        Path.GetFileName(path).Should().MatchRegex(@"^my-shop-[0-9a-f]{32}\.tar\.gz$");
        LaunchSource.NameFromArchive(path).Should().Be("my-shop", "a launch's name comes from this path and nothing else");
        (await File.ReadAllBytesAsync(path)).Should().Equal(bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%20%20")]
    [InlineData("---")]
    public async Task A_blank_or_unusable_name_is_allowed_and_comes_out_as_the_plain_default(string name)
    {
        var response = await Client().PostAsync($"/api/v1/launch/archive?name={name}", Archive());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        LaunchSource.NameFromArchive(Launches.Started.Single().ArchivePath!).Should().Be("app");
    }

    [Fact]
    public async Task No_name_at_all_is_allowed_too()
    {
        (await Client().PostAsync("/api/v1/launch/archive", Archive())).StatusCode.Should().Be(HttpStatusCode.OK);

        LaunchSource.NameFromArchive(Launches.Started.Single().ArchivePath!).Should().Be("app");
    }

    [Fact]
    public async Task A_name_cannot_put_the_archive_anywhere_else()
    {
        var response = await Client().PostAsync(
            $"/api/v1/launch/archive?name={Uri.EscapeDataString("../../escaped/evil")}", Archive());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var path = Launches.Started.Single().ArchivePath!;
        Path.GetDirectoryName(path).Should().Be(Path.Combine(_fixture.WorkDir, "_uploads"));
        LaunchSource.NameFromArchive(path).Should().Be("evil");
    }

    [Fact]
    public async Task A_name_in_persian_survives()
    {
        await Client().PostAsync($"/api/v1/launch/archive?name={Uri.EscapeDataString("فروشگاه")}", Archive());

        LaunchSource.NameFromArchive(Launches.Started.Single().ArchivePath!).Should().Be("فروشگاه");
    }

    [Fact]
    public async Task A_refused_start_deletes_the_file_it_was_given()
    {
        Launches.StartRefusal = new ServiceRefusal("Suspended.", "معلق.");
        var before = Uploads().ToHashSet();

        var response = await Client().PostAsync("/api/v1/launch/archive?name=refused", Archive());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Launches.Started.Should().ContainSingle();
        Uploads().Except(before).Should().BeEmpty("nothing is ever going to read it");
        (await response.JsonAsync()).GetProperty("errors").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_start_that_blows_up_deletes_the_file_too()
    {
        Launches.StartThrows = true;
        var before = Uploads().ToHashSet();

        var response = await Client().PostAsync("/api/v1/launch/archive?name=blew-up", Archive());

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        Uploads().Except(before).Should().BeEmpty("the launch only owns the archive once it exists");
    }

    [Fact]
    public async Task An_empty_body_says_what_to_send_and_starts_nothing()
    {
        var response = await Client().PostAsync("/api/v1/launch/archive?name=empty", Archive([]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await response.JsonAsync()).GetProperty("errors")[0];
        error.GetProperty("message").GetString().Should().Be("Send the project as a gzipped tar in the request body.");
        error.GetProperty("messageFa").GetString().Should().NotBeNullOrWhiteSpace();
        Launches.Started.Should().BeEmpty();
    }

    [Fact]
    public void The_archive_route_carries_the_same_ceiling_as_a_push()
    {
        // The one thing TestServer cannot enforce: see ApiV1ContractHttpTests.
        Limit(typeof(LaunchApiController), nameof(LaunchApiController.StartFromArchive))
            .Should().Be(Limit(typeof(ApiV1Controller), nameof(ApiV1Controller.DeployArchive)))
            .And.Be(512L * 1024 * 1024, "docs/cli-deploy.md §6 publishes 512 MB compressed");

        static object? Limit(Type controller, string method) => controller.GetMethod(method)!
            .GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute))
            .ConstructorArguments[0].Value;
    }

    // ---- reading it back ----------------------------------------------------------------------------

    [Fact]
    public async Task A_view_says_its_enums_by_name_all_the_way_down()
    {
        var id = Guid.CreateVersion7();
        var view = LaunchScript.View(
            LaunchStatus.SucceededWithProblems, id, appId: Guid.CreateVersion7(), appName: "shop", publicUrl: "https://shop.apps.example.com",
            steps: LaunchScript.Steps(verify: LaunchStepState.Failed),
            checks: [new AppCheck("url", AppCheckStatus.Skipped, "Does the address work?", "آدرس؟", "No address yet.")]);
        Given(view.Status, view: view);

        var response = await Client().GetAsync($"/api/v1/launch/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.JsonAsync();
        body.GetProperty("status").GetString().Should().Be("SucceededWithProblems");
        body.GetProperty("source").GetString().Should().Be("Upload");
        body.GetProperty("publicUrl").GetString().Should().Be("https://shop.apps.example.com");
        body.GetProperty("isTerminal").GetBoolean().Should().BeTrue();
        body.GetProperty("steps")[0].GetProperty("state").GetString().Should().Be("Done");
        body.GetProperty("steps")[4].GetProperty("state").GetString().Should().Be("Failed");
        body.GetProperty("checks")[0].GetProperty("status").GetString().Should().Be("Skipped");
    }

    [Fact]
    public async Task A_proposal_says_its_enums_by_name_and_round_trips_through_confirm()
    {
        var id = GivenAwaiting();
        var client = Client();

        var proposal = await (await client.GetAsync($"/api/v1/launch/{id}/proposal")).JsonAsync();

        proposal.GetProperty("inspection").GetProperty("stack").GetString().Should().Be("Node");
        proposal.GetProperty("inspection").GetProperty("kind").GetString().Should().Be("Web");
        proposal.GetProperty("envFields")[1].GetProperty("disposition").GetString().Should().Be("NeedsAnswer");
        proposal.GetProperty("serviceOptions")[0].GetProperty("type").GetString().Should().Be("PostgreSql");
        var defaults = proposal.GetProperty("defaults");
        defaults.GetProperty("kind").GetString().Should().Be("Web");
        defaults.GetProperty("services")[0].GetProperty("type").GetString().Should().Be("PostgreSql");

        // What it was told goes back exactly as it came, and arrives as the enum it started as.
        var confirmed = await client.PostAsync($"/api/v1/launch/{id}/confirm",
            new StringContent(defaults.GetRawText(), Encoding.UTF8, "application/json"));

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        var choices = Launches.Confirmed.Should().ContainSingle().Subject.Choices;
        choices.Kind.Should().Be(ServiceKind.Web);
        choices.Services.Should().ContainSingle().Which.Type.Should().Be(ManagedServiceType.PostgreSql);
        choices.Services[0].Aliases.Should().Equal(new AttachEnvAlias("DB_HOST", "PGHOST"));
        choices.EnvVars.Should().Contain(v => v.Key == "LOG_LEVEL" && v.Value == "info");
    }

    [Fact]
    public async Task A_proposal_sends_no_value_but_a_kept_examples_suggestion()
    {
        var id = GivenAwaiting();

        var response = await Client().GetAsync($"/api/v1/launch/{id}/proposal");

        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(LaunchScript.ExampleValueOfAQuestion, "the inspection carries the file as it was read, and this is what somebody committed by mistake");
        text.Should().NotContain("example-attach-password");
        text.Should().NotContain("example-alias-host");

        var body = JsonDocument.Parse(text).RootElement;
        var fields = body.GetProperty("envFields").EnumerateArray().ToList();
        fields.Where(f => f.GetProperty("disposition").GetString() != "KeepExample")
            .Should().OnlyContain(f => f.GetProperty("suggestedValue").ValueKind == JsonValueKind.Null);
        fields.Single(f => f.GetProperty("key").GetString() == "LOG_LEVEL").GetProperty("suggestedValue").GetString().Should().Be("info");

        body.GetProperty("inspection").GetProperty("envVars").EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == "LOG_LEVEL")
            .GetProperty("exampleValue").GetString().Should().Be("info", "that one is the proposal's own suggestion");
    }

    [Fact]
    public async Task A_proposal_that_is_past_asking_is_a_409_naming_the_status()
    {
        var id = Given(LaunchStatus.Deploying);

        var response = await Client().GetAsync($"/api/v1/launch/{id}/proposal");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.JsonAsync();
        body.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("status").GetString().Should().Be("Deploying");
    }

    // ---- which folder ---------------------------------------------------------------------------------

    [Fact]
    public async Task Choosing_a_folder_answers_the_proposal_for_it_with_nothing_secret_on_it()
    {
        var id = Guid.CreateVersion7();
        Launches.Add(_fixture.WorkspaceId, LaunchScript.View(LaunchStatus.AwaitingConfirmation, id),
            LaunchScript.Proposal(id, LaunchScript.Inspection(roots: ["services/api", "services/web"])));

        var response = await Client().PostAsync($"/api/v1/launch/{id}/root", HttpConversation.Json(new { candidate = "services/api" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(LaunchScript.ExampleValueOfAQuestion);
        JsonDocument.Parse(text).RootElement.GetProperty("inspection").GetProperty("rootDirectory").GetString().Should().Be("services/api");
        Launches.ChosenRoots.Should().ContainSingle().Which.Candidate.Should().Be("services/api");
    }

    [Fact]
    public async Task An_unknown_folder_is_refused()
    {
        var id = Guid.CreateVersion7();
        Launches.Add(_fixture.WorkspaceId, LaunchScript.View(LaunchStatus.AwaitingConfirmation, id),
            LaunchScript.Proposal(id, LaunchScript.Inspection(roots: ["services/api"])));

        var response = await Client().PostAsync($"/api/v1/launch/{id}/root", HttpConversation.Json(new { candidate = "nope" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.JsonAsync()).GetProperty("errors")[0].GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ---- the quote ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_quote_is_the_services_with_its_figures_already_written()
    {
        var id = GivenAwaiting(LaunchScript.Charged);

        var response = await Client().PostAsync($"/api/v1/launch/{id}/quote",
            ChoicesJson("""{ "name": "shop", "kind": "Worker", "services": [ { "type": "Redis" } ] }"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.JsonAsync();
        body.GetProperty("firstHourMinor").GetInt64().Should().Be(1_200);
        body.GetProperty("balanceMinor").GetInt64().Should().Be(30_000);
        body.GetProperty("currency").GetString().Should().Be("USD");
        body.GetProperty("exempt").GetBoolean().Should().BeFalse();
        body.GetProperty("lines").GetArrayLength().Should().Be(2);
        body.GetProperty("firstHour").GetString().Should().Be(MinorUnits.Format(1_200, "USD"));
        body.GetProperty("perHour").GetString().Should().Be(MinorUnits.Format(1_200, "USD"));
        body.GetProperty("balance").GetString().Should().Be(MinorUnits.Format(30_000, "USD"));

        var asked = Launches.Quoted.Should().ContainSingle().Subject.Choices;
        asked.Kind.Should().Be(ServiceKind.Worker, "names coming in are read as names");
        asked.Services.Should().ContainSingle().Which.Type.Should().Be(ManagedServiceType.Redis);
    }

    [Fact]
    public async Task A_refused_quote_is_still_a_quote()
    {
        var refused = LaunchScript.Charged with
        {
            BalanceMinor = 0,
            Refusal = new ServiceRefusal("Your balance cannot pay for the first hour.", "موجودی کافی نیست."),
            RefusalIsAboutMoney = true
        };
        var id = GivenAwaiting(refused);

        var response = await Client().PostAsync($"/api/v1/launch/{id}/quote", ChoicesJson());

        response.StatusCode.Should().Be(HttpStatusCode.OK, "nothing was refused yet: this says what would be");
        var quote = await response.JsonAsync();
        quote.GetProperty("refusal").GetProperty("message").GetString().Should().Be("Your balance cannot pay for the first hour.");
        quote.GetProperty("refusalIsAboutMoney").GetBoolean().Should().BeTrue("the CLI has only this to say whether billing could help");
    }

    [Fact]
    public async Task What_a_launch_left_and_a_deleted_app_go_over_the_wire()
    {
        var id = Guid.CreateVersion7();
        var view = LaunchScript.View(LaunchStatus.Failed, id, appGone: true,
            created: [new LaunchCreatedThing("database", "shop-postgresql", "/databases/x", Ready: false)]);
        Launches.Add(_fixture.WorkspaceId, view);

        var answer = await (await Client().GetAsync($"/api/v1/launch/{id}")).JsonAsync();

        answer.GetProperty("appGone").GetBoolean().Should().BeTrue();
        var thing = answer.GetProperty("created").EnumerateArray().Should().ContainSingle().Subject;
        thing.GetProperty("kind").GetString().Should().Be("database");
        thing.GetProperty("name").GetString().Should().Be("shop-postgresql");
        thing.GetProperty("linkPath").GetString().Should().Be("/databases/x");
        thing.GetProperty("ready").GetBoolean().Should().BeFalse();
    }

    // ---- confirming -----------------------------------------------------------------------------------

    [Fact]
    public async Task Confirm_validates_first_and_a_refusal_stops_it_with_every_field_named()
    {
        Launches.ValidateRefusals =
        [
            new ServiceRefusal("Choose a size.", "یک اندازه انتخاب کنید.", "InstanceSizeKey"),
            new ServiceRefusal("Plan quota exceeded.", "سقف پلن پر شده است.")
        ];
        var id = GivenAwaiting();

        var response = await Client().PostAsync($"/api/v1/launch/{id}/confirm", ChoicesJson());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await response.JsonAsync()).GetProperty("errors");
        errors.GetArrayLength().Should().Be(2);
        errors[0].GetProperty("field").GetString().Should().Be("InstanceSizeKey");
        errors[1].GetProperty("field").ValueKind.Should().Be(JsonValueKind.Null);

        Launches.Validated.Should().ContainSingle();
        Launches.Confirmed.Should().BeEmpty("nothing is created once a check has said no");
    }

    [Fact]
    public async Task A_refusal_from_confirm_itself_is_the_same_shape()
    {
        Launches.ConfirmRefusals = [new ServiceRefusal("Too late.", "دیر شد.")];
        var id = GivenAwaiting();

        var response = await Client().PostAsync($"/api/v1/launch/{id}/confirm", ChoicesJson());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.JsonAsync()).GetProperty("errors")[0].GetProperty("message").GetString().Should().Be("Too late.");
    }

    [Fact]
    public async Task A_successful_confirm_answers_the_launch_by_name()
    {
        var id = GivenAwaiting();

        var response = await Client().PostAsync($"/api/v1/launch/{id}/confirm", ChoicesJson());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("status").GetString().Should().Be("Creating");
    }

    [Theory]
    [InlineData("""{ "name": "shop", "kind": 1 }""")]
    [InlineData("""{ "name": "shop", "services": [ { "type": 3 } ] }""")]
    public async Task An_enum_sent_as_a_number_is_refused_because_this_api_only_speaks_names(string json)
    {
        var id = GivenAwaiting();

        var response = await Client().PostAsync($"/api/v1/launch/{id}/confirm", ChoicesJson(json));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.JsonAsync()).GetProperty("errors")[0].GetProperty("message").GetString().Should().Contain("by name");
        Launches.Confirmed.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{ "kind": "NotAKind", "name": "shop" }""")]
    [InlineData("""{ "services": [] }""")]
    [InlineData("null")]
    [InlineData("")]
    public async Task A_body_that_is_not_choices_is_a_refusal_rather_than_a_500(string json)
    {
        var id = GivenAwaiting();

        var response = await Client().PostAsync($"/api/v1/launch/{id}/confirm", ChoicesJson(json));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.JsonAsync()).GetProperty("errors").GetArrayLength().Should().Be(1);
        Launches.Validated.Should().BeEmpty();
    }

    [Fact]
    public async Task Lists_and_a_name_sent_as_null_do_not_reach_the_service_as_nulls()
    {
        // They get past the parser: "required" only means present. The real service walks all three.
        var id = GivenAwaiting();

        await Client().PostAsync($"/api/v1/launch/{id}/confirm",
            ChoicesJson("""{ "name": null, "services": null, "envVars": [ null ] }"""));

        var choices = Launches.Validated.Should().ContainSingle().Subject.Choices;
        choices.Name.Should().BeEmpty();
        choices.Services.Should().BeEmpty();
        choices.EnvVars.Should().BeEmpty();
    }

    // ---- retry and abandon ----------------------------------------------------------------------------

    [Fact]
    public async Task Retry_answers_the_launch_or_the_refusal()
    {
        var id = Given(LaunchStatus.Failed);
        var client = Client();

        var retried = await client.PostAsync($"/api/v1/launch/{id}/retry", null);
        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        (await retried.JsonAsync()).GetProperty("status").GetString().Should().Be("Failed");
        Launches.Retried.Should().ContainSingle();

        Launches.RetryRefusals = [new ServiceRefusal("There is nothing to try again here.", "چیزی نیست.")];
        var refused = await client.PostAsync($"/api/v1/launch/{id}/retry", null);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.JsonAsync()).GetProperty("errors")[0].GetProperty("message").GetString().Should().Be("There is nothing to try again here.");
    }

    [Fact]
    public async Task Abandon_answers_or_says_it_is_too_late()
    {
        var id = GivenAwaiting();
        var client = Client();

        var abandoned = await client.PostAsync($"/api/v1/launch/{id}/abandon", null);
        abandoned.StatusCode.Should().Be(HttpStatusCode.OK);
        (await abandoned.JsonAsync()).GetProperty("abandoned").GetBoolean().Should().BeTrue();

        Launches.AbandonAllowed = false;
        var refused = await client.PostAsync($"/api/v1/launch/{id}/abandon", null);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await refused.JsonAsync()).GetProperty("errors")[0];
        error.GetProperty("message").GetString().Should().Contain("too late");
        error.GetProperty("messageFa").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ---- who may ask --------------------------------------------------------------------------------

    public static TheoryData<string, string> EveryRouteNamingALaunch => new()
    {
        { "GET", "" },
        { "GET", "/proposal" },
        { "POST", "/root" },
        { "POST", "/quote" },
        { "POST", "/confirm" },
        { "POST", "/retry" },
        { "POST", "/abandon" }
    };

    private static Task<HttpResponseMessage> Ask(HttpClient client, string verb, string route, Guid id) => verb == "GET"
        ? client.GetAsync($"/api/v1/launch/{id}{route}")
        : client.PostAsync($"/api/v1/launch/{id}{route}", route == "/root" ? HttpConversation.Json(new { candidate = "x" }) : ChoicesJson());

    [Theory]
    [MemberData(nameof(EveryRouteNamingALaunch))]
    public async Task Another_workspaces_launch_is_a_404_on_every_route_and_reaches_nothing(string verb, string route)
    {
        var id = GivenAwaiting(workspace: _fixture.OtherWorkspaceId);

        var response = await Ask(Client(), verb, route, id);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.DocumentedErrorAsync()).Should().Be("Launch not found.");
        Launches.ChosenRoots.Should().BeEmpty();
        Launches.Quoted.Should().BeEmpty();
        Launches.Validated.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
        Launches.Retried.Should().BeEmpty();
        Launches.Abandoned.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryRouteNamingALaunch))]
    public async Task A_launch_that_does_not_exist_answers_the_same(string verb, string route)
    {
        (await Ask(Client(), verb, route, Guid.CreateVersion7())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public static TheoryData<string> ChangingRoutes => new()
    {
        "/root", "/confirm", "/retry", "/abandon"
    };

    [Theory]
    [MemberData(nameof(ChangingRoutes))]
    public async Task A_viewers_token_cannot_change_anything(string route)
    {
        var id = GivenAwaiting();

        var response = await Ask(Client(SystemRole.Viewer), "POST", route, id);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Launches.ChosenRoots.Should().BeEmpty();
        Launches.Validated.Should().BeEmpty();
        Launches.Confirmed.Should().BeEmpty();
        Launches.Retried.Should().BeEmpty();
        Launches.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public async Task A_viewers_token_cannot_start_one_either_way()
    {
        var viewer = Client(SystemRole.Viewer);

        (await viewer.PostAsync("/api/v1/launch", HttpConversation.Json(new { cloneUrl = "https://github.com/you/shop" })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsync("/api/v1/launch/archive?name=x", Archive()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        Launches.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task A_viewers_token_may_read_and_ask_what_it_would_cost()
    {
        var id = GivenAwaiting(LaunchScript.Charged);
        var viewer = Client(SystemRole.Viewer);

        (await viewer.GetAsync($"/api/v1/launch/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/v1/launch/{id}/proposal")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsync($"/api/v1/launch/{id}/quote", ChoicesJson())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task No_token_is_a_401_and_a_browser_session_is_not_a_token()
    {
        var id = GivenAwaiting();
        var anonymous = _fixture.Panel.CreateClient();
        anonymous.BaseAddress = new Uri("https://localhost");
        var browser = await _fixture.SignedInAs(_fixture.WorkspaceId, $"launch-api-browser-{Interlocked.Increment(ref _nextUser)}@example.com");

        (await anonymous.GetAsync($"/api/v1/launch/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await browser.GetAsync($"/api/v1/launch/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await browser.PostAsync($"/api/v1/launch/{id}/confirm", ChoicesJson())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Launches.Confirmed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(LaunchApiController.Start))]
    [InlineData(nameof(LaunchApiController.StartFromArchive))]
    [InlineData(nameof(LaunchApiController.ChooseRoot))]
    [InlineData(nameof(LaunchApiController.Confirm))]
    [InlineData(nameof(LaunchApiController.Retry))]
    [InlineData(nameof(LaunchApiController.Abandon))]
    public void Every_endpoint_that_changes_something_declares_the_capability_and_the_token_scheme(string method)
    {
        var authorize = typeof(LaunchApiController).GetMethod(method)!.GetCustomAttributes<AuthorizeAttribute>().ToList();

        authorize.Should().ContainSingle();
        authorize[0].Policy.Should().Be(Capabilities.AppsCreate);
        authorize[0].AuthenticationSchemes.Should().Be(TokenAuthenticationHandler.SchemeName);
    }
}
