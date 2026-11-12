using System.Net;
using System.Text;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Git;
using Xunit;

namespace Harbora.Tests.Git;

/// <summary>
/// What the client sends and how it reads the answer, against each provider's documented API: GitHub
/// v3, GitLab v4, Gitea v1. A handler stands in for the provider, so these say the requests are the
/// documented ones; they cannot say a real provider answers them the same way, and none has been asked.
/// </summary>
public sealed class GitProviderClientTests
{
    /// <summary>Answers by path and query, and keeps every request it was sent.</summary>
    internal sealed class ScriptedProvider : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => Json("[]");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return Answer(request);
        }

        public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
            return response;
        }
    }

    internal sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static string GitHubRepos(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i =>
        $$"""{"full_name":"acme/repo-{{i}}","clone_url":"https://github.com/acme/repo-{{i}}.git","default_branch":"main","private":{{(i % 2 == 0 ? "true" : "false")}}}""")) + "]";

    private static int Page(HttpRequestMessage request) =>
        int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"] ?? "1");

    [Fact]
    public async Task GitHub_is_asked_for_the_accounts_repositories_with_its_token_and_read_page_after_page()
    {
        var provider = new ScriptedProvider
        {
            Answer = r => ScriptedProvider.Json(Page(r) == 1 ? GitHubRepos(1, 100) : GitHubRepos(101, 7))
        };

        var repositories = await new GitProviderClient(new Factory(provider))
            .ListRepositoriesAsync(GitProviderType.GitHub, "", "ghp_token", default);

        repositories.Should().HaveCount(107, "a short page is the last one");
        provider.Requests.Should().HaveCount(2);
        provider.Requests[0].RequestUri!.ToString().Should().Be("https://api.github.com/user/repos?per_page=100&sort=updated&page=1");
        provider.Requests[0].Headers.Authorization!.ToString().Should().Be("token ghp_token");
        provider.Requests[0].Headers.Accept.ToString().Should().Contain("application/vnd.github+json");
        repositories[1].Should().Be(new RemoteRepository("acme/repo-2", "https://github.com/acme/repo-2.git", "main", true));
    }

    [Fact]
    public async Task GitHub_Enterprise_is_asked_at_its_own_address()
    {
        var provider = new ScriptedProvider();

        await new GitProviderClient(new Factory(provider))
            .ListRepositoriesAsync(GitProviderType.GitHub, "https://ghe.example.com/api/v3/", "t", default);

        provider.Requests.Single().RequestUri!.GetLeftPart(UriPartial.Path).Should().Be("https://ghe.example.com/api/v3/user/repos");
    }

    [Fact]
    public async Task GitLab_is_asked_for_the_projects_the_account_is_a_member_of_with_a_bearer_token()
    {
        var provider = new ScriptedProvider
        {
            Answer = _ => ScriptedProvider.Json(
                """[{"path_with_namespace":"group/sub/shop","http_url_to_repo":"https://gitlab.example.com/group/sub/shop.git","default_branch":"trunk","visibility":"internal"}]""",
                headers: ("X-Total", "1"))
        };

        var repositories = await new GitProviderClient(new Factory(provider))
            .ListRepositoriesAsync(GitProviderType.GitLab, "https://gitlab.example.com", "glpat-token", default);

        var request = provider.Requests.Single();
        request.RequestUri!.ToString().Should().Be(
            "https://gitlab.example.com/api/v4/projects?membership=true&per_page=100&order_by=last_activity_at&page=1");
        request.Headers.Authorization!.ToString().Should().Be("Bearer glpat-token",
            "an OAuth token from Connect with GitLab only travels as a bearer token");
        repositories.Should().ContainSingle().Which.Should().Be(
            new RemoteRepository("group/sub/shop", "https://gitlab.example.com/group/sub/shop.git", "trunk", true));
    }

    [Fact]
    public async Task Gitea_reads_on_while_its_reported_total_says_there_is_more_even_when_its_pages_are_short()
    {
        // An install whose MAX_RESPONSE_ITEMS is 30 answers 30 to a request for 50.
        string Repos(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i =>
            $$"""{"full_name":"me/r{{i}}","clone_url":"https://code.example.com/me/r{{i}}.git","default_branch":"main","private":false}""")) + "]";
        var provider = new ScriptedProvider
        {
            Answer = r => Page(r) switch
            {
                1 => ScriptedProvider.Json(Repos(1, 30), headers: ("X-Total-Count", "45")),
                _ => ScriptedProvider.Json(Repos(31, 15), headers: ("X-Total-Count", "45"))
            }
        };

        var repositories = await new GitProviderClient(new Factory(provider))
            .ListRepositoriesAsync(GitProviderType.Gitea, "https://code.example.com/", "gitea-token", default);

        repositories.Should().HaveCount(45);
        provider.Requests.Select(r => r.RequestUri!.ToString()).Should().Equal(
            "https://code.example.com/api/v1/user/repos?limit=50&page=1",
            "https://code.example.com/api/v1/user/repos?limit=50&page=2");
        provider.Requests[0].Headers.Authorization!.ToString().Should().Be("token gitea-token");
    }

    [Fact]
    public async Task A_list_stops_at_the_ceiling_however_many_the_account_has()
    {
        var provider = new ScriptedProvider { Answer = r => ScriptedProvider.Json(GitHubRepos((Page(r) - 1) * 100, 100)) };

        var repositories = await new GitProviderClient(new Factory(provider))
            .ListRepositoriesAsync(GitProviderType.GitHub, "", "t", default);

        repositories.Should().HaveCount(IGitProviderClient.MaxRepositories);
        provider.Requests.Should().HaveCount(IGitProviderClient.MaxRepositories / 100);
    }

    [Fact]
    public async Task A_refused_token_is_an_exception_that_carries_the_status_and_not_the_token()
    {
        var provider = new ScriptedProvider { Answer = _ => ScriptedProvider.Json("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized) };

        var act = () => new GitProviderClient(new Factory(provider))
            .ListRepositoriesAsync(GitProviderType.GitHub, "", "ghp_secret", default);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        thrown.Which.Message.Should().NotContain("ghp_secret");
    }
}
