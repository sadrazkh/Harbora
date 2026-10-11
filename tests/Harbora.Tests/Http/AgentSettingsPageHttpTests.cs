using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Agent;
using Harbora.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The agent half of the settings page, through the real pipeline: saving the model, fallbacks and
/// daily limit, refusing a bad limit where the owner can see it, keeping non-owners out, and the
/// "test the agent model" button end to end — the real controller, the real bench and the real chat
/// client, against a scripted provider at the HTTP layer (no test here can reach a real one).
///
/// <para>
/// The panel renders Persian by default in tests and Razor's encoder writes non-Latin text as
/// entities, so these assert on the <c>data-</c> attributes and on stored state, never on a sentence.
/// Every test puts the platform-wide settings back, because the whole collection shares one panel.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public sealed class AgentSettingsPageHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private void ForgetAssistantSettings() =>
        Panel.Seed(db => db.Settings.RemoveRange(db.Settings.Where(s => s.Key.StartsWith("assistant."))));

    private async Task<HttpClient> SignedInOwner(string ip, string name)
    {
        Panel.GivenUser(fixture.WorkspaceId, $"{name}@example.com", SystemRole.Owner);
        return await Panel.SignedInAs(ip, $"{name}@example.com");
    }

    private static string FormValue(string html, string name) =>
        System.Text.RegularExpressions.Regex.Match(html, $"name=\"{name}\" value=\"(?<v>[^\"]*)\"").Groups["v"].Value;

    // ---- saving ----------------------------------------------------------------------------

    [Fact]
    public async Task Saving_stores_the_agent_settings_under_the_contract_keys_and_the_page_shows_them_back()
    {
        var owner = await SignedInOwner("192.0.2.170", "agent-settings-save");
        try
        {
            var token = await owner.AntiforgeryTokenFrom("/settings");
            var response = await owner.PostFormAsync("/settings/assistant", token,
                ("enabled", "true"), ("provider", "openai"), ("model", "assistant/model:free"),
                ("apiKey", "sk-http-save"), ("baseUrl", "https://openrouter.ai/api"),
                ("agentModel", "vendor/agent:free"), ("agentFallbackModels", " a/b:free , c/d:free,, a/b:free"),
                ("agentDailyRequestLimit", "۲۰۰"));

            response.RedirectPath().Should().BeEquivalentTo("/settings", "the action redirects to the Index route, whatever its casing");

            var stored = Panel.Read(db => db.Settings.ToDictionary(s => s.Key, s => s.Value));
            stored[AgentSettingKeys.Model].Should().Be("vendor/agent:free");
            stored[AgentSettingKeys.FallbackModels].Should().Be("a/b:free,c/d:free");
            stored[AgentSettingKeys.DailyRequestLimit].Should().Be("200", "Persian digits are read as the digits they are");
            stored["assistant.model"].Should().Be("assistant/model:free", "the explainer's own fields are saved by the same form");

            var html = await (await owner.GetAsync("/settings")).Content.ReadAsStringAsync();
            html.Should().Contain("data-agent-settings");
            FormValue(html, "agentModel").Should().Be("vendor/agent:free");
            FormValue(html, "agentFallbackModels").Should().Be("a/b:free,c/d:free");
            FormValue(html, "agentDailyRequestLimit").Should().Be("200");
        }
        finally
        {
            ForgetAssistantSettings();
        }
    }

    [Fact]
    public async Task Blank_agent_fields_store_blanks_so_the_default_limit_applies_and_the_box_stays_empty()
    {
        var owner = await SignedInOwner("192.0.2.171", "agent-settings-blank");
        try
        {
            var token = await owner.AntiforgeryTokenFrom("/settings");
            await owner.PostFormAsync("/settings/assistant", token,
                ("enabled", "true"), ("provider", "openai"), ("model", "m/x"),
                ("agentModel", ""), ("agentFallbackModels", ""), ("agentDailyRequestLimit", ""));

            var read = Panel.Read(db => new AgentSettingsService(db).GetAsync(CancellationToken.None).GetAwaiter().GetResult());
            read.DailyRequestLimitText.Should().BeEmpty();
            read.DailyRequestLimit.Should().Be(AgentSettingKeys.DefaultDailyRequestLimit);

            var html = await (await owner.GetAsync("/settings")).Content.ReadAsStringAsync();
            FormValue(html, "agentDailyRequestLimit").Should().BeEmpty();
        }
        finally
        {
            ForgetAssistantSettings();
        }
    }

    [Theory]
    [InlineData("-3")]
    [InlineData("many")]
    [InlineData("1.5")]
    public async Task A_limit_that_is_not_a_non_negative_whole_number_is_refused_visibly_and_nothing_is_saved(string limit)
    {
        Panel.Seed(db => db.Settings.Add(new Setting { Key = "assistant.model", Value = "before/model" }));
        var owner = await SignedInOwner("192.0.2.172", "agent-settings-refuse-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var token = await owner.AntiforgeryTokenFrom("/settings");
            var response = await owner.PostFormAsync("/settings/assistant", token,
                ("enabled", "true"), ("provider", "openai"), ("model", "after/model"),
                ("agentDailyRequestLimit", limit));

            response.RedirectPath().Should().BeEquivalentTo("/settings", "the action redirects to the Index route, whatever its casing");

            var html = await (await owner.GetAsync("/settings")).Content.ReadAsStringAsync();
            html.Should().Contain("data-settings-error",
                "TempData[\"Error\"] was never drawn on this page, so a refusal here would have vanished");

            var stored = Panel.Read(db => db.Settings.Where(s => s.Key.StartsWith("assistant.")).ToDictionary(s => s.Key, s => s.Value));
            stored["assistant.model"].Should().Be("before/model", "a refused form must not half-save the fields beside the bad one");
            stored.Should().NotContainKey(AgentSettingKeys.DailyRequestLimit);
        }
        finally
        {
            ForgetAssistantSettings();
        }
    }

    [Fact]
    public async Task Zero_is_a_valid_limit_and_is_kept_as_zero()
    {
        var owner = await SignedInOwner("192.0.2.173", "agent-settings-zero");
        try
        {
            var token = await owner.AntiforgeryTokenFrom("/settings");
            await owner.PostFormAsync("/settings/assistant", token,
                ("enabled", "true"), ("provider", "openai"), ("model", "m/x"), ("agentDailyRequestLimit", "0"));

            Panel.Read(db => db.Settings.Single(s => s.Key == AgentSettingKeys.DailyRequestLimit).Value).Should().Be("0");
        }
        finally
        {
            ForgetAssistantSettings();
        }
    }

    // ---- who may ---------------------------------------------------------------------------

    [Theory]
    [InlineData("/settings/assistant")]
    [InlineData("/settings/assistant/agent-test")]
    public async Task A_member_is_sent_to_access_denied_and_nothing_runs_or_changes(string path)
    {
        var email = $"agent-settings-member-{(path.EndsWith("agent-test") ? "test" : "save")}@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var member = await Panel.SignedInAs("192.0.2.174", email);
        try
        {
            var token = await member.AntiforgeryTokenFrom("/settings");
            var response = await member.PostFormAsync(path, token,
                ("enabled", "true"), ("provider", "openai"), ("agentModel", "x/y"));

            response.StatusCode.Should().Be(HttpStatusCode.Found);
            response.RedirectPath().Should().Be("/account/denied");
            Panel.Read(db => db.Settings.Any(s => s.Key.StartsWith("assistant."))).Should().BeFalse();
            Panel.Read(db => db.AuditLogs.Any(a => a.Action == "assistant.agent_tested")).Should().BeFalse();
        }
        finally
        {
            ForgetAssistantSettings();
        }
    }

    [Fact]
    public async Task The_settings_page_does_not_render_the_agent_block_for_a_member()
    {
        Panel.GivenUser(fixture.WorkspaceId, "agent-settings-member-page@example.com", SystemRole.Member);
        var member = await Panel.SignedInAs("192.0.2.175", "agent-settings-member-page@example.com");

        var html = await (await member.GetAsync("/settings")).Content.ReadAsStringAsync();

        html.Should().NotContain("data-agent-settings", "which model this installation is wired to is not a member's business");
        html.Should().NotContain("data-agent-bench-run");
    }

    // ---- "test the agent model" ------------------------------------------------------------

    [Fact]
    public async Task Testing_an_unconfigured_agent_says_why_shows_no_table_and_audits_nothing()
    {
        ForgetAssistantSettings();
        var owner = await SignedInOwner("192.0.2.176", "agent-bench-unconfigured");

        var token = await owner.AntiforgeryTokenFrom("/settings");
        var response = await owner.PostFormAsync("/settings/assistant/agent-test", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("data-agent-bench-state=\"not-run\"");
        html.Should().NotContain("data-agent-bench-row");
        Panel.Read(db => db.AuditLogs.Any(a => a.Action == "assistant.agent_tested")).Should().BeFalse(
            "nothing was asked of any provider, so nothing was tested");
    }

    [Fact]
    public async Task Testing_the_agent_model_runs_the_real_bench_through_the_real_client_and_shows_a_table_and_audits_it()
    {
        // The model answers every prompt correctly except "list my apps", which it answers in words.
        var handler = new RecordingChatHandler(request =>
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!;
            var prompt = body["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
            var testCase = AgentModelBench.Cases.Single(c => c.Prompt == prompt);

            return RecordingChatHandler.Reply(testCase.Prompt == "list my apps"
                ? RecordingChatHandler.TextReply("You have two apps.", "answering/model:free")
                : RecordingChatHandler.ToolCallReply(testCase.Tool, JsonSerializer.Serialize(testCase.Arguments),
                    model: "answering/model:free"));
        });

        await using var root = new HarboraWebFactory();
        await using var scripted = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(OpenAiCompatibleAgentChatClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler)));

        var client = ConfigureAndSignIn(scripted, "192.0.2.177");
        await Login(client, "agent-bench-http@example.com");

        var token = await client.AntiforgeryTokenFrom("/settings");
        var response = await client.PostFormAsync("/settings/assistant/agent-test", token);

        // What the provider was sent: six calls, the owner's key, OpenRouter's URL and headers, and
        // the nine fake tools as JSON objects — never the panel's real ones.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Should().HaveCount(6);
        foreach (var seen in handler.Requests)
        {
            seen.Url.ToString().Should().Be("https://openrouter.ai/api/v1/chat/completions");
            seen.Authorization!.Parameter.Should().Be("sk-http-bench");
            seen.Headers.Should().ContainKey("HTTP-Referer");
            seen.Json["model"]!.GetValue<string>().Should().Be("vendor/agent:free");
            seen.Json.ContainsKey("models").Should().BeFalse("no fallbacks are configured");
            seen.Json["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>())
                .Should().BeEquivalentTo(AgentModelBench.ToolSpecs.Select(t => t.Name));
        }

        // What the owner sees.
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("data-agent-bench-state=\"partial\"");
        html.Should().Contain("data-agent-bench-verdict");
        System.Text.RegularExpressions.Regex.Matches(html, "data-agent-bench-row").Should().HaveCount(6);
        System.Text.RegularExpressions.Regex.Matches(html, "data-outcome=\"passed\"").Should().HaveCount(5);
        System.Text.RegularExpressions.Regex.Matches(html, "data-outcome=\"failed\"").Should().HaveCount(1);
        html.Should().Contain("answering/model:free", "the model that answered is in the table");
        html.Should().Contain("create_database(engine=&quot;postgres&quot;, name=&quot;shop-db&quot;)");

        // And that it was recorded.
        using var scope = scripted.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<HarboraDbContext>()
            .AuditLogs.Single(a => a.Action == "assistant.agent_tested");
        audit.MetadataJson.Should().Contain("\"passed\":5").And.Contain("\"failed\":1")
            .And.Contain("\"model\":\"vendor/agent:free\"");
    }

    [Fact]
    public async Task A_provider_that_rate_limits_shows_no_answer_rows_not_failures_in_the_page()
    {
        var handler = RecordingChatHandler.Always(
            """{"error":{"message":"Provider returned error","code":429}}""", HttpStatusCode.TooManyRequests);

        await using var root = new HarboraWebFactory();
        await using var scripted = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(OpenAiCompatibleAgentChatClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler)));

        var client = ConfigureAndSignIn(scripted, "192.0.2.178");
        await Login(client, "agent-bench-http@example.com");

        var token = await client.AntiforgeryTokenFrom("/settings");
        var html = await (await client.PostFormAsync("/settings/assistant/agent-test", token)).Content.ReadAsStringAsync();

        html.Should().Contain("data-agent-bench-state=\"failed\"");
        System.Text.RegularExpressions.Regex.Matches(html, "data-outcome=\"noanswer\"").Should().HaveCount(6);
        html.Should().NotContain("data-outcome=\"failed\"");
        html.Should().NotContain("data-outcome=\"passed\"");
    }

    // ---- a second host with its own scripted provider --------------------------------------

    private const string BenchEmail = "agent-bench-http@example.com";

    /// <summary>
    /// Seeds the derived host the way <see cref="HarboraHttpFixture"/> seeds the shared one — a
    /// finished first run, a workspace, an owner — plus an agent configured for OpenRouter, then
    /// returns a client that has not yet signed in.
    /// </summary>
    private static HttpClient ConfigureAndSignIn(WebApplicationFactory<Program> host, string ip)
    {
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

            var workspace = new Workspace { Id = Guid.CreateVersion7(), Name = "Harbora", Slug = "harbora-bench", IsDefault = true };
            db.Workspaces.Add(workspace);
            db.Settings.Add(new Setting { Key = SettingKeys.SetupCompleted, Value = "true" });

            db.Settings.Add(new Setting { Key = "assistant.enabled", Value = "true" });
            db.Settings.Add(new Setting { Key = "assistant.provider", Value = "openai" });
            db.Settings.Add(new Setting { Key = "assistant.base_url", Value = "https://openrouter.ai/api" });
            db.Settings.Add(new Setting { Key = "assistant.api_key", Value = protector.Protect("sk-http-bench"), IsSecret = true });
            db.Settings.Add(new Setting { Key = AgentSettingKeys.Model, Value = "vendor/agent:free" });

            var user = new User
            {
                Email = BenchEmail, DisplayName = BenchEmail, PasswordHash = hasher.Hash(HarboraWebFactory.TestPassword),
                Role = SystemRole.Owner, IsActive = true, EmailVerifiedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Admin });
            db.SaveChanges();
        }

        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });
        client.DefaultRequestHeaders.Add(HarboraWebFactory.RemoteIpHeader, ip);
        return client;
    }

    private static async Task Login(HttpClient client, string email)
    {
        var token = await client.AntiforgeryTokenFrom("/account/login");
        var response = await client.PostFormAsync("/account/login", token,
            ("Email", email), ("Password", HarboraWebFactory.TestPassword));
        response.StatusCode.Should().Be(HttpStatusCode.Found);
    }
}
