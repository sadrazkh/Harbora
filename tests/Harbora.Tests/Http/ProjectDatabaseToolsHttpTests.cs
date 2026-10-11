using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The project and database tools inside the booted panel: every one of them resolves from the panel's own
/// container (a constructor dependency nobody registered would otherwise surface only when a person first
/// asks), and a plan of two of them runs through the real request — sign-in, the entitlement filter,
/// antiforgery, the capability policies, the real quota and project services — with only the model scripted.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ProjectDatabaseToolsHttpTests(AssistantPanelFixture fixture) : IClassFixture<AssistantPanelFixture>
{
    private static readonly string[] ToolNames =
    [
        "create_project", "add_environment", "create_database", "attach_database", "detach_database",
        "rotate_database_password", "stop_database", "start_database", "delete_database", "delete_project"
    ];

    [Fact]
    public void Every_project_and_database_tool_is_built_by_the_panels_own_container()
    {
        using var scope = fixture.Panel.Services.CreateScope();

        var tools = scope.ServiceProvider.GetServices<IAgentTool>().ToList();

        tools.Select(t => t.Name).Should().Contain(ToolNames);
        tools.Where(t => ToolNames.Contains(t.Name)).Should().HaveCount(ToolNames.Length, "each is registered once");
        scope.ServiceProvider.GetRequiredService<AgentToolRegistry>().All.Select(t => t.Name).Should().Contain(ToolNames);
    }

    [Fact]
    public async Task A_new_project_and_an_environment_in_it_are_planned_approved_and_created_over_http()
    {
        fixture.GivenUser(fixture.WorkspaceId, "tools-owner@example.com");
        var (client, csrf) = await fixture.SignedInAs("203.0.113.200", "tools-owner@example.com");
        fixture.Chat.Clear();
        fixture.Chat.ThenCalls(
                fixture.Chat.Call("create_project", """{"name":"Web shop","slug":"web-shop"}"""),
                fixture.Chat.Call("add_environment", """{"project":"web-shop","name":"staging"}"""))
            .ThenText("Approve the plan to create web-shop with a staging environment.");

        var stream = await Post(client, csrf, "/assistant/conversations/new/messages",
            new { text = "create a project web-shop with a staging environment" });
        stream.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = Frames(await stream.Content.ReadAsStringAsync()).Single(f => f.Event == "plan").Data;
        var steps = plan.GetProperty("steps");
        steps.EnumerateArray().Select(s => s.GetProperty("tool").GetString()).Should().Equal("create_project", "add_environment");
        steps[1].GetProperty("detailEn").GetString().Should().Contain("created by an earlier step");
        fixture.Read(db => db.Projects.IgnoreQueryFilters().Any(p => p.Slug == "web-shop")).Should().BeFalse();

        var approve = await Post(client, csrf, $"/assistant/plans/{plan.GetProperty("id").GetGuid()}/approve", new { });

        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await approve.JsonAsync();
        result.GetProperty("status").GetString().Should().Be("executed");
        result.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("state").GetString()).Should().Equal("done", "done");
        var environments = fixture.Read(db => db.Environments.IgnoreQueryFilters()
            .Where(e => e.WorkspaceId == fixture.WorkspaceId && e.Project!.Slug == "web-shop")
            .OrderBy(e => e.Slug).Select(e => e.Slug).ToList());
        environments.Should().Equal("production", "staging");
        fixture.Read(db => db.AuditLogs.IgnoreQueryFilters().Count(a => a.WorkspaceId == fixture.WorkspaceId
                && (a.Action == "project.created" || a.Action == "environment.created")))
            .Should().BeGreaterThanOrEqualTo(2);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string csrf, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return await client.SendAsync(request);
    }

    private static List<(string Event, JsonElement Data)> Frames(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n'))
            .Select(lines => (
                lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..],
                JsonDocument.Parse(lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..]).RootElement.Clone()))
            .ToList();
}
