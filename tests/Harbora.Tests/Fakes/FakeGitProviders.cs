using System.Net;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Microsoft.Extensions.Configuration;

namespace Harbora.Tests.Fakes;

/// <summary>
/// A provider API that answers what a test says it holds, and records who asked with which token.
/// What the real client sends over the wire is <c>GitProviderClientTests</c>' business; this is for the
/// services that call it.
/// </summary>
public sealed class FakeGitProviders : IGitProviderClient
{
    private int _nextHook;

    /// <summary>What each token's account can see.</summary>
    public Dictionary<string, List<RemoteRepository>> Repositories { get; } = new(StringComparer.Ordinal);

    /// <summary>Thrown by every list, while set.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Thrown by a list asked with this token only, for a host shared by tests that must not see each other's failures.</summary>
    public Dictionary<string, Exception> Failing { get; } = new(StringComparer.Ordinal);

    public List<(GitProviderType Type, string ApiBaseUrl, string Token)> Listed { get; } = [];

    /// <summary>The hooks on each repository, by its full name.</summary>
    public Dictionary<string, List<RemoteHook>> Hooks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Thrown by every hook call made with this token.</summary>
    public Dictionary<string, Exception> HookFailing { get; } = new(StringComparer.Ordinal);

    /// <summary>Every hook call, in order: <c>list</c>, <c>create</c> or <c>update</c>, with what it was given.</summary>
    public List<(string Call, string FullName, string Token, HookSpec? Spec)> HookCalls { get; } = [];

    /// <summary>The real webhook service over this fake provider, for a harness that builds its services by hand.</summary>
    public Harbora.Infrastructure.Git.GitHookService HookService(
        Harbora.Data.HarboraDbContext db, ISecretProtector protector, IAuditLogger audit, ISystemClock clock,
        string? panelDomain = "panel.example.com") => new(
        db, this, protector, audit,
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PANEL_DOMAIN"] = panelDomain }).Build(),
        new Harbora.Infrastructure.Git.GitHookStatusCache(), clock,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<Harbora.Infrastructure.Git.GitHookService>.Instance);

    public FakeGitProviders Holding(string token, params RemoteRepository[] repositories)
    {
        Repositories[token] = [.. repositories];
        return this;
    }

    public Task<IReadOnlyList<RemoteRepository>> ListRepositoriesAsync(GitProviderType type, string apiBaseUrl, string token, CancellationToken ct)
    {
        lock (Listed) Listed.Add((type, apiBaseUrl, token));
        if (FailWith is { } failure) throw failure;
        if (Failing.TryGetValue(token, out var own)) throw own;
        return Task.FromResult<IReadOnlyList<RemoteRepository>>(Repositories.TryGetValue(token, out var held) ? held : []);
    }

    public Task<IReadOnlyList<RemoteHook>> ListHooksAsync(GitProviderType type, string apiBaseUrl, string token, string fullName, CancellationToken ct)
    {
        Record("list", fullName, token, null);
        return Task.FromResult<IReadOnlyList<RemoteHook>>(HooksOf(fullName).ToList());
    }

    public Task<RemoteHook> CreateHookAsync(GitProviderType type, string apiBaseUrl, string token, string fullName, HookSpec spec, CancellationToken ct)
    {
        Record("create", fullName, token, spec);
        var hook = new RemoteHook($"hook-{Interlocked.Increment(ref _nextHook)}", spec.Url, true);
        HooksOf(fullName).Add(hook);
        return Task.FromResult(hook);
    }

    public Task UpdateHookAsync(GitProviderType type, string apiBaseUrl, string token, string fullName, string hookId, HookSpec spec, CancellationToken ct)
    {
        Record("update", fullName, token, spec);
        var hooks = HooksOf(fullName);
        var at = hooks.FindIndex(h => h.Id == hookId);
        if (at < 0) throw new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);
        hooks[at] = hooks[at] with { Url = spec.Url, Active = true };
        return Task.CompletedTask;
    }

    private void Record(string call, string fullName, string token, HookSpec? spec)
    {
        lock (HookCalls) HookCalls.Add((call, fullName, token, spec));
        if (HookFailing.TryGetValue(token, out var failure)) throw failure;
    }

    private List<RemoteHook> HooksOf(string fullName)
    {
        lock (Hooks)
        {
            if (!Hooks.TryGetValue(fullName, out var hooks)) Hooks[fullName] = hooks = [];
            return hooks;
        }
    }
}
