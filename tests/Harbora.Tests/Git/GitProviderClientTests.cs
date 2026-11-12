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

    // ---- push webhooks ----

    private static readonly HookSpec Spec = new("https://panel.example.com/webhooks/git/0190aaaa-0000-7000-8000-000000000001", "the-secret");

    [Fact]
    public async Task GitHub_hooks_are_listed_added_and_updated_on_the_repository_as_its_api_documents()
    {
        var provider = new ScriptedProvider
        {
            Answer = r => r.Method == HttpMethod.Get
                ? ScriptedProvider.Json("""[{"id":7,"active":false,"config":{"url":"https://elsewhere.example/hook"}},{"id":42,"active":true,"config":{"url":"https://panel.example.com/webhooks/git/x"}}]""")
                : ScriptedProvider.Json("""{"id":43,"active":true,"config":{"url":"https://panel.example.com/webhooks/git/0190aaaa-0000-7000-8000-000000000001"}}""",
                    r.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
        };
        var client = new GitProviderClient(new Factory(provider));

        var hooks = await client.ListHooksAsync(GitProviderType.GitHub, "", "ghp_t", "acme/shop", default);
        var made = await client.CreateHookAsync(GitProviderType.GitHub, "", "ghp_t", "acme/shop", Spec, default);
        await client.UpdateHookAsync(GitProviderType.GitHub, "", "ghp_t", "acme/shop", "42", Spec, default);

        hooks.Should().Equal(new RemoteHook("7", "https://elsewhere.example/hook", false), new RemoteHook("42", "https://panel.example.com/webhooks/git/x", true));
        made.Id.Should().Be("43");
        provider.Requests.Select(r => $"{r.Method} {r.RequestUri}").Should().Equal(
            "GET https://api.github.com/repos/acme/shop/hooks?per_page=100",
            "POST https://api.github.com/repos/acme/shop/hooks",
            "PATCH https://api.github.com/repos/acme/shop/hooks/42");
        provider.Requests.Should().OnlyContain(r => r.Headers.Authorization!.ToString() == "token ghp_t");

        var created = System.Text.Json.JsonDocument.Parse(provider.Bodies[1]!).RootElement;
        created.GetProperty("name").GetString().Should().Be("web");
        created.GetProperty("active").GetBoolean().Should().BeTrue();
        created.GetProperty("events").EnumerateArray().Select(e => e.GetString()).Should().Equal("push");
        created.GetProperty("config").GetProperty("url").GetString().Should().Be(Spec.Url);
        created.GetProperty("config").GetProperty("content_type").GetString().Should().Be("json");
        created.GetProperty("config").GetProperty("secret").GetString().Should().Be("the-secret");
        System.Text.Json.JsonDocument.Parse(provider.Bodies[2]!).RootElement.TryGetProperty("name", out _).Should().BeFalse(
            "an edit does not rename the hook");
    }

    [Fact]
    public async Task GitLab_hooks_live_on_the_url_encoded_project_and_carry_the_secret_as_their_token()
    {
        var provider = new ScriptedProvider
        {
            Answer = r => r.Method == HttpMethod.Get
                ? ScriptedProvider.Json("""[{"id":5,"url":"https://panel.example.com/webhooks/git/x","alert_status":"disabled"},{"id":6,"url":"https://other.example/","push_events":true}]""")
                : ScriptedProvider.Json("""{"id":9,"url":"https://panel.example.com/webhooks/git/0190aaaa-0000-7000-8000-000000000001"}""", HttpStatusCode.Created)
        };
        var client = new GitProviderClient(new Factory(provider));

        var hooks = await client.ListHooksAsync(GitProviderType.GitLab, "https://gitlab.example.com", "glpat", "group/sub/shop", default);
        await client.CreateHookAsync(GitProviderType.GitLab, "https://gitlab.example.com", "glpat", "group/sub/shop", Spec, default);
        await client.UpdateHookAsync(GitProviderType.GitLab, "https://gitlab.example.com", "glpat", "group/sub/shop", "5", Spec, default);

        hooks[0].Active.Should().BeFalse("GitLab turned it off after failures, so it delivers nothing");
        hooks[1].Active.Should().BeTrue();
        provider.Requests.Select(r => $"{r.Method} {r.RequestUri!.AbsoluteUri}").Should().Equal(
            "GET https://gitlab.example.com/api/v4/projects/group%2Fsub%2Fshop/hooks?per_page=100",
            "POST https://gitlab.example.com/api/v4/projects/group%2Fsub%2Fshop/hooks",
            "PUT https://gitlab.example.com/api/v4/projects/group%2Fsub%2Fshop/hooks/5");

        var body = System.Text.Json.JsonDocument.Parse(provider.Bodies[1]!).RootElement;
        body.GetProperty("url").GetString().Should().Be(Spec.Url);
        body.GetProperty("token").GetString().Should().Be("the-secret", "the webhook processor compares X-Gitlab-Token with the secret");
        body.GetProperty("push_events").GetBoolean().Should().BeTrue();
        body.GetProperty("tag_push_events").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Gitea_hooks_are_of_its_own_type_and_signed_with_the_secret()
    {
        var provider = new ScriptedProvider
        {
            Answer = r => r.Method == HttpMethod.Get
                ? ScriptedProvider.Json("""[{"id":3,"type":"gitea","active":true,"config":{"url":"https://panel.example.com/webhooks/git/x","content_type":"json"}}]""")
                : ScriptedProvider.Json("""{"id":4,"type":"gitea","active":true,"config":{"url":"https://panel.example.com/webhooks/git/y"}}""", HttpStatusCode.Created)
        };
        var client = new GitProviderClient(new Factory(provider));

        var hooks = await client.ListHooksAsync(GitProviderType.Gitea, "https://code.example.com/", "gt", "me/app", default);
        await client.CreateHookAsync(GitProviderType.Gitea, "https://code.example.com/", "gt", "me/app", Spec, default);
        await client.UpdateHookAsync(GitProviderType.Gitea, "https://code.example.com/", "gt", "me/app", "3", Spec, default);

        hooks.Should().ContainSingle().Which.Should().Be(new RemoteHook("3", "https://panel.example.com/webhooks/git/x", true));
        provider.Requests.Select(r => $"{r.Method} {r.RequestUri}").Should().Equal(
            "GET https://code.example.com/api/v1/repos/me/app/hooks?limit=50",
            "POST https://code.example.com/api/v1/repos/me/app/hooks",
            "PATCH https://code.example.com/api/v1/repos/me/app/hooks/3");
        var body = System.Text.Json.JsonDocument.Parse(provider.Bodies[1]!).RootElement;
        body.GetProperty("type").GetString().Should().Be("gitea");
        body.GetProperty("config").GetProperty("secret").GetString().Should().Be("the-secret");
        body.GetProperty("config").GetProperty("content_type").GetString().Should().Be("json");
    }

    [Fact]
    public void A_hook_spec_prints_its_address_and_never_its_secret() =>
        Spec.ToString().Should().Contain(Spec.Url).And.NotContain("the-secret");
}
