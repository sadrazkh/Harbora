using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Clean up disk removes the build directories nothing owns, through the booted panel, and says so
/// in its own clause of the result sentence rather than inside the disks' measured figure.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class MonitoringBuildDirectoryHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task Clean_up_disk_removes_a_leftover_build_directory_and_says_how_many()
    {
        var workDir = Panel.Services.GetRequiredService<IOptions<HarboraRuntimeOptions>>().Value.WorkDir;
        var leftover = Path.Combine(workDir, "left-behind-" + Guid.NewGuid().ToString("N")[..8], "3");
        Directory.CreateDirectory(leftover);
        await File.WriteAllTextAsync(Path.Combine(leftover, "Dockerfile"), "FROM scratch");

        Panel.GivenUser(fixture.WorkspaceId, "build-dirs-owner@example.com", SystemRole.Owner);
        var client = Panel.ClientFrom("100.64.78.1");
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        var login = await client.AntiforgeryTokenFrom("/account/login");
        await client.PostFormAsync("/account/login", login,
            ("Email", "build-dirs-owner@example.com"), ("Password", HarboraWebFactory.TestPassword));

        var token = await client.AntiforgeryTokenFrom("/monitoring");
        var response = await client.PostFormAsync("/monitoring/cleanup", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Directory.Exists(leftover).Should().BeFalse("no deployment owns it, so nothing will read it again");

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync());
        html.Should().MatchRegex(@"Removed [1-9][0-9]* leftover build director\(ies\)");
    }
}
