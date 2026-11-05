using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Services;
using Harbora.Shared.Inspection;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Harbora.Tests.Launch.LaunchFixtures;

namespace Harbora.Tests.Launch;

/// <summary>
/// A repository with several apps in it. The tree is deleted the moment it has been read and the person
/// chooses afterwards, so every folder has to be read in that one moment: a folder only named later
/// would launch with no database, no variables and the wrong port, and nothing would say so.
/// </summary>
public sealed class LaunchRootChoiceTests
{
    private static readonly SourceInspection Root = Bare() with
    {
        Stack = DetectedStack.Unknown,
        Port = null,
        RootCandidates = ["api", "web"],
        Notes = [new InspectionNote("several-apps-found", "Several apps.", "چند اپ.")]
    };

    /// <summary>The only one of the two that needs a database.</summary>
    private static readonly SourceInspection Api = Bare(port: 8080) with
    {
        Services = [Certain(DetectedServiceKind.Postgres)],
        EnvVars =
        [
            Var("DB_HOST", EnvVarDisposition.AliasOfAttach, null, DetectedServiceKind.Postgres, EnvAliasRole.Host),
            Var("LOG_LEVEL", EnvVarDisposition.KeepExample, "info")
        ]
    };

    private static readonly SourceInspection Web = Bare(port: 3000) with { Stack = DetectedStack.Static };

    private static async Task<Guid> TwoAppsAsync(LaunchHarness h)
    {
        h.Git.Files.AddRange(["api/package.json", "web/index.html"]);
        h.Reader.Folders["api"] = Api;
        h.Reader.Folders["web"] = Web;
        return await h.ReadyGitAsync(Root);
    }

    private static string TreeOf(LaunchHarness h, Guid id) => Path.Combine(h.WorkDir, "_launch", id.ToString());

    [Fact]
    public async Task Each_folder_is_read_while_the_tree_exists_and_the_choice_decides_what_is_proposed()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);

        h.Reader.FoundOnDisk.Should().BeEquivalentTo(["api", "web"], "this was the last moment either could be read");
        Directory.Exists(TreeOf(h, id)).Should().BeFalse("the tree is already gone by the time anybody chooses");

        var api = await h.Request().ChooseRootAsync(id, "api", default);

        api.Ok.Should().BeTrue();
        api.Value!.Defaults.RootDirectory.Should().Be("api");
        api.Value.Defaults.ContainerPort.Should().Be(8080);
        api.Value.Defaults.Services.Should().ContainSingle().Which.Type.Should().Be(ManagedServiceType.PostgreSql);
        api.Value.Defaults.Services[0].Aliases.Should().Equal(new AttachEnvAlias("DB_HOST", "PGHOST"));
        api.Value.Inspection.RootDirectory.Should().Be("api");
        api.Value.Inspection.RootCandidates.Should().Equal("api", "web");

        var web = await h.Request().ChooseRootAsync(id, "web", default);

        web.Ok.Should().BeTrue("the person can change their mind");
        web.Value!.Defaults.RootDirectory.Should().Be("web");
        web.Value.Defaults.ContainerPort.Should().Be(3000);
        web.Value.Defaults.Services.Should().BeEmpty();
        web.Value.EnvFields.Should().BeEmpty();
        web.Value.Inspection.RootCandidates.Should().Equal("api", "web");

        h.Git.Checkouts.Should().ContainSingle("choosing reads what was stored, and clones nothing");
    }

    [Fact]
    public async Task With_nothing_chosen_the_proposal_is_only_the_question()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);

        var proposal = await h.Request().ProposeAsync(id, default);

        proposal!.Inspection.RootDirectory.Should().BeNull();
        proposal.Inspection.RootCandidates.Should().Equal("api", "web");
        proposal.Inspection.Notes.Should().Contain(n => n.Code == "several-apps-found");
        proposal.Defaults.RootDirectory.Should().BeNull();
        proposal.Defaults.Services.Should().BeEmpty("nothing it could propose yet is about the app the person means");
    }

    [Fact]
    public async Task A_choice_is_remembered_by_the_next_read_of_the_proposal()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);

        await h.Request().ChooseRootAsync(id, "api", default);

        var again = await h.Request().ProposeAsync(id, default);
        again!.Defaults.RootDirectory.Should().Be("api");
        again.Defaults.Services.Should().ContainSingle();
    }

    [Fact]
    public async Task Confirming_with_no_folder_chosen_is_refused_in_both_languages_and_creates_nothing()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);
        var choices = await h.DefaultsAsync(id);

        var outcome = await h.Request().ConfirmAsync(id, choices, null, default);

        outcome.Ok.Should().BeFalse();
        var refusal = outcome.Refusals.Single(r => r.Field == nameof(LaunchChoices.RootDirectory));
        refusal.Message.Should().Contain("api").And.Contain("web");
        refusal.MessageFa.Should().Contain("api").And.Contain("web");
        h.Row(id).Status.Should().Be(LaunchStatus.AwaitingConfirmation);
        h.Store().Apps.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_only_named_in_the_form_is_not_a_choice_because_nothing_was_worked_out_for_it()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);
        var choices = await h.DefaultsAsync(id);

        (await h.Request().ValidateConfirmAsync(id, choices with { RootDirectory = "api" }, default))
            .Should().Contain(r => r.Field == nameof(LaunchChoices.RootDirectory));
    }

    [Fact]
    public async Task Another_folder_typed_over_the_chosen_one_is_refused_rather_than_launched_with_the_wrong_answers()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);
        var choices = (await h.Request().ChooseRootAsync(id, "api", default)).Value!.Defaults;

        var refusals = await h.Request().ValidateConfirmAsync(id, choices with { RootDirectory = "web" }, default);

        refusals.Should().ContainSingle(r => r.Field == nameof(LaunchChoices.RootDirectory))
            .Which.MessageFa.Should().Contain("web");
    }

    [Fact]
    public async Task The_launch_is_built_from_the_chosen_folder_all_the_way_down()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);
        var choices = (await h.Request().ChooseRootAsync(id, "api", default)).Value!.Defaults;

        var view = (await h.Request().ConfirmAsync(id, choices, null, default)).Value!;

        view.Status.Should().Be(LaunchStatus.WaitingForServices);
        var app = h.Store().Apps.IgnoreQueryFilters().Single();
        app.BuildContextPath.Should().Be("api");
        app.ContainerPort.Should().Be(8080);
        h.Store().ManagedServices.IgnoreQueryFilters().Single().Type.Should().Be(ManagedServiceType.PostgreSql);
        ManagedServiceAttachEnv.ReadAliases(h.Store().AppManagedServices.IgnoreQueryFilters().Single().EnvAliasesJson)
            .Should().Equal(new AttachEnvAlias("DB_HOST", "PGHOST"));
        h.Store().EnvironmentVariables.IgnoreQueryFilters().Single().Key.Should().Be("LOG_LEVEL");
    }

    [Fact]
    public async Task A_form_that_does_not_post_the_folder_still_means_the_one_that_was_chosen()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);
        var choices = (await h.Request().ChooseRootAsync(id, "web", default)).Value!.Defaults;

        var outcome = await h.Request().ConfirmAsync(id, choices with { RootDirectory = null }, null, default);

        outcome.Ok.Should().BeTrue();
        h.Store().Apps.IgnoreQueryFilters().Single().BuildContextPath.Should().Be("web");
    }

    [Fact]
    public async Task A_retry_stays_on_the_chosen_folder()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);
        var choices = (await h.Request().ChooseRootAsync(id, "api", default)).Value!.Defaults;
        h.Creates.RefuseApp = new Harbora.Application.Abstractions.ServiceRefusal("No address left.", "نشانی نمانده است.");

        (await h.Request().ConfirmAsync(id, choices, null, default)).Value!.Status.Should().Be(LaunchStatus.Failed);
        (await h.Request().RetryAsync(id, null, default)).Value!.Status.Should().Be(LaunchStatus.WaitingForServices);

        h.Store().Apps.IgnoreQueryFilters().Single().BuildContextPath.Should().Be("api");
        h.Store().EnvironmentVariables.IgnoreQueryFilters().Single().Value.Should().Be("info", "read again from that folder's own example file");
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData("../api")]
    public async Task A_folder_that_was_not_found_is_refused_in_both_languages_and_changes_nothing(string candidate)
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);
        await h.Request().ChooseRootAsync(id, "api", default);

        var outcome = await h.Request().ChooseRootAsync(id, candidate, default);

        outcome.Ok.Should().BeFalse();
        outcome.Refusals[0].Message.Should().Contain("api").And.Contain("web");
        outcome.Refusals[0].MessageFa.Should().Contain("api").And.Contain("web");
        (await h.DefaultsAsync(id)).RootDirectory.Should().Be("api", "the earlier choice stands");
    }

    [Fact]
    public async Task A_source_with_one_app_has_nothing_to_choose()
    {
        using var h = new LaunchHarness();
        var id = await h.ReadyGitAsync(Bare());

        (await h.Request().ChooseRootAsync(id, "api", default)).Ok.Should().BeFalse();
        (await h.Request().ValidateConfirmAsync(id, await h.DefaultsAsync(id), default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Choosing_is_over_once_it_is_confirmed_and_is_nobody_elses_to_do()
    {
        using var h = new LaunchHarness();
        var id = await TwoAppsAsync(h);

        (await h.OtherWorkspace().ChooseRootAsync(id, "api", default)).Ok.Should().BeFalse();
        (await h.DefaultsAsync(id)).RootDirectory.Should().BeNull();

        var choices = (await h.Request().ChooseRootAsync(id, "web", default)).Value!.Defaults;
        await h.Request().ConfirmAsync(id, choices, null, default);

        var late = await h.Request().ChooseRootAsync(id, "api", default);
        late.Ok.Should().BeFalse();
        late.Refusals[0].MessageFa.Should().NotBeNullOrWhiteSpace();
        LaunchJson.ReadStored(h.Row(id).InspectionJson).Chosen.Should().Be("web");
    }

    [Fact]
    public async Task A_candidates_own_root_is_relative_to_it_and_is_prefixed()
    {
        using var h = new LaunchHarness();
        h.Reader.Folders["api"] = Api with { RootDirectory = "server" };
        h.Reader.Folders["web"] = Web;
        var id = await h.ReadyGitAsync(Root);

        var proposal = (await h.Request().ChooseRootAsync(id, "api", default)).Value!;

        proposal.Defaults.RootDirectory.Should().Be("api/server");
        proposal.Inspection.RootCandidates.Should().Equal("api", "web");
    }

    [Fact]
    public async Task A_folder_holding_several_apps_of_its_own_is_replaced_by_them_so_a_choice_never_leads_to_another_list()
    {
        using var h = new LaunchHarness();
        h.Reader.Folders["services/billing"] = Api;
        h.Reader.Folders["services/search"] = Web;
        h.Reader.Folders["services"] = Bare() with { Stack = DetectedStack.Unknown, RootCandidates = ["billing", "search"] };
        h.Reader.Folders["web"] = Web;
        var id = await h.ReadyGitAsync(Root with { RootCandidates = ["services", "web"] });

        (await h.Request().ProposeAsync(id, default))!.Inspection.RootCandidates
            .Should().Equal("services/billing", "services/search", "web");

        (await h.Request().ChooseRootAsync(id, "services", default)).Ok.Should().BeFalse();

        var chosen = (await h.Request().ChooseRootAsync(id, "services/billing", default)).Value!;
        chosen.Defaults.RootDirectory.Should().Be("services/billing");
        chosen.Defaults.Services.Should().ContainSingle();
        chosen.Inspection.RootCandidates.Should().Equal("services/billing", "services/search", "web");
    }

    [Fact]
    public async Task A_folder_chosen_from_a_github_zips_wrapper_keeps_the_whole_path()
    {
        using var h = new LaunchHarness();
        h.Reader.Folders["shop-main/api"] = Api;
        h.Reader.Folders["shop-main/web"] = Web;
        var id = await h.ReadyGitAsync(Root with { RootCandidates = ["shop-main/api", "shop-main/web"] });

        var chosen = (await h.Request().ChooseRootAsync(id, "shop-main/api/", default)).Value!;

        chosen.Defaults.RootDirectory.Should().Be("shop-main/api", "a trailing slash is the same folder");
        chosen.Defaults.Services.Should().ContainSingle();
    }

    [Fact]
    public async Task App_key_is_laravels_format_when_the_artisan_file_is_in_the_chosen_folder()
    {
        using var h = new LaunchHarness();
        h.Git.Files.AddRange(["api/artisan", "web/index.html"]);
        var withKey = Bare() with { EnvVars = [Var("APP_KEY", EnvVarDisposition.GeneratedSecret)] };
        h.Reader.Folders["api"] = withKey;
        h.Reader.Folders["web"] = withKey;
        var id = await h.ReadyGitAsync(Root);

        var choices = (await h.Request().ChooseRootAsync(id, "api", default)).Value!.Defaults;
        (await h.Request().ConfirmAsync(id, choices, null, default)).Ok.Should().BeTrue();

        new PassthroughProtector().Unprotect(h.Store().EnvironmentVariables.IgnoreQueryFilters().Single().Value)
            .Should().StartWith("base64:");
    }

    [Fact]
    public async Task A_branch_that_was_not_there_is_said_on_the_chosen_folder_too()
    {
        using var h = new LaunchHarness();
        h.Reader.Inspection = Root;
        h.Reader.Folders["api"] = Api;
        h.Reader.Folders["web"] = Web;
        var id = (await h.Request().StartAsync(h.GitRequest(gitRef: "develop"), default)).Value;
        await h.Worker().InspectAsync(id, default);

        var chosen = (await h.Request().ChooseRootAsync(id, "api", default)).Value!;

        chosen.Inspection.Notes.Should().Contain(n => n.Code == "ref-not-found");
    }

    [Fact]
    public void A_row_that_cannot_be_read_is_nothing_rather_than_an_exception()
    {
        foreach (var json in new[] { null, "", "not json", "{}", """{"stack":"Node"}""" })
        {
            var stored = LaunchJson.ReadStored(json);

            stored.Root.Should().Be(SourceInspection.Nothing);
            stored.Candidates.Should().BeEmpty();
            stored.Current.Should().Be(SourceInspection.Nothing);
        }
    }

    [Fact]
    public void What_is_stored_reads_back_with_every_folder_and_the_choice()
    {
        var written = LaunchJson.Write(new StoredInspection(
            Root, new Dictionary<string, SourceInspection> { ["API-Server"] = Api with { RootDirectory = "API-Server" } }, "API-Server"));

        var read = LaunchJson.ReadStored(written);

        read.Chosen.Should().Be("API-Server");
        read.Candidates.Keys.Should().Equal("API-Server");
        read.Current.RootDirectory.Should().Be("API-Server");
        read.Current.Services.Should().ContainSingle().Which.Kind.Should().Be(DetectedServiceKind.Postgres);
        read.Current.RootCandidates.Should().Equal(Root.RootCandidates);
        written.Should().Contain("\"Postgres\"", "enums are stored as names");
    }
}
